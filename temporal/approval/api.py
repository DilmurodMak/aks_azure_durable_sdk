import base64
import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Annotated
from uuid import uuid4

from fastapi import FastAPI, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from opentelemetry import trace
from pydantic import ValidationError
from temporalio.client import Client, WorkflowExecutionStatus
from temporalio.common import WorkflowIDReusePolicy
from temporalio.exceptions import WorkflowAlreadyStartedError
from temporalio.service import RPCError, RPCStatusCode

from approval.catalogue import STEPS
from approval.models import (
    ChainInput,
    ChainResult,
    Decision,
    DecisionForm,
    Key,
    Progress,
    SaveIdentities,
    SavePolicy,
    StartRequest,
    StepResult,
    comment_problem,
    step_id,
)
from approval.runtime import configure_telemetry, connect
from approval.search import ACCOUNT, CLIENT, CORRELATION, POLICY_VERSION, STATE, STEP, attributes
from approval.store import Conflict, Store
from approval.workflows import ApprovalChain, ApprovalStep

logger = logging.getLogger(__name__)
STATIC = Path(__file__).resolve().parent.parent / "static"


@asynccontextmanager
async def lifespan(app: FastAPI):
    provider = configure_telemetry("temporal-approval-console")
    app.state.client = await connect()
    app.state.store = Store(Path(os.getenv("APPROVAL_DATA_DIR", ".data")))
    yield
    provider.shutdown()


app = FastAPI(title="Temporal approval spike", lifespan=lifespan, openapi_url="/openapi/v1.json")


@app.middleware("http")
async def local_console_guard(request: Request, call_next):
    if request.url.hostname not in {"127.0.0.1", "localhost", "testserver"}:
        return JSONResponse({"title": "Keep this simulator on loopback."}, status_code=403)
    if request.method not in {"GET", "HEAD"}:
        origin = request.headers.get("origin")
        allowed = {f"http://{request.headers.get('host')}"}
        if request.headers.get("x-approval-console") != "spike" or (
            origin and origin not in allowed
        ):
            return JSONResponse({"title": "Untrusted console request."}, status_code=403)
    response = await call_next(request)
    response.headers["Cache-Control"] = "no-cache"
    return response


@app.exception_handler(Conflict)
async def conflict_handler(request: Request, error: Conflict):
    logger.warning("Request conflict: %s", error)
    return JSONResponse({"title": str(error)}, status_code=409)


@app.exception_handler(KeyError)
async def not_found_handler(request: Request, error: KeyError):
    logger.warning("Not found: %s", error)
    return JSONResponse({"title": "Workflow or policy was not found."}, status_code=404)


@app.exception_handler(RequestValidationError)
async def validation_handler(request: Request, error: RequestValidationError):
    logger.warning("Invalid request at %s", request.url.path)
    return JSONResponse({"title": "Invalid request", "detail": str(error)}, status_code=400)


@app.exception_handler(RPCError)
async def scheduler_handler(request: Request, error: RPCError):
    logger.error("Temporal request failed: %s", error.status, exc_info=True)
    status = 404 if error.status == RPCStatusCode.NOT_FOUND else 502
    return JSONResponse(
        {"title": "Temporal request failed; inspect console logs."}, status_code=status
    )


def client(request: Request) -> Client:
    return request.app.state.client


def store(request: Request) -> Store:
    return request.app.state.store


@app.get("/api/health")
async def health(request: Request):
    result = await client(request).service_client.check_health()
    return {"ready": result, "engine": "Temporal", "namespace": client(request).namespace}


@app.get("/api/environment")
async def environment():
    return {
        "deployment": os.getenv("APPROVAL_DEPLOYMENT", "Docker Desktop"),
        "temporalUiUrl": os.getenv("TEMPORAL_UI_URL", "http://127.0.0.1:8088"),
    }


@app.get("/api/policies")
async def policies(request: Request):
    return store(request).policies()


@app.post("/api/policies")
async def save_policy(payload: SavePolicy, request: Request):
    if any(item.workflow_code not in STEPS for item in payload.overrides):
        return JSONResponse(
            {"title": "Override business step is not in the catalogue."}, status_code=400
        )
    return store(request).save(payload)


@app.get("/api/business-steps")
async def business_steps():
    return [{"code": code, "name": name} for code, name in STEPS.items()]


@app.post("/api/policies/{policy_id}/activate")
async def activate(policy_id: Key, version: int, request: Request):
    return store(request).activate(policy_id, version)


@app.get("/api/demo-identities")
async def identities(request: Request):
    return store(request).identities()


@app.post("/api/demo-identities")
async def save_identities(payload: SaveIdentities, request: Request):
    return store(request).save_identities(payload)


@app.post("/api/workflows", status_code=201)
async def start(payload: StartRequest, request: Request):
    snapshot = store(request).resolve(
        payload.client_id, payload.account_id, payload.workflow_code, payload.budget_amount
    )
    try:
        resolved = ChainInput(
            plan_id=payload.plan_id,
            version=payload.version,
            requester=payload.requester,
            correlation_id=payload.correlation_id or uuid4().hex,
            policy=snapshot,
            apply_decision_delay_seconds=payload.apply_decision_delay_seconds,
        )
    except ValidationError as error:
        raise Conflict(str(error)) from error
    with trace.get_tracer("approval").start_as_current_span(
        "approval.start",
        attributes={
            "approval.client": snapshot.client_id,
            "approval.account": snapshot.account_id or "",
            "approval.startedBy": resolved.requester,
            "approval.correlationId": resolved.correlation_id,
            "approval.budgetAmount": str(snapshot.routing.budget_amount),
            "approval.currency": snapshot.routing.currency,
        },
    ):
        try:
            handle = await client(request).start_workflow(
                ApprovalChain.run,
                resolved,
                id=payload.instance_id,
                task_queue=os.getenv("TEMPORAL_TASK_QUEUE", "approval"),
                search_attributes=attributes(resolved, "Starting"),
                memo={"input": resolved},
                id_reuse_policy=WorkflowIDReusePolicy.REJECT_DUPLICATE,
            )
        except WorkflowAlreadyStartedError as error:
            raise Conflict("Instance already exists. Use a fresh ID.") from error
    return {
        "instanceId": handle.id,
        "correlationId": resolved.correlation_id,
        "policyId": snapshot.id,
        "policyVersion": snapshot.version,
        "source": snapshot.source,
        "routing": snapshot.routing,
        "snapshot": snapshot,
    }


@app.get("/api/workflows")
async def workflows(
    request: Request,
    client_id: Key | None = None,
    account_id: Key | None = None,
    workflow_code: Key | None = None,
    state: Annotated[str | None, Query(pattern=r"^[A-Za-z]{1,30}$")] = None,
    policy_version: Annotated[int | None, Query(ge=1)] = None,
    cursor: str | None = None,
    page_size: Annotated[int, Query(ge=1, le=100)] = 20,
):
    queue = os.getenv("TEMPORAL_TASK_QUEUE", "approval")
    if not queue.replace("-", "").isalnum():
        raise RuntimeError("Use an alphanumeric or hyphenated application task queue.")
    predicates = ["WorkflowType = 'ApprovalChain'", f"TaskQueue = '{queue}'"]
    for key, value in [
        (CLIENT, client_id),
        (ACCOUNT, account_id),
        (STEP, workflow_code),
        (STATE, state),
    ]:
        if value is not None:
            predicates.append(f"{key.name} = '{value}'")
    if policy_version is not None:
        predicates.append(f"{POLICY_VERSION.name} = {policy_version}")
    try:
        token = base64.b64decode(cursor, validate=True) if cursor else None
    except ValueError as error:
        raise Conflict("Invalid page cursor.") from error
    iterator = client(request).list_workflows(
        " AND ".join(predicates), page_size=page_size, next_page_token=token
    )
    await iterator.fetch_next_page()
    rows = []
    for item in iterator.current_page:
        search = item.typed_search_attributes
        rows.append(
            {
                "instanceId": item.id,
                "runId": item.run_id,
                "runtimeStatus": item.status.name if item.status else "Unknown",
                "createdAt": item.start_time,
                "state": search.get(STATE),
                "clientId": search.get(CLIENT),
                "accountId": search.get(ACCOUNT),
                "workflowCode": search.get(STEP),
                "policyVersion": search.get(POLICY_VERSION),
                "correlationId": search.get(CORRELATION),
            }
        )
    return {
        "items": rows,
        "nextCursor": base64.b64encode(iterator.next_page_token).decode()
        if iterator.next_page_token
        else None,
    }


@app.get("/api/workflows/{instance_id}")
async def status(instance_id: Key, request: Request):
    handle = client(request).get_workflow_handle(instance_id, result_type=ChainResult)
    metadata = await handle.describe()
    if metadata.workflow_type != "ApprovalChain":
        raise KeyError(instance_id)
    original: ChainInput = await metadata.memo_value("input", type_hint=ChainInput)
    progress = await handle.query(ApprovalChain.query_progress)
    result = await handle.result() if metadata.status == WorkflowExecutionStatus.COMPLETED else None
    return {
        "instanceId": instance_id,
        "runtimeStatus": metadata.status.name if metadata.status else "Unknown",
        "progress": progress,
        "snapshot": original.policy,
        "routing": original.policy.routing,
        "input": original,
        "result": result,
    }


@app.get("/api/workflows/{instance_id}/approvals")
async def approvals(instance_id: Key, request: Request):
    parent = client(request).get_workflow_handle(instance_id)
    metadata = await parent.describe()
    original: ChainInput = await metadata.memo_value("input", type_hint=ChainInput)
    parent_progress: Progress = await parent.query(ApprovalChain.query_progress)
    rows = []
    for stage_number, stage in enumerate(original.policy.stages, 1):
        for reviewer, role in enumerate(stage.approvals, 1):
            child_id = step_id(instance_id, stage_number, reviewer)
            child = client(request).get_workflow_handle(child_id, result_type=StepResult)
            result = None
            can_view = True
            try:
                child_metadata = await child.describe()
            except RPCError as error:
                if error.status != RPCStatusCode.NOT_FOUND:
                    raise
                can_view = False
                state = (
                    "NotStarted"
                    if metadata.status != WorkflowExecutionStatus.RUNNING
                    else "Queued"
                    if parent_progress.stage_number == stage_number
                    else "Blocked"
                )
                progress = None
            else:
                if child_metadata.status == WorkflowExecutionStatus.COMPLETED:
                    result = await child.result()
                    state = result.status
                    progress = None
                elif child_metadata.status == WorkflowExecutionStatus.RUNNING:
                    progress = await child.query(ApprovalStep.query_progress)
                    state = progress.state
                else:
                    progress = None
                    state = child_metadata.status.name if child_metadata.status else "Unknown"
            rows.append(
                {
                    "instanceId": child_id,
                    "parentInstanceId": instance_id,
                    "version": original.version,
                    "requester": original.requester,
                    "rank": f"{stage_number}.{reviewer}",
                    "stage": stage.name,
                    "role": role.role,
                    "approver": role.approver,
                    "state": state,
                    "canDecide": state == "Waiting"
                    and metadata.status == WorkflowExecutionStatus.RUNNING
                    and parent_progress.stage_number == stage_number,
                    "canView": can_view,
                    "progress": progress,
                    "result": result,
                }
            )
    return rows


@app.post("/api/approvals/{child_id}/decision", status_code=202)
async def decide(child_id: str, payload: DecisionForm, request: Request):
    if problem := comment_problem(payload.outcome, payload.comment):
        return JSONResponse({"title": problem}, status_code=400)
    if len(child_id) > 100 or not all(c.isascii() and (c.isalnum() or c == "-") for c in child_id):
        return JSONResponse({"title": "Invalid step ID."}, status_code=400)
    handle = client(request).get_workflow_handle(child_id)
    metadata = await handle.describe()
    if metadata.workflow_type != "ApprovalStep" or not metadata.parent_id:
        raise Conflict("This is not a reviewer step.")
    rows = await approvals(metadata.parent_id, request)
    row = next(item for item in rows if item["instanceId"] == child_id)
    if not row["canDecide"]:
        raise Conflict("This review is not waiting in its parent's active stage.")
    await handle.signal(
        ApprovalStep.decide,
        Decision(
            approver=payload.acting_as,
            outcome=payload.outcome,
            version=payload.version,
            comment=payload.comment,
        ),
    )
    return {"status": "DecisionSentNotYetApplied", "instanceId": child_id}


@app.get("/")
async def index():
    return FileResponse(STATIC / "index.html")


app.mount("/static", StaticFiles(directory=STATIC), name="static")
