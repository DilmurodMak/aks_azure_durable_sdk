#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
base="${1:-http://127.0.0.1:5080}"
prefix="${2:-console-demo-http-$(date -u +%Y%m%d%H%M%S)}"
if [[ ! "$base" =~ ^http://127\.0\.0\.1:508[01]$ ]] ||
   [[ ! "$prefix" =~ ^console-demo-[a-zA-Z0-9-]{1,58}$ ]]; then
  printf 'ERROR: Use loopback console port 5080/5081 and a fresh console-demo- prefix.\n' >&2
  exit 2
fi
evidence=".build/evidence/$prefix"
if [ -e "$evidence" ]; then printf 'ERROR: Evidence path already exists.\n' >&2; exit 2; fi
mkdir -p "$evidence"
policy_prefix="console-policy-$(date -u +%Y%m%d%H%M%S)-$$"
policy_number=0
post() {
  curl --fail-with-body --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' \
    --data "$2" "$base$1"
}
status() {
  curl --fail --silent --show-error --max-time 60 "$base/api/approvals/$1?history=true"
}
wait_for() {
  local id="$1" expected="$2" events="${3:-0}" deadline=$((SECONDS+60))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if [ "$expected" = Waiting ]; then
      if ! status "$id" > "$evidence/$id.status.json"; then sleep 1; continue; fi
    else
      curl --fail --silent --show-error --max-time 60 "$base/api/workflows/$id" \
        > "$evidence/$id.status.json"
    fi
    if jq -e --arg expected "$expected" --argjson count "$events" '
      if $expected=="Waiting" then
        .runtimeStatus=="Running" and .customStatus.state == "Waiting" and .result==null and
        ([.history[]|select(.eventType=="EventRaised" and .name=="Decision")]|length)>=$count and
        ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
        ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0
      else .runtimeStatus=="Completed" and .result.status==$expected and .result.version==3 end' \
      "$evidence/$id.status.json" >/dev/null; then return; fi
    sleep 1
  done
  printf 'ERROR: %s did not reach %s.\n' "$id" "$expected" >&2
  return 1
}
start() {
  policy_number=$((policy_number+1))
  local policy_id="$policy_prefix-$policy_number"
  # Set actors in the separate simulator for this test's client scope.
  post /api/demo-identities "$(jq -nc --arg client "$policy_id" \
    '{clientId:$client,accountId:null,assignments:{Finance:"finance"}}')" > "$evidence/$1.identities.json"
  post /api/policies "$(jq -nc --arg id "$policy_id" --argjson minutes "$2" \
    '{id:$id,clientId:$id,baseVersion:0,
      defaultStages:[{name:"finance",roles:["Finance"],timeoutMinutes:$minutes}],overrides:[]}')" > "$evidence/$1.policy.json"
  post /api/workflows "$(jq -nc --arg id "$1" --arg client "$policy_id" \
    '{instanceId:$id,clientId:$client,workflowCode:"B07",planId:"sunny-soda",version:3,requester:"requester",budgetAmount:7500}')" \
    > "$evidence/$1.start.json"
  jq -e --arg id "$1" --arg policy "$policy_id" \
    '.instanceId==$id and .policyId==$policy and .policyVersion==1' "$evidence/$1.start.json" >/dev/null
  wait_for "$1-s1-r1" Waiting
}
check_child() {
  local child="$1-s1-r1" expected="$2"
  status "$child" > "$evidence/$child.history.json"
  jq -e --arg expected "$expected" '
    .runtimeStatus=="Completed" and .result.status==$expected and
    ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
    any(.history[]; .eventType=="TaskScheduled" and .name=="RecordSubmission") and
    any(.history[]; .eventType=="EventRaised" and .name=="Decision") and
    any(.history[]; .eventType=="ExecutionCompleted") and
    ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0
  ' "$evidence/$child.history.json" >/dev/null
  jq -e --arg child "$child" --arg expected "$expected" '
    (.result.steps|length)==1 and .result.steps[0].instanceId==$child and
    .result.steps[0].role=="Finance" and .result.steps[0].status==$expected
  ' "$evidence/$1.status.json" >/dev/null
}
id="$prefix-approval"
child="$id-s1-r1"
start "$id" 2
count=0
for body in \
  '{"actingAs":"requester","outcome":"Approve","version":3}' \
  '{"actingAs":"someone-else","outcome":"Approve","version":3}' \
  '{"actingAs":"finance","outcome":"Approve","version":2}' \
  '{"actingAs":"finance","outcome":"approve","version":3}'; do
  count=$((count+1))
  post "/api/approvals/$child/decision" "$body" > "$evidence/refusal-$count.json"
  wait_for "$child" Waiting "$count"
  cp "$evidence/$child.status.json" "$evidence/still-waiting-$count.json"
done
post "/api/approvals/$child/decision" '{"actingAs":"finance","outcome":"Approve","version":3}' > "$evidence/approve.json"
wait_for "$id" Approved
jq -e '.result.version==3 and .result.receipt=="sunny-soda:v3:approved"' "$evidence/$id.status.json" >/dev/null
check_child "$id" Approved
start "$prefix-timeout" 0.1
post "/api/approvals/$prefix-timeout-s1-r1/decision" '{"actingAs":"finance","outcome":"Approve","version":2}' \
  > "$evidence/timeout-stale-decision.json"
wait_for "$prefix-timeout" Timeout
jq -e '.result.receipt==null' "$evidence/$prefix-timeout.status.json" >/dev/null
check_child "$prefix-timeout" Timeout
start "$prefix-reject" 2
# A rejection needs a reason: the API refuses it without one and nothing is sent to the workflow.
no_comment="$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 60 \
  --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' \
  --data '{"actingAs":"finance","outcome":"Reject","version":3}' "$base/api/approvals/$prefix-reject-s1-r1/decision")"
[ "$no_comment" = 400 ]
post "/api/approvals/$prefix-reject-s1-r1/decision" \
  '{"actingAs":"finance","outcome":"Reject","version":3,"comment":"Budget is wrong"}' \
  > "$evidence/reject-decision.json"
wait_for "$prefix-reject" Rejected
jq -e '.result.receipt==null' "$evidence/$prefix-reject.status.json" >/dev/null
jq -e '.result.steps[0].status=="Rejected" and .result.steps[0].decidedBy=="finance" and
  .result.steps[0].comment=="Budget is wrong" and (.result.steps[0].decidedAt|length)>0' \
  "$evidence/$prefix-reject.status.json" >/dev/null
check_child "$prefix-reject" Rejected
printf 'PASS: saved Finance policies start workflows; four invalid child decisions keep one timer; parent Approve has the only receipt, Reject/Timeout have none. Evidence: %s\n' "$evidence"
