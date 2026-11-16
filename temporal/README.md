---
title: Self-hosted Temporal approval spike
description: Run the Python approval chain with a real Temporal Server, PostgreSQL, browser console and traces on Docker Desktop.
ms.date: 2026-11-16
ms.topic: how-to
---

## What is running

This is the approval-parity implementation, not a workflow designer or a
production Temporal deployment. It does not change the Durable worker, AKS or
the existing policy and identity stores.

| Component        | Local address or purpose                         |
| ---------------- | ------------------------------------------------ |
| Approval console | <http://127.0.0.1:5090>                          |
| Temporal Web UI  | <http://127.0.0.1:8088>                          |
| Jaeger traces    | <http://127.0.0.1:16686>                         |
| Temporal gRPC    | `127.0.0.1:7233`                                 |
| PostgreSQL       | Private Compose network; named persistent volume |
| Python worker    | One container, 0.5 CPU and 512 MiB limit         |

Temporal Server 1.32.2 runs its services in one container against PostgreSQL
16.11, with separate execution and visibility databases. This is not
`temporal server start-dev`. The application uses Python 3.13 and Temporal SDK
1.36.1. The browser console, worker, engine, database, Temporal UI and Jaeger
are separate services in the `temporal-approval-spike` Compose project.

## Start or stop

Prerequisites: Docker Desktop, `uv`, a supported Python interpreter (the tested
interpreter was Python 3.13), Bash and `curl`.

From the repository root:

```bash
bash temporal/scripts/start.sh
```

The script resolves the locked environment, generates a local database password
in ignored `.env`, starts PostgreSQL and the Temporal Server, initializes the
namespace and typed Search Attributes, builds the application and verifies the
console health endpoint. Subsequent starts preserve database and policy data.

The image build stages hash-verified Linux dependencies with `uv` on the host,
then builds without downloading Python dependencies inside Docker. Direct
dependency downloads inside Docker failed TLS in the tested environment;
certificate verification was not disabled. The staged dependencies and build
logs are disposable and ignored.

To stop only this stack without deleting data:

```bash
cd temporal
docker compose stop
```

`docker compose down` also retains the named PostgreSQL volume and local policy
files. Do not remove the volume unless intentionally discarding this spike's
workflow histories. Jaeger uses memory storage and loses traces when restarted.

## Use the approval console

1. Open Identities and assign demo users to roles for a client/account.
2. Save identities, then configure a roles-only policy in Policies. The example
   uses parallel Finance and Legal reviews followed by Lead.
3. Save and activate. An unchanged save returns the existing version; a real
   edit creates the next immutable version. Browsing a saved policy is read-only;
   Edit enables changes, and Activate selected switches future starts.
4. Start a business step with a fresh instance ID. The chain and resolved
   people are pinned in the input. Requester self-review and the same reviewer
   in different stages are refused before scheduling.
5. In Approvals, search and refresh. Reviewers in the active stage can act in
   either order. Later stages remain blocked.
6. Approve, Reject or Return for changes. Reject and Return require a comment.
   The first negative outcome or timeout ends the chain, cancels remaining
   waiting reviewers and prevents later stages from starting.

Policy configuration and identity assignments currently use JSON form editors;
there is no business-friendly visual designer, copy-to-customer, draft or
test-before-publish lifecycle. Those were explicitly excluded from this first
implementation.

Policies and identities live in ignored [`.data/`](./.data/) files, not in the
Durable stores. Account, then client, then agency-default precedence matches the
existing spike, including the account-default-versus-client-override trade-off.

## Workflow topology and the dashboard

The current parity implementation deliberately matches Durable:

```text
ApprovalChain (one business approval request)
  ApprovalStep: Finance ----+
  ApprovalStep: Legal ------+ parallel stage 1
  ApprovalStep: Lead          sequential stage 2
```

These are real child executions. Temporal's overall execution list shows both
parents and children; it does not mean they are unrelated top-level business
workflows. Filter the Temporal UI with:

```sql
WorkflowType = 'ApprovalChain'
```

The custom console already lists only approval-chain executions on its own
application task queue, then presents reviewer steps within each chain.

Child-per-reviewer is valid, but not required for parallel approvals, independent
deadlines or reusability. Both Temporal and Durable can hold reviewer state,
durable timers and decision events inside one chain execution. A child makes
more sense when the subprocess needs its own lifecycle, complex activity
history, routing or independent operational controls.

For these simple approval waits, a single execution with internal reviewer state
is a reasonable simplification to evaluate in both implementations together.
The current parent/child design has not been silently refactored, and the
100-chain measurements apply only to this design.

## Search, history, logs and traces

The API uses Temporal Visibility filters, not an all-inputs scan. Its allowlisted
filters are client, account, business step, policy version and business state.
Results are paginated with opaque cursors.

Typed Search Attributes are `ClientId`, `AccountId`, `BusinessStep`, `PolicyId`,
`PolicyVersion`, `ApprovalState` and `CorrelationId`. Workflow code updates
business state as it progresses. Visibility can lag; the workflow Query and
result are authoritative before a decision.

`Completed` is an engine status. A completed chain can have business result
Rejected, Returned or Timeout; use `ApprovalState` to search those outcomes.
Search Attributes contain opaque identifiers, never comments.

```bash
cd temporal
docker compose logs worker
docker compose logs server
docker compose logs postgres
```

Worker business logs record actor, decision, timestamp, comment and correlation
ID. The recording activity is not an immutable external audit store. Activities
can run more than once, and `ApplyDecision` is still a non-idempotent stand-in.
Activity retries are bounded at three attempts; a delayed apply heartbeats.

Jaeger receives SDK interceptor and business spans from the console and worker.
Search service `temporal-approval-worker` with tag `approval.correlationId`.
This uses the documented tracing interceptor, not the experimental
duration-preserving plugin. Workflow-side interceptor span durations should not
be treated as engine latency measurements.

## Verify

From [this folder](./):

```bash
uv sync --frozen
uv run ruff check approval tests scripts
uv run ruff format --check approval tests scripts
TEMPORAL_TEST=1 uv run pytest -q
uv run python scripts/acceptance.py --restart --burst 100
```

The integration tests use unique task queues and temporary file stores, so they
do not modify user-created policies. The acceptance script creates its own
client scope and keeps evidence under ignored `.build/evidence/`.
Without `TEMPORAL_TEST=1`, only unit tests run; integration tests are skipped.

Recorded on 2026-11-16:

| Check                                                   | Result                                                                                           |
| ------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| Unit, API and real-server workflow tests                | 29 passed                                                                                        |
| Completed history replay                                | Approved, Rejected and Returned parent histories replayed                                        |
| Policy edit during a wait                               | Original keeps v1; new start uses v2; unchanged save does not version                            |
| Worker and Temporal Server container restarts mid-wait  | Same approval completed afterward                                                                |
| Parallel out-of-order approvals, then stage-2 rejection | Rejected with comment; stage 3 never created                                                     |
| Per-step override and Return                            | Passed                                                                                           |
| Indexed combined search and pagination                  | Passed                                                                                           |
| Browser decision flow                                   | Missing-comment warning, rejection record, sibling Canceled and future stage NotStarted verified |
| Trace correlation                                       | One trace includes console and worker services and business correlation tags                     |
| Local 100-chain burst                                   | 100 Approved, 0 failures, 300 decisions, 400 logical executions                                  |
| Script-observed times                                   | 18.70 s total; p50 17.64 s, p95 18.37 s, max 18.69 s                                             |

Acceptance evidence: `.build/evidence/acceptance-20261116071206-218f18`.
The tested application image ID is
`sha256:8b082957bfc3e523ac8d6db14d7f3099c89559f4ddf9b3172f20e73c4c47865e`.
After the run, worker memory was 93.12 MiB, Temporal Server 268.9 MiB,
PostgreSQL 199.1 MiB and console 81.16 MiB. These are post-run observations,
not peak usage, and exclude other Docker/host overhead.

The local Python async HTTP harness polls every 0.5 seconds; the earlier
Durable AKS test used shell clients and one-second polling. Different
hardware, placement, backend resources and harnesses mean 18.70 s versus 15 s
is not an engine performance ranking.

## Limits and next qualifications

* All interfaces are loopback-only simulation tools, not authenticated production
  APIs. The local Temporal server has no production identity/authorization setup.
* Single PostgreSQL and Temporal Server containers are not HA. Container restart
  recovery is not a database backup/restore or regional disaster-recovery test.
* Jaeger is ephemeral; production telemetry retention and server/database metrics
  scraping/alerts remain to be configured.
* Business policy versioning is implemented. Worker Deployment Versioning,
  safe incompatible code upgrades and live policy migration are not tested.
* No notifications, delegation, threshold rules, draft publishing, real human
  authentication, external immutable audit store or duplicate-safe business write.
* Timeout validation retains the same 24-hour ceiling; days-long waits and
  sustained load are not qualified.
* Pylance tooling was unavailable during validation. Runtime integration,
  replay, Python syntax compilation and Ruff checks were used; no editor-based
  type-diagnostic pass is claimed.

See the [platform comparison](../DURABLE-SDK-VS-TEMPORAL.md) for the architecture,
capability boundaries and proposed enterprise qualification.
