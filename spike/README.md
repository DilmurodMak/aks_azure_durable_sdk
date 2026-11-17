---
title: Approval spike demo
description: Use HTTP requests and dashboards to test the local emulator or the worker deployed to AKS.
ms.date: 2026-11-17
ms.topic: how-to
---

## Before the demo

Run all commands from the repository root. Install local Docker Desktop, the .NET SDK in
[global.json](../global.json), and `jq`; Azure also needs Azure CLI.
Complete the [build steps](../src/README.md#build-and-test).
If these services are already running, reuse them instead of starting duplicates.

What the spike proved, what it did not, and which requirements it covers are in
[FINDINGS.md](./FINDINGS.md).

The same worker runs both demos: `ApprovalChain` coordinates saved policies and
`ApprovalStep` handles each reviewer. `approval-local` and
`default` are task hubs, not workflow versions; `version: 3` is the plan version.

## Demo 1: everything local

```text
HTTP file -> Local Console UI :5080 -> emulator :8080 <-> local worker
                                         |
                              local dashboard :8082
```

The emulator and its dashboard run in local Docker Desktop.

Terminal 1:

```bash
docker compose up -d emulator
source scripts/use-env.sh local
dotnet run --project src/Approval.Worker -c Release --no-build
```

Terminal 2:

```bash
source scripts/use-env.sh local
dotnet run --project src/Approval.Console -c Release --no-build
```

Open [approvals.http](../approvals.http) in an HTTP client such as VS Code REST
Client. Keep `@base = http://127.0.0.1:5080` and choose a fresh `@instance`.

1. Send **Environment**: confirm `EMULATOR` and task hub `approval-local`.
2. Save the example policy once, then send **Start** through `/api/workflows`.
   Query the waiting reviewer steps and use the returned child ID for decisions.
   The parent is `Running`; the child has structured status `state: Waiting`.
3. Open the [local dashboard](http://localhost:8082/subscriptions/local/schedulers/emulator/taskhubs/approval-local/orchestrations).
   Find that same instance ID and inspect Timeline, History, and Flow.
4. Send the stale decision to the child, then its status: it must still be waiting.
5. Send **Approve** to the child, then query the parent workflow: expect
   `Approved`, version `3`, receipt `sunny-soda:v3:approved`.
   The child itself has no receipt. Watch its event and the parent's apply step in the dashboard.

For buttons instead of HTTP, use the [Local Console UI](http://localhost:5080).
The **Acting as** dropdown can demonstrate wrong-approver/self-approval refusals.
Run Reject on a separate fresh instance; do not approve and reject the same run.

Local Docker Desktop keeps the emulator/dashboard running after a terminal closes. Its
state is **in memory**: restarting/recreating it clears instances.
`docker compose stop emulator` stops it deliberately.

## Demo 2: test the cloud worker locally

First complete [Azure provisioning](../infra/README.md) and
[worker deployment](../src/README.md#deploy-the-worker-to-aks).
AKS and its worker must be running. Use an account with the configured task-hub
permissions, and keep your current public IP on the allowlists.

```text
HTTP file -> Local Console UI :5081 -> Azure Scheduler <-> AKS worker
                                           |
                               Azure dashboard in your browser
```

In a new terminal:

```bash
az login
export AZURE_SUBSCRIPTION_ID='<approved-subscription>'
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
# Optional: set DEPLOYMENT_NAME if it is not durable-spike-infra.
source scripts/use-env.sh azure
printf 'Scheduler: %s\nTask hub: %s\nDashboard: %s\n' \
  "$ENDPOINT" "$TASKHUB" "$DASHBOARD_URL"
dotnet run --project src/Approval.Console -c Release --no-build
```

The script reads `schedulerEndpoint` and other outputs from the existing Bicep
deployment and writes ignored `env/azure.env`. No endpoint or token is hard-coded.

1. In [approvals.http](../approvals.http), set `@base = http://127.0.0.1:5081`
   and use a new instance ID.
2. Send **Environment**: confirm `AZURE`, the actual Azure endpoint, and `default`.
3. Repeat the policy save, workflow start, child decision, and parent-result checks from Demo 1.
4. Open the [Local Console UI connected to Azure](http://localhost:5081), then
   **Open Scheduler dashboard**. Alternatively, open the printed
   `DASHBOARD_URL`. Sign in with your authorized Entra account.
5. Find the same instance and inspect its events, result, and connected worker.
   The workflow is processed by the AKS pod, not a local worker.

You are testing AKS **from your local machine**: the Local Console UI's SDK connects to
Azure Scheduler, which dispatches work to the AKS worker. The `.http` file
calls the console's HTTP endpoints; Scheduler communication uses the SDK.
No AKS ingress or `kubectl port-forward` is needed for this path.

The emulator dashboard at port 8082 only shows **local** runs. It is not a
proxy for Azure. Use the actual Azure dashboard link for cloud runs.
Do not start a local worker against the Azure hub while AKS is consuming it.
Switching shell settings does not change an already-running process.

## What to check

| Test                                                         | Expected business result                                                                                      |
| ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| Valid approval                                               | `Approved`, version 3, receipt `sunny-soda:v3:approved`                                                       |
| Explicit rejection                                           | `Rejected` with the comment; the other waiting reviewers are `Canceled`, later stages never start, no receipt |
| Return for changes                                           | `Returned` with the comment; ends the chain like a rejection                                                  |
| No decision before the deadline                              | `Timeout`, no receipt; ends the chain                                                                         |
| Reject or return without a comment                           | Refused with HTTP 400; nothing is sent to the workflow                                                        |
| Requester holds a required role, or one person in two stages | Start refused with HTTP 409; nothing is scheduled                                                             |
| Wrong actor, self-approval, stale version, or typo           | Refused; still waiting on the original timer                                                                  |
| Replace the waiting worker                                   | New pod; the same instance can complete                                                                       |
| Kill the worker during the delayed apply step                | Receipt attempt on both pods; evidence of possible duplicates                                                 |

`202 Accepted` means a decision event was sent. Runtime `Completed` alone
does not mean approved: check the returned business result.
For dashboard **Raise Event**, use name `Decision` and:

```json
{"approver":"finance","outcome":"Approve","version":3,"comment":"Looks good"}
```

`outcome` is `Approve`, `Reject` or `Return`. `Reject` and `Return` need a `comment`
of at most 1,000 characters; it is optional for `Approve`.

## Configure a chain once, then start workflows

In the Local Console UI, work through the numbered tabs left to right:

1. **Identities.** Pick a client (and optionally an account) and give each role a
   simulated user (for example `FinanceController` -> `finance`). **Load example roles**
   fills a starter set. Choose **Save identities**; the console then opens **Policies**
   automatically. This simulates User Administration; it creates no policy version.
   After a policy exists, the page lists any role that active policy still needs a user
   for, with a one-click **Add** chip.
2. **Policies.** One policy per client (or per account) holds a **default chain** that applies
   to every business step, plus optional **step overrides** that replace it for one step.
   Choose **New policy**, or **Use example** for a default of Lead then Finance + Legal and
   a Gate 2 (`B07`) override that adds Client sign-off.
   Build each chain from stage cards in the required order. **Add parallel reviewer** puts
   another role in the same stage. Each reviewer role is a dropdown of the roles from
   step 1, so a typo cannot create an unresolvable policy. **Add step override** picks a
   business step from the dropdown (see below); a step can have only one override.
   No person belongs in a policy.
3. Choose **Save & activate** for a new policy. The immutable version is immediately active.
   One version covers the whole set, so editing any chain creates the next version of the
   whole policy. To reuse an existing version, choose it in **Saved policy version**:
   selection automatically activates and loads it. No extra activation click.
4. **Start workflow.** The selected policy's client and account are filled in; choose the
   business step and supply the business plan, plan version, requester, and a fresh instance ID.
   An optional **Correlation ID** ties the dashboard tags, traces and logs together; one is
   generated when you leave it empty. The start is refused with an explicit message if the
   requester holds a required role or the same person would review in two stages.
   A line under the heading shows which chain will apply, for example "B08 will use the client
   default chain from sunny-approvals v2". The requester is a dropdown of demo users
   (default: a plan owner who is not an approver).
5. **Approvals.** There is nothing to pick: every running workflow is listed as a block,
   plus the most recent workflow so its finished steps stay visible. A block header shows
   the business step, which rule supplied the chain (for example `client override`), and the
   policy version. Each block shows every planned reviewer:
   ranks `1.1` and `1.2` are parallel in stage 1; `2.1` waits for all of stage 1.
   Future steps are greyed out with disabled decision controls. Finance and Legal
   may respond in either order; both must approve before the next stage unlocks.
   A waiting row has a comment box and three buttons: **Approve**, **Return for changes**
   and **Reject**. Returning or rejecting needs a comment. A rejection, return or timeout ends
   the plan: the other reviewers still waiting in that stage become `Canceled` and the later
   stages show `NotStarted`. Finished rows show who decided, when, and the comment.
   Each block uses that run's pinned snapshot, not an edited policy. The **Latest result**
   panel under the table shows the newest response: it updates after each decision and when
   you press **Refresh**, and it appears only on this tab.
6. Use **Pinned policy & result** in a block header to inspect the version used by that run.
   Selected saved policies are greyed out and read-only. **Edit** appears beside
   **New policy** only for a selected saved policy. Choose Edit to change
   chains or overrides, then **Save** or **Cancel**. An unchanged
   save creates no new version; real changes create the next immutable version.
   Existing workflows always keep their previous snapshot.

**Acting as** is a dropdown of role/user assignments. For the workflows shown,
the choices come from their pinned reviewers, not current editable simulator settings.
It is a simulation, not sign-in or role authorization.
**Test a stale decision** (collapsed under Acting as) sends a deliberately wrong plan version.

Approve, Return and Reject lock that row while the worker responds. Finished approvals stay
visible with history access but no decision controls, until their workflow is no longer
running or the most recent. A workflow whose steps cannot be loaded shows an inline error
without hiding the others.
A refused decision stays waiting with an explanation and enabled controls.
**View** only inspects status, so it leaves a waiting approval actionable.
Future steps have no child instance/history yet, so View is disabled until started.
If delivery is uncertain, decision controls stay locked until status is reconciled.

When a workflow starts, the chain is chosen by the narrower owner first: the account's policy,
then the client's, then `agency-default`. Inside one owner, a step override beats that
owner's default chain. So an account policy with a default chain wins over the client's override
for the same step; give the account policy only overrides (no default) if the account should
inherit the client's chain for every other step. If nothing supplies a chain for the step,
the start fails with an explicit error rather than a guessed chain. The chosen rule (for example
`client override`) is recorded in the pinned snapshot. Local and Azure-connected consoles
have separate JSON registry files under [approval_policies/](../approval_policies/); these survive
console restarts, independently of the in-memory emulator.

Simulator assignments are persisted separately under [demo_settings/](../demo_settings/).
Missing role assignments block a new start with an explicit error. Assignment
edits affect new starts, not a workflow's already-resolved reviewers.
In production, User Administration would resolve assignments and validated
sign-in would supply the acting identity; the demo does not implement that security.

The **Add step override** dropdown lists the 34 business workflows in the
analysis whose template includes Approval & sign-off, grouped by phase. For example,
**B07 - Tactical plan & Gate 2 approval** is in phase P3 and uses the K1 pattern.
The list is hard-coded in [BusinessStepCatalogue.cs](../src/Approval.Console/BusinessStepCatalogue.cs)
and served by `GET /api/business-steps`; saving an override for any other code is rejected.
Steps that are change/re-approval only (K4), queues, timers, or integrations are excluded
because this console does not implement those patterns. Approval stages are reviewer
checkpoints inside the chosen business step, not campaign phases.
The policy is keyed by business step, not by phase or capability, because the analysis reuses
one step under several capabilities (for example `E09` under five), and a step's chain should
follow it. One versioned set per client matches the analysis' client configuration registry
(`CP105`: promote and roll back as a unit, record the version in force).
Fixed-USD budget-threshold stage routing is now implemented and verified on AKS.
Generalized branching, materiality-based re-approval, a mandatory internal first approver, and
effective-dated promotion are not implemented yet and would extend a chain, not this model.
A production system would read the catalogue from the workflow registry instead of this list.

The one-time worker deployment enables this behavior. After that, saving and
activating configuration does not restart the worker or change its image.
Only built-in operations are configurable; adding a new business activity needs code.

The two orchestrators form one reusable approval/sign-off pattern:

```text
ApprovalChain (one business workflow)
  -> stage 1: ApprovalStep for Lead
  -> stage 2: ApprovalStep for Finance + ApprovalStep for Legal   (parallel)
  -> ApplyDecision once, if every step approved
  (a reject, return or timeout ends the chain; later stages never start)
```

`ApprovalChain` coordinates the policy; `ApprovalStep` is the reusable
reviewer wait and timer. A policy with only one reviewer uses the same engine.
In a real integration a notification would be sent when an approval becomes
actionable. This spike does not send notifications; the Local Console UI is the inbox.

The parent runs the stages in order and the reviewers of one stage in parallel. The first
reviewer step that ends `Rejected`, `Returned` or `Timeout` ends the plan: the parent raises a
`Cancel` event to the reviewers still waiting in that stage, which finish as `Canceled` and
cancel their timers, and it never starts the next stage. Reviewers who already finished keep
their own outcome. If two steps end the plan together, the first one processed decides the
status. The chain returns `Approved` only when every step approved, and only then calls
`ApplyDecision`. The chain output lists every step that started, with who decided, when and
why, plus `stagesNotStarted`. A finished child cancels its pending durable timer; timeout
timers fire normally. Cancellation is not a successful timer firing, and the dashboard can
retain the historical Timer Created node.

Each finished step runs the `RecordDecision` activity once: a structured log line (instance,
correlation ID, client, account, plan, stage, role, status, who, when, comment) and a span. It
is the place where a real audit-store write belongs; see [FINDINGS.md](./FINDINGS.md).

Progress is structured (`state`, `stage`, `stageNumber`, `stageCount`), not
filtered by a hard-coded display label. Example: `Waiting: review (2/3)`.
Parent and child tags share `client`, `account`, `workflow`, `plan`, `version`, `startedBy`,
`correlationId`, `policy`, `policyVersion`, and `stage`; children also have `parent`. The
display name is for dashboard readability, not workflow control.

## Traces in Application Insights

The console and the worker register the SDK's `Microsoft.DurableTask` activity source and
the spike's `Approval.Spike` source with OpenTelemetry. Both export to Application Insights
when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set; locally it is unset, so nothing is
exported. `source scripts/use-env.sh azure` reads the connection string and
`APPINSIGHTS_APP_ID` from the deployed resource into the ignored `env/azure.env`, and
`scripts/deploy-worker.sh` passes the string to the worker pod.

`approval.start` and `approval.decision.sent` come from the console, `approval.submitted` and
`approval.decision.recorded` from the worker; each has the tags `approval.client`,
`approval.account`, `approval.startedBy` and `approval.correlationId`. Spans can take
several minutes to appear. See [FINDINGS.md](./FINDINGS.md) for a query and the pitfalls.

## Automated demo checks

```bash
bash scripts/test-console.sh http://127.0.0.1:5080
bash scripts/test-console.sh http://127.0.0.1:5081
bash scripts/test-policy.sh http://127.0.0.1:5080
bash scripts/test-policy.sh http://127.0.0.1:5081

source scripts/use-env.sh azure
bash scripts/test-approval.sh sunny-soda-fresh-id
bash scripts/test-inflight.sh
bash scripts/test-step-one.sh
bash scripts/test-burst.sh 100 100
```

The AKS scripts need `kubectl`/`kubelogin` and the selected AKS context; they delete
only the demo worker pod. The in-flight test force-deletes it during a 30-second
activity. Results, IDs, logs, and history go to ignored `.build/evidence/`.
Pod Ready is not proof of Scheduler connection; measure actual workflow resume.
Run the scripts against a throwaway console started with `POLICY_STORE_PATH` and
`DEMO_IDENTITIES_PATH` pointing at a temporary folder, so your saved policies stay untouched.

The policy test keeps workflow A waiting on version 1, activates version 2,
and starts B without restarting any service. It verifies A's original ordering,
B's parallel reviews, a rejection that ends the chain and cancels the other reviewer,
the separation-of-duties refusal, timeout, a default chain applied to a second business
step, an override for `B07`, and no remaining waiting children.

`test-step-one.sh` is the step-one definition of done: a three-stage chain, parallel stage 1
approved out of order, a worker restart mid-wait, a stage 2 rejection with a comment, the
chain ending `Rejected` with stage 3 never started, and the tags on the instance data. When
`APPINSIGHTS_APP_ID` is set it also waits for the Application Insights trace.

`test-burst.sh CHAINS CLIENTS` starts that many three-approval chains with that many concurrent
script clients, approves every step, and records the total time, per-chain times and failures.

## Verified on 2026-11-15

These are historical results. The budget-aware build was verified on
2026-11-17 with 191 .NET tests, a fresh AKS configuration-only A/B demonstration,
worker replacement, safety acceptance checks and current Application Insights
evidence. The [updated findings](./FINDINGS.md#fresh-aks-budget-routing-verification)
record exact image digests, the tested source fingerprint and rerun instructions
for both Durable and Temporal.

Code under test: commit `af50e35`. Its source, scripts and infrastructure are exactly what
was built and deployed; only the documentation changed afterwards. What ran on AKS is
identified by the worker image
`acr7q4pxow7wtuks.azurecr.io/approval-worker@sha256:4a0300c5500bcb3d5da745fe45241386ecf749333b14087c34f20f8831572da7`
(ACR run `cha`), with zero container restarts. The Application Insights resource is
`appi-durable-spike-7q4pxow7wtuks`. Every run used throwaway policy and identity stores, and
your saved policies were not touched.

| Check                                                          | Where                  | Result                                                                                 |
| -------------------------------------------------------------- | ---------------------- | -------------------------------------------------------------------------------------- |
| 180 automated tests, format, build with no warnings            | Local                  | Pass                                                                                   |
| `test-policy.sh` and `test-console.sh`                         | Emulator, local worker | Pass (`policy-demo-s1-local`, `console-demo-s1-local`)                                 |
| `test-policy.sh`                                               | Azure Scheduler, AKS   | Pass at 17:31Z (`policy-demo-final-aks`)                                               |
| `test-console.sh`                                              | Azure Scheduler, AKS   | Pass (`console-demo-s1-aks`)                                                           |
| `test-approval.sh`: pod replacement, timeout, stale, receipt   | Azure Scheduler, AKS   | Pass; resume 3 s (`approval-s1-aks`)                                                   |
| `test-inflight.sh`: receipt redelivered on a new pod           | Azure Scheduler, AKS   | Pass (`console-demo-inflight-s1-aks`)                                                  |
| `test-step-one.sh`: the six-step definition of done            | Azure Scheduler, AKS   | Pass at 14:35Z (`step-one-aks2`)                                                       |
| `test-burst.sh 100 100`: 300 decisions, 400 instances          | Azure Scheduler, AKS   | 100 of 100 `Approved` in 15 s; p50 13 s, p95 14 s, 0 failures (`burst-20261115172259`) |
| Application Insights trace of one chain                        | Azure                  | `approval-console` and `approval-worker` spans with the business tags in one trace     |
| Azure dashboard page                                           | Browser                | Pending browser sign-in; not counted as passed                                         |

`step-one-aks1` is a kept failing run: the Application Insights check found nothing because of
the sampling problem described in [FINDINGS.md](./FINDINGS.md); the other five steps passed.
Earlier results (approval, rejection, timeout, refused decisions, local dashboard walkthrough)
were recorded on 2026-11-13 and 2026-11-14 for the previous chain behavior; the checks above
replace them.

The automated tests cover ranked parallel/blocked/completed projections, server-side
current-stage guards, a chain that ends on the first reject, return or timeout and cancels the
other waiting reviewers, a reviewer who finished first keeping their outcome, comment rules,
separation of duties, the correlation ID and tags, unchanged-save deduplication, automatic
activation, business-step catalogue validation, default-and-override resolution (narrower
owner first), and identity changes that neither version policies nor change running snapshots.
The contracts have no legacy shapes, so run only fresh instances against the current worker.

## Limits

**Acting as is a simulation, not authentication.** Keep both Console UI instances
local. `ApplyDecision` logs a stand-in receipt, not a duplicate-safe business
update. The decision record is not stored outside the scheduler. Governed policy
publishing/audit, Teams, notifications, delegation and sustained load are later; the
full list is in [FINDINGS.md](./FINDINGS.md).
Policy versions are immutable, but this local editor has no real administrative
authorization, approval-to-publish, effective-date scheduling, or shared cloud store.
The number of refused decisions is not capped yet; each adds history. Rate limits,
a refusal budget, and history rollover need qualification before production.
Remote dashboard UI verification still requires browser sign-in; successful SDK
tests alone are not dashboard proof. Data Contributor can terminate/purge and
must not be treated as a production approver role.

Back to [repository overview](../README.md) or [source code guide](../src/README.md).
