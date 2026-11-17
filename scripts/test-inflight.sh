#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME.}"
: "${ENDPOINT:?Set ENDPOINT to the Azure Scheduler endpoint.}"
: "${TASKHUB:?Set TASKHUB to its task hub.}"
: "${SCHEDULER_AUTH:?Use the AzureCli client identity.}"
if [ "$SCHEDULER_AUTH" != AzureCli ] || [ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]; then
  printf 'ERROR: Use the approved AKS context and AzureCli client, not a local Azure worker.\n' >&2
  exit 2
fi
instance="${1:-console-demo-inflight-$(date -u +%Y%m%d%H%M%S)}"
if [[ ! "$instance" =~ ^console-demo-inflight-[a-zA-Z0-9-]{1,59}$ ]]; then
  printf 'ERROR: Use a new console-demo-inflight- instance ID.\n' >&2
  exit 2
fi
evidence=".build/evidence/$instance"
if [ -e "$evidence" ]; then printf 'ERROR: Evidence already exists.\n' >&2; exit 2; fi
mkdir -p "$evidence"
base="http://127.0.0.1:${CONSOLE_PORT:-5081}"
child="$instance-s1-r1"
policy_id="inflight-policy-$(date -u +%Y%m%d%H%M%S)-$$"
client() { "${DOTNET:-dotnet}" src/Approval.Client/bin/Release/net10.0/Approval.Client.dll "$@"; }
pods() { kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike "$@"; }
post() {
  curl --fail-with-body --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' \
    --data "$2" "$base$1"
}
curl --fail --silent --show-error --max-time 60 "$base/api/environment" > "$evidence/console-environment.json"
if ! jq -e --arg endpoint "$ENDPOINT" --arg taskHub "$TASKHUB" \
    '.environment=="AZURE" and .endpoint==$endpoint and .taskHub==$taskHub' \
    "$evidence/console-environment.json" >/dev/null; then
  printf 'ERROR: Console must use the same Azure Scheduler endpoint and task hub as the client.\n' >&2
  exit 2
fi
# Set the simulator actor independently; policies contain only reviewer roles and stages.
post /api/demo-identities "$(jq -nc --arg client "$policy_id" \
  '{clientId:$client,accountId:null,assignments:{Finance:"finance"}}')" > "$evidence/identities.json"
post /api/policies "$(jq -nc --arg id "$policy_id" \
  '{id:$id,clientId:$id,baseVersion:0,
    defaultStages:[{name:"finance",roles:["Finance"],timeoutMinutes:5}],overrides:[]}')" > "$evidence/policy.json"
post /api/workflows "$(jq -nc --arg id "$instance" --arg client "$policy_id" \
  '{instanceId:$id,clientId:$client,workflowCode:"B07",planId:"sunny-soda",version:3,
    requester:"requester",budgetAmount:7500,applyDecisionDelaySeconds:30}')" > "$evidence/start.json"
jq -e --arg id "$instance" --arg policy "$policy_id" \
  '.instanceId==$id and .policyId==$policy and .policyVersion==1' "$evidence/start.json" >/dev/null
deadline=$((SECONDS+60))
while [ "$SECONDS" -lt "$deadline" ]; do
  if ! client status --instance "$child" --history > "$evidence/child-waiting.json"; then sleep 1; continue; fi
  if jq -e '.runtimeStatus=="Running" and .customStatus.state=="Waiting" and .result==null' \
      "$evidence/child-waiting.json" >/dev/null; then break; fi
  sleep 1
done
jq -e '.runtimeStatus=="Running" and .customStatus.state=="Waiting" and .result==null and
  ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
  ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0' \
  "$evidence/child-waiting.json" >/dev/null
pods get pods --selector app.kubernetes.io/name=approval-worker --output json > "$evidence/pods-before.json"
jq -e '.items|length==1' "$evidence/pods-before.json" >/dev/null
old="$(jq -r '.items[0].metadata.name' "$evidence/pods-before.json")"
client decide --instance "$child" --approver finance --version 3 --outcome Approve > "$evidence/decision.json"
deadline=$((SECONDS+30))
attempt_seen=false
while [ "$SECONDS" -lt "$deadline" ]; do
  pods logs "$old" > "$evidence/attempt-before.log"
  if grep -F "ApplyDecision attempt instance=$instance receipt=sunny-soda:v3:approved delaySeconds=30" \
      "$evidence/attempt-before.log" >/dev/null; then
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
jq -e --arg id "$instance" --arg child "$child" '
  .instanceId==$id and .runtimeStatus=="Completed" and .result.status=="Approved" and
  .result.version==3 and .result.receipt=="sunny-soda:v3:approved" and
  (.result.steps|length)==1 and .result.steps[0].instanceId==$child and
  .result.steps[0].role=="Finance" and .result.steps[0].status=="Approved" and
  any(.history[]; .eventType=="SubOrchestrationInstanceCreated" and .name=="ApprovalStep") and
  any(.history[]; .eventType=="SubOrchestrationInstanceCompleted") and
  ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==1
' "$evidence/status.json" >/dev/null
client status --instance "$child" --history > "$evidence/child-history.json"
jq -e '
  .runtimeStatus=="Completed" and .result.status=="Approved" and
  any(.history[]; .eventType=="TaskScheduled" and .name=="RecordSubmission") and
  any(.history[]; .eventType=="EventRaised" and .name=="Decision") and
  any(.history[]; .eventType=="ExecutionCompleted") and
  ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
  ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0
' "$evidence/child-history.json" >/dev/null
pods get pods --selector app.kubernetes.io/name=approval-worker --output json > "$evidence/pods-after.json"
jq -e '.items|length==1' "$evidence/pods-after.json" >/dev/null
new="$(jq -r '.items[0].metadata.name' "$evidence/pods-after.json")"
if [ "$old" = "$new" ]; then printf 'ERROR: Pod was not replaced.\n' >&2; exit 1; fi
pods logs "$new" > "$evidence/attempt-after.log"
grep -F "ApplyDecision attempt instance=$instance receipt=sunny-soda:v3:approved delaySeconds=30" \
  "$evidence/attempt-after.log" >/dev/null
if grep -Eiq 'unauthenticated|permissiondenied|statuscode[:= ]+(401|403)' \
    "$evidence/attempt-before.log" "$evidence/attempt-after.log"; then
  printf 'ERROR: Authentication/authorization errors appear in the worker logs.\n' >&2
  exit 1
fi
printf 'PASS: parent %s final receipt activity redelivered on %s and %s; child has no receipt. This proves at-least-once attempts, NOT a duplicate-safe business update.\n' "$instance" "$old" "$new"
