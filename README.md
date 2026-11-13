---
title: Durable approval spike on AKS
description: Validate one durable approval locally with an emulator and in Azure with an AKS worker.
ms.date: 2026-11-13
ms.topic: overview
---

## What this spike is about

Prove that a standalone .NET Durable Task SDK worker can wait for a human
decision, survive worker replacement, and resume the **same approval instance**.
This uses one `PlanApproval` workflow, not the Azure Functions runtime.

The test request is "Sunny Soda, plan version 3." We check:

* Approval after replacing the worker pod
* Explicit rejection and no-response timeout, with no approval receipt
* Refusal of the wrong approver, self-approval, and stale plan versions
* In-flight activity redelivery, demonstrating why business updates need deduplication

## What runs where

| Location                    | Components                                                                                                                  |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Azure, provisioned by Bicep | AKS with two fixed nodes, ACR, VNet/NAT egress, managed identities/RBAC, and Durable Task Scheduler with task hub `default` |
| AKS, deployed separately    | One containerized approval worker using workload identity                                                                   |
| Laptop                      | Approval console/HTTP endpoints and optional CLI                                                                            |
| Docker on the laptop        | Scheduler emulator and its dashboard, using task hub `approval-local`                                                       |

```text
Local: HTTP file/buttons -> laptop console -> emulator       <-> laptop worker
Cloud: HTTP file/buttons -> laptop console -> Azure Scheduler <-> AKS worker
```

Only the worker is deployed to AKS. The demo console stays on your laptop.
Scheduler stores workflow state; the worker executes orchestrators and activities.

## Start here

| Guide                                   | Use it for                                                                            |
| --------------------------------------- | ------------------------------------------------------------------------------------- |
| [Demo and tests](spike/README.md)       | Local emulator demo, laptop-to-Azure demo, HTTP requests, dashboards, and pass checks |
| [Source code](src/README.md)            | Project map, build/test commands, HTTP/CLI interfaces, and worker image deployment    |
| [Azure infrastructure](infra/README.md) | Bicep provisioning, access configuration, costs, and cleanup                          |
| [HTTP requests](approvals.http)         | Start an approval, send a decision, and inspect its status/history                    |

## Demo pages

| URL                                                           | What you see                                                       |
| ------------------------------------------------------------- | ------------------------------------------------------------------ |
| <http://localhost:5080>                                       | Our approval console connected to the local emulator               |
| <http://localhost:8082>                                       | Microsoft's local Scheduler dashboard                              |
| <http://localhost:5081>                                       | Our laptop console connected to Azure Scheduler and the AKS worker |
| The console's **Open Scheduler dashboard** link in Azure mode | Microsoft's Azure dashboard, using your Entra sign-in              |

`localhost` and `127.0.0.1` both mean your laptop. These URLs work after
starting the services in the [demo guide](spike/README.md).
The local dashboard does **not** show Azure runs.

## Boundaries

This is a green-deployment spike, not a production approval service.
**Acting as** is simulated; `ApplyDecision` logs a stand-in receipt.
Real identity binding, duplicate-safe destination updates, policy chains,
Teams, audit storage, and production monitoring are not included.
