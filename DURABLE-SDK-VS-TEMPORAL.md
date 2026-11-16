---
title: Durable Task SDK on AKS versus self-hosted Temporal
description: Initial research comparing the existing approval spike with a proposed self-hosted Temporal and Python implementation on the same AKS cluster.
ms.date: 2026-11-16
ms.topic: reference
---

## Initial conclusion

Both platforms can implement the same configuration-driven approval and sign-off
workflow. The main difference is **who operates the durable execution backend**,
not whether sequential and parallel approvals are possible.

* Our .NET Durable Task workers run on AKS, but Azure Durable Task Scheduler
  stores history and dispatches work outside the cluster.
* Self-hosted Temporal puts the Temporal Service under our operational ownership,
  alongside application workers. Persistence, security, monitoring, upgrades,
  backup and availability become responsibilities of our platform team.
* Temporal offers documented Signals, Queries, Workflow Updates, replay testing,
  time-skipping tests and Worker Deployment Versioning. These are useful
  enterprise capabilities, but their value must be demonstrated with our workflow.
* Python is a suitable language for the proposed Temporal exploration. Its
  official SDK has stable releases and the features needed for this approval
  pattern. Temporal .NET is also a stable option; Python is not required to
  compensate for an immature .NET SDK.
* A business workflow library with copy, edit, test and activation is an
  application layer we would build over either engine, not a feature supplied
  by either operational dashboard.
* Durable does support dashboard search and workflow code versioning. Its
  dashboard tag filtering must not be confused with tag-query support in our
  pinned .NET SDK; Temporal has a more expressive, typed Visibility query model.

The separate [Python Temporal spike](./temporal/README.md) is now implemented
and tested on Docker Desktop with persistent PostgreSQL. The deployment target
was changed from AKS to local Docker during implementation; no Temporal
resources were deployed to AKS. Keep the current Durable implementation unchanged
as the baseline. Same-AKS deployment remains a future comparison option.

> [!IMPORTANT]
> Temporal is implemented locally, not on AKS.
> The recorded local results below are distinct from documented, untested capabilities.
> No conclusion about which platform is faster, cheaper or more reliable is justified yet.

## Scope and evidence

The comparison is:

```text
Current baseline:
  .NET application workers on AKS
    -> managed Azure Durable Task Scheduler

Implemented local comparison:
  Python application worker on Docker Desktop
    -> self-hosted SQL-backed Temporal Server in Docker
    -> PostgreSQL execution and visibility databases on a persistent volume

Future deployment option, not performed:
  Python worker + self-hosted Temporal Service on the SAME AKS cluster
    -> explicitly sized and qualified persistence
```

Temporal Cloud is not the target. Its hosted control plane, access management,
support promises and pricing must not be attributed to self-hosted Temporal.

The Durable baseline was read at repository commit `6a89309`; its implementation
and recorded deployment are described in [the spike guide](./spike/README.md) and
[the findings](./spike/FINDINGS.md). Public documentation and release metadata
were checked on 2026-11-15. Business authoring, search and versioning were
investigated further on 2026-11-16. Release numbers are a research snapshot;
implementation must pin a tested SDK, server, CLI, UI and Helm chart combination.

### What our Durable spike actually proved

| Area                  | Recorded evidence                                                                                       |
| --------------------- | ------------------------------------------------------------------------------------------------------- |
| Approval behavior     | Sequential stages, parallel reviewers, comments, Return, reject/timeout cancellation and skipped stages |
| Policy behavior       | Default chain plus business-step overrides, immutable versions and a snapshot pinned at start           |
| Recovery              | A waiting approval survives worker replacement; an in-flight activity can be redelivered                |
| Traceability          | Actor, outcome, timestamp, comment, correlation ID and business tags                                    |
| Observability         | Correlated Application Insights spans; Azure dashboard data inspected, rendered walkthrough pending     |
| Burst                 | 100 chains, 300 decisions, 400 workflow instances; all Approved in 15 seconds on one application pod    |
| Timing                | Script-observed per-chain p50 13 seconds, p95 14 seconds, maximum 15 seconds                            |
| Resource observation  | After the burst: 47 millicores and 120 MiB; this is not peak CPU or memory                              |
| Verification          | 180 tests recorded as passing; this research does not rerun or extend them                              |

The burst measurement includes HTTP requests, script scheduling and one-second
polling. It is not isolated engine execution time or a sustained-throughput result.
The one-pod measurement excludes Azure's managed scheduler resources.

Relevant implementation evidence:

* [ApprovalChain](./src/Approval.Worker/ApprovalChain.cs)
* [ApprovalStep and recording activities](./src/Approval.Worker/ApprovalStep.cs)
* [Policy resolution](./src/Approval.Console/PolicyStore.cs)
* [Worker registration and tracing](./src/Approval.Worker/Program.cs)
* [Worker deployment and limits](./deploy/worker.yaml)
* [Burst harness](./scripts/test-burst.sh)
* [Step-one acceptance script](./scripts/test-step-one.sh)

## Architecture and operational ownership

### Current Durable deployment

```text
Local console / client
        |
        v
Azure Durable Task Scheduler
  task hub, durable history, dispatch and dashboard
        ^
        | TLS + managed identity / AKS Workload Identity
        |
AKS: approval-worker
  ApprovalChain + ApprovalStep + activities
        |
        v
Application Insights
```

This uses the **standalone Durable Task SDK**, not the Azure Functions runtime.
Microsoft documents the managed Scheduler as the backend for these SDKs.
Do not assume that Durable Functions' Azure Storage or MSSQL provider options
are drop-in backend options for this implementation. [MS-SDK] [MS-STORAGE]

### Proposed self-hosted Temporal deployment

```text
Local approval API / client
        |
        v
Same AKS cluster, dedicated temporal-spike namespace
  Temporal Frontend <-> History <-> Matching
                      + internal system Worker service
        |                              ^
        v                              |
  PostgreSQL                      Python application worker
    execution persistence           ApprovalChain
    visibility/search               ApprovalStep + activities

  Temporal Web UI -> Frontend
  server/worker logs, metrics and traces -> observability backend
```

The internal Temporal Worker service is **not** our approval application worker.
The server components and application workers are independently operated and
scaled. The official Helm chart deploys server components; its current README
explicitly requires separately supplied persistence databases. [T-HELM]

PostgreSQL supports execution persistence and current Visibility, including
Search Attributes. For the spike, separate execution and visibility databases
can share one PostgreSQL instance. Elasticsearch is not mandatory for this
starting point, although the visibility guide recommends Elasticsearch or
OpenSearch for larger workloads. Benchmark SQL visibility before making a
production sizing decision. [T-HELM] [T-VISIBILITY]

| Responsibility           | Durable SDK + managed Scheduler                          | Self-hosted Temporal on AKS                               |
| ------------------------ | -------------------------------------------------------- | --------------------------------------------------------- |
| Application code         | We own workflows, activities, APIs and business rules    | Same responsibility                                       |
| Application worker pods  | We own deployments, resources, readiness and rollouts    | Same responsibility                                       |
| Durable backend          | Azure operates Scheduler infrastructure                  | We operate Temporal services and their configuration      |
| Execution database       | Managed inside the Scheduler service                     | We supply, secure, size, back up and restore persistence  |
| Backend upgrades         | Managed service responsibility                           | Server, schemas, UI, CLI and chart compatibility are ours |
| Platform access          | Azure identity and task-hub RBAC                         | TLS/mTLS and configured authentication/authorization      |
| Incident investigation   | Worker evidence plus managed-service diagnostics/support | Worker, server, database, network and storage evidence    |
| Portability              | Workers are portable; this backend remains Azure-hosted  | Server and workers can run on supported non-Azure stacks  |
| Availability             | Depends on Scheduler SKU and our application topology    | Depends on the complete server/database/AKS topology      |

Sources: [MS-SCHEDULER], [MS-IDENTITY], [MS-BILLING], [T-DEPLOY],
[T-PRODUCTION], [T-SECURITY].

### Same cluster does not mean the same failure boundary

If AKS is unavailable, Durable workers stop, but the managed Scheduler remains a
separate service. Temporal application workers and Temporal Service would both
be unavailable when their shared cluster is unavailable.

A PostgreSQL persistent volume can preserve data through a pod replacement;
it does not establish database HA, disaster recovery or a tested restore.
Using managed PostgreSQL outside AKS changes the database failure boundary,
but the Temporal Service itself is still self-hosted.

Our [AKS configuration](./infra/modules/aks.bicep) disables node autoscaling.
The [application deployment](./deploy/worker.yaml) has one replica and uses
`Recreate`. Neither is a production availability design for this comparison.

## How the same approval concept maps to Temporal

Keep the business pattern unchanged:

```text
One reusable ApprovalChain
  -> Stage 1: Finance + Legal, in parallel
  -> Stage 2: Lead, after BOTH stage-1 approvals
  -> ApplyDecision, only when every required review approves

First Reject / Return / Timeout:
  stop remaining waiting reviewers
  preserve decisions already completed
  never start later stages
  return the ending business result with the decision record
```

| Current concept                   | Temporal equivalent                                           |
| --------------------------------- | ------------------------------------------------------------- |
| ApprovalChain orchestrator        | Workflow definition and execution                             |
| ApprovalStep sub-orchestrator     | Child Workflow per reviewer                                   |
| RecordSubmission / RecordDecision | Activities                                                    |
| ApplyDecision                     | Activity with explicit retry and idempotency choices          |
| Decision external event           | Signal for the parity implementation                          |
| Querying progress                 | Workflow Query plus execution/Visibility APIs                 |
| Request with processed response   | Workflow Update, evaluated separately from Signals            |
| Durable approval deadline         | Workflow timer / timed wait condition                         |
| Cancel event to waiting children  | Cancellation request and explicit child cleanup/result logic  |
| Task hub                          | Namespace for isolation, plus Task Queues for routing workers |
| Parent instance ID                | Workflow ID; also record Temporal Run ID                      |
| Dashboard tags                    | Typed Search Attributes for indexing; Memo for other metadata |
| Pinned policy snapshot            | Same immutable business input to the Workflow                 |
| Replay-safe orchestration code    | Deterministic Workflow code, isolated from activity I/O       |

These are conceptual mappings, not identical API semantics. In particular,
Task Queues are routing mechanisms, not a policy store or human inbox.
[T-PYTHON] [T-MESSAGES] [T-CANCEL] [T-PY-OBS]

A parent plus reviewer children is a design choice, **not a requirement of either
platform**. Both can coordinate reviewers inside a single workflow. Retain the
current child-per-reviewer design for the first comparison to avoid changing
architecture and engine simultaneously.

For the currently demonstrated requirements, the independent reviewer lifecycle
is optional. A simpler topology would keep reviewer decisions, deadlines and
parallel waits inside one chain execution, on either engine. Child workflows are
more appropriate when a subprocess needs substantial separate history or
operational controls; a human approval gate alone does not require one.

Both dashboards may list child executions beside parents. Filter by registered
workflow type/name `ApprovalChain` for business-level runs. The local Temporal
console already groups reviewers under the chain. Dashboard presentation is
not, by itself, a reason to change orchestration semantics.

For the existing two-stage burst shape, that means one parent plus three children:
four workflow executions, three decisions, three timers and seven logical activity
calls per chain. Retries add attempts; these counts are not billing units.

## Business-managed workflows without redeployment

The requirement is for a business user to browse a library, copy a workflow
for a customer, modify its steps/conditions/routing, save it, test it, and activate
it without code or a new application deployment.

**Both engines can support this through a configuration-driven interpreter.**
Neither the self-hosted Temporal Web UI nor the Scheduler dashboard supplies
that complete business-authoring and publishing experience. Searching execution
histories is different from browsing a library of editable business templates.
[T-UI] [MS-DASHBOARD]

Temporal's official Python DSL sample demonstrates a workflow interpreting
user-supplied sequence, parallel and activity definitions. This supports the
architectural approach, not a claim that the sample provides an approval
designer, conditional routing, governance or production-ready publishing.
[T-DSL]

Our existing [ApprovalChain](./src/Approval.Worker/ApprovalChain.cs) already
interprets a smaller vocabulary: ordered stages, parallel roles and timeouts.
It reads an immutable resolved policy snapshot rather than embedding a
customer's chain in worker code.

### The business experience versus the current spike

| Business action                  | Durable approval spike today                                                                   | Required layer for either engine                                                               |
| -------------------------------- | ---------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| Browse a workflow library        | Saved policy/version dropdown, not a governed template catalogue                               | Separate browse-only catalogue with ownership, customer scope and descriptions                 |
| Copy for a particular customer   | No copy-to-customer operation; a new policy can be entered manually                            | Clone into a new customer-scoped draft with a new ID and a reference to its source             |
| Edit steps, conditions, routing  | Stage order, roles, parallelism, timeouts and per-business-step overrides; no rule expressions | Validated editor over supported operations and a bounded condition/routing vocabulary          |
| Save the modification            | Changed content creates an immutable version and immediately activates it                      | Save draft independently of activation; record author, reason and changes                      |
| Test before activation           | Scripts exercise active policies; no business-facing candidate-version preview                 | Test the exact draft version in an isolated run with safe activity substitutes                 |
| Activate without an app release  | Already works for the supported chain configuration; new starts get the active snapshot        | Publish/activate only the tested version, with authorization, concurrency control and rollback |

Two existing behaviors must change to support this business-user experience:
[selecting a saved policy](./src/Approval.Console/wwwroot/console.js) activates it,
and [saving a changed policy](./src/Approval.Console/PolicyStore.cs) activates it.
Browsing must become read-only and Save draft must be separate from Activate.
This research changes neither behavior.

The [policy contract](./src/Approval.Contracts/ApprovalPolicy.cs) contains stages,
roles and timeouts, not arbitrary business conditions. Adding or reordering
existing review stages is already data-only. A new condition operator or a new
external integration needs an implementation and deployment before a business
user can select it.

### Proposed management layer

```text
Business workflow catalogue
  -> Copy template for customer
  -> Edit a versioned JSON definition
  -> Save draft
  -> Validate and run isolated scenarios against that exact draft
  -> Publish / activate the tested version
  -> Start new executions with an immutable definition snapshot
                  |
                  v
       Predeployed workflow interpreter
       Durable ApprovalChain OR Temporal ApprovalChain
```

An illustrative rule such as "add Finance when amount exceeds 500,000" can be
data-only once the interpreter supports that comparison, its typed inputs and
the route it selects. Arbitrary Python/C# scripts, new activity code and
unsupported operators are not no-deployment edits.

The layer needs versioned definitions and schemas, allowed step types,
bounded/deterministic rule evaluation, validation of role coverage and
separation of duties, test evidence, and an activation audit. Do not execute
untrusted expressions with general-purpose `eval` or let an editor bypass
mandatory client approval rules. Test external actions through safe substitutes,
not real notifications or business writes.

Persist the definition snapshot and routing inputs, or obtain external facts
through recorded activity results. A workflow must not repeatedly read the
latest mutable configuration during replay.

### Editing configuration while approvals are running

The existing [policy test](./scripts/test-policy.sh) already demonstrates:

1. Start chain A on policy v1 and leave it waiting.
2. Save and activate policy v2 without restarting the console or worker.
3. Start chain B, which uses v2.
4. Finish A using its original v1 chain, and B using its v2 chain.

This proves hot configuration activation **for new executions**, not live
rewriting of chain A. The Temporal spike should preserve that contract.

Changing an existing run is a separate capability: design an explicit change
request via events/Signals/Updates, authorize it, choose a safe stage boundary,
preserve completed approvals, reconcile existing timers and children, and record
the old/new definition versions. Neither a policy activation nor worker code
versioning automatically performs that migration. [T-MESSAGES] [MS-VERSION]

### Signals versus Updates

A Temporal Signal, like our current external Decision event, is acknowledged
before workflow processing completes. It does not tell the caller that the
review has been applied.

A Workflow Update can validate a request and return a processed result or error.
This could improve the approval API by returning "accepted decision" or "wrong
actor/version" without polling. However:

* Update processing requires an available worker; bound API waits and reconcile
  ambiguous client timeouts.
* A rejected Update validator does not substitute for an external audit of
  attempted decisions.
* Updates do not authenticate the human approver.
* Keep Signals for the apples-to-apples baseline, then test Updates as a separate
  product/API improvement. [T-MESSAGES]

### Business policy versions are not worker code versions

Both implementations need to distinguish:

1. Plan version: which submitted business document the decision addresses.
2. Policy/business-definition version: which approval chain was selected and pinned.
3. Workflow code/deployment version: which replay-compatible implementation runs.

The edit/test/activate scenario mainly concerns the second version. It is
application-managed on either platform. Definition-schema/interpreter
compatibility must also be recorded when the supported configuration vocabulary
changes.

Pinning a policy does not make incompatible workflow code changes replay-safe.
The current green-deployment spike deliberately avoids migration of old shapes;
it has not demonstrated a safe code upgrade while old workflows remain active.

Durable SDK supports context-based versioning and worker matching strategies,
including strict matching. Its documentation explicitly covers the standalone
.NET SDK. Do not describe versioning as absent from Durable. [MS-VERSION]

The pinned v1.28.0 SDK exposes `context.Version` and worker
`VersioningOptions` with `None`, `Strict` and `CurrentOrOlder` matching, plus
`Reject` or `Fail` mismatch handling. Configure them explicitly rather than
assuming every SDK shares the defaults described for Functions.
[MS-VERSION-CONTEXT] [MS-VERSION-OPTIONS]

Our [worker startup](./src/Approval.Worker/Program.cs) does not configure
`UseVersioning`; the policy version and immutable container digest do not enable
engine code-version routing. Qualifying in-flight code upgrades remains a
separate test, not a proven gap in Durable's platform capabilities.

Temporal documents pinned Worker Deployment Versions, traffic ramping and rollback,
as well as workflow patching. Pinned long-lived workflows require the old worker
version to remain available, or an explicit compatible upgrade strategy.
Minimum supported server/SDK/CLI/UI versions matter. [T-VERSION]

## Search and visibility in a browser

**Durable has search; Temporal has a richer typed execution-search model.**
The engine capability, SDK exposure and our own console implementation need to
be evaluated separately.

### Durable Scheduler dashboard versus the pinned SDK

Microsoft documents dashboard filtering by instance ID, orchestration name,
runtime status, creation window and tag key or value. Orchestration tags are
string metadata usable for filtering; activity tags are history annotations,
not queryable orchestration fields. Tags are immutable after scheduling and the
serialized orchestration tag dictionary has a documented 1,000-byte limit.
[MS-DASHBOARD] [MS-TAGS]

However, the exact [v1.28.0 OrchestrationQuery][MS-QUERY] and its
[gRPC serialization][MS-QUERY-GRPC] expose:

* `CreatedFrom` and `CreatedTo`
* `Statuses` and `TaskHubNames`
* `InstanceIdPrefix`
* `PageSize` and `ContinuationToken`
* `FetchInputsAndOutputs`

They do **not** expose `Tags`, a name predicate, or an arbitrary filter
expression. Do not show code assigning `OrchestrationQuery.Tags`; it would not
compile against the SDK used here. Dashboard tag filtering is a service/UI
capability, not evidence that the same query is exposed by this client API.

The [current console listing](./src/Approval.Console/ApprovalChainService.cs)
calls `GetAllInstancesAsync` with inputs/outputs, traverses the results, then
filters by workflow name in application code. It is not a custom indexed search
API, and indexing arbitrary JSON inputs/custom status has not been demonstrated.
Our existing start tags include client, account, business step, policy version
and correlation ID, but there are no equivalent search controls in this UI.

### Temporal Search Attributes

Temporal supports registered, typed custom Search Attributes, including Keyword,
Int and Datetime. Values can be assigned at start and updated from workflow
code, and combined with default attributes in SQL-like List Filters through the
Web UI, CLI and listing API. The proposed PostgreSQL Visibility store supports
this feature; Elasticsearch is not required for the initial experiment.
[T-SEARCH] [T-LIST] [T-UI] [T-VISIBILITY]

For example, after registering and maintaining the relevant fields, a proposed
approval search could be:

```sql
WorkflowType = 'ApprovalChain'
AND ExecutionStatus = 'Running'
AND ClientId = 'customer-001'
AND BusinessStep = 'B07'
AND DefinitionVersion = 2
AND ApprovalState = 'Waiting'
```

These are proposed custom attribute names, not fields already created here.
Register searchable metadata through supported engine tooling; do not modify
Temporal's database indexes/schemas directly. Memo is not a substitute for
indexed Search Attributes.

Visibility can lag workflow state. Use an execution Query or authoritative
business data to confirm current state before an action. Search is not
authorization, and Search Attributes are readable metadata not protected by a
payload codec; use opaque IDs, not comments, secrets or personal data.
[T-SEARCH]

### Practical comparison and application design

| Search requirement                      | Durable SDK v1.28.0 / Scheduler                                      | Self-hosted Temporal with Visibility                            |
| --------------------------------------- | -------------------------------------------------------------------- | --------------------------------------------------------------- |
| Browse operational executions           | Scheduler dashboard                                                  | Temporal Web UI                                                 |
| Query status/time/ID in our API         | Paged SDK filters; name filtering is currently application-side      | Listing API over default Search Attributes                      |
| Custom client/account/step search       | Dashboard tag filter; no tag predicate on the pinned query API       | Registered typed Search Attributes in the listing API           |
| Combined numeric/range predicates       | Not established for Scheduler tags or the pinned SDK                 | Supported List Filter operators for suitable attribute types    |
| Search changing approval state          | Custom status is readable; immutable start tags cannot track changes | Workflow-maintained Search Attributes; allow for visibility lag |
| Browse/copy editable business templates | Separate definition catalogue needed                                 | Same; execution search is not a template library                |

Both platforms distinguish engine status from business outcome: a chain can be
engine `Completed` with business result `Rejected` or `Returned`. Filtering only
for engine `Failed` does not find rejected approvals. An inbox needs explicit
business-state/outcome fields, not just runtime status.

For a browser-facing product, put an authenticated, tenant-scoped API between
the browser and either engine. Use supported paged filters and allowlisted
search fields. Do not expose engine credentials or unrestricted control APIs.

For Durable custom/dynamic business search, either qualify a supported public
API that exposes the necessary service filters, or maintain our own indexed
read model in SQL/Cosmos through idempotent activities/events and reconciliation.
The pinned .NET query API alone does not provide Temporal-equivalent custom
queries. Do not rely on undocumented dashboard endpoints or call an all-inputs
scan an indexed-search solution.

For Temporal, that API can translate approved UI filters to the supported
Visibility listing API. An external read model may still be useful for joins
with business records, long-term retention or a cross-engine inbox.

Durable search claims here are documentation/tagged-source checks, not a new
authenticated dashboard walkthrough. Temporal's local combined filters and
pagination have now passed integration tests; an indexed-search scale benchmark
has not been performed.

## Maturity and Python suitability

The release snapshot below was verified from official releases. The local
Temporal implementation now uses the recorded Python SDK and Server versions;
see its [implementation guide](./temporal/README.md) for the deployed stack.

| Component               | Verified release snapshot       | Interpretation                                      |
| ----------------------- | ------------------------------- | --------------------------------------------------- |
| Durable .NET SDK        | v1.28.0, released 2026-11-04    | GA SDK; this repository uses that version           |
| Temporal Server         | v1.32.2, released 2026-11-02    | Maintained self-hosted platform release             |
| Temporal Python SDK     | 1.36.1, released 2026-11-14     | Stable maintained SDK; 1.0.0 released 2023-01-05    |
| Temporal .NET SDK       | 1.22.0, released 2026-11-10     | Stable maintained SDK; 1.0.0 released 2023-12-05    |

Release history supports treating Python as an established candidate, not a
preview-only experiment. It does not prove that Python is more reliable or faster
than .NET, and version numbers are not a maturity score.
[MS-SDK] [MS-RELEASES] [T-SERVER-RELEASES] [T-PY-RELEASES] [T-NET-RELEASES]

The Python SDK documents typed client calls, async workflows, Signals, Queries,
Updates, children, cancellation, timers, replay and time-skipping tests. Its
Workflow sandbox helps catch nondeterministic calls but is not a security
boundary. Keep database/HTTP calls in activities and avoid blocking the async
worker with synchronous I/O. [T-PYTHON] [T-PY-TEST] [T-SANDBOX]

Python is recommended for this isolated exploration because it meets the required
feature set and the implementation need not remain C#. If code sharing with the
current C# contracts and API becomes the priority, Temporal .NET is a reasonable
alternative and would reduce language-related comparison variables.

### Mature platform does not mean every feature is equally mature

| Feature                         | Research finding and implication                                                    |
| ------------------------------- | ----------------------------------------------------------------------------------- |
| Workflow execution and messages | Documented in maintained Python and .NET SDKs; qualify our business use             |
| Worker Deployment Versioning    | Documented with explicit version minimums; test chosen pinned versions              |
| Python tracing interceptor      | Established documented integration; workflow-side spans have no duration            |
| New Python OTel plugin          | Explicitly experimental; durable duration-preserving spans need separate evaluation |
| Self-hosted archival            | Explicitly experimental and disabled by default; not a compliance guarantee         |
| Self-hosted enterprise controls | Security extension points exist; ready-made Cloud governance is not included        |

Sources: [T-PY-OBS], [T-VERSION], [T-ARCHIVAL], [T-SECURITY], [T-PRODUCTION].

## Logs, tracing, debugging and operations

### Logs and trace correlation

Our Durable worker emits structured JSON logs and uses a replay-safe logger.
The SDK's `Microsoft.DurableTask` source plus application business spans reach
Application Insights. The recorded spike found a sampling issue and ingestion
delay; those are observations about our integration, not universal platform
limitations. [MS-OTEL] [MS-DIAGNOSTICS]

Temporal Python supports workflow/activity logging and OpenTelemetry integration.
Use the workflow-aware logger to avoid replay noise and attach Workflow ID,
Run ID, activity attempt, client/account, plan/policy version and correlation ID.
Collect **server** and **database** logs as well as application logs.

The documented `TracingInterceptor` propagates context across clients, workflows
and activities, but workflow-side spans are completed with no duration.
The newer `OpenTelemetryPlugin` supports replay-safe duration-preserving spans
but is experimental. Do not silently rely on the experimental plugin to claim
full production observability maturity. [T-PY-OBS]

Use the same Application Insights destination, or the same OTLP collector/backend,
for both spikes where practical. Verify the actual span tree, restart behavior,
sampling, duplicate attempts and export latency. Engine history is authoritative
for workflow state; a missing exported span is not proof work never ran.

### Debugging a production incident

| Question                           | Durable baseline                                         | Temporal investigation path                                       |
| ---------------------------------- | -------------------------------------------------------- | ----------------------------------------------------------------- |
| Which step is waiting?             | Custom status, pinned plan and Scheduler history         | Workflow Query, child history and Web UI                          |
| Was a decision sent or processed?  | Event history versus step result                         | Signal history or Update lifecycle/result                         |
| Which attempt failed?              | Activity failure/history and worker evidence             | Activity retry/timeout history and attempt-specific logs          |
| Why is work not being picked up?   | Worker connection, task hub, identity and backend checks | Task Queue pollers, queue latency, worker slots and server health |
| Did a code change break replay?    | Version-aware implementation and history investigation   | Replay stored histories with the proposed worker code             |
| Why is the backend slow?           | Azure service diagnostics and support                    | Frontend/History/Matching, persistence and visibility metrics     |
| Can an operator recover the flow?  | Supported management APIs/dashboard actions              | Cancel, terminate, reset and other authorized operations          |

Temporal's Web UI and CLI are operator tools, not a business approver inbox.
Queries and replay help investigation; attaching a debugger to production pods
is not the preferred first step. Replay locally with restricted histories and
stubbed external activities; never accidentally perform production side effects.
[MS-DIAGNOSTICS] [T-DEBUG] [T-PY-TEST]

### Metrics that must accompany the comparison

Collect application worker CPU/memory peaks, workflow completion latency,
activity attempts, failures and restart recovery. For Temporal also capture
Task Queue schedule-to-start latency, worker polling/slots, server errors/latency,
history growth, persistence errors/latency and database utilization.

Server, worker and persistence metrics are documented. Exporting application
traces alone is not sufficient to operate self-hosted Temporal. Keep metrics
labels low-cardinality; use correlation IDs in logs/traces, not every metrics
series. [T-MONITOR] [T-PRODUCTION] [T-PY-OBS]

## Enterprise considerations beyond the workflow code

### Identity, authorization and audit

Azure Workload Identity and Scheduler RBAC already authenticate our worker.
They do not enforce business role assignments: the console's Acting as selector
is still a simulation. [MS-IDENTITY]

Self-hosted Temporal offers opt-in TLS/mTLS and authorization integration through
ClaimMapper and Authorizer extension points. It does not automatically provide
an enterprise RBAC/control-plane/audit product by installing Helm.
Kubernetes RBAC alone does not authorize Temporal API calls. [T-SECURITY] [T-PRODUCTION]

For either platform, enterprise completion requires an authenticated approval API,
least-privilege operator access, separation of duties and an external,
idempotently written business audit record. Never expose a raw engine control
API as the public approval API.

### Retention and data handling

Scheduler retention is SKU-dependent: current documentation lists up to 30 days
for Consumption and 90 days for Dedicated. Temporal closed execution retention
is configured by namespace; retaining more history has storage and operational
cost. Neither workflow history nor application logs are automatically an immutable
business ledger. [MS-BILLING] [T-ARCHIVAL]

Temporal archival is documented as experimental. The documented built-in providers
do not establish an Azure Blob archival solution for us. Do not assume archival
provides our legal retention, purge or audit guarantees without implementation
and testing.

Keep policy versions and business audit data outside both engine schemas.
Reference large documents instead of embedding them in every message/history.
Index opaque client/account IDs rather than comments, secrets or personal data;
Visibility/Search Attributes need a separate data-exposure review.

### Availability, scaling and disaster recovery

The existing single worker proves restart recovery, not uninterrupted availability.
Microsoft's current billing/capacity guidance lists HA for Dedicated with three
capacity units, not Consumption. A production decision must qualify the chosen
Scheduler SKU rather than assume "managed" means every HA feature is enabled.
[MS-BILLING]

For Temporal, independently size application workers, Temporal services and the
database. More approval worker pods do not fix a slow persistence database.
Choose history shard count deliberately and plan HA replicas, disruption budgets,
anti-affinity, database recovery and server failover tests. [T-PRODUCTION]

An operator reset or workflow retry can re-run business activities. Neither
platform makes a database/payment/notification side effect exactly-once merely
because orchestration state is durable.

### Upgrades and support

Temporal server, persistence schemas, Helm chart, UI/CLI and application SDK are
separate upgrade surfaces. The server upgrade guide requires sequential minor
upgrades and release-specific schema checks. New server images are not always
compatible with old charts. Pin versions and rehearse upgrades under load.
[T-UPGRADE] [T-DEPLOY] [T-HELM]

Durable removes backend-server administration, but we still own worker code,
container/runtime updates, deterministic evolution and operational readiness.
Obtain the applicable Azure SLA/support terms and any self-hosted Temporal
support agreement explicitly; a product's maturity is not our deployment's SLA.

### Cost

Self-hosted Temporal Server and Python SDK are MIT-licensed. Temporal Cloud's
per-action bill is not applicable here. That does not make self-hosting free:
count server pods, PostgreSQL, storage/backup, monitoring, idle capacity,
upgrades and engineering/on-call ownership. [T-SERVER-LICENSE] [T-PY-LICENSE]

The Durable bill includes Scheduler plus AKS and telemetry. Consumption charges
are based on dispatched actions, including activity results and external events;
400 workflow instances or 700 logical activity calls do not give the complete
billable action count. [MS-BILLING]

Same-cluster experiments may use spare capacity, but production cost comparisons
must include allocated capacity for Temporal services and persistence.

## Proposed Temporal spike in this repository

Keep the engine-parity baseline unchanged. Evaluate the targeted authoring
and search scenario as an additional track, not as the whole E8 product.

1. Place the later Temporal implementation under `temporal/` with its own Python
   environment, dependencies, tests, worker image and deployment configuration.
2. Preserve the existing policy shapes and business semantics through JSON
   fixtures/contracts, not a Python dependency on the compiled C# projects.
3. Start with the same pinned policy/people resolution at submission. Preserve
   account-before-client precedence, including its documented risk; changing
   precedence or resolving people at stage start is a separate experiment.
4. Implement one reusable parent and reviewer children, the same activities,
   comments, outcomes, deadlines, start guards and cancellation records.
5. Use a separate thin loopback API/client for Temporal and preserve the same
   acceptance scenarios. Do not mix SDK code into the existing Durable worker.
6. Deploy the real Temporal Service, not `temporal server start-dev`, to a
   dedicated AKS namespace. Use one application worker for the baseline benchmark
   and record all backend/database resources separately.
7. For a contained spike, use PostgreSQL with persistent storage and separate
   execution/visibility databases. For enterprise qualification, select managed
   or HA PostgreSQL and rehearse backup/restore. A single in-cluster database is
   explicitly a spike-only availability design.
8. Add private service connectivity, resource requests/limits, namespace quotas
   and network policies before allowing the stack to share cluster capacity.
   Validate allocatable CPU, memory and pod/IP capacity first. Do not modify the
   Durable namespace, shared cluster settings or existing stores casually.
9. Reuse the observability destination where practical; separately evaluate
   tracing interceptor versus experimental plugin, replay and Worker Versioning.
10. Add the business-library and search experiments below after baseline parity.
    Apply equivalent requirements to both engines instead of attributing the
    custom editor or registry to Temporal itself.

The initial approval-parity track is implemented under
[temporal/](./temporal/README.md) on Docker Desktop. The AKS topology and resource
qualification above remain proposals. No AKS deployment occurred, and the
business-library/draft/publish track remains unimplemented.

## Tests and findings to record after implementation

### Common acceptance scenarios

| Scenario                              | Required evidence for both platforms                                            |
| ------------------------------------- | ------------------------------------------------------------------------------- |
| Single reviewer                       | Same reusable parent, one child, Approved result and one final apply            |
| Sequential plus parallel              | Either stage-1 reviewer may approve first; next stage waits for both            |
| Reject / Return / Timeout             | End chain, cancel waiting siblings, preserve prior decisions, skip later stages |
| Invalid decisions                     | Wrong actor/version/comment refused; original deadline unchanged                |
| Separation-of-duties conflict         | Submission refused before any workflow is scheduled                             |
| Policy edit during wait               | Existing execution keeps snapshot; new execution uses active version            |
| Worker restart mid-wait               | Same logical execution completes without losing accepted decisions              |
| Activity redelivery                   | Attempts visible; an idempotent test sink detects duplicate side effects        |
| Decision/cancel/timer race            | Deterministic outcome, no orphan reviewer and complete decision record          |
| Observability                         | Find execution, actor/comment, attempts and correlated traces after restart     |

### Business-library, search and versioning experiments

These are proposed tests, not additional completed features:

1. Browse a published template without changing any active selection.
2. Copy it into customer A's draft with a distinct ID and source reference;
   customer B's configuration must remain unchanged.
3. Change stage order/parallelism and, once the interpreter supports it, a
   condition such as the budget threshold for an extra reviewer.
4. Save draft v2 while chain A still waits on v1. Prove ordinary starts still
   select v1 until explicit activation.
5. Test that exact immutable draft with below/above/equal-threshold inputs,
   missing facts, invalid roles, reject/return/timeout paths and safe activities.
   Record the draft version/hash with the results; a later edit invalidates them.
6. Activate only the tested version; verify new chain B uses v2, A stays on v1,
   the worker image digest is unchanged and no application rollout occurred.
7. Roll back the active definition pointer for future starts without changing
   existing runs or their recorded decisions.
8. Search executions by customer, business step, definition version and
   business state through the browser API, with pagination and enforced scope.
   On Temporal, test Search Attribute updates/lag. On Durable, explicitly
   distinguish dashboard-only tags from SDK filters or a separate read model.
9. Separately deploy worker code v2 while a code-v1 execution waits and prove
   correct version routing and recovery. Do not count the no-deployment policy
   change as a worker-code-versioning test.

### Additional enterprise qualification

* Export a running history and replay it against compatible and incompatible
  worker code; document failure diagnostics.
* Deploy new worker code while old approvals wait; demonstrate safe routing,
  rollback and continued old-version availability.
* Restart a Temporal server component, then separately interrupt persistence.
  Record accepted messages, recovery time and data integrity.
* Restore persistence into an isolated environment and verify execution/Visibility
  consistency. A worker restart is not a database restore test.
* Use time-skipping for long-deadline logic tests, but also run a real persisted
  wait/restart test. Time-skipping does not prove days of infrastructure uptime.
* Test authorized/read-only/forbidden operator actions and exposure of stored
  payloads and Search Attributes.
* Measure sustained load, repeated bursts and peak resource usage, not only a
  one-off success count.

### Fair benchmark protocol

Repeat the exact 100-chain, two-stage, three-reviewer shape on both platforms.
Run them separately on the shared cluster so they do not compete for resources.
Use equal application-worker limits and capture the entire Temporal backend
footprint. Repeat warm/cold runs and report median/p95 completion and failures.

Record script-observed time separately from engine timestamps, polling intervals,
activity retries and injected delays. A Python-versus-C# run compares two complete
stacks, not a controlled language-independent engine benchmark. Native Updates
must not replace Signals in only one timed baseline.

### Current findings status

| Dimension                   | Durable evidence                                            | Self-hosted Temporal status                                                           |
| --------------------------- | ----------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| Business-rule parity        | Recorded acceptance passes                                  | 29 tests; local Docker acceptance passes                                              |
| Worker recovery             | Recorded AKS restart passes                                 | Container restart mid-wait passed                                                     |
| Backend/database recovery   | Not qualified by the current spike                          | Temporal Server restart passed; database restore not tested                           |
| 100-chain burst             | 15 seconds; 100 Approved, zero failures                     | Local Docker: 18.70 seconds, 100 Approved, zero failures; not a controlled comparison |
| Replay/debugging workflow   | Mocked tests and history inspection                         | Approved, Rejected and Returned histories replayed                                    |
| Safe code rollout           | SDK capability documented; not exercised                    | Documented tools; not exercised                                                       |
| Business template lifecycle | Partial policy editor; no clone/draft/test/publish flow     | Policy editor/version activation; no governed authoring lifecycle                     |
| Custom execution search     | Dashboard documented; pinned SDK query lacks tag predicates | Typed attributes, combined filters and pagination tested locally                      |
| Logs and traces             | Application Insights integration tested                     | JSON logs; correlated console/worker trace in local Jaeger                            |
| Enterprise security/audit   | Still incomplete                                            | Still requires design and implementation                                              |
| Operating cost/effort       | Managed backend; no complete TCO study                      | Additional responsibilities; not measured                                             |

Keep [the existing requirement findings](./spike/FINDINGS.md) as the application
coverage baseline. Changing engine does not deliver notifications, delegation,
materiality routing, a governed policy registry, real identity or an immutable
audit store by itself. Do not turn capability availability into a claim that an
entire analysis requirement is covered.

### Local implementation findings

The [implementation guide](./temporal/README.md) records the exact stack,
commands, image ID, results and limitations. Local acceptance completed at
2026-11-16T07:12:35Z and kept evidence under
`temporal/.build/evidence/acceptance-20261116071206-218f18`.

The SQL-backed server and application worker were independently restarted while
a reviewer waited. Both recovered; the original chain retained its policy v1
while a new chain used v2. A stage-2 rejection preserved the comment and prevented
stage 3 from being created. Combined client, business-step, policy-version and
business-outcome filtering worked through the Visibility API.

One application worker was limited to 0.5 CPU and 512 MiB. The local 100-chain
burst (300 decisions, 400 logical executions) had zero failures in 18.70 seconds,
with per-chain p50 17.64 seconds and p95 18.37 seconds. This used Docker Desktop
and an async HTTP harness with 0.5-second polling, not the same hardware,
backend placement or harness as the 15-second Durable AKS run. Neither number
establishes which engine is faster.

The implementation tests caught a cancellation race: a sibling can finish its
timeout before receiving the cancel Signal. The parent now handles the specific
closed-execution signal failure and preserves the sibling's own result. This is
an application-level concurrency consideration, not a Temporal durability failure.

No AKS deployment, production identity, immutable business audit, database
restore, controlled comparative benchmark or safe worker-code upgrade has been
qualified by the local result.

## Decision after the spike

Retain Durable if the same business outcomes are achieved with materially less
operational effort and the Azure-managed backend fits enterprise constraints.

Consider self-hosted Temporal if measured replay/debugging, messaging, deployment
controls or backend ownership/portability provide sufficient value to justify
the additional operational responsibility.

Neither decision should be based on a single 15-second burst, release numbers,
UI appearance or untested assumptions about platform maturity.

## Verified sources

Microsoft sources:

* [Standalone Durable Task SDK overview][MS-SDK]
* [Durable Task Scheduler architecture][MS-SCHEDULER]
* [Storage providers, standalone SDK versus Functions][MS-STORAGE]
* [Orchestration and worker versioning][MS-VERSION]
* [Pinned SDK version context][MS-VERSION-CONTEXT]
* [Pinned SDK worker versioning options][MS-VERSION-OPTIONS]
* [Scheduler dashboard search and filters][MS-DASHBOARD]
* [Scheduler tags and limitations][MS-TAGS]
* [Pinned SDK OrchestrationQuery fields][MS-QUERY]
* [Pinned SDK query serialization][MS-QUERY-GRPC]
* [Scheduler identity and RBAC][MS-IDENTITY]
* [Scheduler SKUs, capacity, actions and retention][MS-BILLING]
* [SDK diagnostics][MS-DIAGNOSTICS]
* [SDK OpenTelemetry tracing][MS-OTEL]
* [Durable .NET releases][MS-RELEASES]

Temporal sources:

* [Python SDK developer guide][T-PYTHON]
* [Python SDK source and supported capabilities][T-PY-README]
* [Official Python DSL interpreter sample][T-DSL]
* [Temporal Web UI and execution search][T-UI]
* [Search Attributes and data handling][T-SEARCH]
* [List Filter query operators][T-LIST]
* [Python messaging: Signals, Queries and Updates][T-MESSAGES]
* [Python cancellation semantics][T-CANCEL]
* [Python testing, time-skipping and replay][T-PY-TEST]
* [Python logs, metrics and tracing maturity][T-PY-OBS]
* [Python debugging][T-DEBUG]
* [Python Workflow sandbox][T-SANDBOX]
* [Worker Deployment Versioning and version minimums][T-VERSION]
* [Self-hosted deployment][T-DEPLOY]
* [Current Helm chart and persistence requirements][T-HELM]
* [SQL and other Visibility stores][T-VISIBILITY]
* [Self-hosted production checklist][T-PRODUCTION]
* [Self-hosted security integration][T-SECURITY]
* [Self-hosted monitoring][T-MONITOR]
* [Server and schema upgrade procedure][T-UPGRADE]
* [Self-hosted archival and experimental status][T-ARCHIVAL]
* [Server releases][T-SERVER-RELEASES]
* [Python releases][T-PY-RELEASES]
* [.NET releases][T-NET-RELEASES]
* [Server license][T-SERVER-LICENSE]
* [Python SDK license][T-PY-LICENSE]

[MS-SDK]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/durable-task-overview
[MS-SCHEDULER]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler
[MS-STORAGE]: https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-storage-providers?pivots=durable-task-sdks
[MS-VERSION]: https://learn.microsoft.com/en-us/azure/durable-task/common/durable-orchestration-versioning?pivots=durable-task-sdks
[MS-VERSION-CONTEXT]: https://github.com/microsoft/durabletask-dotnet/blob/v1.28.0/src/Abstractions/TaskOrchestrationContext.cs
[MS-VERSION-OPTIONS]: https://github.com/microsoft/durabletask-dotnet/blob/v1.28.0/src/Worker/Core/DurableTaskWorkerOptions.cs
[MS-DASHBOARD]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler-dashboard
[MS-TAGS]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler-tags
[MS-QUERY]: https://github.com/microsoft/durabletask-dotnet/blob/v1.28.0/src/Client/Core/OrchestrationQuery.cs
[MS-QUERY-GRPC]: https://github.com/microsoft/durabletask-dotnet/blob/v1.28.0/src/Client/Grpc/GrpcDurableTaskClient.cs
[MS-IDENTITY]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler-identity
[MS-BILLING]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler-billing
[MS-DIAGNOSTICS]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/durable-task-diagnostics
[MS-OTEL]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/durable-task-scheduler-opentelemetry-tracing
[MS-RELEASES]: https://github.com/microsoft/durabletask-dotnet/releases/tag/v1.28.0
[T-PYTHON]: https://docs.temporal.io/develop/python
[T-PY-README]: https://github.com/temporalio/sdk-python
[T-DSL]: https://github.com/temporalio/samples-python/tree/main/dsl
[T-UI]: https://docs.temporal.io/web-ui
[T-SEARCH]: https://docs.temporal.io/search-attribute
[T-LIST]: https://docs.temporal.io/list-filter
[T-MESSAGES]: https://docs.temporal.io/develop/python/workflows/message-passing
[T-CANCEL]: https://docs.temporal.io/develop/python/workflows/cancellation
[T-PY-TEST]: https://docs.temporal.io/develop/python/best-practices/testing-suite
[T-PY-OBS]: https://docs.temporal.io/develop/python/platform/observability
[T-DEBUG]: https://docs.temporal.io/develop/python/best-practices/debugging
[T-SANDBOX]: https://docs.temporal.io/develop/python/best-practices/python-sdk-sandbox
[T-VERSION]: https://docs.temporal.io/production-deployment/worker-deployments/worker-versioning
[T-DEPLOY]: https://docs.temporal.io/self-hosted-guide/deployment
[T-HELM]: https://github.com/temporalio/helm-charts/blob/main/README.md
[T-VISIBILITY]: https://docs.temporal.io/self-hosted-guide/visibility
[T-PRODUCTION]: https://docs.temporal.io/self-hosted-guide/production-checklist
[T-SECURITY]: https://docs.temporal.io/self-hosted-guide/security
[T-MONITOR]: https://docs.temporal.io/self-hosted-guide/monitoring
[T-UPGRADE]: https://docs.temporal.io/self-hosted-guide/upgrade-server
[T-ARCHIVAL]: https://docs.temporal.io/self-hosted-guide/archival
[T-SERVER-RELEASES]: https://github.com/temporalio/temporal/releases/tag/v1.32.2
[T-PY-RELEASES]: https://github.com/temporalio/sdk-python/releases
[T-NET-RELEASES]: https://github.com/temporalio/sdk-dotnet/releases
[T-SERVER-LICENSE]: https://github.com/temporalio/temporal/blob/main/LICENSE
[T-PY-LICENSE]: https://github.com/temporalio/sdk-python/blob/main/LICENSE
