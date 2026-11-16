import asyncio
from datetime import timedelta

from temporalio import workflow
from temporalio.common import RetryPolicy
from temporalio.exceptions import ApplicationError

with workflow.unsafe.imports_passed_through():
    from approval.activities import apply_decision, record_decision, record_submission
    from approval.models import (
        ENDING,
        ApplyRequest,
        ChainInput,
        ChainResult,
        Decision,
        DecisionRecord,
        Progress,
        StepInput,
        StepRecord,
        StepResult,
        refusal,
        step_id,
    )
    from approval.search import STATE, attributes

ACTIVITY_TIMEOUT = timedelta(seconds=90)
RETRY = RetryPolicy(maximum_attempts=3)


@workflow.defn(name="ApprovalStep")
class ApprovalStep:
    @workflow.init
    def __init__(self, request: StepInput) -> None:
        self.request = request
        self.result: StepResult | None = None
        self.progress = Progress(
            state="Starting", stage=request.stage, stage_number=request.stage_number
        )

    @workflow.signal(name="Decision")
    def decide(self, decision: Decision) -> None:
        if self.result is not None:
            return
        problem = refusal(self.request, decision)
        if problem:
            workflow.logger.warning("Decision refused: %s", problem)
            self.progress = self.progress.model_copy(
                update={
                    "refused_decisions": self.progress.refused_decisions + 1,
                    "last_refusal": problem,
                }
            )
            return
        status = {"Approve": "Approved", "Reject": "Rejected", "Return": "Returned"}[
            decision.outcome
        ]
        self.result = StepResult(
            status=status,
            version=self.request.chain.version,
            decided_by=self.request.approver,
            decided_at=workflow.now(),
            comment=decision.comment.strip() if decision.comment else None,
        )

    @workflow.signal(name="Cancel")
    def cancel(self, reason: str) -> None:
        if self.result is None:
            self.result = StepResult(
                status="Canceled",
                version=self.request.chain.version,
                decided_at=workflow.now(),
            )

    @workflow.query(name="progress")
    def query_progress(self) -> Progress:
        return self.progress

    @workflow.run
    async def run(self, request: StepInput) -> StepResult:
        await workflow.execute_activity(
            record_submission, request, start_to_close_timeout=ACTIVITY_TIMEOUT, retry_policy=RETRY
        )
        self.progress = self.progress.model_copy(update={"state": "Waiting"})
        workflow.upsert_search_attributes([STATE.value_set("Waiting")])
        try:
            await workflow.wait_condition(
                lambda: self.result is not None,
                timeout=timedelta(minutes=request.timeout_minutes),
            )
        except TimeoutError:
            self.result = StepResult(
                status="Timeout", version=request.chain.version, decided_at=workflow.now()
            )
        assert self.result is not None
        await workflow.execute_activity(
            record_decision,
            DecisionRecord(request=request, result=self.result),
            start_to_close_timeout=ACTIVITY_TIMEOUT,
            retry_policy=RETRY,
        )
        self.progress = self.progress.model_copy(
            update={"state": self.result.status, "completed": 1}
        )
        workflow.upsert_search_attributes([STATE.value_set(self.result.status)])
        return self.result


@workflow.defn(name="ApprovalChain")
class ApprovalChain:
    @workflow.init
    def __init__(self, request: ChainInput) -> None:
        self.progress = Progress(
            state="Starting",
            stage=request.policy.stages[0].name,
            stage_number=1,
            total=sum(len(stage.approvals) for stage in request.policy.stages),
        )

    @workflow.query(name="progress")
    def query_progress(self) -> Progress:
        return self.progress

    @workflow.run
    async def run(self, request: ChainInput) -> ChainResult:
        records: list[StepRecord] = []
        ending = None
        last_stage = 0
        for stage_number, stage in enumerate(request.policy.stages, 1):
            last_stage = stage_number
            self.progress = self.progress.model_copy(
                update={"state": "Running", "stage": stage.name, "stage_number": stage_number}
            )
            workflow.upsert_search_attributes([STATE.value_set("Waiting")])
            handles = []
            for reviewer, role in enumerate(stage.approvals, 1):
                child = await workflow.start_child_workflow(
                    ApprovalStep.run,
                    StepInput(
                        parent_instance_id=workflow.info().workflow_id,
                        chain=request,
                        stage=stage.name,
                        stage_number=stage_number,
                        role=role.role,
                        approver=role.approver,
                        timeout_minutes=stage.timeout_minutes,
                    ),
                    id=step_id(workflow.info().workflow_id, stage_number, reviewer),
                    search_attributes=attributes(request, "Starting"),
                )
                handles.append(child)
            pending = set(handles)
            try:
                while pending:
                    done, pending = await workflow.wait(
                        pending, return_when=asyncio.FIRST_COMPLETED
                    )
                    # Preserve policy ordering when several completions arrive in one workflow task.
                    for child in handles:
                        if child not in done:
                            continue
                        outcome = await child
                        self.progress = self.progress.model_copy(
                            update={"completed": self.progress.completed + 1}
                        )
                        if ending is None and outcome.status in ENDING:
                            ending = outcome.status
                            for sibling in handles:
                                if sibling in pending:
                                    try:
                                        await sibling.signal(ApprovalStep.cancel, ending)
                                    except ApplicationError as error:
                                        if error.type != "ExternalWorkflowExecutionNotFound":
                                            raise
                                        workflow.logger.info(
                                            "Reviewer %s finished during cancellation; preserving its result.",
                                            sibling.id,
                                        )
                for role, child in zip(stage.approvals, handles, strict=True):
                    outcome = await child
                    records.append(
                        StepRecord(
                            **outcome.model_dump(),
                            instance_id=child.id,
                            stage=stage.name,
                            role=role.role,
                            approver=role.approver,
                        )
                    )
            except BaseException:
                for child in handles:
                    if not child.done():
                        child.cancel()
                raise
            if ending is not None:
                break
        status = ending or "Approved"
        receipt = None
        if status == "Approved":
            workflow.upsert_search_attributes([STATE.value_set("Applying")])
            receipt = await workflow.execute_activity(
                apply_decision,
                ApplyRequest(
                    receipt=f"{request.plan_id}:v{request.version}:approved",
                    delay_seconds=request.apply_decision_delay_seconds,
                ),
                start_to_close_timeout=ACTIVITY_TIMEOUT,
                heartbeat_timeout=timedelta(seconds=5),
                retry_policy=RETRY,
            )
        self.progress = self.progress.model_copy(update={"state": status})
        workflow.upsert_search_attributes([STATE.value_set(status)])
        return ChainResult(
            status=status,
            version=request.version,
            policy_id=request.policy.id,
            policy_version=request.policy.version,
            steps=records,
            stages_not_started=[stage.name for stage in request.policy.stages[last_stage:]],
            receipt=receipt,
        )
