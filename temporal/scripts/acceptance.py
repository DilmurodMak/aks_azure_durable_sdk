"""Exercise Docker or loopback-forwarded AKS through the public console API."""

import argparse
import asyncio
import json
import math
import os
import statistics
import time
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlparse
from uuid import uuid4

import httpx

ROOT = Path(__file__).resolve().parent.parent


async def command(*args: str) -> None:
    process = await asyncio.create_subprocess_exec(
        *args,
        cwd=ROOT,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    stdout, stderr = await asyncio.wait_for(process.communicate(), 300)
    if process.returncode:
        raise RuntimeError(f"Restart operation failed: {stderr.decode()}")
    print(stdout.decode().strip())


async def restart_component(target: str, component: str) -> None:
    if target == "docker":
        await command("docker", "compose", "restart", component)
        return
    expected = os.environ["AKS_CLUSTER_NAME"]
    process = await asyncio.create_subprocess_exec(
        "kubectl", "config", "current-context", stdout=asyncio.subprocess.PIPE
    )
    stdout, _ = await process.communicate()
    if process.returncode or stdout.decode().strip() != expected:
        raise RuntimeError("Current context does not match the approved AKS cluster.")
    prefix = ("kubectl", "--context", expected, "--namespace", "temporal-spike")
    await command(*prefix, "rollout", "restart", f"deployment/temporal-{component}")
    await command(
        *prefix, "rollout", "status", f"deployment/temporal-{component}", "--timeout=240s"
    )


async def run(burst_count: int, restart: bool, target: str, base: str) -> None:
    parsed = urlparse(base)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1":
        raise ValueError("Acceptance endpoint must use HTTP on 127.0.0.1.")
    prefix = f"acceptance-{datetime.now(UTC):%Y%m%d%H%M%S}-{uuid4().hex[:6]}"
    evidence = Path(".build/evidence") / prefix
    evidence.mkdir(parents=True)
    async with httpx.AsyncClient(
        base_url=base,
        timeout=60,
        limits=httpx.Limits(max_connections=150, max_keepalive_connections=100),
        headers={"X-Approval-Console": "spike"},
    ) as http:
        environment = await http.get("/api/environment")
        environment.raise_for_status()
        if (environment.json()["deployment"] == "AKS") != (target == "aks"):
            raise RuntimeError("Console deployment does not match the selected test target.")

        async def get(path: str):
            response = await http.get(path)
            response.raise_for_status()
            return response.json()

        async def post(path: str, payload: dict):
            response = await http.post(path, json=payload)
            response.raise_for_status()
            return response.json()

        async def waiting(id: str, count: int, stage_number: int | None = None):
            for _ in range(120):
                rows = await get(f"/api/workflows/{id}/approvals")
                active = [row for row in rows if row["canDecide"]]
                if len(active) == count and (
                    stage_number is None
                    or all(int(row["rank"].split(".")[0]) == stage_number for row in active)
                ):
                    return rows
                await asyncio.sleep(0.5)
            raise AssertionError(f"{id}: expected {count} waiting reviewers")

        async def complete(id: str, expected: str):
            for _ in range(120):
                result = await get(f"/api/workflows/{id}")
                if result["result"] is not None:
                    assert result["result"]["status"] == expected, result
                    (evidence / f"{id}.json").write_text(json.dumps(result, indent=2))
                    return result
                await asyncio.sleep(0.5)
            raise AssertionError(f"{id} did not finish")

        async def start(id: str, step="B07"):
            return await post(
                "/api/workflows",
                {
                    "instanceId": id,
                    "clientId": prefix,
                    "workflowCode": step,
                    "planId": "sunny-soda",
                    "version": 3,
                    "requester": "requester",
                    "budgetAmount": "7500",
                    "correlationId": id,
                },
            )

        async def decide(
            id: str, stage: int, reviewer: int, actor: str, outcome="Approve", comment=None
        ):
            return await post(
                f"/api/approvals/{id}-s{stage}-r{reviewer}/decision",
                {
                    "actingAs": actor,
                    "outcome": outcome,
                    "version": 3,
                    "comment": comment,
                },
            )

        async def save(version: int, stages: list, overrides=None):
            return await post(
                "/api/policies",
                {
                    "id": prefix,
                    "clientId": prefix,
                    "baseVersion": version - 1,
                    "defaultStages": stages,
                    "overrides": overrides or [],
                },
            )

        parallel = {"name": "review", "roles": ["Finance", "Legal"], "timeoutMinutes": 15}
        lead = {"name": "lead", "roles": ["Lead"], "timeoutMinutes": 15}
        owner = {"name": "final", "roles": ["Owner"], "timeoutMinutes": 15}
        await post(
            "/api/demo-identities",
            {
                "clientId": prefix,
                "assignments": {
                    "Finance": "finance",
                    "Legal": "legal",
                    "Lead": "lead",
                    "Owner": "owner",
                },
            },
        )
        await save(1, [parallel, lead, owner])
        a = f"{prefix}-original"
        await start(a)
        rows = await waiting(a, 2)
        assert rows[-1]["state"] == "Blocked"
        same = await save(2, [parallel, lead, owner])
        assert same["version"] == 1
        await save(2, [owner])
        b = f"{prefix}-edited"
        await start(b)
        assert (await get(f"/api/workflows/{a}"))["snapshot"]["version"] == 1
        assert (await get(f"/api/workflows/{b}"))["snapshot"]["version"] == 2
        await waiting(b, 1)
        await decide(b, 1, 1, "owner")
        await complete(b, "Approved")
        print("PASS policy edit isolation; unchanged saves do not version")

        await decide(a, 1, 2, "legal")
        rows = await waiting(a, 1)
        assert next(row for row in rows if row["canDecide"])["approver"] == "finance"
        if restart:
            await restart_component(target, "worker")
            await waiting(a, 1)
            await restart_component(target, "server")
            for _ in range(90):
                response = await http.get("/api/health")
                if response.status_code == 200 and response.json()["ready"]:
                    break
                await asyncio.sleep(1)
            else:
                raise AssertionError("Server did not become healthy after restart")
            await waiting(a, 1)
            print(f"PASS {target} worker and SQL-backed server restart mid-wait")
        await decide(a, 1, 1, "finance")
        await waiting(a, 1, 2)
        await decide(a, 2, 1, "lead", "Reject", "Budget requires changes")
        result = await complete(a, "Rejected")
        assert result["result"]["stagesNotStarted"] == ["final"]
        rows = await waiting(a, 0)
        assert rows[-1]["state"] == "NotStarted" and not rows[-1]["canView"]
        print("PASS parallel approvals out of order; stage 2 rejects; stage 3 never starts")

        await save(3, [parallel, lead], [{"workflowCode": "B08", "stages": [owner]}])
        override = f"{prefix}-override"
        started = await start(override, "B08")
        assert started["source"] == "client override"
        await waiting(override, 1)
        await decide(override, 1, 1, "owner", "Return", "Review fees")
        await complete(override, "Returned")
        print("PASS per-step override and Return with comment")

        query = {
            "client_id": prefix,
            "workflow_code": "B07",
            "policy_version": 1,
            "state": "Rejected",
        }
        for _ in range(60):
            response = await http.get("/api/workflows", params=query)
            response.raise_for_status()
            found = response.json()
            if any(row["instanceId"] == a for row in found["items"]):
                break
            await asyncio.sleep(0.5)
        else:
            raise AssertionError("Indexed filter did not find the rejected v1 run")
        (evidence / "search.json").write_text(json.dumps(found, indent=2))
        print("PASS indexed client + step + policy version + business outcome search")

        burst = None
        if burst_count:

            async def drive(index):
                id = f"{prefix}-burst-{index}"
                started = time.perf_counter()
                await start(id)
                await waiting(id, 2)
                await decide(id, 1, 2, "legal")
                await decide(id, 1, 1, "finance")
                await waiting(id, 1, 2)
                await decide(id, 2, 1, "lead")
                await complete(id, "Approved")
                return time.perf_counter() - started

            started = time.perf_counter()
            times = sorted(await asyncio.gather(*(drive(i) for i in range(burst_count))))
            burst = {
                "chains": burst_count,
                "approvals": burst_count * 3,
                "instances": burst_count * 4,
                "approved": burst_count,
                "failures": 0,
                "wallSeconds": round(time.perf_counter() - started, 2),
                "p50Seconds": round(statistics.median(times), 2),
                "p95Seconds": round(times[math.ceil(len(times) * 0.95) - 1], 2),
                "maxSeconds": round(max(times), 2),
                "note": f"{target}, async HTTP harness, 0.5s polling; not directly comparable with the Durable curl benchmark.",
            }
            print(json.dumps(burst, indent=2))
        summary = {
            "completedAt": datetime.now(UTC).isoformat(),
            "prefix": prefix,
            "engine": "Temporal 1.32.2",
            "deployment": environment.json()["deployment"],
            "target": target,
            "base": base,
            "policyIsolation": "Pass",
            "workerAndServerRestart": "Pass" if restart else "Not run",
            "rejectionSkipsLaterStages": "Pass",
            "commentsAndReturn": "Pass",
            "indexedSearch": "Pass",
            "burst": burst,
        }
        (evidence / "summary.json").write_text(json.dumps(summary, indent=2))
        print(f"Evidence: {evidence}")
        print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--burst", type=int, default=0, choices=range(0, 101))
    parser.add_argument("--restart", action="store_true")
    parser.add_argument("--target", choices=["docker", "aks"], default="docker")
    parser.add_argument("--base", default="http://127.0.0.1:5090")
    args = parser.parse_args()
    asyncio.run(run(args.burst, args.restart, args.target, args.base))
