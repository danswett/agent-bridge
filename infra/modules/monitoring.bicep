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

@description('The GitHub repository whose queued runs A-GHA watches, owner/name.')
param githubRepository string = 'danswett/agent-bridge'

@description('A run queued longer than this, in minutes, counts as Actions stalling. Busy hours on the shared runner pool queue runs for a while, so not too low.')
param githubStallMinutes int = 30

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
    window: 'PT15M'
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
    // a revision drains, so a deploy produces a few; 30 in 15 minutes is well past that. Metric alerts take 1, 5, 15 or 30 minute windows, not 10.
    name: 'A-5XX relay server errors'
    description: 'More than 30 responses with a 5xx status in 15 minutes. Runbook: ops/runbooks/relay-unavailable.md'
    severity: 2
    metric: 'Requests'
    aggregation: 'Total'
    operator: 'GreaterThan'
    threshold: 30
    window: 'PT15M'
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

// A-GHA (#658). On 2026-11-04 a GitHub Actions outage stalled every pipeline (CI, merges, the
// Windows release, relay deploys, even ops-probe.yml) for hours with no alert. Anything running
// on Actions can't report that, so this runs in Azure: every 5 minutes it reads GitHub's status
// page and this repository's queued runs, and records one availability result, `github-actions`,
// that the alert below watches. About $2 a month (some 90,000 built-in actions).
//
// A source it can't reach counts as no evidence, never as an outage: the status page or the
// API being unreachable from Azure isn't GitHub being down, and the message says which sources
// answered. Anonymous API calls share a per-address hourly limit with the region's other Logic
// Apps; if the message shows the runs API failing for long, give it a read-only token (#658).
var ingestionEndpoint = replace(first(filter(split(appInsights.properties.ConnectionString, ';'), p => startsWith(p, 'IngestionEndpoint=')))!, 'IngestionEndpoint=', '')

resource githubWatch 'Microsoft.Logic/workflows@2019-05-01' = {
  name: 'logic-agent-bridge-github-watch'
  location: location
  tags: tags
  properties: {
    state: 'Enabled'
    parameters: {
      ingestUrl: { value: '${ingestionEndpoint}v2/track' }
      iKey: { value: appInsights.properties.InstrumentationKey }
      repository: { value: githubRepository }
      stallMinutes: { value: githubStallMinutes }
    }
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      parameters: {
        ingestUrl: { type: 'String' }
        iKey: { type: 'SecureString' }
        repository: { type: 'String' }
        stallMinutes: { type: 'Int' }
      }
      triggers: {
        every5: { type: 'Recurrence', recurrence: { frequency: 'Minute', interval: 5 } }
      }
      actions: {
        // Each answer is read only inside a condition on its status code: a 429 or an HTML error
        // page has no JSON to read, and reading it anyway would fail the run before it reports.
        components: {
          type: 'InitializeVariable'
          runAfter: {}
          inputs: { variables: [{ name: 'components', type: 'array', value: [] }] }
        }
        queued: {
          type: 'InitializeVariable'
          runAfter: { components: ['Succeeded'] }
          inputs: { variables: [{ name: 'queued', type: 'array', value: [] }] }
        }
        status: {
          type: 'Http'
          runAfter: { queued: ['Succeeded'] }
          inputs: { method: 'GET', uri: 'https://www.githubstatus.com/api/v2/summary.json', retryPolicy: { type: 'fixed', count: 1, interval: 'PT20S' } }
        }
        runs: {
          type: 'Http'
          runAfter: { queued: ['Succeeded'] }
          inputs: {
            method: 'GET'
            uri: 'https://api.github.com/repos/@{parameters(\'repository\')}/actions/runs?status=queued&per_page=100'
            headers: { 'User-Agent': 'agent-bridge-ops', Accept: 'application/vnd.github+json' }
            // One quick retry keeps each check inside its 5 minutes, so a 15-minute window holds 3.
            retryPolicy: { type: 'fixed', count: 1, interval: 'PT20S' }
          }
        }
        statusAnswered: {
          type: 'If'
          runAfter: { status: ['Succeeded', 'Failed', 'TimedOut'] }
          expression: { and: [{ equals: ['@outputs(\'status\')?[\'statusCode\']', 200] }] }
          actions: {
            keepComponents: {
              type: 'SetVariable'
              runAfter: {}
              inputs: { name: 'components', value: '@coalesce(body(\'status\')?[\'components\'], json(\'[]\'))' }
            }
          }
          else: { actions: {} }
        }
        runsAnswered: {
          type: 'If'
          runAfter: { runs: ['Succeeded', 'Failed', 'TimedOut'] }
          expression: { and: [{ equals: ['@outputs(\'runs\')?[\'statusCode\']', 200] }] }
          actions: {
            keepQueued: {
              type: 'SetVariable'
              runAfter: {}
              inputs: { name: 'queued', value: '@coalesce(body(\'runs\')?[\'workflow_runs\'], json(\'[]\'))' }
            }
          }
          else: { actions: {} }
        }
        degraded: {
          type: 'Query'
          runAfter: { statusAnswered: ['Succeeded', 'Failed', 'Skipped', 'TimedOut'] }
          inputs: {
            from: '@variables(\'components\')'
            where: '@and(contains(createArray(\'Actions\', \'Pages\'), item()?[\'name\']), not(equals(item()?[\'status\'], \'operational\')))'
          }
        }
        degradedNames: {
          type: 'Select'
          runAfter: { degraded: ['Succeeded', 'Failed', 'Skipped', 'TimedOut'] }
          inputs: { from: '@coalesce(body(\'degraded\'), json(\'[]\'))', select: '@concat(item()?[\'name\'], \' \', item()?[\'status\'])' }
        }
        // `queued` is a run waiting for a runner: not `waiting` (an environment approval, such as
        // the production gate) and not `pending` (a concurrency group), which are normal. Timed from
        // run_started_at, which each re-run attempt resets; created_at would make a re-run of an old
        // run look stalled at once.
        stalled: {
          type: 'Query'
          runAfter: { runsAnswered: ['Succeeded', 'Failed', 'Skipped', 'TimedOut'] }
          inputs: {
            from: '@variables(\'queued\')'
            where: '@less(ticks(coalesce(item()?[\'run_started_at\'], item()?[\'created_at\'])), ticks(addMinutes(utcNow(), mul(-1, parameters(\'stallMinutes\')))))'
          }
        }
        report: {
          type: 'Http'
          // Runs whatever happened above, so a malformed answer still reports (as no evidence) rather
          // than going silent; A-GHA-SILENT catches the watch not reporting at all.
          runAfter: { degradedNames: ['Succeeded', 'Failed', 'Skipped', 'TimedOut'], stalled: ['Succeeded', 'Failed', 'Skipped', 'TimedOut'] }
          // The instrumentation key stays out of run history.
          runtimeConfiguration: { secureData: { properties: ['inputs'] } }
          inputs: {
            method: 'POST'
            uri: '@parameters(\'ingestUrl\')'
            headers: { 'Content-Type': 'application/json' }
            body: [
              {
                name: 'Microsoft.ApplicationInsights.Availability'
                time: '@utcNow()'
                iKey: '@parameters(\'iKey\')'
                data: {
                  baseType: 'AvailabilityData'
                  baseData: {
                    ver: 2
                    id: '@guid()'
                    name: 'github-actions'
                    duration: '00:00:01'
                    runLocation: 'azure-logic-app'
                    success: '@and(equals(length(coalesce(body(\'degraded\'), json(\'[]\'))), 0), equals(length(coalesce(body(\'stalled\'), json(\'[]\'))), 0))'
                    message: '@{concat(\'status page: \', if(equals(outputs(\'status\')?[\'statusCode\'], 200), if(equals(length(coalesce(body(\'degradedNames\'), json(\'[]\'))), 0), \'Actions and Pages operational\', join(body(\'degradedNames\'), \', \')), \'unreachable\'), \'; runs queued over \', string(parameters(\'stallMinutes\')), \' min: \', if(equals(outputs(\'runs\')?[\'statusCode\'], 200), string(length(coalesce(body(\'stalled\'), json(\'[]\')))), concat(\'unknown (API \', string(outputs(\'runs\')?[\'statusCode\']), \')\')))}'
                  }
                }
              }
            ]
          }
        }
      }
    }
  }
}

// One email when it starts and one when it clears (autoMitigate), not one every 5 minutes.
// Below 50% over 15 minutes: at least two of the three checks in the window found a problem.
resource githubAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'A-GHA GitHub Actions or Pages degraded'
  location: 'global'
  tags: tags
  properties: {
    description: 'GitHub reports Actions or Pages degraded, or runs here have sat queued over ${githubStallMinutes} minutes. Runbook: ops/runbooks/github-outage.md'
    severity: 3
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'c1'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'microsoft.insights/components'
          metricName: 'availabilityResults/availabilityPercentage'
          timeAggregation: 'Average'
          operator: 'LessThan'
          threshold: 50
          dimensions: [{ name: 'availabilityResult/name', operator: 'Include', values: ['github-actions'] }]
        }
      ]
    }
    autoMitigate: true
    actions: [{ actionGroupId: actionGroup.id }]
  }
}
// A-GHA-SILENT. A metric alert never fires on missing data, so a watch that stopped (disabled,
// deleted, or failing before it reports) would look exactly like GitHub being fine. This fires
// when no github-actions result has arrived in 30 minutes. A log alert every 15 minutes costs
// about $0.50 a month.
resource githubWatchSilent 'Microsoft.Insights/scheduledQueryRules@2022-06-15' = {
  name: 'A-GHA-SILENT GitHub watch not reporting'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    description: 'No github-actions result from logic-agent-bridge-github-watch in 30 minutes, so A-GHA is blind. Runbook: ops/runbooks/github-outage.md'
    severity: 3
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT30M'
    criteria: {
      allOf: [
        {
          query: 'availabilityResults | where name == "github-actions"'
          timeAggregation: 'Count'
          operator: 'LessThan'
          threshold: 1
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [actionGroup.id] }
  }
}
output appInsightsId string = appInsights.id
output actionGroupId string = actionGroup.id
