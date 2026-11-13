import { PrincipalType, ResourceTags, SpikeConfiguration } from './types.bicep'

param environmentName string
param location string
param kubernetesVersion string
param allowedClientIpAddresses string[]
param operatorPrincipalId string
param operatorPrincipalType PrincipalType
param workloadNamespace string
param workerServiceAccountName string
param nodeVmSize string
param nodeCount int
param tags ResourceTags

var suffix = uniqueString(subscription().id, resourceGroup().id)
var clusterName = 'aks-${environmentName}-${suffix}'
var registryName = 'acr${suffix}'
var schedulerName = 'dts-${environmentName}-${suffix}'
var taskHubName = 'default'
var clientCidrs = [for address in allowedClientIpAddresses: '${address}/32']

resource clusterIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-aks-${environmentName}-${suffix}'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-worker-${environmentName}-${suffix}'
  location: location
  tags: tags
}

module network './modules/network.bicep' = {
  params: {
    name: 'vnet-${environmentName}-${suffix}'
    location: location
    clusterIdentityPrincipalId: clusterIdentity.properties.principalId
    tags: tags
  }
}

var allowedSourceCidrs = union(clientCidrs, [
  '${network.outputs.outboundIpAddress}/32'
])

module cluster './modules/aks.bicep' = {
  params: {
    name: clusterName
    location: location
    kubernetesVersion: kubernetesVersion
    clusterIdentityResourceId: clusterIdentity.id
    subnetResourceId: network.outputs.subnetResourceId
    authorizedSourceCidrs: allowedSourceCidrs
    operatorPrincipalId: operatorPrincipalId
    operatorPrincipalType: operatorPrincipalType
    nodeVmSize: nodeVmSize
    nodeCount: nodeCount
    tags: tags
  }
}

module registry './modules/registry.bicep' = {
  params: {
    name: registryName
    location: location
    kubeletPrincipalId: cluster.outputs.kubeletPrincipalId
    tags: tags
  }
}

resource workerFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: workerIdentity
  name: 'aks-worker'
  properties: {
    issuer: cluster.outputs.oidcIssuerUrl
    subject: 'system:serviceaccount:${workloadNamespace}:${workerServiceAccountName}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

module scheduler './modules/scheduler.bicep' = {
  params: {
    name: schedulerName
    location: location
    taskHubName: taskHubName
    allowedSourceCidrs: allowedSourceCidrs
    workerPrincipalId: workerIdentity.properties.principalId
    operatorPrincipalId: operatorPrincipalId
    operatorPrincipalType: operatorPrincipalType
    tags: tags
  }
}

output configuration SpikeConfiguration = {
  resourceGroupName: resourceGroup().name
  clusterName: clusterName
  clusterResourceId: cluster.outputs.resourceId
  registryName: registryName
  registryLoginServer: registry.outputs.loginServer
  schedulerName: schedulerName
  schedulerResourceId: scheduler.outputs.resourceId
  schedulerEndpoint: scheduler.outputs.endpoint
  taskHubName: taskHubName
  taskHubResourceId: scheduler.outputs.taskHubResourceId
  workloadIdentityClientId: workerIdentity.properties.clientId
  workloadIdentityResourceId: workerIdentity.id
  workloadNamespace: workloadNamespace
  workerServiceAccountName: workerServiceAccountName
  outboundIpAddress: network.outputs.outboundIpAddress
  allowedSourceCidrs: allowedSourceCidrs
}
