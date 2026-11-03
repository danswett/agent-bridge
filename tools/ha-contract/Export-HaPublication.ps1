#Requires -Version 7.5
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', 'script:Get-BridgePublicationSettings', Justification = 'The isolated stub must keep the legacy function name.')]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Output = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-publication.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
$path = Join-Path $Source 'hooks\decision-ha-websocket.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors) { throw "Publication source does not parse: $($errors[0])" }

# As in Export-HaContract.ps1, only named functions are loaded. No top-level
# legacy code, installed config, token or transport is evaluated.
foreach ($name in @('ConvertTo-BridgePublicationJson', 'Get-BridgePublicationHash',
    'Assert-BridgePublicationArtifact', 'Assert-BridgePublicationPolicy',
    'Assert-BridgePublicationProgress', 'Assert-BridgePublicationWriter',
    'Get-BridgePinnedCardFailure', 'Get-BridgeRegisteredCardArtifact')) {
    $found = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false))
    if ($found.Count -ne 1) { throw "Expected one definition of $name." }
    . ([scriptblock]::Create($found[0].Extent.Text.Replace("function $name", "function script:$name")))
}

$script:DecisionBridgeConfig = @{ DashboardUrlPath = 'agent-decisions' }
function script:Get-BridgePublicationSettings { $script:Settings }
function Copy-Value($Value) { ConvertFrom-Json -AsHashtable -InputObject (ConvertTo-Json -Depth 20 -InputObject $Value) }
function Test-Accepted([scriptblock]$Action) {
    try { & $Action; return $true }
    catch { return $false }
}

$cardText = "const CARD_VERSION = '1.0.0';"
$cardUrl = 'data:text/javascript;base64,' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cardText)) +
    '#agent-bridge-reply-card.js?v=1.0.0'
$card = @{ version = '1.0.0'; hash = Get-BridgePublicationHash $cardText }
$render = @{ version = '2.0.0'; hash = ('b' * 64) }
$policy = @{
    protocol = 1; authority = 'test-authority'; writer = 'test-writer'; generation = 3
    dashboard = 'agent-decisions'; mode = 'advance'; card = $card; render = $render
    highCard = $card; highRender = $render; legacyCard = ''
}
$baselineSettings = @{ authority = 'test-authority'; participant = 'test-writer'; writer = 'test-writer'; generation = 3 }
$validations = [Collections.Generic.List[object]]::new()
$progress = [Collections.Generic.List[object]]::new()
$writers = [Collections.Generic.List[object]]::new()

foreach ($name in @('valid', 'pinned', 'extra-member', 'missing-member', 'protocol', 'identity', 'generation',
    'dashboard', 'mode', 'hash', 'artifact-member', 'version', 'target-above-high', 'advance-below-high')) {
    $value = Copy-Value $policy
    switch ($name) {
        'pinned' { $value.mode = 'pin'; $value.card = @{ version = '0.9.0'; hash = ('c' * 64) } }
        'extra-member' { $value.extra = $true }
        'missing-member' { $value.Remove('legacyCard') }
        'protocol' { $value.protocol = 2 }
        'identity' { $value.writer = 'not a writer' }
        'generation' { $value.generation = 0 }
        'dashboard' { $value.dashboard = 'other-dashboard' }
        'mode' { $value.mode = 'automatic' }
        'hash' { $value.card.hash = ('A' * 64) }
        'artifact-member' { $value.card.extra = $true }
        'version' { $value.render.version = '2.0' }
        'target-above-high' { $value.render.version = '3.0.0' }
        'advance-below-high' { $value.highRender.version = '3.0.0' }
    }
    $validations.Add(@{ name = $name; policy = $value; accepted = (Test-Accepted { Assert-BridgePublicationPolicy $value }) })
}

foreach ($name in @('unchanged', 'catch-up', 'authority-change', 'generation-regression', 'writer-same-generation',
    'mode-same-generation', 'high-regression', 'high-conflict', 'target-conflict', 'advance-upgrade',
    'explicit-pin', 'pinned-target-change', 'pin-exit-new-generation')) {
    $previous = Copy-Value $policy
    $current = Copy-Value $policy
    switch ($name) {
        'catch-up' { $current.generation = 5; $current.writer = 'next-writer' }
        'authority-change' { $current.authority = 'other-authority' }
        'generation-regression' { $current.generation = 2 }
        'writer-same-generation' { $current.writer = 'other-writer' }
        'mode-same-generation' { $current.mode = 'pin' }
        'high-regression' { $current.mode = 'pin'; $current.generation = 4; $current.highRender.version = '1.0.0'; $current.render.version = '1.0.0' }
        'high-conflict' { $current.highRender.hash = ('c' * 64); $current.render.hash = ('c' * 64) }
        'target-conflict' { $current.mode = 'pin'; $previous.mode = 'pin'; $current.render.hash = ('c' * 64) }
        'advance-upgrade' { $current.render.version = '3.0.0'; $current.highRender.version = '3.0.0' }
        'explicit-pin' { $current.mode = 'pin'; $current.generation = 4; $current.render.version = '1.0.0' }
        'pinned-target-change' { $current.mode = 'pin'; $previous.mode = 'pin'; $current.render.version = '1.0.0' }
        'pin-exit-new-generation' { $previous.mode = 'pin'; $previous.render.version = '1.0.0'; $current.generation = 4 }
    }
    $progress.Add(@{
        name = $name; previous = $previous; current = $current
        accepted = (Test-Accepted { Assert-BridgePublicationProgress -Previous $previous -Current $current })
    })
}

foreach ($name in @('exact', 'observer', 'wrong-authority', 'wrong-writer', 'wrong-generation', 'older',
    'equal-version-conflict', 'newer', 'pin-exact', 'pin-newer', 'pin-alias',
    'pin-missing-card', 'pin-opaque-card', 'pin-wrong-card', 'card-repair')) {
    $value = Copy-Value $policy
    $script:Settings = Copy-Value $baselineSettings
    $artifact = Copy-Value $render
    $url = $cardUrl
    $component = 'render'
    if ($name.StartsWith('pin-', [StringComparison]::Ordinal) -or $name -eq 'card-repair') { $value.mode = 'pin' }
    switch ($name) {
        'observer' { $script:Settings.participant = 'observer' }
        'wrong-authority' { $script:Settings.authority = 'other-authority' }
        'wrong-writer' { $script:Settings.writer = 'other-writer' }
        'wrong-generation' { $script:Settings.generation = 2 }
        'older' { $artifact.version = '1.0.0' }
        'equal-version-conflict' { $artifact.hash = ('c' * 64) }
        'newer' { $artifact.version = '3.0.0' }
        'pin-newer' { $artifact.version = '3.0.0' }
        'pin-alias' { $artifact.version = '02.0.0' }
        'pin-missing-card' { $url = '' }
        'pin-opaque-card' { $url = '/local/agent-bridge-reply-card.js?v=1.0.0' }
        'pin-wrong-card' { $value.card.hash = ('c' * 64) }
        'card-repair' { $component = 'card'; $artifact = Copy-Value $card; $url = '' }
    }
    $state = @{ Policy = $value; CardUrl = $url; Kind = 'established' }
    $writers.Add(@{
        name = $name; policy = $value; settings = $script:Settings; component = $component
        artifact = $artifact; cardUrl = $url
        accepted = (Test-Accepted { Assert-BridgePublicationWriter -State $state -Component $component -Artifact $artifact })
    })
}

$canonical = ConvertTo-BridgePublicationJson $policy
$fixture = [ordered]@{
    source = @{ commit = (& git -C $Source rev-parse HEAD).Trim(); dirty = [bool](& git -C $Source status --porcelain -- hooks/decision-ha-websocket.ps1) }
    policy = $policy; canonical = $canonical; hash = Get-BridgePublicationHash $canonical
    resourceUrl = 'data:text/javascript;base64,ZXhwb3J0IHt9Ow==#agent-bridge-publication-policy.js?policy=' +
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($canonical))
    validations = $validations.ToArray(); progress = $progress.ToArray(); writers = $writers.ToArray()
}
[IO.File]::WriteAllText($Output, ($fixture | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
Write-Information "Recorded publication contract from $($fixture.source.commit) to $Output" -InformationAction Continue
