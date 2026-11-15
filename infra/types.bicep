@export()
type ResourceTags = {
  *: string
}

@export()
type PrincipalType = 'User' | 'Group' | 'ServicePrincipal'

@export()
type SpikeConfiguration = {
  resourceGroupName: string
  clusterName: string
  clusterResourceId: string
  registryName: string
  registryLoginServer: string
  schedulerName: string
  schedulerResourceId: string
  schedulerEndpoint: string
  taskHubName: string
  taskHubResourceId: string
  workloadIdentityClientId: string
  workloadIdentityResourceId: string
  workloadNamespace: string
  workerServiceAccountName: string
  outboundIpAddress: string
  allowedSourceCidrs: string[]
  applicationInsightsName: string
  applicationInsightsResourceId: string
}
