---
title: Demo identity settings
description: Simulator-only role assignments kept outside approval policy configuration.
ms.date: 2026-11-13
ms.topic: reference
---

## Simulator storage

The **Identities** tab (step 1) writes scoped role-to-user mappings here:

* `local.json`: emulator-connected console
* `azure.json`: Azure-connected local console

These are ignored runtime files, separate from [approval policies](../approval_policies/).
Changing an assignment does not create a policy version. New workflow starts
resolve reviewers from these settings; already-running workflows retain their
resolved reviewer snapshot.

This simulates User Administration only. In production, a role service owns
assignments and a validated sign-in supplies the acting person's identity.
Being signed in does not by itself authorize an approval.
