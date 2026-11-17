import asyncio
import os
from contextlib import asynccontextmanager
from datetime import timedelta
from uuid import uuid4

import pytest
from temporalio.client import WorkflowExecutionStatus
from temporalio.contrib.pydantic import pydantic_data_converter
from temporalio.service import RPCError, RPCStatusCode
from temporalio.worker import Replayer, Worker

from approval.activities import apply_decision, record_decision, record_submission
from approval.models import ChainInput, ChainResult, Decision, Progress, StepResult
from approval.runtime import connect
from approval.search import attributes
from approval.workflows import ApprovalChain, ApprovalStep

pytestmark = pytest.mark.skipif(
    os.getenv("TEMPORAL_TEST") != "1", reason="Start the Docker stack and set TEMPORAL_TEST=1."
)


def chain(timeout=2) -> ChainInput:
    return ChainInput.model_validate(
        {
            "planId": "sunny-soda",
            "version": 3,
            "requester": "requester",
            "correlationId": "test-correlation",
            "policy": {
                "id": "policy",
                "clientId": "client",
                "accountId": None,
                "workflowCode": "B07",
                "version": 1,
                "source": "client default",
                "routing": {
                    "budgetAmount": "7500",
                    "currency": "USD",
                    "stages": [
                        {
                            "stage": {
                                "name": "parallel",
                                "roles": ["Finance", "Legal"],
                                "timeoutMinutes": timeout,
                            },
                            "included": True,
                            "reason": "Included: unconditional.",
                        },
                        {
                            "stage": {"name": "release", "roles": ["Owner"]},
                            "included": True,
                            "reason": "Included: unconditional.",
                        },
                        {
                            "stage": {"name": "final", "roles": ["Lead"]},
                            "included": True,
                            "reason": "Included: unconditional.",
                        },
                    ],
                },
                "stages": [
                    {
                        "name": "parallel",
                        "timeoutMinutes": timeout,
                        "approvals": [
                            {"role": "Finance", "approver": "finance"},
                            {"role": "Legal", "approver": "legal"},
                        ],
                    },
                    {
                        "name": "release",
                        "timeoutMinutes": 2,
                        "approvals": [{"role": "Owner", "approver": "owner"}],
                    },
                    {
                        "name": "final",
                        "timeoutMinutes": 2,
                        "approvals": [{"role": "Lead", "approver": "lead"}],
                    },
                ],
            },
        }
    )


@asynccontextmanager
async def running(request):
    client = await connect()
    queue = f"test-{uuid4().hex}"
    async with Worker(
        client,
        task_queue=queue,
        workflows=[ApprovalChain, ApprovalStep],
        activities=[record_submission, record_decision, apply_decision],
    ):
        handle = await client.start_workflow(
            ApprovalChain.run,
            request,
            id=queue,
            task_queue=queue,
            search_attributes=attributes(request, "Starting"),
        )
        yield client, handle


async def waiting(client, workflow_id):
    handle = client.get_workflow_handle(workflow_id, result_type=StepResult)
    for _ in range(100):
        try:
            progress: Progress = await handle.query(
                ApprovalStep.query_progress, rpc_timeout=timedelta(seconds=5)
            )
            if progress.state == "Waiting":
                return handle
        except RPCError as error:
            if error.status != RPCStatusCode.NOT_FOUND:
                raise
        await asyncio.sleep(0.1)
    raise AssertionError(f"{workflow_id} never became Waiting")


async def finish(handle):
    return await asyncio.wait_for(handle.result(), 20)


async def test_parallel_ordered_stages_and_single_apply_with_replay():
    async with running(chain()) as (client, parent):
        finance = await waiting(client, f"{parent.id}-s1-r1")
        legal = await waiting(client, f"{parent.id}-s1-r2")
        await legal.signal(
            ApprovalStep.decide, Decision(approver="legal", outcome="Approve", version=3)
        )
        await finish(legal)
        assert (await parent.query(ApprovalChain.query_progress)).stage_number == 1
        await finance.signal(
            ApprovalStep.decide, Decision(approver="finance", outcome="Approve", version=3)
        )
        owner = await waiting(client, f"{parent.id}-s2-r1")
        await owner.signal(
            ApprovalStep.decide, Decision(approver="owner", outcome="Approve", version=3)
        )
        lead = await waiting(client, f"{parent.id}-s3-r1")
        await lead.signal(
            ApprovalStep.decide, Decision(approver="lead", outcome="Approve", version=3)
        )
        result: ChainResult = await finish(parent)
        assert result.status == "Approved"
        assert result.receipt == "sunny-soda:v3:approved"
        assert [step.role for step in result.steps] == ["Finance", "Legal", "Owner", "Lead"]
        history = await parent.fetch_history()
        names = [
            e.activity_task_scheduled_event_attributes.activity_type.name
            for e in history.events
            if e.HasField("activity_task_scheduled_event_attributes")
        ]
        assert names == ["ApplyDecision"]
        await Replayer(
            workflows=[ApprovalChain, ApprovalStep], data_converter=pydantic_data_converter
        ).replay_workflow(history)


@pytest.mark.parametrize(("outcome", "expected"), [("Reject", "Rejected"), ("Return", "Returned")])
async def test_negative_decision_cancels_waiting_sibling_and_skips_stages(outcome, expected):
    async with running(chain()) as (client, parent):
        finance = await waiting(client, f"{parent.id}-s1-r1")
        sibling = await waiting(client, f"{parent.id}-s1-r2")
        await finance.signal(
            ApprovalStep.decide,
            Decision(approver="finance", outcome=outcome, version=3, comment="Budget needs work"),
        )
        result = await finish(parent)
        assert result.status == expected
        assert result.receipt is None
        assert result.stages_not_started == ["release", "final"]
        assert [s.status for s in result.steps] == [expected, "Canceled"]
        assert result.steps[0].decided_by == "finance"
        assert result.steps[0].comment == "Budget needs work"
        assert (await sibling.result()).status == "Canceled"
        with pytest.raises(RPCError) as error:
            await client.get_workflow_handle(f"{parent.id}-s2-r1").describe()
        assert error.value.status == RPCStatusCode.NOT_FOUND
        await Replayer(
            workflows=[ApprovalChain, ApprovalStep], data_converter=pydantic_data_converter
        ).replay_workflow(await parent.fetch_history())


async def test_second_stage_reject_never_creates_third_stage():
    async with running(chain()) as (client, parent):
        for i, actor in [(2, "legal"), (1, "finance")]:
            child = await waiting(client, f"{parent.id}-s1-r{i}")
            await child.signal(
                ApprovalStep.decide, Decision(approver=actor, outcome="Approve", version=3)
            )
        owner = await waiting(client, f"{parent.id}-s2-r1")
        await owner.signal(
            ApprovalStep.decide,
            Decision(approver="owner", outcome="Reject", version=3, comment="No"),
        )
        result = await finish(parent)
        assert result.status == "Rejected"
        assert result.stages_not_started == ["final"]
        assert [s.status for s in result.steps] == ["Approved", "Approved", "Rejected"]


async def test_invalid_decisions_do_not_complete_or_reset_deadline():
    async with running(chain(timeout=0.04)) as (client, parent):
        child = await waiting(client, f"{parent.id}-s1-r1")
        for decision in [
            Decision(approver="requester", outcome="Approve", version=3),
            Decision(approver="wrong", outcome="Approve", version=3),
            Decision(approver="finance", outcome="Approve", version=2),
            Decision(approver="finance", outcome="approve", version=3),
            Decision(approver="finance", outcome="Reject", version=3),
        ]:
            await child.signal(ApprovalStep.decide, decision)
        result = await finish(parent)
        assert result.status == "Timeout"
        progress = await child.query(ApprovalStep.query_progress)
        assert progress.refused_decisions == 5
        history = await child.fetch_history()
        timers = [e for e in history.events if e.HasField("timer_started_event_attributes")]
        assert len(timers) == 1
        assert not result.receipt


async def test_waiting_workflow_recovers_on_a_new_worker():
    client = await connect()
    queue = f"restart-{uuid4().hex}"
    options = dict(
        task_queue=queue,
        workflows=[ApprovalChain, ApprovalStep],
        activities=[record_submission, record_decision, apply_decision],
    )
    async with Worker(client, **options):
        parent = await client.start_workflow(
            ApprovalChain.run,
            chain(),
            id=queue,
            task_queue=queue,
            search_attributes=attributes(chain(), "Starting"),
        )
        await waiting(client, f"{parent.id}-s1-r1")
    async with Worker(client, **options):
        child = await waiting(client, f"{parent.id}-s1-r1")
        await child.signal(
            ApprovalStep.decide,
            Decision(
                approver="finance", outcome="Return", version=3, comment="After worker replacement"
            ),
        )
        assert (await finish(parent)).status == "Returned"
        assert (await parent.describe()).status == WorkflowExecutionStatus.COMPLETED
