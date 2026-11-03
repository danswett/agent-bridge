#Requires -Version 7.5
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', 'script:Read-BridgePublicationState', Justification = 'The recorded legacy function keeps its original name.')]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Output = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-publication-observation.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
$path = Join-Path $Source 'hooks\decision-ha-websocket.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors) { throw "Publication source does not parse: $($errors[0])" }

# Load functions, never the source script's top-level config or transport.
foreach ($name in @('ConvertTo-BridgePublicationJson', 'Assert-BridgePublicationArtifact',
    'Assert-BridgePublicationPolicy', 'Assert-BridgePublicationProgress', 'Read-BridgePublicationState')) {
    $found = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false))
    if ($found.Count -ne 1) { throw "Expected one definition of $name." }
    . ([scriptblock]::Create($found[0].Extent.Text.Replace("function $name", "function script:$name")))
}

$script:DecisionBridgeConfig = @{ DashboardUrlPath = 'agent-decisions' }
function script:Get-BridgePublicationReceiptPath { 'isolated-memory-receipt' }
function script:Read-BridgeInstallRecord {
    param($Path)
    if ($Path -cne 'isolated-memory-receipt') { throw 'Unexpected receipt path in isolated recorder.' }
    $script:Previous
}
function script:Save-BridgePublicationReceipt { param($Policy) $script:Saved = $Policy }
function Copy-Value($Value) { ConvertFrom-Json -AsHashtable -InputObject (ConvertTo-Json -Depth 30 -InputObject $Value) }
$policy = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-publication.json') -Raw |
    ConvertFrom-Json -AsHashtable).policy
$url = 'data:text/javascript;base64,ZXhwb3J0IHt9Ow==#agent-bridge-publication-policy.js?policy=' +
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-BridgePublicationJson $policy)))
$cases = [Collections.Generic.List[object]]::new()
foreach ($name in @('empty', 'legacy-dashboard', 'legacy-card', 'established', 'missing-config',
    'lost-local-policy', 'lost-dashboard-policy', 'duplicate-policy', 'duplicate-card',
    'invalid-resource', 'invalid-owned-id', 'invalid-owned-type', 'invalid-dashboard',
    'duplicate-dashboard', 'denied-config', 'invalid-config', 'invalid-resources',
    'invalid-dashboards', 'malformed-policy', 'generation-regression')) {
    $script:Previous = $null
    $script:Saved = $null
    $script:CaseResources = @()
    $script:CaseDashboards = @()
    $script:CaseConfig = @{}
    $script:ConfigError = ''
    $owned = @{ id = 'policy'; type = 'module'; url = $url }
    if ($name -in @('established', 'missing-config', 'denied-config', 'invalid-config', 'generation-regression')) {
        $script:CaseResources = @($owned)
        $script:CaseDashboards = @(@{ id = 'target'; url_path = 'agent-decisions' })
    }
    switch ($name) {
        'legacy-dashboard' { $script:CaseDashboards = @(@{ id = 'old'; url_path = 'copilot-decisions' }) }
        'legacy-card' { $script:CaseResources = @(@{ id = 'card'; type = 'module'; url = '/local/agent-bridge-reply-card.js?v=1.0.0' }) }
        'missing-config' { $script:ConfigError = 'config_not_found' }
        'lost-local-policy' { $script:Previous = Copy-Value $policy }
        'lost-dashboard-policy' {
            $script:CaseDashboards = @(@{ id = 'target'; url_path = 'agent-decisions' })
            $script:CaseConfig = @{ agent_bridge_publication = $null }
        }
        'duplicate-policy' { $script:CaseResources = @($owned, $owned) }
        'duplicate-card' { $script:CaseResources = @(@{ id = 'a'; type = 'module'; url = '/local/agent-bridge-reply-card.js' }, @{ id = 'b'; type = 'module'; url = '/other/agent-bridge-reply-card.js' }) }
        'invalid-resource' { $script:CaseResources = @(@{ url = 42 }) }
        'invalid-owned-id' { $script:CaseResources = @(@{ url = $url; id = ''; type = 'module' }) }
        'invalid-owned-type' { $script:CaseResources = @(@{ url = $url; id = 'policy'; type = 'js' }) }
        'invalid-dashboard' { $script:CaseDashboards = @(@{ url_path = 'other' }) }
        'duplicate-dashboard' { $script:CaseDashboards = @(@{ id = 'a'; url_path = 'agent-decisions' }, @{ id = 'b'; url_path = 'agent-decisions' }) }
        'denied-config' { $script:ConfigError = 'unauthorized' }
        'invalid-config' { $script:CaseConfig = @() }
        'invalid-resources' { $script:CaseResources = @{} }
        'invalid-dashboards' { $script:CaseDashboards = @{} }
        'malformed-policy' { $script:CaseResources = @(@{ id = 'policy'; type = 'module'; url = 'bad#agent-bridge-publication-policy.js' }) }
        'generation-regression' { $script:Previous = Copy-Value $policy; $script:Previous.generation++ }
    }
    $accepted = $false
    $kind = $null
    try {
        $state = Read-BridgePublicationState -Invoker {
            param([hashtable[]]$commands)
            $responses = [Collections.Generic.List[object]]::new()
            $result = $null
            switch ($commands[0].type) {
                'lovelace/resources' { $result = $script:CaseResources }
                'lovelace/dashboards/list' { $result = $script:CaseDashboards }
                'lovelace/config' {
                    if ($script:ConfigError) { throw ('WebSocket command ''lovelace/config'' failed: {"code":"' + $script:ConfigError + '"}') }
                    $result = $script:CaseConfig
                }
                default { throw 'Unexpected command in isolated recorder.' }
            }
            # The real WebSocket reader returns PSCustomObjects, not hashtables.
            $wire = ConvertFrom-Json -NoEnumerate -Depth 30 -InputObject (ConvertTo-Json -Depth 30 -InputObject $result)
            $responses.Add($wire)
            return ,$responses.ToArray()
        }
        $accepted = $true
        $kind = $state.Kind
    }
    catch { $accepted = $false; Write-Verbose "$name refused: $_" }
    $positive = $name -in @('empty', 'legacy-dashboard', 'legacy-card', 'established', 'missing-config')
    if ($positive -ne $accepted) { throw "Unexpected legacy observation outcome for $name; do not record a broken boundary." }
    $cases.Add(@{
        name = $name; resources = $script:CaseResources; dashboards = $script:CaseDashboards
        config = $script:CaseConfig; configError = $script:ConfigError; previous = $script:Previous
        accepted = $accepted; kind = $kind; saved = [bool]$script:Saved
    })
}
$fixture = [ordered]@{
    source = @{ commit = (& git -C $Source rev-parse HEAD).Trim(); dirty = [bool](& git -C $Source status --porcelain -- hooks/decision-ha-websocket.ps1) }
    cases = $cases.ToArray()
}
[IO.File]::WriteAllText($Output, ($fixture | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
Write-Information "Recorded publication observation from $($fixture.source.commit) to $Output" -InformationAction Continue
