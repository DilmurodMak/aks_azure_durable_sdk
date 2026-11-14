---
title: Saved approval policies
description: Local policy storage for the approval spike consoles.
ms.date: 2026-11-13
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
They contain required roles, stages, ordering, and timeouts, not people's names.
Demo role-to-user mappings are separate in [demo_settings/](../demo_settings/).
Do not edit policy files
while a console is writing; use the UI for version-safe changes.

See the [demo guide](../spike/README.md) for the setup and execution flow.
