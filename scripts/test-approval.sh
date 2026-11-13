#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME to the approved spike cluster.}"
: "${ENDPOINT:?Set ENDPOINT to the Azure Scheduler endpoint.}"
: "${TASKHUB:?Set TASKHUB to its task hub.}"
: "${SCHEDULER_AUTH:?Set SCHEDULER_AUTH=AzureCli for the laptop client.}"
if [ "$SCHEDULER_AUTH" != AzureCli ] ||
   [ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]; then
  printf 'ERROR: Use the laptop AzureCli identity and the approved Kubernetes context.\n' >&2
  exit 2
fi

prefix="${1:-sunny-soda-v3}"
if [[ ! "$prefix" =~ ^[a-zA-Z0-9-]{1,60}$ ]]; then
  printf 'ERROR: Test prefix must contain 1-60 ASCII letters, digits, or hyphens.\n' >&2
  exit 2
fi
dotnet="${DOTNET:-dotnet}"
evidence=".build/evidence/$prefix"
if [ -e "$evidence" ]; then
  printf 'ERROR: Evidence already exists at %s. Use a fresh prefix; history is not overwritten.\n' "$evidence" >&2
  exit 2
fi
mkdir -p "$evidence"

client() {
  "$dotnet" src/Approval.Client/bin/Release/net10.0/Approval.Client.dll "$@"
}

wait_for() {
  local instance="$1" expected="$2" deadline=$((SECONDS + 180)) status
  while [ "$SECONDS" -lt "$deadline" ]; do
    status="$(client status --instance "$instance")"
    printf '%s\n' "$status" > "$evidence/$instance.status.json"
    if [ "$expected" = Waiting ]; then
      if jq --exit-status '.runtimeStatus == "Running" and .customStatus == "Waiting for Finance" and .result == null' \
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
  client start --instance "$1" --plan sunny-soda --version 3 \
    --requester requester --approver finance --timeout-minutes "$2" > "$evidence/$1.start.json"
  wait_for "$1" Waiting
  cp "$evidence/$1.status.json" "$evidence/$1.waiting.json"
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
wait_for "$main" Waiting
cp "$evidence/$main.status.json" "$evidence/$main.after-replacement.json"
decided="$SECONDS"
client decide --instance "$main" --approver finance --version 3 --outcome Approve > "$evidence/$main.decision.json"
wait_for "$main" Approved
resume_seconds=$((SECONDS - decided))
client status --instance "$main" --history > "$evidence/$main.history.json"
jq --exit-status '.result.receipt == "sunny-soda:v3:approved"' "$evidence/$main.history.json" >/dev/null
jq --exit-status '
  [.history[] |
    if .eventType == "TaskScheduled" or .eventType == "EventRaised"
    then .eventType + ":" + .name else .eventType end] as $events |
  ["ExecutionStarted", "TaskScheduled:RecordSubmission", "TimerCreated",
   "EventRaised:Decision", "TaskScheduled:ApplyDecision", "ExecutionCompleted"] as $expected |
  (reduce $events[] as $event (0;
    if . < ($expected | length) and $event == $expected[.] then . + 1 else . end)) == ($expected | length)
' "$evidence/$main.history.json" >/dev/null

start "$timeout" 2
timeout_started="$SECONDS"
wait_for "$timeout" Timeout
timeout_seconds=$((SECONDS - timeout_started))
client status --instance "$timeout" --history > "$evidence/$timeout.history.json"
jq --exit-status '
  def epoch: sub("\\+00:00$"; "Z") | sub("\\.[0-9]+Z$"; "Z") | fromdateiso8601;
  [.history[] | select(.eventType == "TimerCreated")][0] as $timer |
  (($timer.fireAt | epoch) - ($timer.timestamp | epoch)) == 120 and
  (.lastUpdatedAt | epoch) >= ($timer.fireAt | epoch)
' "$evidence/$timeout.history.json" >/dev/null

start "$stale" 2
client decide --instance "$stale" --approver finance --version 2 --outcome Approve > "$evidence/$stale.decision.json"
wait_for "$stale" RejectedStaleVersion
client status --instance "$stale" --history > "$evidence/$stale.history.json"
for instance in "$timeout" "$stale"; do
  jq --exit-status \
    '.result.receipt == null and ([.history[] | select(.eventType == "TaskScheduled" and .name == "ApplyDecision")] | length == 0)' \
    "$evidence/$instance.history.json" >/dev/null
done

kubectl --context "$AKS_CLUSTER_NAME" --namespace durable-spike \
  logs "$new_pod" > "$evidence/worker-after.log"
if grep -Eiq 'unauthenticated|permissiondenied|statuscode[:= ]+(401|403)' \
    "$evidence/worker-before.log" "$evidence/worker-after.log"; then
  printf 'ERROR: Authentication/authorization errors appear in the worker logs.\n' >&2
  exit 1
fi
grep --fixed-strings 'sunny-soda:v3:approved' "$evidence/worker-after.log" >/dev/null
jq --null-input \
  --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg instance "$main" --arg oldPod "$old_pod" --arg newPod "$new_pod" \
  --arg image "$(jq --raw-output '.items[0].status.containerStatuses[0].imageID' "$evidence/pods-after.json")" \
  --argjson waitingSeconds "$waiting_seconds" --argjson replacementSeconds "$replacement_seconds" \
  --argjson resumeSeconds "$resume_seconds" --argjson timeoutSeconds "$timeout_seconds" \
  '{completedAt: $completedAt, instanceId: $instance, oldPod: $oldPod, newPod: $newPod, image: $image,
    waitingSeconds: $waitingSeconds, replacementSeconds: $replacementSeconds,
    resumeSeconds: $resumeSeconds, timeoutObservedSeconds: $timeoutSeconds,
    restartApproval: "Pass", timeout: "Pass", staleVersion: "Pass",
    receipt: "sunny-soda:v3:approved", historyOrder: "Pass"}' > "$evidence/summary.json"
cat "$evidence/summary.json"
