import { PrincipalType, ResourceTags } from '../types.bicep'

param name string
param location string
param taskHubName string
param allowedSourceCidrs string[]
param workerPrincipalId string
param operatorPrincipalId string
param operatorPrincipalType PrincipalType
param tags ResourceTags

var workerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '80d0d6b0-f522-40a4-8886-a5a11720c375'
)
var dataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0ad04412-c4d5-4796-b79c-f76d14c8d402'
)

resource scheduler 'Microsoft.DurableTask/schedulers@2025-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    ipAllowlist: allowedSourceCidrs
    sku: {
      name: 'Consumption'
    }
  }
}

resource taskHub 'Microsoft.DurableTask/schedulers/taskHubs@2025-11-01' = {
  parent: scheduler
  name: taskHubName
}

resource workerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(taskHub.id, workerPrincipalId, workerRoleId)
  scope: taskHub
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: workerRoleId
  }
}

resource clientAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(taskHub.id, operatorPrincipalId, dataContributorRoleId)
  scope: taskHub
  properties: {
    principalId: operatorPrincipalId
    principalType: operatorPrincipalType
    roleDefinitionId: dataContributorRoleId
  }
}

output resourceId string = scheduler.id
output endpoint string = scheduler.properties.endpoint
output taskHubResourceId string = taskHub.id
