using './main.bicep'

var envName = readEnvironmentVariable('AZURE_ENV_NAME', 'durable-spike')

param environmentName = envName
param resourceGroupName = readEnvironmentVariable('AZURE_RESOURCE_GROUP', '${envName}-rg')
param location = readEnvironmentVariable('AZURE_LOCATION')
param kubernetesVersion = readEnvironmentVariable('AKS_KUBERNETES_VERSION')
param operatorPrincipalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param operatorPrincipalType = readEnvironmentVariable('AZURE_PRINCIPAL_TYPE', 'User')
param allowedClientIpAddresses = [
  readEnvironmentVariable('AZURE_CLIENT_IP')
]

param workloadNamespace = 'durable-spike'
param workerServiceAccountName = 'durable-worker'
param nodeVmSize = 'Standard_D4s_v5'
param nodeCount = 2
