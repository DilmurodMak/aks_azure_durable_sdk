import asyncio
import logging

from opentelemetry import trace
from temporalio import activity

from approval.models import ApplyRequest, DecisionRecord, StepInput

logger = logging.getLogger(__name__)


def tags(request: StepInput) -> dict[str, str | int]:
    return {
        "approval.client": request.chain.policy.client_id,
        "approval.account": request.chain.policy.account_id or "",
        "approval.startedBy": request.chain.requester,
        "approval.correlationId": request.chain.correlation_id,
        "approval.plan": request.chain.plan_id,
        "approval.version": request.chain.version,
        "approval.policyVersion": request.chain.policy.version,
        "approval.stage": request.stage,
        "approval.role": request.role,
    }


@activity.defn(name="RecordSubmission")
async def record_submission(request: StepInput) -> str:
    with trace.get_tracer("approval").start_as_current_span(
        "approval.submitted", attributes=tags(request)
    ):
        logger.info("Submission recorded", extra={"facts": tags(request)})
    return request.chain.plan_id


@activity.defn(name="RecordDecision")
async def record_decision(record: DecisionRecord) -> str:
    facts = tags(record.request)
    facts.update(record.result.model_dump(mode="json", by_alias=True))
    facts["instanceId"] = activity.info().workflow_id or ""
    with trace.get_tracer("approval").start_as_current_span(
        "approval.decision.recorded",
        attributes={**tags(record.request), "approval.status": record.result.status},
    ):
        logger.info("Decision recorded", extra={"facts": facts})
    return record.result.status


@activity.defn(name="ApplyDecision")
async def apply_decision(request: ApplyRequest) -> str:
    logger.info(
        "ApplyDecision attempt",
        extra={"facts": {"receipt": request.receipt, "attempt": activity.info().attempt}},
    )
    for _ in range(request.delay_seconds):
        activity.heartbeat(request.receipt)
        await asyncio.sleep(1)
    return request.receipt
