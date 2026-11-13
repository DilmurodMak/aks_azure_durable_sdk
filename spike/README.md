---
title: Sunny Soda approval spike
description: Prove that one approval survives an AKS worker replacement, then test timeout and a stale plan version.
ms.date: 2026-11-13
ms.topic: how-to
---

## What we are proving

Run a standalone .NET 10 Durable Task SDK worker on AKS with Azure Durable
Task Scheduler. No Functions runtime, Teams, business database, or real
Media Plan update is involved.

```text
Laptop client -> PlanApproval -> RecordSubmission -> Waiting for Finance
                                                   |
                                            replace worker pod
                                                   |
                          Decision -> validate -> ApplyDecision -> Approved
                              |
                 no response: Timeout / version 2: RejectedStaleVersion
```

Only the exact outcome `Approve`, from the assigned approver and for the
requested version, can approve. Self-approval is rejected. Approver strings
are test inputs, not verified end-user identities; Scheduler authenticates
the operator, not the person named in the payload.

`ApplyDecision` logs and returns `sunny-soda:v3:approved`. This is a stand-in,
not a database receipt or an exactly-once business update. Activities can retry.

## Build and configure

The solution contains [worker](../src/Approval.Worker/),
[client](../src/Approval.Client/), and [shared types](../src/Approval.Contracts/),
plus [unit tests](../tests/Approval.Tests/).
Install the SDK pinned in [global.json](../global.json), Docker, Azure CLI,
compatible `kubectl`, `kubelogin`, and `jq`.

```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore
```

If your network blocks NuGet, add
`--source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json`
to restore. Lock files remain required.

Set `ENDPOINT`, `TASKHUB`, and `SCHEDULER_AUTH` explicitly. Missing settings
fail; the application never falls back to the emulator.

| Where           | ENDPOINT                 | SCHEDULER_AUTH     |
| --------------- | ------------------------ | ------------------ |
| Local emulator  | `http://localhost:8080`  | `None`             |
| Laptop to Azure | Bicep scheduler endpoint | `AzureCli`         |
| AKS worker      | Bicep scheduler endpoint | `WorkloadIdentity` |

`None` is allowed only on loopback HTTP. Azure requires HTTPS. On AKS,
the admission webhook injects the tenant and federated token file.
Plain `ManagedIdentity` uses a different credential path and is not the
workload-federated setup deployed by this Bicep.

## Validate in this order

1. Start the official emulator, run the worker locally, and exercise the CLI:

   ```bash
   docker run --rm --name approval-emulator \
     -p 127.0.0.1:8080:8080 -p 127.0.0.1:8082:8082 \
     mcr.microsoft.com/dts/dts-emulator:latest
   ```

   In another terminal, set the emulator settings above, then run:

   ```bash
   dotnet run --project src/Approval.Worker --configuration Release --no-build
   ```

   Use a third terminal with the same settings for the client commands below.
   Stop the local worker before testing Azure or AKS.

2. Set Azure settings and run the worker on your laptop against Scheduler.
   Use the operator identity authorized by the [infrastructure](../infra/README.md).
   This separates code/Scheduler issues from AKS image/federation issues.
3. Stop the laptop worker, build and push the Linux image, then deploy one pod.
4. Run the restart approval, timeout, and stale-version tests.

For Azure, read the existing deployment outputs; do not hard-code credentials:

```bash
export AZURE_SUBSCRIPTION_ID='<approved-subscription>'
export DEPLOYMENT_NAME='durable-spike-infra'
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
az login
configuration="$(az deployment sub show --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.configuration.value --output json)"
export ENDPOINT="$(jq -r .schedulerEndpoint <<< "$configuration")"
export TASKHUB="$(jq -r .taskHubName <<< "$configuration")"
export AZURE_CLIENT_ID="$(jq -r .workloadIdentityClientId <<< "$configuration")"
export AKS_CLUSTER_NAME="$(jq -r .clusterName <<< "$configuration")"
export REGISTRY_NAME="$(jq -r .registryName <<< "$configuration")"
export REGISTRY_LOGIN_SERVER="$(jq -r .registryLoginServer <<< "$configuration")"
export SCHEDULER_AUTH=AzureCli
```

Keep the laptop's actual public egress IPv4 on the AKS/Scheduler allowlists.
Changing VPNs can change it. Allow time for RBAC propagation.
If k9s shows `Context: n/a` or DNS errors, check AKS is running first.

## Deploy the worker

```bash
export IMAGE_TAG="sunny-soda-$(date -u +%Y%m%d%H%M%S)"
docker buildx build --platform linux/amd64 --load \
  --tag "$REGISTRY_LOGIN_SERVER/approval-worker:$IMAGE_TAG" .
az acr login --name "$REGISTRY_NAME"
docker push "$REGISTRY_LOGIN_SERVER/approval-worker:$IMAGE_TAG"
digest="$(az acr repository show --name "$REGISTRY_NAME" \
  --image "approval-worker:$IMAGE_TAG" --query digest --output tsv)"
export WORKER_IMAGE="$REGISTRY_LOGIN_SERVER/approval-worker@$digest"
bash scripts/deploy-worker.sh
```

The [Dockerfile](../Dockerfile) runs as a non-root user.
The [manifest](../deploy/worker.yaml) uses one fixed replica, resource limits,
and 60 seconds for graceful termination. Its namespace/service account match
`system:serviceaccount:durable-spike:durable-worker`.
The deployment script validates inputs and the Kubernetes context.

The kubelet's `AcrPull` does not permit image publishing. Verify the build/push
identity has the permissions needed for the chosen path. `az acr build` and
local `docker push` have different permission requirements; do not add roles
unless the approved build identity needs them.

## Run the three acceptance cases

```bash
dotnet src/Approval.Client/bin/Release/net10.0/Approval.Client.dll start \
  --instance sunny-soda-v3-001 --plan sunny-soda --version 3 \
  --requester requester --approver finance --timeout-minutes 15
dotnet src/Approval.Client/bin/Release/net10.0/Approval.Client.dll status \
  --instance sunny-soda-v3-001
```

Observe `Running` and `Waiting for Finance`, delete only the worker pod, and
wait for a Ready replacement. Then send:

```bash
dotnet src/Approval.Client/bin/Release/net10.0/Approval.Client.dll decide \
  --instance sunny-soda-v3-001 --approver finance --version 3 --outcome Approve
dotnet src/Approval.Client/bin/Release/net10.0/Approval.Client.dll status \
  --instance sunny-soda-v3-001 --history
```

For a complete automated run, use a fresh prefix instead of manually starting
the same instance first:

```bash
bash scripts/test-approval.sh sunny-soda-v3
```

This deletes only the selected approval-worker pod, waits for its replacement,
then runs a two-minute no-response case and a version-2 decision.
It refuses to overwrite an existing instance or evidence directory.

| Check                                           | Pass condition                                      |
| ----------------------------------------------- | --------------------------------------------------- |
| Image, worker identity, and Scheduler connection | Worker processes work without 401/403 errors         |
| Laptop identity and connectivity                | Client starts and queries the instance              |
| Persisted wait                                  | Runtime `Running`, status `Waiting for Finance`      |
| Replacement recovery                            | New pod name, original instance completes           |
| Approval                                        | `Approved`, version 3, `sunny-soda:v3:approved`        |
| History                                         | Start, submission, timer, decision, apply, completion |
| No response                                     | `Timeout`, no receipt and no `ApplyDecision` step     |
| Version 2 decision                              | `RejectedStaleVersion`, no receipt or apply step      |

Runtime `Completed` alone is not approval. Inspect the business result.
View the same instance in Scheduler's dashboard; SDK-exported history is
also captured. Each run writes IDs, pod names, history, logs, and timings
under `.build/evidence/<prefix>/`. Do not commit credentials or tokens.

## Stage 2, not covered

Multi-step policies, trusted reviewer identity binding, Teams cards, an audit
store, real duplicate-safe destination updates, monitoring-backend traces,
code upgrades with waiting instances, high availability, autoscaling, and load.
Do not infer production latency or availability guarantees from this test.

References: [official Human Interaction sample][sample],
[AKS quickstart][aks], and [Scheduler emulator][emulator].

[sample]: https://github.com/Azure-Samples/Durable-Task-Scheduler/tree/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/durable-task-sdks/dotnet/HumanInteraction
[aks]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/quickstart-aks-durable-task-scheduler
[emulator]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/develop-with-durable-task-scheduler
