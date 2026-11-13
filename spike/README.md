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

The same `PlanApproval` workflow runs in both demos. `approval-local` and
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
2. Send **Start**, then **Business status and durable history** until the
   instance is `Running` with `Waiting for Finance`.
3. Open the [local dashboard](http://localhost:8082/subscriptions/local/schedulers/emulator/taskhubs/approval-local/orchestrations).
   Find that same instance ID and inspect Timeline, History, and Flow.
4. Send **Refused stale event**, then status: it must still be waiting.
5. Send **Approve**, then status again: expect `Approved`, version `3`,
   receipt `sunny-soda:v3:approved`. Watch the event and apply step in the dashboard.

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
3. Repeat Start, status, stale refusal, valid approval, and status from Demo 1.
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

## Automated demo checks

```bash
bash scripts/test-console.sh http://127.0.0.1:5080
bash scripts/test-console.sh http://127.0.0.1:5081

source scripts/use-env.sh azure
bash scripts/test-approval.sh sunny-soda-fresh-id
bash scripts/test-inflight.sh
```

The last two need `kubectl`/`kubelogin` and the selected AKS context; they delete
only the demo worker pod. The in-flight test force-deletes it during a 30-second
activity. Results, IDs, logs, and history go to ignored `.build/evidence/`.
Pod Ready is not proof of Scheduler connection; measure actual workflow resume.

## Limits

**Acting as is a simulation, not authentication.** Keep both Console UI instances
local. `ApplyDecision` logs a stand-in receipt, not a duplicate-safe business
update. Policy chains, Teams, audit storage, trace backends, and load are later.
Remote dashboard UI verification still requires browser sign-in; successful SDK
tests alone are not dashboard proof. Data Contributor can terminate/purge and
must not be treated as a production approver role.

Back to [repository overview](../README.md) or [source code guide](../src/README.md).
