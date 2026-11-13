#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
base="${1:-http://127.0.0.1:5080}"
prefix="${2:-console-demo-http-$(date -u +%Y%m%d%H%M%S)}"
if [[ ! "$base" =~ ^http://127\.0\.0\.1:508[01]$ ]] ||
   [[ ! "$prefix" =~ ^console-demo-[a-zA-Z0-9-]{1,60}$ ]]; then
  printf 'ERROR: Use loopback console port 5080/5081 and a fresh console-demo- prefix.\n' >&2
  exit 2
fi
evidence=".build/evidence/$prefix"
if [ -e "$evidence" ]; then printf 'ERROR: Evidence path already exists.\n' >&2; exit 2; fi
mkdir -p "$evidence"
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
    status "$id" > "$evidence/$id.status.json"
    if jq -e --arg expected "$expected" --argjson count "$events" '
      if $expected=="Waiting" then
        .runtimeStatus=="Running" and .customStatus=="Waiting for Finance" and .result==null and
        ([.history[]|select(.eventType=="EventRaised" and .name=="Decision")]|length)>=$count and
        ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
        ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0
      else .runtimeStatus=="Completed" and .result.status==$expected end' \
      "$evidence/$id.status.json" >/dev/null; then return; fi
    sleep 1
  done
  printf 'ERROR: %s did not reach %s.\n' "$id" "$expected" >&2
  return 1
}
start() {
  post /api/approvals "$(jq -nc --arg id "$1" --argjson minutes "$2" \
    '{instanceId:$id,planId:"sunny-soda",version:3,requester:"requester",approver:"finance",timeoutMinutes:$minutes}')" \
    > "$evidence/$1.start.json"
  wait_for "$1" Waiting
}
id="$prefix-approval"
start "$id" 2
count=0
for body in \
  '{"actingAs":"requester","outcome":"Approve","version":3}' \
  '{"actingAs":"someone-else","outcome":"Approve","version":3}' \
  '{"actingAs":"finance","outcome":"Approve","version":2}' \
  '{"actingAs":"finance","outcome":"approve","version":3}'; do
  count=$((count+1))
  post "/api/approvals/$id/decision" "$body" > "$evidence/refusal-$count.json"
  wait_for "$id" Waiting "$count"
  cp "$evidence/$id.status.json" "$evidence/still-waiting-$count.json"
done
post "/api/approvals/$id/decision" '{"actingAs":"finance","outcome":"Approve","version":3}' > "$evidence/approve.json"
wait_for "$id" Approved
jq -e '.result.version==3 and .result.receipt=="sunny-soda:v3:approved"' "$evidence/$id.status.json" >/dev/null
start "$prefix-timeout" 0.1
post "/api/approvals/$prefix-timeout/decision" '{"actingAs":"finance","outcome":"Approve","version":2}' >/dev/null
wait_for "$prefix-timeout" Timeout
jq -e '.result.receipt==null and
  ([.history[]|select(.eventType=="TimerCreated")]|length)==1 and
  ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0' \
  "$evidence/$prefix-timeout.status.json" >/dev/null
start "$prefix-reject" 2
post "/api/approvals/$prefix-reject/decision" '{"actingAs":"finance","outcome":"Reject","version":3}' >/dev/null
wait_for "$prefix-reject" Rejected
jq -e '.result.receipt==null' "$evidence/$prefix-reject.status.json" >/dev/null
printf 'PASS: four invalid decisions remain waiting with one timer; valid Approve, explicit Reject, and timeout without receipt. Evidence: %s\n' "$evidence"
