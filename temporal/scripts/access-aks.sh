#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
: "${AKS_CLUSTER_NAME:?Source env/azure.env first.}"
case "${1:-}" in
  console) service=temporal-console; ports=5091:5090 ;;
  ui) service=temporal-ui; ports=8089:8080 ;;
  *) printf 'Usage: bash temporal/scripts/access-aks.sh console|ui\n' >&2; exit 2 ;;
esac
if [[ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]]; then
  printf 'Current kubectl context does not match the approved AKS cluster.\n' >&2
  exit 2
fi
printf 'Keep this terminal open. Access: http://127.0.0.1:%s\n' "${ports%%:*}"
exec kubectl --context "$AKS_CLUSTER_NAME" --namespace temporal-spike \
  port-forward --address 127.0.0.1 "service/$service" "$ports"
