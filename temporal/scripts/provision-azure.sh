#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mode="${1:---preview}"
if [[ "$mode" != --preview && "$mode" != --apply ]]; then
  printf 'Usage: bash temporal/scripts/provision-azure.sh [--preview|--apply]\n' >&2
  exit 2
fi
: "${AKS_CLUSTER_NAME:?Source env/azure.env for the approved deployment first.}"
directory="$PWD/.build/temporal-azure"
mkdir -p "$directory"
chmod 700 "$directory"
umask 077
trap 'rm -f "$directory/postgres.parameters.json"' EXIT

az deployment sub show --name "${DEPLOYMENT_NAME:-durable-spike-infra}" \
  --output json > "$directory/base-deployment.json"
export TEMPORAL_PROVISION_DIRECTORY="$directory" EXPECTED_AKS_CLUSTER="$AKS_CLUSTER_NAME"
python3 - <<'PY'
import json
import os
import secrets
import subprocess
from pathlib import Path

directory = Path(os.environ["TEMPORAL_PROVISION_DIRECTORY"])
base = json.loads((directory / "base-deployment.json").read_text())
configuration = base["properties"]["outputs"]["configuration"]["value"]
if configuration["clusterName"] != os.environ["EXPECTED_AKS_CLUSTER"]:
    raise SystemExit("Deployment outputs do not match the approved AKS cluster.")
parameters = {key: {"value": value["value"]} for key, value in base["properties"]["parameters"].items()}
group = configuration["resourceGroupName"]
vaults = json.loads(subprocess.check_output(
    ["az", "keyvault", "list", "-g", group, "--query", "[?starts_with(name, 'kv-tmp-')].name", "-o", "json"],
    text=True,
))
if len(vaults) > 1:
    raise SystemExit("More than one Temporal vault exists; select the deployment explicitly.")
sql_parameters = {
    "operatorPrincipalId": parameters["operatorPrincipalId"],
    "operatorAllowedIpAddresses": parameters["allowedClientIpAddresses"],
}
if not vaults:
    sql_parameters.update({
        "initializePasswords": {"value": True},
        "administratorPassword": {"value": "Aa1!" + secrets.token_urlsafe(36)},
        "applicationPassword": {"value": "Aa1!" + secrets.token_urlsafe(36)},
    })
# Older base outputs lack the VNet ID; get it from the actual AKS subnet rather than guessing a name.
subnet = subprocess.check_output(
    ["az", "aks", "show", "-g", group, "-n", configuration["clusterName"],
     "--query", "agentPoolProfiles[0].vnetSubnetId", "-o", "tsv"], text=True
).strip()
sql_parameters["virtualNetworkName"] = {"value": subnet.split("/")[-3]}
subnets = json.loads(subprocess.check_output(
    ["az", "network", "vnet", "subnet", "list", "-g", group,
     "--vnet-name", subnet.split("/")[-3], "-o", "json"], text=True
))
endpoint_subnet = next((s for s in subnets if s["name"] == "private-endpoints"), None)
if endpoint_subnet and endpoint_subnet.get("networkSecurityGroup"):
    sql_parameters["privateEndpointNsgResourceId"] = {
        "value": endpoint_subnet["networkSecurityGroup"]["id"]
    }
issuer = subprocess.check_output(
    ["az", "aks", "show", "-g", group, "-n", configuration["clusterName"],
     "--query", "oidcIssuerProfile.issuerUrl", "-o", "tsv"], text=True
).strip()
sql_parameters["oidcIssuerUrl"] = {"value": issuer}
(directory / "postgres.parameters.json").write_text(json.dumps({"parameters": sql_parameters}))
(directory / "base-configuration.json").write_text(json.dumps(configuration))
PY

group="$(jq -er '.resourceGroupName' "$directory/base-configuration.json")"
az bicep build --file infra/main.bicep --outfile "$directory/main.json"
az bicep build --file infra/temporal.bicep --outfile "$directory/temporal.json"
az deployment group what-if --resource-group "$group" --name temporal-infra \
  --template-file "$directory/temporal.json" --parameters @"$directory/postgres.parameters.json" \
  --no-pretty-print > "$directory/temporal.what-if.json"
for preview in "$directory/temporal.what-if.json"; do
  jq -e 'all(.changes[]; .changeType != "Delete")' "$preview" >/dev/null
  jq -r '.changes[] | select(.changeType != "Ignore" and .changeType != "NoChange") |
    "\(.changeType) \(.resourceId)"' "$preview"
done
if [[ "$mode" == --preview ]]; then
  printf 'Preview only. Review changes, then use --apply to provision.\n'
  exit 0
fi
az deployment group create --resource-group "$group" --name temporal-infra \
  --template-file "$directory/temporal.json" --parameters @"$directory/postgres.parameters.json" \
  --query properties.outputs.configuration.value --output json > "$directory/configuration.json"
printf 'Temporal PostgreSQL infrastructure deployed. Non-secret outputs: %s/configuration.json\n' "$directory"
