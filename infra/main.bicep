targetScope = 'subscription'

import { PrincipalType, ResourceTags, SpikeConfiguration } from './types.bicep'

@description('Short environment label. Use lowercase letters, digits and hyphens.')
@minLength(3)
@maxLength(20)
param environmentName string = 'durable-spike'

@description('Azure region supporting both AKS and Durable Task Scheduler.')
param location string

@description('Dedicated resource group created for this spike.')
param resourceGroupName string = '${environmentName}-rg'

@description('A currently supported Kubernetes version selected for the deployment region.')
param kubernetesVersion string

@description('Public IPv4 addresses of authorized operators/test clients, without CIDR suffixes. Each becomes a /32 allowlist entry.')
@minLength(1)
param allowedClientIpAddresses string[]

@description('Entra object ID of the operator/client principal, not an application client ID.')
@minLength(36)
@maxLength(36)
param operatorPrincipalId string

param operatorPrincipalType PrincipalType = 'User'

@description('Kubernetes namespace the future SDK worker deployment must use.')
@minLength(1)
@maxLength(63)
param workloadNamespace string = 'durable-spike'

@description('Kubernetes service account bound to the SDK worker managed identity.')
@minLength(1)
@maxLength(63)
param workerServiceAccountName string = 'durable-worker'

@description('Linux VM size supported by the selected region and Kubernetes version.')
param nodeVmSize string = 'Standard_D4s_v5'

@description('Fixed system-node count. This infrastructure does not configure autoscaling.')
@minValue(2)
param nodeCount int = 2

param tags ResourceTags = {}

var resourceTags = union({
  environment: environmentName
  purpose: 'durable-sdk-spike'
}, tags)

resource spikeResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: resourceTags
}

module stack './stack.bicep' = {
  scope: spikeResourceGroup
  params: {
    environmentName: environmentName
    location: location
    kubernetesVersion: kubernetesVersion
    allowedClientIpAddresses: allowedClientIpAddresses
    operatorPrincipalId: operatorPrincipalId
    operatorPrincipalType: operatorPrincipalType
    workloadNamespace: workloadNamespace
    workerServiceAccountName: workerServiceAccountName
    nodeVmSize: nodeVmSize
    nodeCount: nodeCount
    tags: resourceTags
  }
}

output configuration SpikeConfiguration = stack.outputs.configuration
