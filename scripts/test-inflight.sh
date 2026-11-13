#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME.}"
: "${SCHEDULER_AUTH:?Use the AzureCli client identity.}"
if [ "$SCHEDULER_AUTH" != AzureCli ] || [ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]; then
  printf 'ERROR: Use the approved AKS context and AzureCli client, not a local Azure worker.\n' >&2
  exit 2
fi
instance="${1:-console-demo-inflight-$(date -u +%Y%m%d%H%M%S)}"
if [[ ! "$instance" =~ ^console-demo-inflight-[a-zA-Z0-9-]{1,60}$ ]]; then
  printf 'ERROR: Use a new console-demo-inflight- instance ID.\n' >&2
  exit 2
fi
evidence=".build/evidence/$instance"
if [ -e "$evidence" ]; then printf 'ERROR: Evidence already exists.\n' >&2; exit 2; fi
mkdir -p "$evidence"
client() { "${DOTNET:-dotnet}" src/Approval.Client/bin/Release/net10.0/Approval.Client.dll "$@"; }
pods() { kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike "$@"; }
client start --instance "$instance" --plan sunny-soda --version 3 \
  --requester requester --approver finance --timeout-minutes 5 --apply-delay-seconds 30 > "$evidence/start.json"
deadline=$((SECONDS+60))
while [ "$SECONDS" -lt "$deadline" ]; do
  client status --instance "$instance" > "$evidence/status.json"
  if jq -e '.customStatus=="Waiting for Finance"' "$evidence/status.json" >/dev/null; then break; fi
  sleep 1
done
jq -e '.customStatus=="Waiting for Finance"' "$evidence/status.json" >/dev/null
pods get pods --selector app.kubernetes.io/name=approval-worker --output json > "$evidence/pods-before.json"
jq -e '.items|length==1' "$evidence/pods-before.json" >/dev/null
old="$(jq -r '.items[0].metadata.name' "$evidence/pods-before.json")"
client decide --instance "$instance" --approver finance --version 3 --outcome Approve > "$evidence/decision.json"
deadline=$((SECONDS+30))
attempt_seen=false
while [ "$SECONDS" -lt "$deadline" ]; do
  pods logs "$old" > "$evidence/attempt-before.log"
  if grep -F "$instance" "$evidence/attempt-before.log" | grep -F 'sunny-soda:v3:approved' >/dev/null; then
    attempt_seen=true
    break
  fi
  sleep 1
done
if [ "$attempt_seen" != true ]; then printf 'ERROR: No in-flight attempt observed; not deleting any pod.\n' >&2; exit 1; fi
# Force loss mid-activity; graceful deletion can let the activity finish and does not prove redelivery.
pods delete pod "$old" --grace-period=0 --force --wait=true
pods rollout status deployment/approval-worker --timeout=180s
deadline=$((SECONDS+300))
while [ "$SECONDS" -lt "$deadline" ]; do
  client status --instance "$instance" --history > "$evidence/status.json"
  if jq -e '.runtimeStatus=="Completed"' "$evidence/status.json" >/dev/null; then break; fi
  sleep 3
done
jq -e '.result.status=="Approved" and .result.receipt=="sunny-soda:v3:approved"' "$evidence/status.json" >/dev/null
pods get pods --selector app.kubernetes.io/name=approval-worker --output json > "$evidence/pods-after.json"
new="$(jq -r '.items[0].metadata.name' "$evidence/pods-after.json")"
if [ "$old" = "$new" ]; then printf 'ERROR: Pod was not replaced.\n' >&2; exit 1; fi
pods logs "$new" > "$evidence/attempt-after.log"
grep -F "$instance" "$evidence/attempt-after.log" | grep -F 'sunny-soda:v3:approved' >/dev/null
printf 'PASS: receipt attempt observed on %s and %s for %s. This proves at-least-once attempts, NOT a duplicate-safe business update.\n' "$old" "$new" "$instance"
