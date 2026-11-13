#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME to the approved spike cluster.}"
: "${ENDPOINT:?Set ENDPOINT to the Azure Scheduler endpoint.}"
: "${TASKHUB:?Set TASKHUB to its task hub.}"
: "${SCHEDULER_AUTH:?Set SCHEDULER_AUTH=AzureCli for the local client.}"
if [ "$SCHEDULER_AUTH" != AzureCli ] ||
   [ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]; then
  printf 'ERROR: Use the local AzureCli identity and the approved Kubernetes context.\n' >&2
  exit 2
fi

prefix="${1:-sunny-soda-v3}"
if [[ ! "$prefix" =~ ^[a-zA-Z0-9-]{1,60}$ ]]; then
  printf 'ERROR: Test prefix must contain 1-60 ASCII letters, digits, or hyphens.\n' >&2
  exit 2
fi
dotnet="${DOTNET:-dotnet}"
base="http://127.0.0.1:${CONSOLE_PORT:-5081}"
evidence=".build/evidence/$prefix"
if [ -e "$evidence" ]; then
  printf 'ERROR: Evidence already exists at %s. Use a fresh prefix; history is not overwritten.\n' "$evidence" >&2
  exit 2
fi
mkdir -p "$evidence"
policy_prefix="approval-policy-$(date -u +%Y%m%d%H%M%S)-$$"
policy_number=0

client() {
  "$dotnet" src/Approval.Client/bin/Release/net10.0/Approval.Client.dll "$@"
}

post() {
  curl --fail-with-body --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' \
    --data "$2" "$base$1"
}

curl --fail --silent --show-error --max-time 60 "$base/api/environment" > "$evidence/console-environment.json"
if ! jq --exit-status --arg endpoint "$ENDPOINT" --arg taskHub "$TASKHUB" \
    '.environment=="AZURE" and .endpoint==$endpoint and .taskHub==$taskHub' \
    "$evidence/console-environment.json" >/dev/null; then
  printf 'ERROR: Console must use the same Azure Scheduler endpoint and task hub as the client.\n' >&2
  exit 2
fi

wait_for() {
  local instance="$1" expected="$2" deadline=$((SECONDS + 180)) status
  while [ "$SECONDS" -lt "$deadline" ]; do
    if ! status="$(client status --instance "$instance")"; then sleep 2; continue; fi
    printf '%s\n' "$status" > "$evidence/$instance.status.json"
    if [ "$expected" = Waiting ]; then
      if jq --exit-status '.runtimeStatus == "Running" and .customStatus.state == "Waiting" and .result == null' \
          <<< "$status" >/dev/null; then
        return
      fi
    elif jq --exit-status --arg expected "$expected" \
        '.runtimeStatus == "Completed" and .result.status == $expected and .result.version == 3' \
        <<< "$status" >/dev/null; then
      return
    fi
    if jq --exit-status '.runtimeStatus != "Pending" and .runtimeStatus != "Running"' \
        <<< "$status" >/dev/null; then
      printf 'ERROR: Unexpected terminal result while waiting for %s:\n%s\n' "$expected" "$status" >&2
      return 1
    fi
    sleep 2
  done
  printf 'ERROR: %s did not reach %s within 180 seconds.\n' "$instance" "$expected" >&2
  return 1
}

start() {
  policy_number=$((policy_number+1))
  local policy_id="$policy_prefix-$policy_number"
  # Set actors in the separate simulator for this test's client scope.
  post /api/demo-identities "$(jq -nc --arg client "$policy_id" \
    '{clientId:$client,accountId:null,assignments:{Finance:"finance"}}')" > "$evidence/$1.identities.json"
  post /api/policies "$(jq -nc --arg id "$policy_id" --argjson minutes "$2" \
    '{id:$id,clientId:$id,workflowCode:"Gate2",baseVersion:0,
      stages:[{name:"finance",roles:["Finance"],timeoutMinutes:$minutes}]}')" > "$evidence/$1.policy.json"
  post /api/workflows "$(jq -nc --arg id "$1" --arg client "$policy_id" \
    '{instanceId:$id,clientId:$client,workflowCode:"Gate2",planId:"sunny-soda",version:3,requester:"requester"}')" \
    > "$evidence/$1.start.json"
  jq --exit-status --arg id "$1" --arg policy "$policy_id" \
    '.instanceId==$id and .policyId==$policy and .policyVersion==1' "$evidence/$1.start.json" >/dev/null
  wait_for "$1-s1-r1" Waiting
  cp "$evidence/$1-s1-r1.status.json" "$evidence/$1-s1-r1.waiting.json"
  client status --instance "$1" --history > "$evidence/$1.waiting.json"
  jq --exit-status --arg id "$1" '
    .instanceId==$id and .runtimeStatus=="Running" and .result==null and
    ([.history[]|select(.eventType=="SubOrchestrationInstanceCreated" and .name=="ApprovalStep")]|length)==1
  ' "$evidence/$1.waiting.json" >/dev/null
}

main="$prefix-001"
timeout="$prefix-timeout-001"
stale="$prefix-stale-001"
started="$SECONDS"
start "$main" 15
waiting_seconds=$((SECONDS - started))

kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike get pods \
  --selector app.kubernetes.io/name=approval-worker --output json > "$evidence/pods-before.json"
jq --exit-status '.items | length == 1' "$evidence/pods-before.json" >/dev/null
old_pod="$(jq --raw-output '.items[0].metadata.name' "$evidence/pods-before.json")"
kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike \
  logs "$old_pod" > "$evidence/worker-before.log"
restarted="$SECONDS"
kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike \
  delete pod "$old_pod" --wait=true --timeout=90s

deadline=$((SECONDS + 180))
replacement_ready=false
while [ "$SECONDS" -lt "$deadline" ]; do
  kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike get pods \
    --selector app.kubernetes.io/name=approval-worker --output json > "$evidence/pods-after.json"
  if jq --exit-status --arg old "$old_pod" \
      '[.items[] | select(.metadata.name != $old and .metadata.deletionTimestamp == null) |
        select(any(.status.conditions[]?; .type == "Ready" and .status == "True"))] | length == 1' \
      "$evidence/pods-after.json" >/dev/null; then
    replacement_ready=true
    break
  fi
  sleep 2
done
if [ "$replacement_ready" != true ]; then
  printf 'ERROR: No Ready replacement worker appeared within 180 seconds.\n' >&2
  exit 1
fi
new_pod="$(jq --raw-output '.items[] | select(.metadata.deletionTimestamp == null) | .metadata.name' "$evidence/pods-after.json")"
replacement_seconds=$((SECONDS - restarted))
wait_for "$main-s1-r1" Waiting
cp "$evidence/$main-s1-r1.status.json" "$evidence/$main-s1-r1.after-replacement.json"
client status --instance "$main" --history > "$evidence/$main.after-replacement.json"
jq --exit-status --arg id "$main" --slurpfile before "$evidence/$main.waiting.json" '
  .instanceId==$id and .createdAt==$before[0].createdAt and .runtimeStatus=="Running" and .result==null and
  ([.history[]|select(.eventType=="SubOrchestrationInstanceCreated" and .name=="ApprovalStep")]|length)==1
' "$evidence/$main.after-replacement.json" >/dev/null
decided="$SECONDS"
client decide --instance "$main-s1-r1" --approver finance --version 3 --outcome Approve > "$evidence/$main-s1-r1.decision.json"
wait_for "$main" Approved
resume_seconds=$((SECONDS - decided))
client status --instance "$main" --history > "$evidence/$main.history.json"

start "$timeout" 2
timeout_started="$SECONDS"
wait_for "$timeout" Timeout
timeout_seconds=$((SECONDS - timeout_started))
client status --instance "$timeout" --history > "$evidence/$timeout.history.json"
client status --instance "$timeout-s1-r1" --history > "$evidence/$timeout-s1-r1.history.json"
jq --exit-status '
  def epoch: sub("\\+00:00$"; "Z") | sub("\\.[0-9]+Z$"; "Z") | fromdateiso8601;
  [.history[] | select(.eventType == "TimerCreated")][0] as $timer |
  (($timer.fireAt | epoch) - ($timer.timestamp | epoch)) == 120 and
  (.lastUpdatedAt | epoch) >= ($timer.fireAt | epoch)
' "$evidence/$timeout-s1-r1.history.json" >/dev/null

start "$stale" 2
client decide --instance "$stale-s1-r1" --approver finance --version 2 --outcome Approve > "$evidence/$stale-s1-r1.decision.json"
deadline=$((SECONDS+60))
while [ "$SECONDS" -lt "$deadline" ]; do
  client status --instance "$stale-s1-r1" --history > "$evidence/$stale-s1-r1.refused.json"
  if jq --exit-status '
      .runtimeStatus == "Running" and .customStatus.state == "Waiting" and
      any(.history[]; .eventType == "EventRaised" and .name == "Decision")
    ' "$evidence/$stale-s1-r1.refused.json" >/dev/null; then break; fi
  sleep 2
done
jq --exit-status '
  .runtimeStatus == "Running" and .customStatus.state == "Waiting" and .result == null and
  any(.history[]; .eventType == "EventRaised" and .name == "Decision") and
  ([.history[] | select(.eventType == "TaskScheduled" and .name == "ApplyDecision")] | length == 0) and
  ([.history[] | select(.eventType == "TimerCreated")] | length == 1)
' "$evidence/$stale-s1-r1.refused.json" >/dev/null
client decide --instance "$stale-s1-r1" --approver finance --version 3 --outcome Approve > "$evidence/$stale-s1-r1.valid-decision.json"
wait_for "$stale" Approved
client status --instance "$stale" --history > "$evidence/$stale.history.json"
for instance in "$main" "$stale"; do
  jq --exit-status '
    [.history[] |
      if .eventType=="TaskScheduled" then .eventType+":"+.name else .eventType end] as $events |
    ["ExecutionStarted", "SubOrchestrationInstanceCreated", "SubOrchestrationInstanceCompleted",
      "TaskScheduled:ApplyDecision", "ExecutionCompleted"] as $expected |
    .result.receipt=="sunny-soda:v3:approved" and
    ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==1 and
    (reduce $events[] as $event (0;
      if . < ($expected|length) and $event==$expected[.] then .+1 else . end))==($expected|length)
  ' "$evidence/$instance.history.json" >/dev/null
  client status --instance "$instance-s1-r1" --history > "$evidence/$instance-s1-r1.history.json"
  jq --exit-status '
    [.history[] |
      if .eventType=="TaskScheduled" or .eventType=="EventRaised"
      then .eventType+":"+.name else .eventType end] as $events |
    ["ExecutionStarted", "TaskScheduled:RecordSubmission", "TimerCreated",
      "EventRaised:Decision", "ExecutionCompleted"] as $expected |
    (reduce $events[] as $event (0;
      if . < ($expected|length) and $event==$expected[.] then .+1 else . end))==($expected|length)
  ' "$evidence/$instance-s1-r1.history.json" >/dev/null
done
jq --exit-status \
  '.result.receipt==null and ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0' \
  "$evidence/$timeout.history.json" >/dev/null
for instance in "$main" "$timeout" "$stale"; do
  jq --exit-status --arg child "$instance-s1-r1" '
    (.result.steps|length)==1 and .result.steps[0].instanceId==$child and
    .result.steps[0].role=="Finance" and .result.steps[0].status==.result.status
  ' "$evidence/$instance.history.json" >/dev/null
  jq --exit-status --slurpfile parent "$evidence/$instance.history.json" \
    '.runtimeStatus=="Completed" and .result.status==$parent[0].result.status and
      .result.version==3 and .result.receipt==null and
      any(.history[]; .eventType=="TaskScheduled" and .name=="RecordSubmission") and
      any(.history[]; .eventType=="ExecutionCompleted") and
      ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
      ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0' \
    "$evidence/$instance-s1-r1.history.json" >/dev/null
done

kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike \
  logs "$new_pod" > "$evidence/worker-after.log"
if grep -Eiq 'unauthenticated|permissiondenied|statuscode[:= ]+(401|403)' \
    "$evidence/worker-before.log" "$evidence/worker-after.log"; then
  printf 'ERROR: Authentication/authorization errors appear in the worker logs.\n' >&2
  exit 1
fi
grep --fixed-strings "ApplyDecision attempt instance=$main receipt=sunny-soda:v3:approved" \
  "$evidence/worker-after.log" >/dev/null
jq --null-input \
  --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg instance "$main" --arg oldPod "$old_pod" --arg newPod "$new_pod" \
  --arg image "$(jq --raw-output '.items[0].status.containerStatuses[0].imageID' "$evidence/pods-after.json")" \
  --argjson waitingSeconds "$waiting_seconds" --argjson replacementSeconds "$replacement_seconds" \
  --argjson resumeSeconds "$resume_seconds" --argjson timeoutSeconds "$timeout_seconds" \
  '{completedAt: $completedAt, instanceId: $instance, oldPod: $oldPod, newPod: $newPod, image: $image,
    waitingSeconds: $waitingSeconds, podReadySeconds: $replacementSeconds,
    resumeSeconds: $resumeSeconds, timeoutObservedSeconds: $timeoutSeconds,
    restartApproval: "Pass", timeout: "Pass", staleVersion: "Pass",
    receipt: "sunny-soda:v3:approved", historyOrder: "Pass",
    note: "Saved Finance policies start workflows through the Azure Console. Pod replacement preserves the parent; children own decisions/timers, only approved parents apply receipts. Pod Ready is not Scheduler-connected; resumeSeconds measures real decision processing."}' > "$evidence/summary.json"
cat "$evidence/summary.json"
