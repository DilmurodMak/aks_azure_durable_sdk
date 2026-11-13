---
title: Deploy the AKS Durable Task SDK spike infrastructure
description: Bicep deployment instructions for an isolated AKS, registry, managed identity, restricted network and Durable Task Scheduler stack.
ms.date: 2026-11-13
ms.topic: how-to
---

## What this deploys

Deploy a new isolated infrastructure stack for the standalone .NET Durable Task
SDK spike. This is not a Functions-runtime deployment.

| Component          | Configuration                                                                                     |
| ------------------ | ------------------------------------------------------------------------------------------------- |
| AKS                | Free control-plane tier, two fixed Ubuntu system nodes, Entra/Azure RBAC, local accounts disabled |
| Container registry | Standard SKU; admin credentials and anonymous pulls disabled; kubelet receives AcrPull            |
| Network            | Dedicated VNet/subnet, NAT gateway and static outbound IPv4 address                               |
| Scheduler          | Durable Task Scheduler Consumption SKU and a `default` task hub                                   |
| Worker identity    | User-assigned managed identity, federated to the SDK worker service account                       |
| Operator access    | AKS RBAC Cluster Admin on this cluster; Durable Task Data Contributor on this task hub            |

Scheduler and the AKS API server accept only the supplied client IPv4 addresses
and the stack's NAT egress address, each as a `/32`. Authentication is still
required. Nodes have no individual public IPs. The registry uses its public,
authenticated endpoint; it does not have a private endpoint or IP firewall here.

> [!IMPORTANT]
> Bicep provisions Azure resources and permissions. It does not build the SDK
> application, create Kubernetes namespaces/service accounts, deploy worker pods,
> configure KEDA, or validate the approval journey. Those are subsequent steps.
> Two running nodes, NAT/public IP, registry, and Scheduler usage incur charges.
> The Teams approval extension below is architecture and deployment planning
> only; its bot, callback service, and additional resources are not created.

## Files and source

* [main.bicep](./main.bicep): subscription deployment and dedicated resource group
* [main.bicepparam](./main.bicepparam): inputs read from your shell environment
* [stack.bicep](./stack.bicep): identity, module wiring, and workload federation
* [types.bicep](./types.bicep): shared parameter and output contracts
* [modules/](./modules): network, AKS, registry, and Scheduler resources
* [LICENSE](./LICENSE): Microsoft sample's MIT notice

Adapted from Microsoft's [AKS getting-started guide][quickstart] and
[Bicep sample][sample], inspected at revision `7152d82`.

Intentional differences from the sample:

* Restrict public access instead of allowing all source IPs.
* Add fixed NAT egress so the worker's Scheduler access can be allowlisted.
* Require a currently supported Kubernetes version rather than copying an old default.
* Enable managed Entra/Azure RBAC and disable local cluster accounts.
* Grant the worker Durable Task Worker at task-hub scope; grant the interactive
  operator Data Contributor separately.
* Keep node counts fixed; no node or worker autoscaling is configured.

## Prerequisites

Use an approved non-production subscription, with permission to create resource
groups, resources, and role assignments. For example, Owner or the appropriate
Contributor plus access-administration roles at the required scopes.

Install current Azure CLI and Bicep v0.48.1 or later. For the post-deployment
cluster checks, also install `kubectl` and `kubelogin`.

```bash
az version
az bicep upgrade
az bicep version
az login
```

Provider registration, resource validation, and deployment commands below
contact Azure. Run them yourself only in the intended subscription.

## 1. Select the subscription, region and Kubernetes version

```bash
cd /Users/1989deny/AKS_DURABLE_FUNC

export AZURE_SUBSCRIPTION_ID='<your-subscription-id>'
export AZURE_ENV_NAME='durable-spike'
export AZURE_LOCATION='<region-supporting-both-services>'

az account set --subscription "$AZURE_SUBSCRIPTION_ID"
az account show --query '{subscription:name,id:id,tenant:tenantId}' --output table

az provider show --namespace Microsoft.DurableTask \
  --query "resourceTypes[?resourceType=='schedulers'].locations | [0]" --output table

az aks get-versions --location "$AZURE_LOCATION" --output table
```

Choose a supported, non-preview Kubernetes version from your region's output.
Check regional VM availability, subscription quota, and organization policy.
The default node size is `Standard_D4s_v5`; change it in the parameters file
if your approved test environment requires another supported size.

Register these providers if they are not already registered and you have approval:

```bash
for provider in Microsoft.ContainerService Microsoft.ContainerRegistry \
  Microsoft.Network Microsoft.ManagedIdentity Microsoft.DurableTask; do
  az provider register --namespace "$provider" --wait
done
```

## 2. Supply the deployment inputs

Replace the placeholders before running these commands. `AZURE_CLIENT_IP` must
be the public IPv4 address seen by Azure for your approved network or VPN exit,
not a private laptop address. Supply the address only, without `/32` or another
CIDR suffix. The template constructs exact-host allowlist entries.

```bash
export AKS_KUBERNETES_VERSION='<supported-version-from-the-previous-step>'
export AZURE_CLIENT_IP='<your-public-ipv4-address>'
export AZURE_PRINCIPAL_ID="$(az ad signed-in-user show --query id --output tsv)"
export AZURE_PRINCIPAL_TYPE='User'
```

`AZURE_PRINCIPAL_ID` is the Entra object ID, not an application/client ID.
If deploying for a group or service principal, supply its object ID explicitly
and set `AZURE_PRINCIPAL_TYPE` to `Group` or `ServicePrincipal`; do not use the
signed-in-user command for a non-user identity.

Optional: set `AZURE_RESOURCE_GROUP`; otherwise the group is
`${AZURE_ENV_NAME}-rg`. Keep these values stable when updating the same stack.
For several authorized client addresses, edit `allowedClientIpAddresses` in
[main.bicepparam](./main.bicepparam) to include additional raw IPv4 addresses.

Missing required environment variables cause parameter compilation to fail.
No subscription IDs, credentials, or operator IDs are hard-coded into the files.

## 3. Compile locally

These commands compile templates; they do not provision resources.

```bash
mkdir -p infra/.build

az bicep build \
  --file infra/main.bicep \
  --outfile infra/.build/main.json

az bicep build-params \
  --file infra/main.bicepparam \
  --outfile infra/.build/main.parameters.json
```

The build folder is ignored. Do not commit generated parameter/output files
containing environment-specific identifiers.

## 4. Validate and inspect the deployment plan

```bash
export DEPLOYMENT_NAME="${AZURE_ENV_NAME}-infra"

az deployment sub validate \
  --location "$AZURE_LOCATION" \
  --template-file infra/.build/main.json \
  --parameters @infra/.build/main.parameters.json

az deployment sub what-if \
  --location "$AZURE_LOCATION" \
  --template-file infra/.build/main.json \
  --parameters @infra/.build/main.parameters.json
```

Review the resource group, network/IP restrictions, role assignments, selected
Kubernetes version, and cost-bearing resources. Stop on errors or unexpected
changes. Validation is not a guarantee of quota/capacity or policy acceptance
when the real deployment runs.

## 5. Deploy the services

This step creates billable Azure resources.

```bash
az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$AZURE_LOCATION" \
  --template-file infra/.build/main.json \
  --parameters @infra/.build/main.parameters.json
```

Names and role-assignment IDs are deterministic for the same environment and
resource group. Redeploy after changing your authorized client IP. Removing
or replacing a principal may require explicitly removing old role assignments;
incremental ARM deployment does not automatically delete them.

## 6. Inspect outputs and verify infrastructure

```bash
az deployment sub show --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.configuration.value --output json

export RESOURCE_GROUP="$(az deployment sub show --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.configuration.value.resourceGroupName --output tsv)"

export AKS_CLUSTER_NAME="$(az deployment sub show --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.configuration.value.clusterName --output tsv)"

az aks get-credentials \
  --resource-group "$RESOURCE_GROUP" \
  --name "$AKS_CLUSTER_NAME"

kubelogin convert-kubeconfig --login azurecli
kubectl get nodes

az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" \
  --query '{oidc:oidcIssuerProfile.enabled,workloadIdentity:securityProfile.workloadIdentity.enabled,localAccountsDisabled:disableLocalAccounts,outboundType:networkProfile.outboundType}' \
  --output json
```

Expect the configured node count, OIDC/workload identity enabled, local accounts
disabled, and `userAssignedNATGateway` egress. Inspect Scheduler's `ipAllowlist`
in the portal or ARM output: it must contain only your client `/32` entries and
the NAT `/32`. RBAC assignments can take time to propagate; do not broaden roles
or remove IP restrictions to bypass a propagation or connectivity issue.

No worker pods are expected yet. Healthy nodes and a successful deployment
are infrastructure checks, not a passed SDK approval test.

## 7. Deploy and test the SDK worker

Use these fields from the deployment's `configuration` output:

| Worker setting       | Output field               |
| -------------------- | -------------------------- |
| `ENDPOINT`           | `schedulerEndpoint`        |
| `TASKHUB`            | `taskHubName`              |
| `AZURE_CLIENT_ID`    | `workloadIdentityClientId` |
| Kubernetes namespace | `workloadNamespace`        |
| Service account      | `workerServiceAccountName` |
| Image registry       | `registryLoginServer`      |

The Kubernetes service account must have the annotation
`azure.workload.identity/client-id` set to the worker's client ID, and the worker
pod template needs the label `azure.workload.identity/use: "true"`.
Create the namespace and service account in the application manifests; their
names must match the federated credential created here.

The worker receives only Durable Task Worker on this task hub. The interactive
operator receives Durable Task Data Contributor for starting/querying instances
and sending approval events. Do not assume the worker can submit client requests.
Image publishing/build permissions must also be available to the chosen build
identity; AcrPull on the kubelet only authorizes pulling images.

The [Sunny Soda spike runbook](../spike/README.md) contains the worker/client,
container build, Kubernetes deployment, and three acceptance runs: approval
after pod replacement, timeout, and stale-version rejection.
The AKS worker uses `Authentication=WorkloadIdentity`; the laptop client uses
`Authentication=AzureCli`. Neither uses stored credentials or an implicit
emulator fallback.

## 8. Planned Teams approval-card extension

Use a code-owned Teams bot as the approval interface while the Durable SDK
workflow remains the coordinator. No AI model, Power Automate, or Logic Apps
is required. Start with one assigned reviewer in a personal bot chat.

```text
SDK workflow starts a request and waits durably
  -> notification activity asks the Teams bot to send an approval card
  -> assigned reviewer selects Approve or Reject
  -> authenticated bot callback validates reviewer and request state
  -> persist the decision and signal the waiting SDK instance
  -> workflow resumes; bot updates the card to a read-only outcome
```

The card can display the request title, requester, current status, optional
comment, and Approve/Reject buttons. Use Adaptive Card `Action.Execute` actions
and update the original message after a decision. A hidden or disabled button
is not authorization; the backend must enforce the assigned reviewer.
See Microsoft's [approval-card example][teams-cards].

### Additional infrastructure and tenant prerequisites

These are requirements for a later implementation, not resources deployed by
the current templates.

| Requirement                               | Purpose and remaining work                                                                                                                                                                             |
| ----------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Teams app package and bot registration    | Register an approved bot identity and Teams channel; package/install the app for the pilot reviewer. Tenant app policies and registration permissions must allow it.                                   |
| Bot/API workload on AKS                   | Implement proactive card delivery, authenticated action handling, and SDK event signaling using a currently supported Teams/Agents SDK.                                                                |
| Public application HTTPS endpoint         | Provide an approved hostname, TLS certificate, and ingress routing to the bot messaging endpoint. This is separate from the Kubernetes administration endpoint.                                        |
| Callback workload identity                | Grant the callback service the task-hub data access needed to signal instances. The existing worker-only identity is not a client/signaling identity.                                                  |
| Durable approval and conversation records | Persist request/instance correlation, assigned reviewer, status, decision evidence, and Teams conversation/message references. Choose/provision this store separately; no such store is included here. |

Keep the AKS administration API and Scheduler allowlists unchanged. Microsoft
Teams/Bot Service calls the application HTTPS endpoint; the callback service
reaches Scheduler through the existing AKS NAT egress.

Before provisioning the extension, obtain the real tenant/bot identity settings,
approved HTTPS endpoint, pilot reviewer's directory identity, app-installation
approval, and selected record store. Do not put credentials in the README,
card payload, repository, or returned workflow status.

### Callback and workflow controls

1. Validate incoming platform authentication using the supported SDK, then
   validate the tenant and trusted responder identity against the assigned reviewer.
2. Resolve the request server-side using an opaque request ID; validate its
   pending status and applicable object/version context. Do not trust actor or
   outcome fields supplied in card data as authorization.
3. Persist one accepted decision and make recording/signaling repeat-safe.
   Duplicate clicks, retries, or competing responses must not create another
   decision or repeat a completed business action.
4. Signal the correct waiting SDK instance, then update the card to show the
   accepted outcome. A successful card update alone is not workflow completion.
5. On timeout, mark the request expired and refresh the card. Reject late
   decisions explicitly; never turn a timeout into automatic approval.

If persisting a decision and signaling Scheduler cannot be one transaction,
use a durable retry/reconciliation handoff rather than losing the decision
between those steps. The Teams card is a view, not the source of truth.

### Deployment and validation sequence

1. Complete the base SDK/AKS deployment and console approval tests first.
2. Resolve the tenant, bot identity, endpoint/TLS, data store, and separate
   callback identity requirements. Extend IaC only after those choices are known.
3. Implement and test the bot/card handlers locally with Agents Playground,
   then configure the approved Teams app and bot messaging endpoint.
4. Install the app for one pilot reviewer, retain the conversation/message
   references, and send a Pending approval card.
5. Test Approve, Reject, timeout, unauthorized responder, duplicate click,
   late response, worker replacement, and card-refresh failure. Verify backend
   decision/state and SDK history, not only what the message displays.

Microsoft 365 tenant approval and a working Teams callback are separate from
successful Azure infrastructure provisioning. No Teams integration tests have
been run by creating this package.

## Cost and cleanup

This is a spike configuration, not a production availability design. Review
backup/recovery, retention, support, network policy, upgrade policy, monitoring,
and availability requirements separately.

Inspect the resource group before cleanup. Delete only the isolated test group
created by this deployment, after checking that it contains no shared resources:

```bash
az resource list --resource-group "$RESOURCE_GROUP" --output table
az group delete --name "$RESOURCE_GROUP"
```

Group deletion is destructive and prompts for confirmation. Do not substitute
an existing/shared resource group. Verify deletion and final billing separately.

## Public references

* [Official SDK-on-AKS quickstart][quickstart]
* [Original Bicep infrastructure sample][sample]
* [Durable Task Scheduler identity and task-hub roles][identity]
* [AKS user-assigned NAT gateway][nat]
* [AKS workload identity][workload-identity]
* [Human Interaction SDK sample][approval]
* [Teams approval cards and updated views][teams-cards]
* [Teams extension for the Microsoft 365 Agents SDK][teams-sdk]

[quickstart]: https://learn.microsoft.com/en-us/azure/durable-task/sdks/quickstart-aks-durable-task-scheduler
[sample]: https://github.com/Azure-Samples/Durable-Task-Scheduler/tree/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/scenarios/DocumentProcessingOnAKS/infra
[identity]: https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler-identity
[nat]: https://learn.microsoft.com/en-us/azure/aks/nat-gateway
[workload-identity]: https://learn.microsoft.com/en-us/azure/aks/workload-identity-overview
[approval]: https://github.com/Azure-Samples/Durable-Task-Scheduler/tree/7152d82c656576b9cda47d33ac2de4f61aacff66/samples/durable-task-sdks/dotnet/HumanInteraction
[teams-cards]: https://learn.microsoft.com/en-us/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/up-to-date-views
[teams-sdk]: https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/teams/teams-extension
