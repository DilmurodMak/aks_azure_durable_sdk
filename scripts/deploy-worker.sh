#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME from the Bicep deployment outputs.}"
: "${WORKER_IMAGE:?Set WORKER_IMAGE to the registry image with an immutable sha256 digest.}"
: "${AZURE_CLIENT_ID:?Set AZURE_CLIENT_ID from workloadIdentityClientId.}"
: "${ENDPOINT:?Set ENDPOINT from schedulerEndpoint.}"
: "${TASKHUB:?Set TASKHUB from taskHubName.}"

if [[ ! "$WORKER_IMAGE" =~ ^[a-z0-9.-]+\.azurecr\.io/approval-worker@sha256:[a-f0-9]{64}$ ]] ||
   [[ ! "$AZURE_CLIENT_ID" =~ ^[a-fA-F0-9-]{36}$ ]] ||
   [[ ! "$ENDPOINT" =~ ^https://[a-zA-Z0-9.-]+$ ]] ||
   [[ ! "$TASKHUB" =~ ^[a-zA-Z0-9_-]+$ ]]; then
  printf 'ERROR: Invalid image digest, identity, endpoint, or task hub. No changes applied.\n' >&2
  exit 2
fi

if [ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]; then
  printf 'ERROR: Current Kubernetes context does not match AKS_CLUSTER_NAME.\n' >&2
  exit 2
fi

manifest="$(mktemp)"
trap 'rm -f "$manifest"' EXIT
sed \
  -e "s|__WORKER_IMAGE__|$WORKER_IMAGE|g" \
  -e "s|__AZURE_CLIENT_ID__|$AZURE_CLIENT_ID|g" \
  -e "s|__ENDPOINT__|$ENDPOINT|g" \
  -e "s|__TASKHUB__|$TASKHUB|g" \
  deploy/worker.yaml > "$manifest"

# A namespace must exist before server-side validation can validate its children.
kubectl --context "$AKS_CLUSTER_NAME" create namespace durable-spike --dry-run=client --output yaml |
  kubectl --context "$AKS_CLUSTER_NAME" apply --filename -
kubectl --context "$AKS_CLUSTER_NAME" apply --dry-run=server --filename "$manifest"
kubectl --context "$AKS_CLUSTER_NAME" apply --filename "$manifest"
kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike \
  rollout status deployment/approval-worker --timeout=180s
kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike \
  logs deployment/approval-worker --tail=40
