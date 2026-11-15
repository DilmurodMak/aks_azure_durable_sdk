---
title: Approval spike findings and requirement coverage
description: What step one proved on Azure Durable Task Scheduler and AKS, what it did not, which analysis requirements it covers, and the decisions still open.
ms.date: 2026-11-15
ms.topic: reference
---

## What step one had to prove

Durable Task Scheduler and the SDK on AKS can run one reusable approval and sign-off
workflow, driven by configuration, correctly and durably, and you can see it running.
It does not have to prove the whole product. Run instructions are in the
[demo guide](./README.md); this page records the result.

## Definition of done

One script, [test-step-one.sh](../scripts/test-step-one.sh), runs the whole story on AKS.
All six steps passed on 2026-11-15 (evidence `.build/evidence/step-one-aks2`).

| # | Step                                                                           | Result |
| - | ------------------------------------------------------------------------------ | ------ |
| 1 | Start a three-stage chain for a client account                                 | Pass   |
| 2 | Stage 1 has two parallel reviewers, approved in the opposite order             | Pass   |
| 3 | The worker pod is deleted while one reviewer is still pending                  | Pass   |
| 4 | Stage 2 rejects with a comment                                                 | Pass   |
| 5 | The chain ends `Rejected`; the stage 3 step instance is never created          | Pass   |
| 6 | Tags are on the dashboard data and one Application Insights trace shows the flow | Pass   |

The Azure dashboard page itself still needs a person to sign in; the script checks the
tags on the same instance data the dashboard reads, not the rendered page.

## What the spike proved

* **Long waits survive restarts.** A replacement pod was Ready in 3 to 5 seconds and the
  pending reviewer completed afterwards, in the step-one run and in `test-approval.sh`.
* **One reusable workflow, many configurations.** `ApprovalChain` has no approval rules;
  the versioned policy set supplies them. A single reviewer, a multi-stage sign-off and
  parallel reviewers are the same workflow. 34 business step codes can carry an override.
* **A rejection ends the chain.** The first reject, return or timeout ends the plan. The
  parent raises a `Cancel` event to the reviewers still waiting in that stage, so they
  finish as `Canceled`, and later stages never start. A reviewer who had already finished
  keeps their own outcome. If two reviewers end the plan at nearly the same time, the
  first one the workflow processes decides the status.
* **Every decision has who, what, when and why.** A comment is required to reject or
  return and is recommended otherwise. It is stored on the step result and written once
  by the `RecordDecision` activity (a structured log line and a span).
* **Return for changes is a third outcome.** It ends the chain like a rejection and sends
  the plan back with a comment.
* **Separation of duties is checked at start.** The start is refused (HTTP 409) if the
  requester holds a required role or one person would review in two stages. Before this
  change such a plan started and could only time out.
* **One correlation ID joins everything.** It is supplied or generated at start and is on
  the instance tags, the workflow input, the spans and the log lines, together with the
  account and who started the plan.
* **Traces reach Application Insights.** The SDK emits spans from the
  `Microsoft.DurableTask` source; the console and worker add `Approval.Spike` spans with
  the business tags. One trace crosses `approval-console` and `approval-worker`.
* **Scale is not a concern at this size.** 100 chains started together (300 decisions,
  400 workflow instances) all finished `Approved` in 15 seconds with no failures on one
  worker pod. The pod used 47 millicores and 120 MiB afterwards (evidence
  `.build/evidence/burst-20261115172259`).

## Observability lessons

* **Parent-based sampling silently dropped everything.** ASP.NET Core wraps each request
  in an unrecorded propagation-only activity. Spans started under it were dropped, and the
  worker inherited "not sampled" through the trace context, so nothing reached Application
  Insights. The first step-one run failed for this reason. The fix starts approval spans
  as new recorded roots. A unit test guards it.
* **Ingestion is not instant.** Spans took one to ten minutes to appear. The step-one
  script polls for up to eight minutes.
* **SDK spans carry no business tags.** `orchestration:*` and `activity:*` spans carry the
  instance ID, task name and status (`durabletask.task.*`), but no client, account or
  correlation ID. Find the trace through the tagged `approval.start` span, then read the
  whole trace by `operation_Id`:

```kusto
let spans = union requests, dependencies | where timestamp > ago(3h);
let roots = spans
  | where name == 'approval.start'
    and tostring(customDimensions['approval.correlationId']) == '<correlation id>'
  | distinct operation_Id;
spans | where operation_Id in (roots) | summarize count() by name, cloud_RoleName
```

* **Decisions start their own trace.** `approval.decision.sent` spans carry the same
  correlation ID but are separate traces. Join them by the tag.
* **Activity spans appear twice.** The SDK emits one span when the orchestration schedules
  an activity and one when it runs. That is by design.

## What the spike did not prove

* Waits longer than 24 hours. Validation caps a stage timeout at 1440 minutes.
* Scale beyond one worker pod, thousands of instances, or hundreds of thousands of
  timers. The burst test is a point measurement, not a soak test.
* What happens to a decision record after the scheduler purges its history.
* A duplicate-safe business write. `ApplyDecision` still runs at least once.
* The Azure dashboard UI as a rendered page (needs a browser sign-in).
* Real sign-in, role enforcement, or authorization. "Acting as" is a simulation.
* One person holding two roles inside the same stage. It is allowed and the person must
  decide twice.

## Requirements covered and not covered

The analysis tags each requirement with the capabilities it needs. 544 of the 908
requirements carry at least one of 32 tags (1,518 tag uses in total). Counts below are
requirements per tag; one requirement can have several tags. By capability, step one
covers 7 of 32, partly covers 6 and does not cover 19. By tag use that is 294 covered,
509 partly and 715 not covered.

| Capability (analysis tag) | Requirements | Step one | How |
| ------------------------- | ------------ | -------- | --- |
| Audit trail (`audit_trail`) | 213 | Partly | Each step records who, what, when and why (step result, `RecordDecision` log line and span). Nothing is append-only: scheduler history is purged. |
| Explicit status model (`status_model`) | 157 | Partly | Step states `Waiting`, `Approved`, `Rejected`, `Returned`, `Timeout`, `Canceled`, `NotStarted`, plus workflow status. Not the wider business status model. |
| Decision pinned to exact version (`version_pin`) | 105 | Covered | Plan version travels with every decision and a stale version is refused. The chain is pinned when the workflow starts. |
| Rules decide the path (`conditional_routing`) | 103 | Not covered | A chain is a fixed list of stages. A step override swaps the whole chain; there are no rules. |
| Notify the person who must act (`notify`) | 93 | Not covered | No email or Teams. The Approvals tab is the only inbox. |
| Confirm, lock, immutable version (`lock_version`) | 84 | Not covered | The workflow never locks or confirms the business plan. |
| Flags dependents of a change (`impact_propagation`) | 82 | Not covered | No dependents are flagged when a plan or policy changes. |
| Work queue / inbox (`task_queue`) | 65 | Partly | The Approvals tab lists every step. There is no per-user inbox, filtering or assignment. |
| Comments & decision log (`comments_decisions`) | 65 | Covered | Comment on every decision, required to reject or return. Stored with who and when on the step. |
| Evidence attached (`evidence_attach`) | 57 | Not covered | No attachments. |
| AI step (`ai_step`) | 53 | Not covered | Out of scope. |
| Timers & expiry (`timer_expiry`) | 47 | Covered | One durable timer per step, at most 24 hours. A timeout ends the chain. Longer waits are not accepted. |
| Started or reopened by an event (`event_trigger`) | 44 | Not covered | Workflows start only from the console or API. Nothing reopens them. |
| Vendor / partner takes part (`external_participant`) | 37 | Not covered | No vendor or partner participation. |
| Decision targets / SLA (`sla`) | 31 | Partly | A timeout per stage. No SLA targets, breach reporting or pause rules. |
| Recurring schedule (`schedule`) | 30 | Not covered | No recurring workflows. |
| No self-approval / segregation of duties (`sod`) | 28 | Covered | The requester cannot hold a required role and one person cannot review in two stages (refused at start). Self-approval is also refused at decision time. |
| Publishes a milestone event (`event_publish`) | 26 | Not covered | No milestone events are published. |
| Destination confirms before done (`receipt_confirm`) | 25 | Partly | `ApplyDecision` is a stand-in that returns a receipt. It runs at least once and is not duplicate-safe. |
| Reminders & escalation (`reminder_escalation`) | 25 | Not covered | No reminders or escalation. |
| Configurable approver chains / matrix (`chain_config`) | 24 | Covered | Versioned policy sets per client or account with a default chain and per-step overrides. |
| Delegate / reassign (`delegation`) | 21 | Not covered | No reassign or out-of-office path. |
| Durable workflow / trace ID (`trace_id`) | 19 | Covered | A correlation ID, OpenTelemetry traces in Application Insights and dashboard tags. |
| Retries without duplicates (`retry_idempotent`) | 18 | Partly | Workflows survive worker restarts and replays. The business write is at-least-once. |
| Client takes part (`client_participant`) | 14 | Not covered | A client approver is only a simulated role. No client sign-in. |
| Compensation / rollback (`compensation_rollback`) | 13 | Not covered | Nothing is undone after a rejection. |
| Materiality decides re-approval (`materiality`) | 11 | Not covered | No materiality-based re-approval. |
| Route by amount or size (`threshold_branch`) | 10 | Not covered | No routing by amount. |
| Digest / throttling (`digest_throttle`) | 7 | Not covered | Not applicable without notifications. |
| Parallel steps (`parallel_steps`) | 6 | Covered | Reviewers in one stage run in parallel; stages run in order. |
| Enterprise ticketing sync (`external_ticketing`) | 3 | Not covered | No ticketing integration. |
| Bulk actions (`bulk_actions`) | 2 | Not covered | No bulk approve. |

### The approval requirements we have cited

| Requirement | Acceptance criterion | Step one |
| ----------- | -------------------- | -------- |
| `PS056.2` Approve or reject the submitted tactical plan | Approve, reject, or request changes | Covered: Approve, Reject, Return for changes |
| `PS056.3` | Approval unlocks the plan downstream | Partly: the stand-in `ApplyDecision` only |
| `PS056.4` | Rejection returns the plan with comments and reverts it to editable | Partly: the chain ends `Rejected` or `Returned` with the comment; the plan itself is not reverted |
| `PS056.5` | Decision logged with timestamp, approver, comments | Covered in the workflow output and `RecordDecision`; not in a durable store |
| `PS056.6` | The confirmer cannot approve their own submission | Covered at start and at decision time |
| `PS056.7` | Delegation when the approver is out of office | Not covered |
| `CP019.1` Configurable approval chains | Single- or multi-step | Covered |
| `CP019.2` | Branch by budget size | Not covered |
| `CP019.3` | Reassign a step owner without breaking the audit trail | Not covered |
| `PS060.1` Configure the Gate 2 approval chain | Configurable per client or account | Covered |
| `PS060.2` | Roles enforced by User Administration | Not covered: roles are simulated |
| `PS060.3` | Optional client approval step per client | Partly: add or remove the client role in a stage; no on/off toggle |
| `PS060.4` | Chain changes logged with who and when, without altering recorded decisions | Partly: versions are immutable and running workflows are pinned; the author is not recorded |
| `CP020.1` Audit trail on approvals | Actor, timestamp, decision, comments, immutable | Partly: recorded, not immutable or outside the scheduler |
| `CP105` Client configuration registry | Version, promote and roll back as a unit, effective dates, impact preview | Partly: immutable versions and an active pointer; no effective dates, impact preview or approval-to-publish |

## Decisions to record

These are decisions, not code. Step one deliberately leaves them open.

| Decision | State today | Recommendation |
| -------- | ----------- | -------------- |
| Where policy resolution lives | The console resolves the chain and the people at start and pins a snapshot into the workflow input. | Keep the chain resolved at start so its structure stays pinned. Resolve people when each stage starts, through an activity that records who was chosen. That is what makes reassign and out-of-office possible. |
| Where the audit trail lives | The scheduler's history is the only record, and the scheduler purges history. `RecordDecision` writes a log line and a span. | Write each decision to an append-only store outside the scheduler. Make the `RecordDecision` activity the single writer and idempotent per instance and step, because activities run at least once. |
| Policy precedence | Narrower owner first: account, then client, then `agency-default`. Inside one owner an override beats its default. An account default therefore beats a client override for the same step, and can silently drop a step the client requires. | Rank by specificity at every level (account override, client override, account default, client default, agency override, agency default) and let a client mark stages as required so no narrower owner can remove them. Needs your confirmation because it changes behavior you approved earlier. |
| Policy store | Local JSON files per console. | A relational store as the system of record, with immutable versions, one active pointer per scope and a change log. Keep the file store for development only. |

## Left for step two and later

* Policy lookup inside the engine and resolving people when a stage starts
* Reassign and delegation
* Notifications, reminders and escalation
* Withdraw and supersede
* An audit store, an inbox view, threshold branching and auto-approve
* Draft versus publish for policies, and recording who saved them
* Real Entra identity, and timeouts longer than 24 hours
* Application Insights ingestion with Entra authentication instead of a connection string
