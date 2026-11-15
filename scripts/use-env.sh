# Source this file: source scripts/use-env.sh local|azure
approval_use_env() {
  local target="${1:-}" root configuration destination tenant insights
  root="$(git rev-parse --show-toplevel)" || return 1
  case "$target" in
    local)
      unset AZURE_CLIENT_ID AZURE_TENANT_ID AZURE_FEDERATED_TOKEN_FILE APPLICATIONINSIGHTS_CONNECTION_STRING APPINSIGHTS_APP_ID
      source "$root/env/local.env"
      export POLICY_STORE_PATH="$root/approval_policies/local.json"
      export DEMO_IDENTITIES_PATH="$root/demo_settings/local.json"
      ;;
    azure)
      : "${AZURE_SUBSCRIPTION_ID:?Set the approved AZURE_SUBSCRIPTION_ID first.}"
      configuration="$(az deployment sub show --subscription "$AZURE_SUBSCRIPTION_ID" \
        --name "${DEPLOYMENT_NAME:-durable-spike-infra}" \
        --query properties.outputs.configuration.value --output json)" || return 1
      tenant="$(az account show --subscription "$AZURE_SUBSCRIPTION_ID" --query tenantId --output tsv)" || return 1
      if ! jq -e '.schedulerEndpoint|startswith("https://")' <<< "$configuration" >/dev/null; then
        printf 'ERROR: Invalid Scheduler outputs; environment was not switched.\n' >&2
        return 1
      fi
      insights="$(az resource show --ids "$(jq -r '.applicationInsightsResourceId' <<< "$configuration")" \
        --query '{connection:properties.ConnectionString,appId:properties.AppId}' --output json)" || return 1
      destination="$root/env/azure.env"
      (
        umask 077
        jq -r --arg tenant "$tenant" --argjson insights "$insights" '
          "export ENDPOINT=" + (.schedulerEndpoint|@sh),
          "export TASKHUB=" + (.taskHubName|@sh),
          "export AKS_CLUSTER_NAME=" + (.clusterName|@sh),
          "export AZURE_CLIENT_ID=" + (.workloadIdentityClientId|@sh),
          "export REGISTRY_NAME=" + (.registryName|@sh),
          "export REGISTRY_LOGIN_SERVER=" + (.registryLoginServer|@sh),
          "export APPLICATIONINSIGHTS_CONNECTION_STRING=" + ($insights.connection|@sh),
          "export APPINSIGHTS_APP_ID=" + ($insights.appId|@sh),
          "export SCHEDULER_AUTH=AzureCli",
          "export APPROVAL_ENVIRONMENT=AZURE",
          "export CONSOLE_PORT=5081",
          "export DASHBOARD_URL=" + (("https://dashboard.durabletask.io/subscriptions/" +
            (.schedulerResourceId|split("/")[2]) + "/schedulers/" + .schedulerName +
            "/taskhubs/" + .taskHubName + "?endpoint=" + (.schedulerEndpoint|@uri) +
            "&tenantId=" + ($tenant|@uri))|@sh)
        ' <<< "$configuration" > "$destination"
      ) || return 1
      source "$destination"
      export POLICY_STORE_PATH="$root/approval_policies/azure.json"
      export DEMO_IDENTITIES_PATH="$root/demo_settings/azure.json"
      ;;
    *)
      printf 'Usage: source scripts/use-env.sh local|azure\n' >&2
      return 2
      ;;
  esac
  printf 'Active: %s | endpoint=%s | taskhub=%s | console=http://127.0.0.1:%s | dashboard=%s\n' \
    "$APPROVAL_ENVIRONMENT" "$ENDPOINT" "$TASKHUB" "$CONSOLE_PORT" "$DASHBOARD_URL"
}
approval_use_env "$@"
