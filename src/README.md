---
title: Approval spike source code
description: Projects, configuration, interfaces, build commands, and deployment for the approval worker and console.
ms.date: 2026-11-13
ms.topic: how-to
---

## Projects

| Project                                    | Responsibility                                                                                        |
| ------------------------------------------ | ----------------------------------------------------------------------------------------------------- |
| [Approval.Worker](Approval.Worker/)        | Long-running host for `PlanApproval`, `RecordSubmission`, and `ApplyDecision`; runs locally or on AKS |
| [Approval.Client](Approval.Client/)        | SDK client logic shared by the console; CLI commands `start`, `decide`, and `status`                  |
| [Approval.Contracts](Approval.Contracts/)  | Request/decision/result types, explicit connection settings, and shared JSON options                  |
| [Approval.Console](Approval.Console/)      | Laptop-only demo page and HTTP endpoints; not deployed to AKS                                         |
| [Approval.Tests](../tests/Approval.Tests/) | Workflow, converter, CLI, configuration, and HTTP tests                                               |

The [workflow](Approval.Worker/PlanApproval.cs) records submission, waits on a
`Decision` event or one durable timer, and validates actor, outcome, and plan
version. Invalid decisions keep waiting; valid rejection or timeout has no
receipt. Approval calls `ApplyDecision`, a logged stand-in for a business update.

## Build and test

Run from the repository root with the .NET SDK pinned in [global.json](../global.json):

```bash
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --no-restore
```

If needed, restore from the Microsoft mirror with
`--source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json`.
Warnings are errors; keep `packages.lock.json` files in source control.

## Configuration and interfaces

Use [the environment switch](../scripts/use-env.sh); do not change code between demos.

| Process                      | ENDPOINT                 | TASKHUB          | SCHEDULER_AUTH     |
| ---------------------------- | ------------------------ | ---------------- | ------------------ |
| Local worker and console     | `http://localhost:8080`  | `approval-local` | `None`             |
| Laptop console/CLI for Azure | Bicep Scheduler endpoint | `default`        | `AzureCli`         |
| AKS worker                   | Same Azure endpoint      | `default`        | `WorkloadIdentity` |

Missing settings fail explicitly. Authentication `None` is loopback-only.
The AKS webhook supplies the federated token settings; no secrets are committed.

The [HTTP file](../approvals.http) targets the console, **not Scheduler directly**:

| HTTP operation                         | Purpose                                              |
| -------------------------------------- | ---------------------------------------------------- |
| `GET /api/environment`                 | Confirm local/Azure endpoint and task hub            |
| `POST /api/approvals`                  | Start a fresh instance; returns `201`                |
| `GET /api/approvals`                   | List waiting approvals                               |
| `POST /api/approvals/{id}/decision`    | Send a demo decision; `202` means sent, not approved |
| `GET /api/approvals/{id}?history=true` | Read business status and durable history             |

OpenAPI is at `/openapi/v1.json`. POST requests need JSON and
`X-Approval-Console: spike`; the console accepts only local, same-origin traffic.
Its **Acting as** field is not real approver authentication.

For the CLI, after selecting an environment:

```bash
dotnet run --project src/Approval.Client -c Release --no-build -- --help
```

See the [demo guide](../spike/README.md) for executable local and Azure examples.

## Deploy the worker to AKS

First provision and connect to the cluster using the
[infrastructure guide](../infra/README.md). Then, from the repository root:

```bash
export AZURE_SUBSCRIPTION_ID='<approved-subscription>'
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
source scripts/use-env.sh azure
kubectl config use-context "$AKS_CLUSTER_NAME"

export IMAGE_TAG="approval-$(date -u +%Y%m%d%H%M%S)"
az acr build --subscription "$AZURE_SUBSCRIPTION_ID" \
  --registry "$REGISTRY_NAME" --platform linux/amd64 \
  --image "approval-worker:$IMAGE_TAG" --file Dockerfile .
digest="$(az acr repository show --subscription "$AZURE_SUBSCRIPTION_ID" \
  --name "$REGISTRY_NAME" --image "approval-worker:$IMAGE_TAG" \
  --query digest --output tsv)"
export WORKER_IMAGE="$REGISTRY_LOGIN_SERVER/approval-worker@$digest"
bash scripts/deploy-worker.sh
```

Requires ACR build/publish permissions; kubelet `AcrPull` alone does not grant them.
For a blocked NuGet connection, add `--build-arg NUGET_SOURCE=<mirror-url>`
to the build. The [Dockerfile](../Dockerfile) builds a non-root .NET 10 image.
The [manifest](../deploy/worker.yaml) runs one replica in namespace `durable-spike`
with service account `durable-worker`, matching the Bicep federation.
The [deployment script](../scripts/deploy-worker.sh) validates the context and
immutable image digest, checks the manifest, and waits for rollout.

Use fresh instances. There is one workflow, no migration/compatibility layer.
