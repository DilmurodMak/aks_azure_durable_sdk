#!/usr/bin/env bash
# Burst test: start N approval chains at once, approve every step by script, record time and failures.
# Each chain is the Sunny-style sign-off: stage 1 has two parallel reviewers, stage 2 has one (3 approvals).
# Usage: bash scripts/test-burst.sh [chains=100] [parallel-clients=25]   (needs the console on 127.0.0.1:5080/5081)
set -euo pipefail
cd "$(dirname "$0")/.."
base="${BASE:-http://127.0.0.1:${CONSOLE_PORT:-5081}}"

if [ "${1:-}" = --one ]; then
  # Worker mode: drive one chain from start to completion; print "<id> <status> <seconds>".
  id="$2"; prefix="$3"; started="$SECONDS"
  get() { curl --fail-with-body --silent --show-error --max-time 60 "$base$1"; }
  post() {
    curl --fail-with-body --silent --show-error --max-time 60 \
      --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' --data "$2" "$base$1"
  }
  fail() { printf '%s %s %s\n' "$id" "$1" "$((SECONDS-started))"; exit 0; }
  post /api/workflows "$(jq -nc --arg id "$id" --arg client "$prefix" \
    '{instanceId:$id,clientId:$client,workflowCode:"B07",planId:"burst",version:1,requester:"requester",budgetAmount:7500}')" >/dev/null ||
    fail StartFailed
  for stage in 1 2; do
    expected=$((stage == 1 ? 2 : 1))
    deadline=$((SECONDS+300)); sent=""
    while [ "$SECONDS" -lt "$deadline" ]; do
      rows="$(get "/api/workflows/$id/approvals" 2>/dev/null | jq -c '[.[]|select(.canDecide)]' 2>/dev/null)" || rows="[]"
      if [ "$(jq 'length' <<< "$rows")" = "$expected" ]; then
        for child in $(jq -r '.[].instanceId' <<< "$rows"); do
          approver="$(jq -r --arg c "$child" '.[]|select(.instanceId==$c)|.approver' <<< "$rows")"
          post "/api/approvals/$child/decision" "{\"actingAs\":\"$approver\",\"outcome\":\"Approve\",\"version\":1}" >/dev/null ||
            fail DecisionFailed
        done
        sent=yes; break
      fi
      sleep 1
    done
    [ -n "$sent" ] || fail "Stage${stage}NeverWaiting"
  done
  deadline=$((SECONDS+300))
  while [ "$SECONDS" -lt "$deadline" ]; do
    status="$(get "/api/workflows/$id" 2>/dev/null | jq -r 'select(.runtimeStatus=="Completed")|.result.status' 2>/dev/null)" || status=""
    if [ -n "$status" ]; then printf '%s %s %s\n' "$id" "$status" "$((SECONDS-started))"; exit 0; fi
    sleep 1
  done
  fail NeverCompleted
fi

count="${1:-100}"; parallel="${2:-25}"
if [[ ! "$count" =~ ^[0-9]+$ ]] || [ "$count" -lt 1 ] || [ "$count" -gt 500 ] ||
   [[ ! "$parallel" =~ ^[0-9]+$ ]] || [ "$parallel" -lt 1 ] || [ "$parallel" -gt 100 ]; then
  printf 'ERROR: chains must be 1-500 and parallel clients 1-100.\n' >&2; exit 2
fi
prefix="burst-$(date -u +%Y%m%d%H%M%S)"
evidence=".build/evidence/$prefix"
mkdir -p "$evidence"
post() {
  curl --fail-with-body --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' --data "$2" "$base$1"
}
curl --fail --silent --show-error --max-time 60 "$base/api/environment" > "$evidence/environment.json"
post /api/demo-identities "$(jq -nc --arg client "$prefix" \
  '{clientId:$client,assignments:{Finance:"finance",Legal:"legal",Lead:"lead"}}')" > "$evidence/identities.json"
post /api/policies "$(jq -nc --arg id "$prefix" \
  '{id:$id,clientId:$id,baseVersion:0,overrides:[],defaultStages:[
    {name:"parallel-review",roles:["Finance","Legal"],timeoutMinutes:60},
    {name:"lead-approval",roles:["Lead"],timeoutMinutes:60}]}')" > "$evidence/policy.json"

printf 'Starting %s chains with %s concurrent clients against %s...\n' "$count" "$parallel" "$(jq -r .environment "$evidence/environment.json")"
started="$SECONDS"; export BASE="$base"
seq 1 "$count" | xargs -P "$parallel" -I{} bash "$0" --one "$prefix-{}" "$prefix" > "$evidence/results.txt" 2> "$evidence/errors.txt" || true
elapsed=$((SECONDS-started))

total="$(wc -l < "$evidence/results.txt" | tr -d ' ')"
approved="$(awk '$2=="Approved"{n++} END{print n+0}' "$evidence/results.txt")"
failed=$((count-approved))
percentile() { sort -n "$evidence/seconds.txt" | awk -v p="$1" '{a[NR]=$1} END{i=int((NR*p)+0.999); if(i<1)i=1; print a[i]}'; }
awk '{print $3}' "$evidence/results.txt" > "$evidence/seconds.txt"
jq -n --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg env "$(jq -r .environment "$evidence/environment.json")" \
  --argjson chains "$count" --argjson clients "$parallel" --argjson reported "$total" --argjson approved "$approved" \
  --argjson failed "$failed" --argjson elapsed "$elapsed" \
  --argjson p50 "$(percentile 0.5)" --argjson p95 "$(percentile 0.95)" --argjson max "$(percentile 1)" \
  '{completedAt:$completedAt,environment:$env,chains:$chains,concurrentClients:$clients,approvalsPerChain:3,
    chainsReported:$reported,approved:$approved,failed:$failed,totalSeconds:$elapsed,
    perChainSeconds:{p50:$p50,p95:$p95,max:$max},
    note:"Per-chain time is start to Completed as seen by a script that polls once a second and approves as soon as a step can decide."}' \
  | tee "$evidence/summary.json"
if [ "$failed" -ne 0 ]; then
  printf 'FAILURES:\n' >&2; awk '$2!="Approved"' "$evidence/results.txt" >&2; exit 1
fi
