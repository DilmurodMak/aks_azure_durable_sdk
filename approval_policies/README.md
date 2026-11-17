---
title: Saved approval policies
description: Local policy storage for the approval spike consoles.
ms.date: 2026-11-17
ms.topic: reference
---

The Local Console UI writes policy sets (versions and active selections) here:

* `local.json`: console connected to the emulator
* `azure.json`: local console connected to Azure Scheduler and the AKS worker

A policy is one set per client or account: an optional default chain for every business step
plus per-step overrides, versioned together. Saving changed content creates an immutable
version and activates it atomically. An unchanged save retains the existing version. Selected
policies are read-only until **Edit** is chosen; **Cancel** discards unsaved edits. Selecting an
existing version in the UI activates it for that client/account.
Running workflows retain their original resolved policy snapshots.

These generated files are ignored by Git and survive console restarts.
They contain required roles, stages, ordering, timeouts and optional
`budgetGreaterThan` thresholds, not people's names.
Demo role-to-user mappings are separate in [demo_settings/](../demo_settings/).
Do not edit policy files
while a console is writing; use the UI for version-safe changes.

See the [demo guide](../spike/README.md) for the setup and execution flow.

## USD budget routing

Every workflow start requires the submitted plan version's `budgetAmount` in
USD. A stage without `budgetGreaterThan` is unconditional; a stage with it runs
only when the submitted budget is strictly greater. Equality skips it. Each
chain needs at least one unconditional stage.

Amounts and thresholds must be non-negative, at most `999999999999.99` and have
at most two fractional digits. No currency conversion or general expressions
are supported. The evaluated facts, included/skipped reasons and executable
chain are pinned at start. Skipped roles are not resolved to people.

A threshold-only edit creates and activates a new version, but cannot change a
running workflow. The exact 7,500 / 5,000 / 10,000 scenario was verified on both
AKS engines; see the [fresh findings and rerun instructions](../spike/FINDINGS.md#fresh-aks-budget-routing-verification).
