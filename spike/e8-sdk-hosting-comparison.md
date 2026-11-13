---
title: E8 runtime, hosting and backend options
description: Documentation-backed comparison of Functions versus SDK, hosting platforms, state backends, and best-fit E8 combinations.
ms.date: 2026-11-12
ms.topic: concept
status: Draft recommendation, not approved
---

## Three separate choices

Choose how workflow code runs, where its workers run, and where execution state
is saved. Moving a container does not automatically move its state or remove
Azure dependencies. This is a draft assessment, not deployment or acceptance proof.

## Functions runtime vs SDK

For workflow coordination, the Functions option means Azure Functions runtime
plus Durable Functions, not ordinary stateless functions.

| Key area                 | Functions runtime + Durable Functions                                                                                                                                    | Standalone Durable Task SDK                                                                                                                                          |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| What runs our code       | The Functions host invokes orchestrator and activity functions. An orchestrator coordinates work; an activity performs a task such as calling Media Plan.                | A library inside our API/background-worker process runs registered orchestrators and activities. No Functions runtime is required.                                   |
| APIs and incoming events | Built-in HTTP/event triggers invoke our code. Durable Functions supplies execution start/status/event APIs and status-link helpers, but not a finished approval service. | We build submit-change, case-status, and reviewer-decision endpoints, plus event listeners. Their handlers call SDK methods to start, query, or signal an execution. |
| Durable behavior         | Coordinates steps, retries, timers, and human waits; persists execution through its configured backend.                                                                  | Same core durable behavior. Moving to the SDK does not remove the need for a persistent backend.                                                                     |
| Backend choices          | Scheduler is recommended; Azure Storage or MSSQL are alternatives with provider constraints.                                                                             | The documented standalone SDKs use Azure Durable Task Scheduler. External business SQL does not replace that backend.                                                |
| Work we own              | Workflow code, E8 controls, runtime/extensions, and host/backend configuration. Managed hosting reduces trigger/scaling integration work.                                | Same E8 controls, plus application entry points, SDK packages, worker/scaling configuration, and instrumentation.                                                    |
| Best fit                 | The Functions model is accepted and built-in integration reduces delivery effort.                                                                                        | Ordinary application workers and host flexibility are preferred, and the extra integration work is justified.                                                        |

Sources: [Microsoft hosting-model comparison][frameworks],
[standalone SDK overview][sdk], and [Functions HTTP features][http-features].

E8 still supplies authorization, approval policy, business status, and
duplicate-safe destination calls. An incoming event is not automatically an
accepted approval. These obligations remain under
CP001/CP019, CP044, CP045, and DP065/DP081.

## Hosting options

Azure Functions plans provide managed hosting without introducing a Kubernetes
cluster. Container Apps supports ordinary SDK containers and native managed
Functions integration. AKS supports SDK workers; directly hosting the Functions
runtime on any Kubernetes cluster is a separate, community-supported composition.

### Container Apps vs AKS

This keeps SDK + Scheduler constant to compare the hosting platforms, not
different workflow capabilities.

| Criterion               | Container Apps                                                                                                                       | AKS                                                                                                                           |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| Platform/control        | Deploy images and settings; no direct access to underlying Kubernetes APIs.                                                          | Kubernetes APIs allow more workload placement, policy, and extension control.                                                 |
| Operating effort        | Azure operates nodes; team owns images, application configuration, and recovery.                                                     | Shared cluster/node responsibilities; confirm Standard versus Automatic and named platform owners.                            |
| Scaling                 | Azure manages KEDA underneath. Configure Scheduler-aware rules and replica limits; test activation from zero and cold-start latency. | Use KEDA or equivalent backend-aware worker scaling, plus node capacity. CPU scaling alone cannot activate workers from zero. |
| Workloads/storage       | APIs, background workers, and jobs; persistent Azure Files mounts are available.                                                     | Kubernetes workload/storage controls; a durable wait does not require one pod per case.                                       |
| Networking/security     | Configure identities, API access, private connectivity, DNS, and egress within environment limits.                                   | Configure workload identity and network policies; a private cluster endpoint does not make application APIs private.          |
| Deployment              | Managed revisions and HTTP traffic splitting. Traffic splitting does not select SDK workers receiving Scheduler work.                | Kubernetes rollouts; test worker shutdown and node drains with approvals still waiting.                                       |
| Monitoring              | Platform logs/metrics plus application tracing from case to destination receipt.                                                     | Same application tracing, plus pod/node health, scheduling, and cluster networking diagnostics.                               |
| Cost model              | Consumption resources/requests or Dedicated allocated profiles, plus dependencies and support.                                       | Allocated nodes, applicable management charges, platform operations, and dependencies.                                        |
| Idle baseline           | Zero Consumption replicas remove replica-resource charges, not all platform/backend costs.                                           | Running nodes still bill with zero worker pods; shared capacity is not automatically free.                                    |
| Portability             | Images move; identity, networking, scaling, and deployment settings need adaptation.                                                 | Kubernetes packaging helps compute reuse; Azure integrations and Scheduler state remain dependencies.                         |
| E8 runtime capabilities | SDK + Scheduler preserves waits/history/recovery; E8 owns policy, decisions, and receipts.                                           | Same runtime capability; AKS does not add approval correctness or remove the backend.                                         |
| Prefer when             | Standard APIs/workers fit and avoiding cluster administration is valuable.                                                           | Required Kubernetes controls or a genuinely delivery-ready operated cluster justify reuse.                                    |

Sources: [platform comparison][platform], [Container Apps SDK scaling][aca-scaling],
[AKS scaling][aks-scaling], [AKS responsibilities][aks-support], and
[Container Apps billing][aca-billing].

For Functions on Container Apps, verify generated Durable scaling rules, not just
a successful backend connection. Functions directly on AKS, Google GKE, or AWS
EKS requires runtime/scaling ownership and community best-effort support;
cluster-provider support does not replace managed Functions integration.
See [managed Functions containers][functions-aca] and
[Functions-on-Kubernetes support][functions-k8s].

### Where KEDA fits

[KEDA][keda] means Kubernetes Event-driven Autoscaling. It changes the number
of worker pods based on pending work. It does not execute workflows, store their
state, scale the SQL database, or directly scale ordinary VMs.

| Worker host                       | KEDA's role                                                                                                         | What we configure                                                                                                                                                                   |
| --------------------------------- | ------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Container Apps                    | Azure uses managed KEDA internally; we do not install or operate its underlying cluster.                            | Supported scale rules, identities, concurrency, and minimum/maximum replicas. SDK workers can use the Scheduler scaler; Functions needs the correct deployed Durable configuration. |
| Kubernetes: AKS, GKE, or EKS      | KEDA runs in the cluster and can activate worker pods from zero. Running outside Azure does not prevent using KEDA. | Install/enable a supported KEDA version, match its scaler to the backend, and configure authentication and node capacity.                                                           |
| VMs or servers without Kubernetes | KEDA is not the VM autoscaler. SDK workers can still run without it.                                                | Keep workers running, or integrate backend workload metrics with the host's autoscaler. Scaling to zero needs a separate mechanism to start workers again.                          |

For SQL-backed workers on Kubernetes, the provider documents the
[KEDA MSSQL scaler using `dt.GetScaleRecommendation`][sql-scaling].
For Scheduler-backed workers, qualify a Scheduler-aware scaler/metric;
the [Container Apps SDK guide][aca-scaling] documents its managed integration.
Test activation for due timers and reviewer callbacks, not only new API requests.

## Execution-state backend

| Backend                      | Fits which runtime                               | Main benefit and constraint                                                                                                                                                                                                  |
| ---------------------------- | ------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Azure Durable Task Scheduler | Durable Functions and documented standalone SDKs | Azure manages internal state and push-based dispatch, with a runtime dashboard. No customer SQL backend is supplied; region, tier, retention, cost, and Azure connectivity still matter.                                     |
| Azure Storage                | Durable Functions                                | Mature, usage-based queues/tables/blobs. Polling, partitioning, and replay I/O need qualification; orchestration/entity processing scales to 16 worker nodes per task hub, not 16 workflows.                                 |
| MSSQL / SQL Server           | Durable Functions                                | Runtime history and pending work can live in customer-controlled SQL. Own capacity/recovery configuration; Durable Entities are not supported with .NET isolated. It is not a drop-in backend for the newer standalone SDKs. |

Sources: [provider support and limits][storage-providers],
[Scheduler architecture][scheduler], and [SQL provider][sql-provider].
SQL Server can run outside Azure; Azure SQL Database remains an Azure service.

These are alternatives, not Scheduler plus a second runtime SQL database.
With MSSQL, runtime/provider code processes work persisted in SQL; SQL alone
does not execute workflows or wake workers from zero.

E8 business records and E9 protected evidence remain separate logical
responsibilities in every combination. A runtime database is not the business
approval ledger. [Functions host storage][host-storage] is also distinct from
the chosen durable-state provider.

## Combinations and best fit

| Runtime and host                                                   | Execution backend      | Best fit                                                                                                           | Main constraints                                                                                                                                                                            | Azure dependency                                                                              |
| ------------------------------------------------------------------ | ---------------------- | ------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| Durable Functions on Azure Functions plans                         | Scheduler              | Azure-first delivery with built-in triggers/management APIs and no cluster to operate.                             | Maintain Functions runtime/extensions and host-storage configuration. Scaling and cold starts depend on the plan; Scheduler region/tier must meet requirements.                             | Yes: compute and backend.                                                                     |
| Durable Functions on Container Apps                                | Scheduler              | Custom container dependencies alongside other applications, retaining managed Functions integration.               | Maintain container images and Functions extensions. Deploy the correct Durable scaling configuration and verify wake-up from zero; backend connectivity alone does not prove scaling works. | Yes: compute and backend.                                                                     |
| Standalone SDK on Container Apps                                   | Scheduler              | Ordinary API/workers without the Functions host or cluster administration.                                         | Build HTTP/event handlers and case-status APIs; configure SDK worker scaling. No direct Kubernetes API access; the documented SDK backend remains Scheduler.                                | Yes: compute and backend.                                                                     |
| Standalone SDK on AKS                                              | Scheduler              | Delivery-ready AKS or required Kubernetes controls, with funded operating ownership.                               | Own application APIs/workers plus Kubernetes scaling, capacity, upgrade coordination, and support handoffs. Scheduler remains a separate managed dependency.                                | Yes: compute and backend.                                                                     |
| Standalone SDK on GKE, EKS, or external VMs                        | Azure Scheduler        | Worker compute must be elsewhere, but Azure execution state is permitted.                                          | Requires authenticated connectivity to Azure Scheduler. Qualify cross-cloud latency, network cost, and permitted state placement; moving workers is not a full Azure exit.                  | Yes: backend remains in Azure.                                                                |
| Durable Functions on an Azure host                                 | Azure Storage or MSSQL | Storage economics or database control justify the provider's constraints.                                          | Storage has polling and task-hub scale limits. MSSQL requires database capacity/recovery configuration and does not support Durable Entities with .NET isolated.                            | Yes: external SQL alone does not move compute.                                                |
| Self-hosted Functions runtime on external Kubernetes               | External SQL Server    | Candidate for execution/state outside Azure; accept self-hosting support limits and qualify all host dependencies. | Community best-effort Functions-on-Kubernetes support. Own runtime, host-storage/trigger setup, scaling, and SQL recovery; MSSQL Durable Entities remain unsupported with .NET isolated.    | Potentially none for orchestration; not an automatic full-platform exit.                      |

GKE is Google Kubernetes Engine; EKS is Amazon Elastic Kubernetes Service.

External deployment needs proof for host storage, triggers, identity,
secrets, monitoring, and scaling. Replace or explicitly retain dependencies
such as Event Hubs and Microsoft 365. A SQL-backed workflow core does not make
the entire Connected Media platform cloud-independent.

## Draft recommendation

For Azure-first delivery, platform readiness and acceptance of the Functions
model are decision inputs, not assumed facts.

* If ordinary workers are preferred, use SDK + Scheduler. Reuse qualified,
  delivery-ready AKS; otherwise assess approved Container Apps before adding
  cluster operations solely for E8.
* If Functions is accepted and reduces integration work, use managed Functions
  hosting; Container Apps fits custom-image requirements.
* If execution and state must both leave Azure, qualify the external
  Functions-plus-SQL composition before building affected workflows. Do not label
  SDK workers plus Azure Scheduler as Azure-independent.

Compare total compute, backend, data/evidence, networking, telemetry, and support
cost for the same workload and availability. No measured cheapest option exists.
Changing providers does not automatically migrate running workflow history.

## Azure Durable Task SDK spike on Kubernetes

### Scenario: one test approval request

A requester submits "Sunny Soda test plan." One reviewer approves or rejects it.
If nobody responds before a short test timeout, the result is Timeout, never
automatic approval. Replacing the worker while it waits must not lose the request.

```text
Submit request -> Pending -> Approve / Reject / Timeout
                    |
             replace worker pod
                    |
           resume the same instance
```

Use the standalone .NET Durable Task SDK, an AKS worker, and Azure Durable Task
Scheduler. An interactive console/test client is enough; no product UI, custom
approval API, business SQL database, or real media-plan update is needed.
This tests E8's durable wait/response mechanics, not the complete approval product.

There are two stages below: deploy the infrastructure/application, then validate
the user journey. All entries are placeholders; no deployment or tests are claimed.

### Spike setup placeholders

Record resource and identity references, never credentials.

| Key area           | Record before testing                                                                                                   |
| ------------------ | ----------------------------------------------------------------------------------------------------------------------- |
| Owner and timebox  | TBD: implementer, reviewer, timebox, budget, and cleanup owner                                                          |
| Deployment scope   | TBD: approved subscription/resource group, region, namespace, and new versus existing AKS                               |
| Pinned versions    | TBD: source commit, supported .NET/SDK/Kubernetes versions, and worker image                                            |
| Backend and access | TBD: Scheduler tier, task hub, workload identity, network access, and client identity                                   |
| Test profile       | Proposed: one fixed worker replica, one request at a time; record a short test-only `TimeoutHours` value                |
| Acceptance targets | TBD: start/resume latency and worker-replacement recovery target; these are test criteria, not invented production SLAs |

### 1. Deploy with Bicep and the official samples

Use Microsoft's [SDK-on-AKS quickstart][aks-quickstart] and its
[Bicep infrastructure baseline][aks-bicep]. The baseline provisions AKS, ACR,
networking, Scheduler/task hub, and workload identity/RBAC. It creates a new
cluster by default; explicitly adapt it if reusing an existing cluster.

Reuse that deployment structure with the smaller
[Human Interaction SDK sample][approval-sample]. The approval sample is not
AKS-ready unchanged: infrastructure is provisioned with Bicep; Docker images
and Kubernetes manifests deploy the application.

| Step                            | Action and expected result                                                                                                                                                                                                               | Status  | Evidence / gaps |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- | --------------- |
| 1.1 Review the baseline         | Select supported versions, region/tier, resource scope, permissions, and network restrictions. Review sample defaults rather than treating them as approved production settings.                                                         | Not run | TBD             |
| 1.2 Provision infrastructure    | Adapt and deploy the official Bicep modules. Record the resulting AKS, ACR, Scheduler/task hub, identity, and connectivity configuration.                                                                                                | Not run | TBD             |
| 1.3 Package the approval worker | Containerize the Human Interaction worker using the AKS sample's image/manifest structure. Replace its console-only lifetime with a long-running host and graceful shutdown.                                                             | Not run | TBD             |
| 1.4 Deploy and connect          | Deploy one fixed worker replica. Configure the Azure Scheduler endpoint, task hub, and workload identity; confirm it connects to Azure, not the local emulator.                                                                          | Not run | TBD             |
| 1.5 Prepare the test client     | Run an authorized interactive client that can reach Scheduler. Set the short test timeout and require explicit approve/reject input; blank input or end-of-input must not approve. Confirm an unauthorized Scheduler identity is denied. | Not run | TBD             |

The sample [worker][approval-worker] currently uses `Console.ReadKey()`, and the
[client][approval-client] treats non-reject input as approval. These console
behaviors need the small adaptations above; do not deploy the approval client
as an unattended pod. Use Scheduler/dashboard queries to inspect a no-response
case without sending a decision.

The official AKS baseline uses fixed replicas and explicitly excludes
autoscaling. Keep KEDA/scale-to-zero as a later extension, not a pass criterion
for this initial spike.

### 2. Validate the user journey

Run each path with a fresh request ID. For the restart path, keep the same
instance ID before and after worker replacement. Record status, history,
elapsed time, and logs; every Pass needs evidence.

| Test                          | User action and expected result                                                                                                                                                                              | Status  | Evidence / gaps |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------- | --------------- |
| 2.1 Submit and wait           | Submit "Sunny Soda test plan." Record the instance ID and observe Pending/custom status while the orchestration waits; no approval result appears yet.                                                       | Not run | TBD             |
| 2.2 Approve                   | Explicitly approve a waiting request before its timeout. The returned business result is Approved.                                                                                                           | Not run | TBD             |
| 2.3 Reject                    | Submit a fresh request and explicitly reject it. The returned business result is Rejected, not Approved.                                                                                                     | Not run | TBD             |
| 2.4 No response               | Submit a fresh request, send no decision, and let the short test timer expire. The returned business result is Timeout; no automatic approval occurs.                                                        | Not run | TBD             |
| 2.5 Recover after replacement | Submit a fresh request and leave it waiting. Replace the single worker pod, wait for the replacement to connect, then approve. The original instance completes as Approved without resubmitting the request. | Not run | TBD             |

The sample can report runtime Completed for Approved, Rejected, or Timeout.
Inspect its returned `Status`; Completed alone does not mean approved.

Out of scope for this first spike: multi-stage policies, E1 business-role/version
checks, real notifications, destination updates/receipts, E9 audit, load/HA tests,
and KEDA activation. Record these as untested, not implicitly covered.

### Spike outcome placeholder

| Decision field            | Record after execution                                                                 |
| ------------------------- | -------------------------------------------------------------------------------------- |
| Recommendation            | Pending: Go, No-go, or Repeat this bounded spike with changes                          |
| Reviewer and date         | TBD                                                                                    |
| Results against targets   | TBD: deployment checks, five journey tests, measured values, and failed/blocked checks |
| Open gaps and next action | TBD: issue, owner, mitigation, and required retest                                     |
| Reproduction package      | TBD: source/image versions, adapted Bicep/manifests, client instructions, and results  |

Go requires the deployment and journey checks to pass with evidence. This proves
the bounded SDK-on-AKS scenario, not production E8 readiness. Record cleanup
scope and remove only approved test resources, never a shared cluster.

## Sources

Product claims link to public Microsoft Learn, KEDA, SQL-provider documentation,
and official GitHub examples. The spike examples were checked at repository
revision `7152d82` on 2026-11-12; no deployment, test result, or customer acceptance
is claimed.

Related platform details: [AKS Automatic][aks-automatic] and
[Container Apps persistent storage][aca-storage].

[sdk]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/durable-task-overview
[frameworks]: https://learn.microsoft.com/en-us/azure/durable-task/common/choose-orchestration-framework
[http-features]: https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-http-features
[functions-aca]: https://learn.microsoft.com/en-us/azure/container-apps/functions-overview
[functions-k8s]: https://learn.microsoft.com/en-us/azure/azure-functions/functions-kubernetes-keda
[scheduler]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler
[storage-providers]: https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-storage-providers
[host-storage]: https://learn.microsoft.com/en-us/azure/azure-functions/storage-considerations
[sql-provider]: https://microsoft.github.io/durabletask-mssql/#/introduction
[sql-scaling]: https://microsoft.github.io/durabletask-mssql/#/scaling
[keda]: https://keda.sh/docs/2.21/concepts/
[platform]: https://learn.microsoft.com/en-us/azure/container-apps/compare-options
[aca-scaling]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/durable-task-scheduler-auto-scaling
[aca-storage]: https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts
[aks-support]: https://learn.microsoft.com/en-us/azure/aks/support-policies
[aks-automatic]: https://learn.microsoft.com/en-us/azure/aks/intro-aks-automatic
[aks-scaling]: https://learn.microsoft.com/en-us/azure/aks/concepts-scale
[aca-billing]: https://learn.microsoft.com/en-us/azure/container-apps/billing
[aks-quickstart]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/quickstart-aks-durable-task-scheduler
[aks-bicep]: https://github.com/Azure-Samples/Durable-Task-Scheduler/blob/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/scenarios/DocumentProcessingOnAKS/infra/main.bicep
[approval-sample]: https://github.com/Azure-Samples/Durable-Task-Scheduler/tree/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/durable-task-sdks/dotnet/HumanInteraction
[approval-worker]: https://github.com/Azure-Samples/Durable-Task-Scheduler/blob/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/durable-task-sdks/dotnet/HumanInteraction/Worker/Program.cs
[approval-client]: https://github.com/Azure-Samples/Durable-Task-Scheduler/blob/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/durable-task-sdks/dotnet/HumanInteraction/Client/Program.cs
