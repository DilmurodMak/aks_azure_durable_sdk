import asyncio
import os
from uuid import uuid4

import httpx
import pytest
from temporalio.worker import Worker

from approval.activities import apply_decision, record_decision, record_submission
from approval.api import app
from approval.runtime import connect
from approval.store import Store
from approval.workflows import ApprovalChain, ApprovalStep

pytestmark = pytest.mark.skipif(
    os.getenv("TEMPORAL_TEST") != "1", reason="Requires the Docker Temporal server."
)


@pytest.fixture
async def http(tmp_path, monkeypatch):
    queue = f"api-{uuid4().hex}"
    monkeypatch.setenv("TEMPORAL_TASK_QUEUE", queue)
    temporal = await connect()
    app.state.client = temporal
    app.state.store = Store(tmp_path)
    async with Worker(
        temporal,
        task_queue=queue,
        workflows=[ApprovalChain, ApprovalStep],
        activities=[record_submission, record_decision, apply_decision],
    ):
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app),
            base_url="http://127.0.0.1:5090",
            headers={"X-Approval-Console": "spike"},
        ) as http:
            yield http


async def configure(http, stages=None):
    assert (
        await http.post(
            "/api/demo-identities",
            json={
                "clientId": "client",
                "assignments": {"Finance": "finance", "Legal": "legal", "Owner": "owner"},
            },
        )
    ).status_code == 200
    response = await http.post(
        "/api/policies",
        json={
            "id": "policy",
            "clientId": "client",
            "defaultStages": stages
            or [
                {"name": "review", "roles": ["Finance", "Legal"], "timeoutMinutes": 2},
                {"name": "release", "roles": ["Owner"], "timeoutMinutes": 2},
            ],
        },
    )
    assert response.status_code == 200, response.text


async def test_environment_reports_deployment_specific_links(http, monkeypatch):
    monkeypatch.setenv("APPROVAL_DEPLOYMENT", "AKS")
    monkeypatch.setenv("TEMPORAL_UI_URL", "http://127.0.0.1:8089")
    response = await http.get("/api/environment")
    assert response.status_code == 200
    assert response.json() == {
        "deployment": "AKS",
        "temporalUiUrl": "http://127.0.0.1:8089",
    }


async def start(http, **changes):
    return await http.post(
        "/api/workflows",
        json={
            "instanceId": f"api-{uuid4().hex}",
            "clientId": "client",
            "workflowCode": "B07",
            "planId": "plan",
            "version": 1,
            "requester": "requester",
            **changes,
        },
    )


async def wait_rows(http, parent, count):
    for _ in range(100):
        response = await http.get(f"/api/workflows/{parent}/approvals")
        assert response.status_code == 200, response.text
        rows = response.json()
        if sum(row["canDecide"] for row in rows) == count:
            return rows
        await asyncio.sleep(0.1)
    raise AssertionError("Expected waiting reviewers did not appear")


async def test_http_decision_record_and_skipped_stage(http):
    await configure(http)
    response = await start(http)
    assert response.status_code == 201, response.text
    parent = response.json()["instanceId"]
    rows = await wait_rows(http, parent, 2)
    assert rows[-1]["state"] == "Blocked"
    child = rows[0]["instanceId"]
    payload = {"actingAs": "finance", "outcome": "Reject", "version": 1}
    assert (await http.post(f"/api/approvals/{child}/decision", json=payload)).status_code == 400
    assert (
        await http.post(
            f"/api/approvals/{child}/decision", json={**payload, "comment": "No budget"}
        )
    ).status_code == 202
    rows = await wait_rows(http, parent, 0)
    for _ in range(100):
        status = await http.get(f"/api/workflows/{parent}")
        assert status.status_code == 200, status.text
        data = status.json()
        if data["result"] is not None:
            break
        await asyncio.sleep(0.1)
    assert data["result"]["status"] == "Rejected"
    assert data["result"]["steps"][0]["comment"] == "No budget"
    assert data["result"]["stagesNotStarted"] == ["release"]
    assert (
        await http.post(f"/api/approvals/{child}/decision", json={**payload, "comment": "Again"})
    ).status_code == 409


async def test_start_conflicts_and_browser_guards(http):
    await configure(http)
    assert (await start(http, requester="finance")).status_code == 409
    id = f"api-{uuid4().hex}"
    assert (await start(http, instanceId=id)).status_code == 201
    assert (await start(http, instanceId=id)).status_code == 409
    assert (
        await http.post("/api/policies", json={}, headers={"Origin": "https://evil.invalid"})
    ).status_code == 403
    assert (await http.get("/api/policies", headers={"Host": "public.invalid"})).status_code == 403
    rows = await wait_rows(http, id, 2)
    await http.post(
        f"/api/approvals/{rows[0]['instanceId']}/decision",
        json={"actingAs": "finance", "outcome": "Return", "version": 1, "comment": "Done"},
    )
    await wait_rows(http, id, 0)


async def test_indexed_filters_and_pagination(http):
    await configure(http, [{"name": "review", "roles": ["Finance"], "timeoutMinutes": 2}])
    ids = []
    for step in ["B07", "B07", "B08"]:
        response = await start(http, workflowCode=step)
        assert response.status_code == 201, response.text
        ids.append(response.json()["instanceId"])
    for id in ids:
        await wait_rows(http, id, 1)
    for _ in range(100):
        response = await http.get(
            "/api/workflows",
            params={
                "client_id": "client",
                "workflow_code": "B07",
                "state": "Waiting",
                "page_size": 1,
            },
        )
        assert response.status_code == 200, response.text
        page = response.json()
        if page["items"] and page["nextCursor"]:
            break
        await asyncio.sleep(0.1)
    assert len(page["items"]) == 1
    second = (
        await http.get(
            "/api/workflows",
            params={
                "client_id": "client",
                "workflow_code": "B07",
                "state": "Waiting",
                "page_size": 1,
                "cursor": page["nextCursor"],
            },
        )
    ).json()
    assert second["items"][0]["instanceId"] != page["items"][0]["instanceId"]
    assert (await http.get("/api/workflows", params={"client_id": "x' OR 1=1"})).status_code == 400
    for id in ids:
        await http.post(
            f"/api/approvals/{id}-s1-r1/decision",
            json={"actingAs": "finance", "outcome": "Approve", "version": 1},
        )
        await wait_rows(http, id, 0)
