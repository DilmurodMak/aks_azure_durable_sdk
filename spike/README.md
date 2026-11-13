---
title: Approval spike demo
description: Use HTTP requests and dashboards to test the local emulator or the worker deployed to AKS.
ms.date: 2026-11-13
ms.topic: how-to
---

## Before the demo

Run all commands from the repository root. Install local Docker Desktop, the .NET SDK in
[global.json](../global.json), and `jq`; Azure also needs Azure CLI.
Complete the [build steps](../src/README.md#build-and-test).
If these services are already running, reuse them instead of starting duplicates.

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

| Test                                               | Expected business result                                      |
| -------------------------------------------------- | ------------------------------------------------------------- |
| Valid approval                                     | `Approved`, version 3, receipt `sunny-soda:v3:approved`       |
| Explicit rejection                                 | `Rejected`, no receipt                                        |
| No decision before the deadline                    | `Timeout`, no receipt                                         |
| Wrong actor, self-approval, stale version, or typo | Refused; still waiting on the original timer                  |
| Replace the waiting worker                         | New pod; the same instance can complete                       |
| Kill the worker during the delayed apply step      | Receipt attempt on both pods; evidence of possible duplicates |

`202 Accepted` means a decision event was sent. Runtime `Completed` alone
does not mean approved: check the returned business result.
For dashboard **Raise Event**, use name `Decision` and:

```json
{"approver":"finance","outcome":"Approve","version":3}
```

## Configure a chain once, then start workflows

In the Local Console UI:

1. Open **Policies**. Choose **New policy**, or **Use example** to fill a
   Lead, Finance + Legal, and Client sign-off example.
2. Set client/account and **Business step / workflow**. Add stage cards in the required order.
   **Add parallel reviewer** puts another role in the same stage.
3. Configure required roles only. No person or demo actor belongs in the policy.
4. Choose **Save & activate** for a new policy. The immutable version is immediately active.
   To reuse an existing version, choose it in **Saved policy version**:
   selection automatically activates and loads it. No extra activation click.
5. Open **Demo identities** to separately assign simulated users to those roles
   for the client/account. **Add roles from current policy** copies the role list;
   enter demo users and choose **Save demo identities**. This changes no policy version.
6. Open **Start workflow**. The selected policy's client/account/workflow fields
   are filled in; supply the plan version and a fresh instance ID.
7. Open **Approvals** and select the workflow. Every planned reviewer is shown:
   ranks `1.1` and `1.2` are parallel in group 1; `2.1` waits for all of group 1.
   Future steps are greyed out with disabled decision controls. Finance and Legal
   may respond in either order; both must finish before the next group unlocks.
   The overview uses this run's pinned snapshot, not an edited policy.
8. Use **View pinned policy & result** to inspect the version used by that run.
   Selected saved policies are greyed out and read-only. **Edit** appears beside
   **New policy** only for a selected saved policy. Choose Edit to change
   stages/reviewers, then **Save** or **Cancel**. An unchanged
   save creates no new version; real changes create the next immutable version.
   Existing workflows always keep their previous snapshot.

**Acting as** is a dropdown of role/user assignments. For a selected workflow,
the choices come from its pinned reviewers, not current editable simulator settings.
It is a simulation, not sign-in or role authorization.

Approve/Reject locks that row while the worker responds. In a selected workflow,
finished approvals stay visible with history access but no decision controls.
In **All waiting approvals** mode they disappear after completion.
A refused decision stays waiting with an explanation and enabled controls.
**View** only inspects status, so it leaves a waiting approval actionable.
Future steps have no child instance/history yet, so View is disabled until started.
If delivery is uncertain, decision controls stay locked until status is reconciled.

An account policy takes priority over its client policy, then the
`agency-default` client policy for that workflow. Missing configuration is an
explicit error, not a silently chosen chain. Local and Azure-connected consoles
have separate JSON registry files under [approval_policies/](../approval_policies/); these survive
console restarts, independently of the in-memory emulator.

Simulator assignments are persisted separately under [demo_settings/](../demo_settings/).
Missing role assignments block a new start with an explicit error. Assignment
edits affect new starts, not a workflow's already-resolved reviewers.
In production, User Administration would resolve assignments and validated
sign-in would supply the acting identity; the demo does not implement that security.

The **Business step / workflow** field is a policy lookup code, not a phase:
for example, Gate2 corresponds to business workflow B07 inside phase P3, using
the K1 Approval & sign-off pattern. Approval stages are reviewer checkpoints
inside that business step. The spike does not automatically load the HTML catalogue.

The one-time worker deployment enables this behavior. After that, saving and
activating configuration does not restart the worker or change its image.
Only built-in operations are configurable; adding a new business activity needs code.

The two orchestrators form one reusable approval/sign-off pattern:

```text
ApprovalChain (one business workflow)
  -> stage 1: ApprovalStep for Lead
  -> stage 2: ApprovalStep for Finance + ApprovalStep for Legal
  -> ApplyDecision once, if all approved
```

`ApprovalChain` coordinates the policy; `ApprovalStep` is the reusable
reviewer wait and timer. A policy with only one reviewer uses the same engine.
In a real integration a notification would be sent when an approval becomes
actionable. This spike does not send notifications; the Local Console UI is the inbox.

The parent waits for every required step, including after a rejection/timeout.
It returns `Approved` only if all approve, otherwise `Rejected` if any rejects,
or `Timeout` if any times out. A finished child cancels its pending durable timer;
timeout timers fire normally. Cancellation is not a successful timer firing, and
the dashboard can retain the historical Timer Created node.

Progress is structured (`state`, `stage`, `stageNumber`, `stageCount`), not
filtered by a hard-coded display label. Example: `Waiting: review (2/3)`.
Parent and child tags share `client`, `workflow`, `plan`, `version`, `policy`,
`policyVersion`, and `stage`; children also have `parent`. The display name is
for dashboard readability, not workflow control.

## Automated demo checks

```bash
bash scripts/test-console.sh http://127.0.0.1:5080
bash scripts/test-console.sh http://127.0.0.1:5081
bash scripts/test-policy.sh http://127.0.0.1:5080
bash scripts/test-policy.sh http://127.0.0.1:5081

source scripts/use-env.sh azure
bash scripts/test-approval.sh sunny-soda-fresh-id
bash scripts/test-inflight.sh
```

The last two need `kubectl`/`kubelogin` and the selected AKS context; they delete
only the demo worker pod. The in-flight test force-deletes it during a 30-second
activity. Results, IDs, logs, and history go to ignored `.build/evidence/`.
Pod Ready is not proof of Scheduler connection; measure actual workflow resume.

The policy test keeps workflow A waiting on version 1, activates version 2,
and starts B without restarting any service. It verifies A's original ordering,
B's parallel reviews, wait-all rejection, timeout, and no remaining waiting children.

## Verified on 2026-11-13

| Test                                | Recorded result                                                                                                          |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| Local Console UI / emulator         | Approval, rejection, timeout, and four refused-decision paths passed; evidence `console-demo-green-local-20261113185657` |
| Local Console UI / Azure AKS worker | Same HTTP cases passed; evidence `console-demo-green-azure-20261113185710`                                               |
| AKS idle-worker replacement         | Original instance completed after replacement; measured resume 3 seconds in the recorded run                             |
| AKS in-flight apply                 | Receipt attempt logged on both old and replacement pods; evidence `console-demo-inflight-20261113183747`                 |
| Local dashboard walkthrough         | Tags, connected-worker count, Timeline, History, and Flow inspected                                                      |
| Azure dashboard walkthrough         | Pending browser sign-in; not counted as passed                                                                           |
| Saved-policy local test             | Sequential and parallel reviews, edit isolation, wait-all rejection, and timeout passed at `20:47:31Z`                   |
| Saved-policy Azure/AKS test         | Same cases passed at `20:50:18Z`; the same pod UID and zero container restarts were verified across edits                |
| Local Console UI policy editor      | Save, activate, load, edit, and activate version 2 were exercised while version 1 remained waiting                       |

The first four entries are retained earlier single-approval results, not a production SLA.
The current policy-run test records its own summary under `.build/evidence/policy-demo-*`.
The integrated automated test suite passes 138 tests, including ranked
parallel/blocked/completed projections, server-side current-group guards, unchanged-save
deduplication, automatic activation, and simulator identity changes without policy
version changes or mutation of running snapshots. Roles-only policy starts were
also verified against both existing local and AKS workers.

## Limits

**Acting as is a simulation, not authentication.** Keep both Console UI instances
local. `ApplyDecision` logs a stand-in receipt, not a duplicate-safe business
update. Governed policy publishing/audit, Teams, trace backends, and load are later.
Policy versions are immutable, but this local editor has no real administrative
authorization, approval-to-publish, effective-date scheduling, or shared cloud store.
The number of refused decisions is not capped yet; each adds history. Rate limits,
a refusal budget, and history rollover need qualification before production.
Remote dashboard UI verification still requires browser sign-in; successful SDK
tests alone are not dashboard proof. Data Contributor can terminate/purge and
must not be treated as a production approver role.

Back to [repository overview](../README.md) or [source code guide](../src/README.md).
