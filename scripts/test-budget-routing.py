"""Repeatable budget-routing proof through either console and the real AKS worker."""

import argparse
import asyncio
import hashlib
import json
import os
import subprocess
from datetime import UTC, datetime
from decimal import Decimal
from pathlib import Path
from urllib.parse import urlparse
from uuid import uuid4

import httpx

ROOT = Path(__file__).resolve().parent.parent


def command(*args: str) -> str:
    return subprocess.check_output(args, cwd=ROOT, text=True)


def source_revision() -> dict:
    patterns = [
        "src/**/*.cs",
        "src/**/*.js",
        "src/**/*.html",
        "src/**/*.css",
        "src/**/*.csproj",
        "temporal/approval/*.py",
        "temporal/static/*",
        "temporal/deploy/*",
        "deploy/*",
        "Dockerfile",
        "temporal/Dockerfile",
        "Directory.Build.props",
        "temporal/pyproject.toml",
        "temporal/uv.lock",
        "scripts/test-budget-routing.py",
    ]
    files = sorted(
        {
            path
            for pattern in patterns
            for path in ROOT.glob(pattern)
            if path.is_file() and not {"bin", "obj", "__pycache__"}.intersection(path.parts)
        }
    )
    manifest = {
        str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in files
    }
    return {
        "baseCommit": command("git", "rev-parse", "HEAD").strip(),
        "workingTreeStatus": command("git", "status", "--short"),
        "sourceSha256": hashlib.sha256(json.dumps(manifest, sort_keys=True).encode()).hexdigest(),
        "sourceFiles": manifest,
    }


def cluster_snapshot(engine: str, port: int) -> dict:
    context = os.environ["AKS_CLUSTER_NAME"]
    if command("kubectl", "config", "current-context").strip() != context:
        raise RuntimeError("Current context is not the approved AKS cluster.")
    namespace = "durable-spike" if engine == "durable" else "temporal-spike"
    names = {"approval-worker"} if engine == "durable" else {"temporal-worker", "temporal-console"}
    objects = json.loads(
        command(
            "kubectl",
            "--context",
            context,
            "-n",
            namespace,
            "get",
            "deployments,pods",
            "-o",
            "json",
        )
    )["items"]
    deployments, pods = [], []
    for item in objects:
        name = item["metadata"]["name"]
        if item["kind"] == "Deployment" and name in names:
            deployments.append(
                {
                    "name": name,
                    "uid": item["metadata"]["uid"],
                    "generation": item["metadata"]["generation"],
                    "images": [c["image"] for c in item["spec"]["template"]["spec"]["containers"]],
                }
            )
        if item["kind"] == "Pod" and any(name.startswith(f"{n}-") for n in names):
            pods.append(
                {
                    "name": name,
                    "uid": item["metadata"]["uid"],
                    "restarts": [
                        c["restartCount"]
                        for c in item.get("status", {}).get("containerStatuses", [])
                    ],
                    "imageIds": [
                        c["imageID"] for c in item.get("status", {}).get("containerStatuses", [])
                    ],
                }
            )
    if len(deployments) != len(names) or len(pods) != len(names):
        raise RuntimeError(
            "Expected one ready application pod per deployment before demonstrating."
        )
    snapshot = {
        "deployments": sorted(deployments, key=lambda x: x["name"]),
        "pods": sorted(pods, key=lambda x: x["name"]),
    }
    if engine == "durable":
        snapshot["consolePid"] = command("lsof", "-tiTCP:" + str(port), "-sTCP:LISTEN").strip()
    return snapshot


async def run(args) -> None:
    parsed = urlparse(args.base)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port:
        raise ValueError("Use an explicit HTTP loopback console port.")
    prefix = f"budget-{args.engine}-{datetime.now(UTC):%Y%m%d%H%M%S}-{uuid4().hex[:4]}"
    evidence = ROOT / ".build" / "evidence" / prefix
    evidence.mkdir(parents=True)

    def save(name: str, payload) -> None:
        (evidence / f"{name}.json").write_text(json.dumps(payload, indent=2, default=str) + "\n")

    revision = source_revision()
    save("tested-source", revision)
    async with httpx.AsyncClient(
        base_url=args.base,
        timeout=90,
        headers={"X-Approval-Console": "spike"},
    ) as http:

        async def get(path: str):
            response = await http.get(path)
            response.raise_for_status()
            return response.json()

        async def post(path: str, body: dict):
            response = await http.post(path, json=body)
            response.raise_for_status()
            return response.json()

        async def waiting(id: str, actors: set[str]):
            for _ in range(180):
                rows = await get(f"/api/workflows/{id}/approvals")
                if {row["approver"] for row in rows if row["canDecide"]} == actors:
                    return rows
                await asyncio.sleep(0.5)
            raise AssertionError(f"{id} did not wait for {actors}")

        async def complete(id: str):
            for _ in range(180):
                status = await get(f"/api/workflows/{id}")
                if status["result"] is not None:
                    assert status["result"]["status"] == "Approved", status
                    save(f"{id}-final", status)
                    return status
                await asyncio.sleep(0.5)
            raise AssertionError(f"{id} did not complete")

        async def decide(id: str, actor: str):
            rows = await get(f"/api/workflows/{id}/approvals")
            row = next(r for r in rows if r["canDecide"] and r["approver"] == actor)
            return await post(
                f"/api/approvals/{row['instanceId']}/decision",
                {
                    "actingAs": actor,
                    "outcome": "Approve",
                    "version": 3,
                    "comment": f"Budget demonstration: {actor} approved.",
                },
            )

        def submission(id: str, step="B07"):
            return {
                "instanceId": id,
                "clientId": prefix,
                "workflowCode": step,
                "planId": "budget-plan",
                "version": 3,
                "requester": "requester",
                "budgetAmount": "7500",
                "correlationId": id,
            }

        def policy(version: int, threshold: str):
            return {
                "id": prefix,
                "clientId": prefix,
                "baseVersion": version - 1,
                "defaultStages": [
                    {"name": "Internal", "roles": ["Internal"], "timeoutMinutes": 30},
                    {
                        "name": "Finance",
                        "roles": ["Finance"],
                        "timeoutMinutes": 30,
                        "budgetGreaterThan": threshold,
                    },
                ],
                "overrides": [
                    {
                        "workflowCode": "B08",
                        "stages": [
                            {
                                "name": "Parallel",
                                "roles": ["Internal", "Legal"],
                                "timeoutMinutes": 30,
                            }
                        ],
                    }
                ],
            }

        environment = await get("/api/environment")
        if (
            environment.get("environment") != "AZURE"
            if args.engine == "durable"
            else environment.get("deployment") != "AKS"
        ):
            raise AssertionError("This demonstration must use the Azure/AKS console.")
        save("environment", environment)
        await post(
            "/api/demo-identities",
            {
                "clientId": prefix,
                "assignments": {"Internal": "internal", "Finance": "finance", "Legal": "legal"},
            },
        )
        for bad in [None, "-0.01", "0.001", "NaN"]:
            payload = submission(f"{prefix}-invalid")
            if bad is None:
                del payload["budgetAmount"]
            else:
                payload["budgetAmount"] = bad
            response = await http.post("/api/workflows", json=payload)
            assert response.status_code == 400, response.text

        a, b = f"{prefix}-a", f"{prefix}-b"
        saved = await post("/api/policies", policy(1, "5000"))
        assert saved["version"] == 1
        save("policy-v1", saved)
        original = await post("/api/workflows", submission(a))
        save("run-a-start", original)
        assert original["policyVersion"] == 1
        assert [r["included"] for r in original["routing"]["stages"]] == [True, True]
        assert Decimal(str(original["routing"]["budgetAmount"])) == Decimal("7500")
        await waiting(a, {"internal"})
        before = cluster_snapshot(args.engine, parsed.port)
        save("before-config-edit", before)

        saved = await post("/api/policies", policy(2, "10000"))
        assert saved["version"] == 2, "A threshold-only edit must create v2."
        save("policy-v2", saved)
        unchanged = await post("/api/policies", {**policy(2, "10000"), "baseVersion": 2})
        assert unchanged["version"] == 2
        updated = await post("/api/workflows", submission(b))
        save("run-b-start", updated)
        assert updated["policyVersion"] == 2
        assert [r["included"] for r in updated["routing"]["stages"]] == [True, False]
        assert Decimal(str(updated["routing"]["budgetAmount"])) == Decimal("7500")
        await waiting(b, {"internal"})
        await decide(b, "internal")
        result_b = await complete(b)
        assert len(result_b["result"]["steps"]) == 1
        after = cluster_snapshot(args.engine, parsed.port)
        save("after-config-edit", after)
        assert before == after, "Configuration demonstration must not restart or redeploy apps."
        print(
            "PASS: v1 includes Finance, threshold-only v2 skips it; no application restart/deploy.",
            flush=True,
        )

        await decide(a, "internal")
        save("run-a-still-requires-finance", await waiting(a, {"finance"}))
        pinned = await get(f"/api/workflows/{a}")
        assert pinned["snapshot"]["version"] == 1
        assert pinned["routing"]["stages"][1]["included"]
        save("run-a-pinned-after-v2", pinned)
        smoke = "Not run"
        if args.restart:
            namespace = "durable-spike" if args.engine == "durable" else "temporal-spike"
            deployment = "approval-worker" if args.engine == "durable" else "temporal-worker"
            pod = next(p["name"] for p in after["pods"] if p["name"].startswith(deployment + "-"))
            context = os.environ["AKS_CLUSTER_NAME"]
            command(
                "kubectl",
                "--context",
                context,
                "-n",
                namespace,
                "delete",
                "pod",
                pod,
                "--wait=true",
                "--timeout=90s",
            )
            command(
                "kubectl",
                "--context",
                context,
                "-n",
                namespace,
                "rollout",
                "status",
                "deployment/" + deployment,
                "--timeout=180s",
            )
            await waiting(a, {"finance"})
            replacement = cluster_snapshot(args.engine, parsed.port)
            save("after-worker-replacement", replacement)
            assert after["deployments"] == replacement["deployments"]
            smoke = "Pass"
        await decide(a, "finance")
        result_a = await complete(a)
        assert [s["stage"] for s in result_a["result"]["steps"]] == ["Internal", "Finance"]
        print(
            "PASS: original v1 run still required Finance and completed after worker replacement.",
            flush=True,
        )

        c = f"{prefix}-override"
        selected = await post("/api/workflows", submission(c, "B08"))
        assert selected["source"] == "client override"
        await waiting(c, {"internal", "legal"})
        await decide(c, "legal")
        await waiting(c, {"internal"})
        await decide(c, "internal")
        await complete(c)
        save("registry", await get("/api/policies"))
        print(
            "PASS: same ApprovalChain executes single, sequential and parallel/override routes.",
            flush=True,
        )

        trace = "Not configured/verified; no fresh trace claim."
        if args.trace:
            if args.engine != "durable":
                raise ValueError("Temporal currently has no trace exporter; do not claim a trace.")
            app_id = os.environ["APPINSIGHTS_APP_ID"]
            query = (
                "let spans = union requests, dependencies | where timestamp > ago(2h); "
                "let roots = spans | where name == 'approval.start' and "
                f"tostring(customDimensions['approval.correlationId']) == '{a}' | distinct operation_Id; "
                "spans | where operation_Id in (roots) | summarize n=count() by name, cloud_RoleName"
            )
            for _ in range(30):
                data = json.loads(
                    command(
                        "az",
                        "rest",
                        "--method",
                        "post",
                        "--resource",
                        "https://api.applicationinsights.io",
                        "--url",
                        f"https://api.applicationinsights.io/v1/apps/{app_id}/query",
                        "--body",
                        json.dumps({"query": query}),
                        "-o",
                        "json",
                    )
                )
                save("application-insights", data)
                names = {row[0] for row in data["tables"][0]["rows"]}
                if {"approval.start", "approval.submitted", "approval.decision.recorded"} <= names:
                    trace = "Pass: correlated start, submission and decision spans."
                    break
                await asyncio.sleep(20)
            else:
                raise AssertionError(
                    "Fresh Application Insights evidence did not arrive within 10 minutes."
                )
        summary = {
            "completedAt": datetime.now(UTC).isoformat(),
            "engine": args.engine,
            "base": args.base,
            "prefix": prefix,
            "budgetAmount": "7500",
            "currency": "USD",
            "comparison": "strict >; equality skips",
            "runA": a,
            "runB": b,
            "configurationOnlyIsolation": "Pass",
            "thresholdOnlyVersionChange": "Pass",
            "singleSequentialParallelOverride": "Pass",
            "workerReplacement": smoke,
            "freshTrace": trace,
            "revision": {k: v for k, v in revision.items() if k != "sourceFiles"},
        }
        save("summary", summary)
        print(json.dumps(summary, indent=2), flush=True)
        print(f"Evidence: {evidence}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", choices=["durable", "temporal"], required=True)
    parser.add_argument("--base", required=True)
    parser.add_argument("--restart", action="store_true")
    parser.add_argument("--trace", action="store_true")
    asyncio.run(run(parser.parse_args()))
