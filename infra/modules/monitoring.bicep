// Production monitoring for one stamp (ops/plan.md §5, ops/alerts.md): the action group every
// alert goes to, platform metric alerts on the production app, one outside-in availability
// test, and the resource group's budget. Deployed by infra/deploy.ps1 with -Production only,
// like everything else that concerns production, and only once there's an alert address.
//
// Kept lean on purpose. A standard availability test costs $0.0005 a run, so the design's five
// locations every five minutes would cost about $22 a month against a $40 budget. One test from
// three locations every ten minutes costs about $6.50, and certificate and DNS checks, which only
// need to warn weeks ahead, run once a day in ops-probe.yml instead.

@description('Azure region for the regional resources (Application Insights, the test).')
param location string

@description('Where alerts are emailed. Never committed: deploy.ps1 passes it from -AlertEmail or the existing action group.')
@secure()
param alertEmail string

@description('The production Container App watched by the metric alerts.')
param productionAppName string

@description('Public hostname the availability test checks, the one devices use.')
param productionHostname string

@description('Log Analytics workspace that Application Insights writes into.')
param workspaceId string

param appInsightsName string

@description('Monthly budget for the resource group, in the billing currency (USD).')
param budgetAmount int

@description('First day of the budget\'s first month, yyyy-MM-dd: the existing budget\'s, or the first of the current month (infra/deploy.ps1). Empty: no budget.')
param budgetStartDate string

param tags object

var appId = resourceId('Microsoft.App/containerApps', productionAppName)

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-agent-bridge-ops'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'abops'
    enabled: true
    // Email only, and nothing pages at night: the owner's decision (#568, 2026-11-03).
    emailReceivers: [
      { name: 'owner', emailAddress: alertEmail, useCommonAlertSchema: true }
    ]
  }
}

// Workspace-based, so its data counts against the workspace's daily cap. The test's results are
// a few MB a day.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspaceId
    IngestionMode: 'LogAnalytics'
  }
}

// A-AVAIL: what a device sees, through DNS, the certificate and ingress. SSLCheck fails the test
// on an invalid or expired certificate; warning weeks ahead of expiry is ops-probe.yml's job.
resource healthTest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: 'wt-relay-healthz'
  location: location
  // Required: it links the test to its Application Insights resource.
  tags: union(tags, { 'hidden-link:${appInsights.id}': 'Resource' })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: 'wt-relay-healthz'
    Name: 'Relay /healthz (${productionHostname})'
    Kind: 'standard'
    Enabled: true
    Frequency: 600
    Timeout: 30
    RetryEnabled: true
    Locations: [
      { Id: 'us-ca-sjc-azr' }
      { Id: 'us-va-ash-azr' }
      { Id: 'emea-nl-ams-azr' }
    ]
    Request: {
      RequestUrl: 'https://${productionHostname}/healthz'
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      ContentValidation: { ContentMatch: '"ok":true', IgnoreCase: false, PassIfTextFound: true }
    }
  }
}

resource availabilityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'A-AVAIL relay unreachable'
  location: 'global'
  tags: tags
  properties: {
    description: 'https://${productionHostname}/healthz failing from 2 of 3 locations. Runbook: ops/runbooks/relay-unavailable.md'
    severity: 1
    enabled: true
    scopes: [healthTest.id, appInsights.id]
    evaluationFrequency: 'PT5M'
    // Two test runs at the 10-minute frequency, so one slow run can't fire it.
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: healthTest.id
      componentId: appInsights.id
      failedLocationCount: 2
    }
    autoMitigate: true
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

// Platform alerts on the production app (ops/alerts.md). Thresholds are for one 0.25 vCPU /
// 0.5 GiB replica; revisit them with two weeks of data and whenever the size changes.
var metricAlerts = [
  {
    name: 'A-REPLICAS no relay replica'
    description: 'Production has no running replica. Runbook: ops/runbooks/relay-unavailable.md'
    severity: 1
    metric: 'Replicas'
    aggregation: 'Maximum'
    operator: 'LessThan'
    threshold: 1
    window: 'PT5M'
    frequency: 'PT1M'
    dimensions: []
  }
  {
    // RestartCount is cumulative per replica since it was created, so this reads "the current
    // replica has restarted", and clears when a new replica replaces it.
    name: 'A-RESTART relay restarted'
    description: 'The production replica has restarted (liveness failure, crash or OOM). Runbook: ops/runbooks/relay-resources.md'
    severity: 2
    metric: 'RestartCount'
    aggregation: 'Maximum'
    operator: 'GreaterThan'
    threshold: 0
    window: 'PT15M'
    frequency: 'PT5M'
    dimensions: []
  }
  {
    name: 'A-CRASHLOOP relay restarting repeatedly'
    description: 'The production replica has restarted 3 or more times. Runbook: ops/runbooks/relay-resources.md'
    severity: 1
    metric: 'RestartCount'
    aggregation: 'Maximum'
    operator: 'GreaterThan'
    threshold: 2
    window: 'PT15M'
    frequency: 'PT5M'
    dimensions: []
  }
  {
    name: 'A-MEM relay memory high'
    description: 'Working set over 400 MiB of the replica\'s 512. Runbook: ops/runbooks/relay-resources.md'
    severity: 2
    metric: 'WorkingSetBytes'
    aggregation: 'Average'
    operator: 'GreaterThan'
    threshold: 419430400
    window: 'PT10M'
    frequency: 'PT5M'
    dimensions: []
  }
  {
    name: 'A-CPU relay CPU high'
    description: 'CPU over 80% of the replica\'s 0.25 vCPU for 15 minutes. Runbook: ops/runbooks/relay-resources.md'
    severity: 3
    metric: 'UsageNanoCores'
    aggregation: 'Average'
    operator: 'GreaterThan'
    threshold: 200000000
    window: 'PT15M'
    frequency: 'PT5M'
    dimensions: []
  }
  {
    // A count, not a rate: metric alerts can't divide. The relay answers 503 to new sockets while
    // a revision drains, so a deploy produces a few; 20 in 10 minutes is well past that.
    name: 'A-5XX relay server errors'
    description: 'More than 20 responses with a 5xx status in 10 minutes. Runbook: ops/runbooks/relay-unavailable.md'
    severity: 2
    metric: 'Requests'
    aggregation: 'Total'
    operator: 'GreaterThan'
    threshold: 20
    window: 'PT10M'
    frequency: 'PT5M'
    dimensions: [{ name: 'statusCodeCategory', operator: 'Include', values: ['5xx'] }]
  }
]

resource appAlerts 'Microsoft.Insights/metricAlerts@2018-03-01' = [for a in metricAlerts: {
  name: a.name
  location: 'global'
  tags: tags
  properties: {
    description: a.description
    severity: a.severity
    enabled: true
    scopes: [appId]
    evaluationFrequency: a.frequency
    windowSize: a.window
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'c1'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.App/containerApps'
          metricName: a.metric
          timeAggregation: a.aggregation
          operator: a.operator
          threshold: a.threshold
          dimensions: a.dimensions
        }
      ]
    }
    autoMitigate: true
    actions: [{ actionGroupId: actionGroup.id }]
  }
}]

// A-BUDGET. The credit running out disables the subscription and stops the relay, so cost is an
// availability risk here (ops/runbooks/cost.md). Covers everything in the resource group.
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (!empty(budgetStartDate)) {
  name: 'budget-agent-bridge-relay'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    // To the alert address directly (the API requires contactEmails); not to the action group
    // as well, which would send each one twice.
    notifications: {
      actual50: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 50, thresholdType: 'Actual', contactEmails: [alertEmail] }
      actual80: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 80, thresholdType: 'Actual', contactEmails: [alertEmail] }
      actual100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Actual', contactEmails: [alertEmail] }
      forecast100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Forecasted', contactEmails: [alertEmail] }
    }
  }
}

output appInsightsId string = appInsights.id
output actionGroupId string = actionGroup.id
