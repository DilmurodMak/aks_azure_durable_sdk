targetScope = 'resourceGroup'

@description('PostgreSQL region. Central US is used because this subscription restricts East US 2.')
param postgresLocation string = 'centralus'

@description('Region of the existing AKS VNet and private endpoint.')
param networkLocation string = 'eastus2'

param virtualNetworkName string
param operatorPrincipalId string
param operatorAllowedIpAddresses string[]
param oidcIssuerUrl string
param privateEndpointNsgResourceId string = ''

@description('Set only on initial provisioning. Existing passwords are otherwise left unchanged.')
param initializePasswords bool = false

@secure()
param administratorPassword string = ''

@secure()
param applicationPassword string = ''

param postgresSkuName string = 'Standard_B2s'
param postgresSkuTier 'Burstable' | 'GeneralPurpose' | 'MemoryOptimized' = 'Burstable'

@minValue(32)
param storageSizeGB int = 32

var suffix = uniqueString(subscription().id, resourceGroup().id)
var serverName = 'pg-temporal-${suffix}'
var vaultName = 'kv-tmp-${suffix}'
var administratorLogin = 'temporal_admin'

resource network 'Microsoft.Network/virtualNetworks@2025-05-01' existing = {
  name: virtualNetworkName
}

resource endpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  parent: network
  name: 'private-endpoints'
  properties: {
    addressPrefix: '10.0.8.0/27'
    privateEndpointNetworkPolicies: 'Disabled'
    ...(empty(privateEndpointNsgResourceId) ? {} : {
      networkSecurityGroup: { id: privateEndpointNsgResourceId }
    })
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: serverName
  location: postgresLocation
  tags: {
    purpose: 'temporal-approval-spike'
  }
  sku: {
    name: postgresSkuName
    tier: postgresSkuTier
  }
  properties: {
    version: '16'
    administratorLogin: administratorLogin
    ...(initializePasswords ? {
      administratorLoginPassword: administratorPassword
    } : {})
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
    storage: {
      storageSizeGB: storageSizeGB
      autoGrow: 'Disabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Disabled'
    }
  }
}

resource executionDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: postgres
  name: 'temporal'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource visibilityDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: postgres
  name: 'temporal_visibility'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource secureTransport 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2025-08-01' = {
  parent: postgres
  name: 'require_secure_transport'
  properties: {
    source: 'user-override'
    value: 'on'
  }
}

resource extensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2025-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    source: 'user-override'
    value: 'BTREE_GIN'
  }
}

resource dns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
}

resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: dns
  name: 'temporal-aks'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.id
    }
  }
}

// Private Link keeps the approved cross-region database off the public internet.
resource endpoint 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: 'pe-${serverName}'
  location: networkLocation
  properties: {
    subnet: {
      id: endpointSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'postgres'
        properties: {
          privateLinkServiceId: postgres.id
          groupIds: ['postgresqlServer']
        }
      }
    ]
  }
}

resource dnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  parent: endpoint
  name: 'postgres'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'postgres'
        properties: {
          privateDnsZoneId: dns.id
        }
      }
    ]
  }
}

resource vault 'Microsoft.KeyVault/vaults@2025-05-01' = {
  name: vaultName
  location: networkLocation
  properties: {
    tenantId: tenant().tenantId
    sku: {
      name: 'standard'
      family: 'A'
    }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: [for address in operatorAllowedIpAddresses: {
        value: '${address}/32'
      }]
      virtualNetworkRules: []
    }
  }
}

resource administratorSecret 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = if (initializePasswords) {
  parent: vault
  name: 'postgres-administrator-password'
  properties: {
    value: administratorPassword
  }
}

resource applicationSecret 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = if (initializePasswords) {
  parent: vault
  name: 'postgres-application-password'
  properties: {
    value: applicationPassword
  }
}

var secretsOfficerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
)

resource operatorVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, operatorPrincipalId, secretsOfficerRoleId)
  scope: vault
  properties: {
    roleDefinitionId: secretsOfficerRoleId
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

resource vaultDns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
}

resource vaultDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: vaultDns
  name: 'temporal-aks'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: network.id }
  }
}

resource vaultEndpoint 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: 'pe-${vaultName}'
  location: networkLocation
  properties: {
    subnet: { id: endpointSubnet.id }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: vault.id
          groupIds: ['vault']
        }
      }
    ]
  }
}

resource vaultDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  parent: vaultEndpoint
  name: 'vault'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: { privateDnsZoneId: vaultDns.id }
      }
    ]
  }
}

resource provisionerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-temporal-bootstrap-${suffix}'
  location: networkLocation
}

resource provisionerFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: provisionerIdentity
  name: 'aks-temporal-secrets'
  properties: {
    issuer: oidcIssuerUrl
    subject: 'system:serviceaccount:temporal-spike:temporal-secrets'
    audiences: ['api://AzureADTokenExchange']
  }
}

var secretsUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6'
)

resource provisionerVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, provisionerIdentity.id, secretsUserRoleId)
  scope: vault
  properties: {
    principalId: provisionerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

output configuration object = {
  postgresServerName: postgres.name
  postgresHost: postgres.properties.fullyQualifiedDomainName
  postgresResourceId: postgres.id
  postgresLocation: postgresLocation
  administratorLogin: administratorLogin
  applicationLogin: 'temporal_app'
  executionDatabase: executionDatabase.name
  visibilityDatabase: visibilityDatabase.name
  keyVaultName: vault.name
  privateEndpointName: endpoint.name
  provisionerClientId: provisionerIdentity.properties.clientId
}
