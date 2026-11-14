---
title: Approval spike source code
description: Projects, configuration, interfaces, build commands, and deployment for the approval worker and console.
ms.date: 2026-11-13
ms.topic: how-to
---

## Projects

| Project                                    | Responsibility                                                                                                    |
| ------------------------------------------ | ----------------------------------------------------------------------------------------------------------------- |
| [Approval.Worker](Approval.Worker/)        | Hosts the policy-driven `ApprovalChain`, reusable `ApprovalStep` steps, and business activities locally or on AKS |
| [Approval.Client](Approval.Client/) | SDK decision/status logic shared by the console; CLI commands `decide` and `status` |
| [Approval.Contracts](Approval.Contracts/)  | Request/decision/result types, explicit connection settings, and shared JSON options                              |
| [Approval.Console](Approval.Console/)      | Local Console UI and HTTP endpoints; not deployed to AKS                                                          |
| [Approval.Tests](../tests/Approval.Tests/) | Workflow, converter, CLI, configuration, and HTTP tests                                                           |

The [step workflow](Approval.Worker/ApprovalStep.cs) records submission, waits on a
`Decision` event or one durable timer, and validates actor, outcome, and plan
version. Invalid decisions keep waiting; valid rejection or timeout has no
receipt. Reviewers never apply the business update independently.
The [chain workflow](Approval.Worker/ApprovalChain.cs) runs policy stages in order
and role approvals within each stage in parallel. It waits for every outcome
and calls `ApplyDecision` once only when all required approvals approve.
These are not two competing approval engines: the parent coordinates the saved
chain, and the child provides the reusable decision/timer behavior. Single-step
and multi-step policies use the same parent/child pattern.

The [policy registry](Approval.Console/PolicyStore.cs) saves immutable versions
in root [approval_policies/](../approval_policies/): ignored `local.json` or
`azure.json`. A policy is one set per client or account: an optional default chain for every
business step plus per-step overrides, versioned as one unit. Saving creates and activates
the new version atomically. Selecting an existing version in the UI activates it for its scope.
At start, the chain is chosen narrower owner first (account, then client, then `agency-default`),
and inside an owner a step override beats its default chain.
Policies contain no actor assignments. The separate
[simulator store](Approval.Console/DemoIdentityStore.cs) persists them in
[demo_settings/](../demo_settings/), scoped by client/account.
At each new start, the console resolves the active policy chain and current simulator assignments,
then passes the complete resolved snapshot, including which rule supplied it, into the parent workflow;
workers do not reread editable files. This is a local registry stand-in, not a production configuration service.

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

| Process                        | ENDPOINT                 | TASKHUB          | SCHEDULER_AUTH     |
| ------------------------------ | ------------------------ | ---------------- | ------------------ |
| Local worker and Console UI    | `http://localhost:8080`  | `approval-local` | `None`             |
| Local Console UI/CLI for Azure | Bicep Scheduler endpoint | `default`        | `AzureCli`         |
| AKS worker                     | Same Azure endpoint      | `default`        | `WorkloadIdentity` |

Missing settings fail explicitly. Authentication `None` is loopback-only.
The AKS webhook supplies the federated token settings; no secrets are committed.

The [HTTP file](../approvals.http) targets the console, **not Scheduler directly**:

| HTTP operation                         | Purpose                                                       |
| -------------------------------------- | ------------------------------------------------------------- |
| `GET /api/environment`                 | Confirm local/Azure endpoint and task hub                     |
| `POST /api/approvals/{id}/decision`    | Send a demo decision; `202` means sent, not approved          |
| `GET /api/approvals/{id}?history=true` | Read business status and durable history                      |
| `GET /api/business-steps` | List the hard-coded business workflows (from the analysis) an override may target |
| `GET /api/policies`                    | List saved versions and active selections                     |
| `POST /api/policies` | Save a policy set (default chain + step overrides) as a new active version (`201`); unchanged content returns the same version (`200`); an unknown override step returns `400` |
| `GET /api/policy-match` | Show which chain a workflow started now would use for `clientId`, optional `accountId`, and `workflowCode` |
| `POST /api/policies/{id}/activate`     | Activate a version for new starts                             |
| `POST /api/workflows`                  | Start a chain using the matching active policy                |
| `GET /api/workflows/{id}`              | Read pinned policy, structured progress, and aggregate result |
| `GET /api/workflows` | List workflow instances for the full approval-plan selector |
| `GET /api/workflows/{id}/approvals` | Project every pinned step with stage/reviewer rank, state, and allowed actions |
| `GET /api/demo-identities` | List simulator-only role assignments |
| `POST /api/demo-identities` | Save simulator role assignments without changing policy versions |

OpenAPI is at `/openapi/v1.json`. POST requests need JSON and
`X-Approval-Console: spike`; the console accepts only local, same-origin traffic.
Its **Acting as** field is not real approver authentication.
It selects available role/user assignments from a workflow's pinned reviewers.
The full plan shows future stages without starting them early; only actual waiting
children in the parent's current stage can receive a decision through the client/API.

For the CLI, after selecting an environment:

```bash
dotnet run --project src/Approval.Client -c Release --no-build -- --help
```

The CLI can signal reviewer steps and inspect step/parent history. It cannot
start a standalone reviewer. All business starts use `/api/workflows` and a policy,
including policies with just one stage and one reviewer.

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

The worker image registers the parent and reusable approval step once. Subsequent
policy edits need no image rebuild, restart, or AKS update. New business activity
types still require code. This is a green deployment: the worker accepts only the current
contracts, with no migration or compatibility layer.
