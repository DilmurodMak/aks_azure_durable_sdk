#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
base="${1:-http://127.0.0.1:5080}"
prefix="${2:-policy-demo-$(date -u +%Y%m%d%H%M%S)}"
if [[ ! "$base" =~ ^http://127\.0\.0\.1:508[01]$ ]] ||
   [[ ! "$prefix" =~ ^policy-demo-[a-zA-Z0-9-]{1,45}$ ]]; then
  printf 'ERROR: Use loopback console port5080/5081 and a fresh policy-demo- prefix.\n' >&2
  exit 2
fi
evidence=".build/evidence/$prefix"
if [ -e "$evidence" ]; then printf 'ERROR: Evidence path already exists.\n' >&2; exit 2; fi
mkdir -p "$evidence"
get() { curl --fail-with-body --silent --show-error --max-time 60 "$base$1"; }
post() {
  curl --fail-with-body --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' --data "$2" "$base$1"
}
policy() {
  local version="$1" stages="$2"
  # Demo actors belong to the simulator scope, not the versioned policy.
  post /api/demo-identities "$(jq -nc --arg client "$prefix" \
    '{clientId:$client,accountId:null,assignments:{Lead:"lead",Finance:"finance",Legal:"legal"}}')" \
    > "$evidence/identities-$version.json"
  post /api/policies "$(jq -nc --arg id "$prefix" --argjson base "$((version-1))" --argjson stages "$stages" \
    '{id:$id,clientId:$id,baseVersion:$base,defaultStages:$stages,overrides:[]}')" > "$evidence/policy-$version.json"
  get /api/policies | jq -e --arg id "$prefix" --argjson version "$version" \
    'any(.active[]; .id==$id and .version==$version)' > "$evidence/active-$version.json"
}
start() {
  post /api/workflows "$(jq -nc --arg id "$1" --arg client "$prefix" --arg step "${2:-B07}" \
    '{instanceId:$id,clientId:$client,workflowCode:$step,planId:"sunny-soda",version:3,requester:"requester"}')" \
    > "$evidence/$1.start.json"
}
waiting() {
  local id="$1" count="$2" deadline=$((SECONDS+90))
  while [ "$SECONDS" -lt "$deadline" ]; do
    get "/api/workflows/$id/approvals" | jq '[.[]|select(.canDecide)]' > "$evidence/$id.waiting.json"
    if jq -e --argjson count "$count" 'length==$count' "$evidence/$id.waiting.json" >/dev/null; then return; fi
    sleep 1
  done
  printf 'ERROR: %s did not show %s waiting children.\n' "$id" "$count" >&2; exit 1
}
decide() {
  local id="$1" actor="$2" outcome="${3:-Approve}" child
  child="$(jq -er --arg actor "$actor" '.[]|select(.approver==$actor)|.instanceId' "$evidence/$id.waiting.json")"
  post "/api/approvals/$child/decision" "{\"actingAs\":\"$actor\",\"outcome\":\"$outcome\",\"version\":3}" \
    > "$evidence/$child.decision.json"
}
complete() {
  local id="$1" expected="$2" deadline=$((SECONDS+90))
  while [ "$SECONDS" -lt "$deadline" ]; do
    get "/api/workflows/$id" > "$evidence/$id.result.json"
    if jq -e --arg status "$expected" '.runtimeStatus=="Completed" and .result.status==$status' "$evidence/$id.result.json" >/dev/null; then return; fi
    sleep 1
  done
  printf 'ERROR: %s did not complete as %s.\n' "$id" "$expected" >&2; exit 1
}

policy 1 '[{"name":"lead","roles":["Lead"],"timeoutMinutes":2},{"name":"review","roles":["Finance","Legal"],"timeoutMinutes":2}]'
a="$prefix-a"
start "$a"
waiting "$a" 1
jq -e '.[0].approver=="lead" and .[0].progress.stageNumber==1' "$evidence/$a.waiting.json" >/dev/null

# Activate an edit while A waits, without touching any worker or console process.
policy 2 '[{"name":"parallel","roles":["Finance","Legal"],"timeoutMinutes":2}]'
b="$prefix-b"
start "$b"
waiting "$b" 2
get "/api/workflows/$a" > "$evidence/a-after-edit.json"
get "/api/workflows/$b" > "$evidence/b-after-edit.json"
jq -e '.policyVersion==1 and (.snapshot.stages|length)==2' "$evidence/a-after-edit.json" >/dev/null
jq -e '.policyVersion==2 and (.snapshot.stages|length)==1' "$evidence/b-after-edit.json" >/dev/null
decide "$b" legal
waiting "$b" 1
get "/api/workflows/$b" | jq -e '.runtimeStatus=="Running" and .result==null' >/dev/null
decide "$b" finance
complete "$b" Approved
decide "$a" lead
waiting "$a" 2
decide "$a" finance
waiting "$a" 1
decide "$a" legal
complete "$a" Approved

c="$prefix-reject"
start "$c"
waiting "$c" 2
decide "$c" finance Reject
waiting "$c" 1
get "/api/workflows/$c" | jq -e '.runtimeStatus=="Running" and .result==null' >/dev/null
decide "$c" legal
complete "$c" Rejected
jq -e '.result.receipt==null' "$evidence/$c.result.json" >/dev/null

policy 3 '[{"name":"expire","roles":["Finance","Legal"],"timeoutMinutes":0.1}]'
d="$prefix-timeout"
start "$d"
complete "$d" Timeout
jq -e '.result.receipt==null' "$evidence/$d.result.json" >/dev/null
for id in "$a" "$b" "$c" "$d"; do
  get "/api/workflows/$id/approvals" | jq -e '[.[]|select(.canDecide)]|length==0' >/dev/null
  for child in $(jq -r '.result.steps[].instanceId' "$evidence/$id.result.json"); do
    get "/api/approvals/$child?history=true" > "$evidence/$child.history.json"
    jq -e --arg parent "$id" --arg client "$prefix" '
      .runtimeStatus=="Completed" and
      ([.history[]|select(.eventType=="TaskScheduled" and .name=="ApplyDecision")]|length)==0 and
      any(.history[]; .eventType=="ExecutionStarted" and .tags.parent==$parent and
        .tags.client==$client and .tags.workflow=="B07" and .tags.plan=="sunny-soda" and
        .tags.version=="3" and (.tags.policyVersion|length)>0) and
      ([.history[]|select(.eventType=="TimerCreated")]|length)==1
    ' "$evidence/$child.history.json" >/dev/null
  done
done

# One policy set per client: the default chain covers every business step, and an override changes only B07.
post /api/policies "$(jq -nc --arg id "$prefix" \
  '{id:$id,clientId:$id,baseVersion:3,
    defaultStages:[{name:"review",roles:["Finance"],timeoutMinutes:2}],
    overrides:[{workflowCode:"B07",stages:[
      {name:"lead",roles:["Lead"],timeoutMinutes:2},{name:"review",roles:["Finance"],timeoutMinutes:2}]}]}')" \
  > "$evidence/policy-4.json"
e="$prefix-default"
f="$prefix-override"
start "$e" B08
start "$f" B07
jq -e '.source=="client default" and .policyVersion==4 and (.snapshot.stages|length)==1 and .snapshot.workflowCode=="B08"' \
  "$evidence/$e.start.json" >/dev/null
jq -e '.source=="client override" and .policyVersion==4 and (.snapshot.stages|length)==2 and .snapshot.workflowCode=="B07"' \
  "$evidence/$f.start.json" >/dev/null
get "/api/policy-match?clientId=$prefix&workflowCode=B08" | jq -e '.source=="client default"' >/dev/null
get "/api/policy-match?clientId=$prefix&workflowCode=B07" | jq -e '.source=="client override"' >/dev/null
waiting "$e" 1
decide "$e" finance
complete "$e" Approved
waiting "$f" 1
jq -e '.[0].approver=="lead"' "$evidence/$f.waiting.json" >/dev/null
decide "$f" lead
waiting "$f" 1
decide "$f" finance
complete "$f" Approved
jq -n --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg base "$base" \
  '{completedAt:$completedAt,base:$base,sequential:"Pass",parallelOutOfOrder:"Pass",
    hotEditSnapshotIsolation:"Pass",waitAllOnRejection:"Pass",timeout:"Pass",
    defaultAppliesToAnotherStep:"Pass",stepOverrideReplacesDefaultForOneStep:"Pass",
    waitingChildrenAfterCompletion:0}' > "$evidence/summary.json"
cat "$evidence/summary.json"
