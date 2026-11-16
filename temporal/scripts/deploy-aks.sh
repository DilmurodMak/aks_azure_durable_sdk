#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
: "${AKS_CLUSTER_NAME:?Source env/azure.env first.}"
if [[ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]]; then
  printf 'Current kubectl context does not match the approved cluster.\n' >&2
  exit 2
fi
image="${TEMPORAL_IMAGE:-$(cat temporal/.build/azure-image.txt)}"
if [[ ! "$image" =~ ^[a-z0-9.-]+\.azurecr\.io/temporal-approval@sha256:[a-f0-9]{64}$ ]]; then
  printf 'Use an immutable temporal-approval image digest from the approved ACR.\n' >&2
  exit 2
fi
resource_group="$(az deployment sub show --name "${DEPLOYMENT_NAME:-durable-spike-infra}" \
  --query properties.outputs.configuration.value.resourceGroupName -o tsv)"
configuration="$(az deployment group show -g "$resource_group" -n temporal-infra \
  --query properties.outputs.configuration.value -o json)"
host="$(jq -er '.postgresHost' <<< "$configuration")"
provisioner="$(jq -er '.provisionerClientId' <<< "$configuration")"
vault="$(jq -er '.keyVaultName' <<< "$configuration")"
kube() { kubectl --context "$AKS_CLUSTER_NAME" --namespace temporal-spike "$@"; }
wait_job() {
  local name="$1" deadline=$((SECONDS+300)) result
  while [ "$SECONDS" -lt "$deadline" ]; do
    result="$(kube get job "$name" -o json)"
    if jq -e 'any(.status.conditions[]?; .type=="Complete" and .status=="True")' <<< "$result" >/dev/null; then
      return
    fi
    if jq -e 'any(.status.conditions[]?; .type=="Failed" and .status=="True")' <<< "$result" >/dev/null; then
      kube logs "job/$name" --all-containers=true --tail=20
      printf 'Job %s failed; deployment stopped.\n' "$name" >&2
      return 1
    fi
    sleep 3
  done
  kube get pods
  kube logs "job/$name" --all-containers=true --tail=20
  printf 'Job %s did not complete within 300 seconds.\n' "$name" >&2
  return 1
}
kubectl --context "$AKS_CLUSTER_NAME" apply -f temporal/deploy/aks-foundation.yaml
kube create configmap temporal-config --dry-run=client -o yaml \
  --from-literal=DB=postgres12 --from-literal=DB_PORT=5432 \
  --from-literal=POSTGRES_SEEDS="$host" --from-literal=POSTGRES_USER=temporal_app \
  --from-literal=SQL_TLS_ENABLED=true --from-literal=SQL_HOST_VERIFICATION=true \
  --from-literal=SQL_HOST_NAME="$host" --from-literal=SQL_CA=/etc/ssl/certs/ca-certificates.crt \
  --from-literal=BIND_ON_IP=0.0.0.0 --from-literal=NUM_HISTORY_SHARDS=128 \
  --from-literal=SQL_MAX_CONNS=5 --from-literal=SQL_MAX_IDLE_CONNS=5 \
  --from-literal=SQL_VIS_MAX_CONNS=3 --from-literal=SQL_VIS_MAX_IDLE_CONNS=3 \
  --from-literal=PROMETHEUS_ENDPOINT=0.0.0.0:9090 --from-literal=LOG_LEVEL=info \
  | kube apply -f -
kube create configmap temporal-setup --dry-run=client -o yaml \
  --from-file=temporal/deploy/bootstrap.sql --from-file=temporal/deploy/schema-azure.sh \
  --from-file=temporal/deploy/dynamic.yaml | kube apply -f -
# Precreate named secrets; the one-shot identity can patch only these two.
for name in temporal-db temporal-db-bootstrap; do
  if ! kube get secret "$name" >/dev/null 2>&1; then
    kube create secret generic "$name"
  fi
done
secrets_manifest="$(mktemp)"
trap 'rm -f "$secrets_manifest"' EXIT
sed -e "s|__TEMPORAL_IMAGE__|$image|g" \
  -e "s|__PROVISIONER_CLIENT_ID__|$provisioner|g" -e "s|__KEY_VAULT__|$vault|g" \
  temporal/deploy/aks-secrets.yaml > "$secrets_manifest"
kube delete job temporal-secrets --ignore-not-found --wait
kube apply -f "$secrets_manifest"
wait_job temporal-secrets
kube logs job/temporal-secrets
kube delete job temporal-secrets --wait
rm -f "$secrets_manifest"
trap - EXIT
# The administrator credential is present only for the database role bootstrap.
cleanup_bootstrap() { kube delete secret temporal-db-bootstrap --ignore-not-found; }
trap cleanup_bootstrap EXIT
kube delete job temporal-db-bootstrap --ignore-not-found --wait
kube apply -f temporal/deploy/aks-bootstrap.yaml
wait_job temporal-db-bootstrap
kube logs job/temporal-db-bootstrap
kube delete job temporal-db-bootstrap --wait
cleanup_bootstrap
trap - EXIT
kube delete job temporal-schema --ignore-not-found --wait
kube apply -f temporal/deploy/aks-schema.yaml
wait_job temporal-schema
kube apply -f temporal/deploy/aks-server.yaml
kube rollout status deployment/temporal-server --timeout=300s
kube apply -f temporal/deploy/aks-tools.yaml
manifest="$(mktemp)"
trap 'rm -f "$manifest"' EXIT
sed "s|__TEMPORAL_IMAGE__|$image|g" temporal/deploy/aks-app.yaml > "$manifest"
kube delete job temporal-namespace --ignore-not-found --wait
kube apply --dry-run=server -f "$manifest"
kube apply -f "$manifest"
wait_job temporal-namespace
for deployment in temporal-worker temporal-console temporal-ui; do
  kube rollout status "deployment/$deployment" --timeout=240s
done
kube get pods,services,pvc
printf 'ClusterIP services only. Use loopback kubectl port-forward for browser access.\n'
