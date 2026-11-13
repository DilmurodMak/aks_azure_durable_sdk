import { PrincipalType, ResourceTags } from '../types.bicep'

param name string
param location string
param kubernetesVersion string
param clusterIdentityResourceId string
param subnetResourceId string
param authorizedSourceCidrs string[]
param operatorPrincipalId string
param operatorPrincipalType PrincipalType
param nodeVmSize string
param nodeCount int
param tags ResourceTags

var clusterAdminRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b'
)

resource cluster 'Microsoft.ContainerService/managedClusters@2025-02-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${clusterIdentityResourceId}': {}
    }
  }
  properties: {
    dnsPrefix: name
    kubernetesVersion: kubernetesVersion
    enableRBAC: true
    disableLocalAccounts: true
    aadProfile: {
      managed: true
      enableAzureRBAC: true
      tenantID: tenant().tenantId
    }
    apiServerAccessProfile: {
      authorizedIPRanges: authorizedSourceCidrs
      enablePrivateCluster: false
      disableRunCommand: true
    }
    agentPoolProfiles: [
      {
        name: 'system'
        count: nodeCount
        vmSize: nodeVmSize
        mode: 'System'
        type: 'VirtualMachineScaleSets'
        osType: 'Linux'
        osSKU: 'Ubuntu'
        enableNodePublicIP: false
        enableAutoScaling: false
        maxPods: 30
        vnetSubnetID: subnetResourceId
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      networkPolicy: 'azure'
      loadBalancerSku: 'standard'
      outboundType: 'userAssignedNATGateway'
      serviceCidr: '10.1.0.0/16'
      dnsServiceIP: '10.1.0.10'
    }
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
  }
}

resource operatorAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cluster.id, operatorPrincipalId, clusterAdminRoleId)
  scope: cluster
  properties: {
    principalId: operatorPrincipalId
    principalType: operatorPrincipalType
    roleDefinitionId: clusterAdminRoleId
  }
}

output resourceId string = cluster.id
output oidcIssuerUrl string = cluster.properties.oidcIssuerProfile.issuerURL
output kubeletPrincipalId string = cluster.properties.identityProfile.kubeletidentity.objectId
