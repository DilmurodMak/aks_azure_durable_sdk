#!/usr/bin/env bash
# Step-one definition of done, on AKS, in one run:
#   1. Start a three-stage chain for a client account.
#   2. Stage 1 has two parallel reviewers; approve them out of order.
#   3. Restart the worker pod while a reviewer is still pending.
#   4. Stage 2 rejects, with a comment.
#   5. The chain ends Rejected and stage 3 never starts.
#   6. The tags are on the dashboard data, and (when APPINSIGHTS_APP_ID is set) in Application Insights.
# Needs the Azure console on 127.0.0.1:5081 and: source scripts/use-env.sh azure
set -euo pipefail
cd "$(dirname "$0")/.."
: "${AKS_CLUSTER_NAME:?Run: source scripts/use-env.sh azure}"
: "${ENDPOINT:?Run: source scripts/use-env.sh azure}"
: "${TASKHUB:?Run: source scripts/use-env.sh azure}"
if [ "${SCHEDULER_AUTH:-}" != AzureCli ] || [ "$(kubectl config current-context)" != "$AKS_CLUSTER_NAME" ]; then
  printf 'ERROR: Use the AzureCli identity and the approved Kubernetes context.\n' >&2
  exit 2
fi
prefix="${1:-step-one-$(date -u +%Y%m%d%H%M%S)}"
if [[ ! "$prefix" =~ ^step-one-[a-zA-Z0-9-]{1,45}$ ]]; then
  printf 'ERROR: Use a fresh step-one- prefix (letters, digits, hyphens).\n' >&2
  exit 2
fi
base="http://127.0.0.1:${CONSOLE_PORT:-5081}"
evidence=".build/evidence/$prefix"
if [ -e "$evidence" ]; then printf 'ERROR: Evidence path already exists.\n' >&2; exit 2; fi
mkdir -p "$evidence"
account="ACC-$prefix"
correlation="$prefix-corr"
run="$prefix-run"
requester="requester"
namespace=durable-spike
pods() {
  kubectl --context "$AKS_CLUSTER_NAME" --namespace "$namespace" get pods \
    --selector app.kubernetes.io/name=approval-worker --output json
}

get() { curl --fail-with-body --silent --show-error --max-time 60 "$base$1"; }
post() {
  curl --fail-with-body --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' --header 'X-Approval-Console: spike' --data "$2" "$base$1"
}
check() { # check <description> <jq filter> <file>
  if ! jq -e "$2" "$3" >/dev/null; then printf 'FAIL: %s\n' "$1" >&2; exit 1; fi
  printf 'ok   %s\n' "$1"
}
waiting() {
  local count="$1" deadline=$((SECONDS+120))
  while [ "$SECONDS" -lt "$deadline" ]; do
    get "/api/workflows/$run/approvals" | jq '[.[]|select(.canDecide)]' > "$evidence/waiting.json"
    if jq -e --argjson count "$count" 'length==$count' "$evidence/waiting.json" >/dev/null; then return; fi
    sleep 1
  done
  printf 'ERROR: %s did not show %s reviewers waiting.\n' "$run" "$count" >&2; exit 1
}
decide() { # decide <user> <outcome> [comment]
  local actor="$1" outcome="$2" comment="${3:-}" child
  child="$(jq -er --arg actor "$actor" '.[]|select(.approver==$actor)|.instanceId' "$evidence/waiting.json")"
  post "/api/approvals/$child/decision" "$(jq -nc --arg actor "$actor" --arg outcome "$outcome" --arg comment "$comment" \
    '{actingAs:$actor,outcome:$outcome,version:3} + (if $comment=="" then {} else {comment:$comment} end)')" \
    > "$evidence/$child.decision.json"
}

get /api/environment > "$evidence/console-environment.json"
jq -e --arg endpoint "$ENDPOINT" --arg taskHub "$TASKHUB" \
  '.environment=="AZURE" and .endpoint==$endpoint and .taskHub==$taskHub' "$evidence/console-environment.json" >/dev/null ||
  { printf 'ERROR: Console must use the same Azure Scheduler endpoint and task hub.\n' >&2; exit 2; }

post /api/demo-identities "$(jq -nc --arg client "$prefix" --arg account "$account" \
  '{clientId:$client,accountId:$account,assignments:{Finance:"finance",Legal:"legal",Lead:"lead",Owner:"owner"}}')" \
  > "$evidence/identities.json"
post /api/policies "$(jq -nc --arg id "$prefix" --arg account "$account" \
  '{id:$id,clientId:$id,accountId:$account,baseVersion:0,overrides:[],defaultStages:[
    {name:"parallel-review",roles:["Finance","Legal"],timeoutMinutes:30},
    {name:"lead-approval",roles:["Lead"],timeoutMinutes:30},
    {name:"final-signoff",roles:["Owner"],timeoutMinutes:30}]}')" > "$evidence/policy.json"

printf '== 1. Start the chain\n'
post /api/workflows "$(jq -nc --arg id "$run" --arg client "$prefix" --arg account "$account" \
  --arg requester "$requester" --arg correlation "$correlation" \
  '{instanceId:$id,clientId:$client,accountId:$account,workflowCode:"B07",planId:"sunny-soda",version:3,
    requester:$requester,correlationId:$correlation}')" > "$evidence/start.json"
jq -e --arg correlation "$correlation" '.correlationId==$correlation and .source=="account default"' \
  "$evidence/start.json" >/dev/null
printf 'ok   started %s with correlation %s using the account default chain\n' "$run" "$correlation"

printf '== 2. Stage 1: two parallel reviewers, approved out of order\n'
waiting 2
check 'both stage-1 reviewers can decide at once' '[.[].progress.stageNumber]==[1,1]' "$evidence/waiting.json"
decide legal Approve
waiting 1
check 'finance is still waiting after legal approved first' '.[0].approver=="finance"' "$evidence/waiting.json"
get "/api/workflows/$run" | jq -e '.runtimeStatus=="Running" and .result==null' >/dev/null
printf 'ok   stage 2 has not started while finance is pending\n'

printf '== 3. Restart the worker pod mid-wait\n'
pods > "$evidence/pods-before.json"
old_pod="$(jq -er '.items[0].metadata.name' "$evidence/pods-before.json")"
restarted="$SECONDS"
kubectl --context "$AKS_CLUSTER_NAME" --namespace "$namespace" delete pod "$old_pod" --wait=true --timeout=90s
deadline=$((SECONDS+240)); ready=false
while [ "$SECONDS" -lt "$deadline" ]; do
  pods > "$evidence/pods-after.json"
  if jq -e --arg old "$old_pod" '[.items[]|select(.metadata.name!=$old and .metadata.deletionTimestamp==null)|
      select(any(.status.conditions[]?;.type=="Ready" and .status=="True"))]|length==1' "$evidence/pods-after.json" >/dev/null; then
    ready=true; break
  fi
  sleep 2
done
[ "$ready" = true ] || { printf 'ERROR: no Ready replacement worker within 240 seconds.\n' >&2; exit 1; }
new_pod="$(jq -er '.items[]|select(.metadata.deletionTimestamp==null)|.metadata.name' "$evidence/pods-after.json")"
printf 'ok   %s replaced by %s in %ss\n' "$old_pod" "$new_pod" "$((SECONDS-restarted))"
waiting 1
check 'finance is still waiting after the restart' '.[0].approver=="finance"' "$evidence/waiting.json"

printf '== 4. Finish stage 1, then stage 2 rejects with a comment\n'
decide finance Approve
waiting 1
check 'stage 2 started only after both stage-1 approvals' '.[0].approver=="lead" and .[0].progress.stageNumber==2' "$evidence/waiting.json"
decide lead Reject "Not acceptable for this client"

printf '== 5. The chain ends Rejected; stage 3 never starts\n'
deadline=$((SECONDS+120))
while [ "$SECONDS" -lt "$deadline" ]; do
  get "/api/workflows/$run" > "$evidence/result.json"
  if jq -e '.runtimeStatus=="Completed"' "$evidence/result.json" >/dev/null; then break; fi
  sleep 1
done
check 'chain completed as Rejected without a receipt' '.result.status=="Rejected" and .result.receipt==null' "$evidence/result.json"
check 'steps are Approved, Approved, Rejected' '(.result.steps|map(.status))==["Approved","Approved","Rejected"]' "$evidence/result.json"
check 'the rejection records who, when and why' \
  '.result.steps[2]|.decidedBy=="lead" and .comment=="Not acceptable for this client" and (.decidedAt|length)>0' "$evidence/result.json"
check 'stage 3 is reported as never started' '.result.stagesNotStarted==["final-signoff"]' "$evidence/result.json"
get "/api/workflows/$run/approvals" > "$evidence/approvals.json"
check 'the stage-3 row is NotStarted and nothing is waiting' \
  '(.[-1].state=="NotStarted") and ([.[]|select(.canDecide)]|length)==0' "$evidence/approvals.json"
if get "/api/approvals/$run-s3-r1" >/dev/null 2>&1; then
  printf 'FAIL: the stage-3 step instance exists.\n' >&2; exit 1
fi
printf 'ok   no stage-3 step instance was ever created\n'

printf '== 6. Tags on the dashboard data\n'
get "/api/approvals/$run?history=true" > "$evidence/parent-history.json"
jq -e --arg correlation "$correlation" --arg account "$account" --arg requester "$requester" '
  any(.history[]; .eventType=="ExecutionStarted" and .tags.correlationId==$correlation and
    .tags.account==$account and .tags.startedBy==$requester)' "$evidence/parent-history.json" >/dev/null
printf 'ok   chain instance tags: account, startedBy, correlationId\n'
get "/api/approvals/$run-s2-r1?history=true" > "$evidence/lead-history.json"
jq -e --arg correlation "$correlation" --arg account "$account" '
  any(.history[]; .eventType=="ExecutionStarted" and .tags.correlationId==$correlation and .tags.account==$account) and
  any(.history[]; .eventType=="TaskScheduled" and .name=="RecordDecision")' "$evidence/lead-history.json" >/dev/null
printf 'ok   reviewer instance tags and its RecordDecision activity\n'
dashboard="$(jq -r '.dashboardUrl' "$evidence/console-environment.json")"
printf 'dashboard: %s\n' "$dashboard"

insights_status="not checked (APPINSIGHTS_APP_ID is not set)"
if [ -n "${APPINSIGHTS_APP_ID:-}" ]; then
  printf '== 6b. Application Insights (spans can take a few minutes to appear)\n'
  query="let spans = union requests, dependencies | where timestamp > ago(3h); \
let roots = spans | where name == 'approval.start' and tostring(customDimensions['approval.correlationId']) == '$correlation' | distinct operation_Id; \
spans | where operation_Id in (roots) or tostring(customDimensions['approval.correlationId']) == '$correlation' \
| summarize spans=count() by name, role=cloud_RoleName | order by name asc"
  deadline=$((SECONDS+480)); found=false
  while [ "$SECONDS" -lt "$deadline" ]; do
    if az rest --method post --resource https://api.applicationinsights.io \
        --url "https://api.applicationinsights.io/v1/apps/$APPINSIGHTS_APP_ID/query" \
        --body "$(jq -nc --arg query "$query" '{query:$query}')" --output json > "$evidence/appinsights.json" 2>"$evidence/appinsights.err" &&
       jq -e '
         def n($name): ([.tables[0].rows[]|select(.[0]==$name)|.[2]]|add // 0);
         n("approval.start")>=1 and n("approval.submitted")>=3 and n("approval.decision.sent")>=3 and
         n("approval.decision.recorded")>=3 and
         ([.tables[0].rows[]|.[1]]|unique|contains(["approval-console","approval-worker"]))' \
         "$evidence/appinsights.json" >/dev/null; then
      found=true; break
    fi
    sleep 20
  done
  if [ "$found" = true ]; then
    insights_status="found: one trace spans approval-console and approval-worker with the business tags"
    jq -r '.tables[0].rows[]|"  \(.[2])x \(.[0])  [\(.[1])]"' "$evidence/appinsights.json"
  else
    insights_status="NOT FOUND within 8 minutes; see $evidence/appinsights.json and appinsights.err"
    printf 'FAIL: %s\n' "$insights_status" >&2; exit 1
  fi
fi

jq -n --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg run "$run" --arg correlation "$correlation" \
  --arg oldPod "$old_pod" --arg newPod "$new_pod" --arg dashboard "$dashboard" --arg insights "$insights_status" \
  --arg image "$(jq -er '.items[0].status.containerStatuses[0].imageID' "$evidence/pods-after.json")" \
  '{completedAt:$completedAt,instanceId:$run,correlationId:$correlation,oldPod:$oldPod,newPod:$newPod,image:$image,
    parallelStage1OutOfOrder:"Pass",workerRestartMidWait:"Pass",rejectionWithComment:"Pass",
    chainRejectedStage3NeverStarted:"Pass",dashboardTags:"Pass",applicationInsights:$insights,dashboard:$dashboard}' \
  > "$evidence/summary.json"
cat "$evidence/summary.json"
