#Requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Templates = (Join-Path $PSScriptRoot '..\..\src\AgentBridge.HomeAssistant\Publication\session-templates.json'),
    [string]$Output = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-renderer.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
function Import-SpecFunction([string]$Path, [string[]]$Names = @()) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Source $Path), [ref]$tokens, [ref]$errors)
    if ($errors) { throw "Cannot parse $Path" }
    foreach ($definition in $ast.EndBlock.Statements) {
        if ($definition -isnot [Management.Automation.Language.FunctionDefinitionAst]) { continue }
        if ($Names.Count -and $definition.Name -notin $Names) { continue }
        $name = $definition.Name
        . ([scriptblock]::Create($definition.Extent.Text.Replace("function $name", "function script:$name")))
    }
    return ,$ast
}
# Define functions only. No settings, files, network or legacy top-level code runs.
$mqtt = Import-SpecFunction 'hooks\decision-mqtt.ps1'
$common = Import-SpecFunction 'hooks\decision-bridge-common.ps1' @('Get-BridgeTuningAxes', 'Get-BridgeTuningAxisLabel')
$null = Import-SpecFunction 'hooks\decision-ha-websocket.ps1' @(
    'Save-CopilotSessionDashboard', 'Test-BridgeActivityCardServed', 'Get-BridgeDashboardInputSignature',
    'ConvertTo-BridgePublicationJson', 'Get-BridgePublicationHash')
foreach ($pair in @(
    @{ Ast = $mqtt; Names = @('CopilotMqttConfig', 'CopilotMqttUsageAccountSlots', 'CopilotEndSessionConfirmNote', 'CopilotEndSessionLapsedNote') },
    @{ Ast = $common; Names = @('BridgeTuningAxes') }
)) {
    foreach ($name in $pair.Names) {
        $assignment = @($pair.Ast.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq "`$script:$name"
        })
        if ($assignment.Count -ne 1 -or $assignment[0].Right.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -or $node -is [Management.Automation.Language.InvokeMemberExpressionAst]
        }, $true).Count) { throw "Not a literal assignment: $name" }
        . ([scriptblock]::Create($assignment[0].Extent.Text))
    }
}
$script:CopilotMqttMaxFields = 12
$script:DecisionBridgeConfig = @{ DashboardUrlPath = 'agent-decisions' }
$script:Trusted = $true
$script:CardUrl = ''
$script:Captured = $null
$script:InputJson = ''
$script:Artifact = @{ version = '1.13.0'; hash = ('b' * 64) }
$script:Policy = @{ authority = 'test-authority'; writer = 'test-writer'; generation = 3 }
function script:Update-BridgePublicationFence {
    param($Component, $Artifact)
    if ($Component -cne 'render' -or $Artifact -ne $script:Artifact) { throw 'Unexpected renderer fence request.' }
    @{ Policy = $script:Policy; CardUrl = $script:CardUrl }
}
function script:Get-BridgeRenderArtifact { $script:Artifact }
function script:Test-BridgeServedCardContent {
    param($ReplyCardUrl)
    if ($ReplyCardUrl -cne $script:CardUrl) { throw 'Unexpected served card in recorder.' }
    $script:Trusted
}
function script:Get-BridgePublicationHash {
    param([string]$Text)
    if ($Text.StartsWith('{"cardTrusted":', [StringComparison]::Ordinal)) { $script:InputJson = $Text }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
function script:Initialize-BridgeDashboard {}
function script:Remove-BridgeLegacyDashboard {}
function script:Get-BridgeDashboardPublication { @{ Verified = $true; InputSignature = $script:Captured.agent_bridge_publication.inputHash } }
function script:Invoke-CopilotHaWebSocket {
    param([hashtable[]]$Commands)
    if ($Commands.Count -ne 1 -or $Commands[0].type -cne 'lovelace/config/save') { throw 'Unexpected recorder operation.' }
    $script:Captured = $Commands[0].config
}
$machine = [pscustomobject]@{
    Slug = 'alpha'; Machine = 'Alpha'; IncludeProfile = $true; IncludeResume = $true; IncludeAgent = $true
    IncludeTuning = $true; IncludePermissions = $true; IncludeDetailed = $true; IsDev = $true; Online = $true
    SessionNodes = @('agent_bridge_one')
}
$session = [pscustomobject]@{ Node = 'agent_bridge_one'; Name = "Review $([char]::ConvertFromUtf32(0x1F9E0))"; Machine = 'Alpha'; Kind = 'copilot' }
$cases = [Collections.Generic.List[object]]::new()
$templatesByVersion = [ordered]@{}
$lastTemplate = ''
$versions = @('', '1.0.0', '1.12.0', '1.13.0', '1.15.0', '1.16.0', '1.17.0', '1.18.0',
    '1.19.0', '1.20.0', '1.22.0', '1.25.0', '1.27.0', '1.30.0', '1.31.0', '1.35.0', '1.40.0', '1.42.0')
foreach ($version in $versions) {
    $script:CardUrl = if ($version) { "/local/agent-bridge-reply-card.js?v=$version" } else { '' }
    foreach ($scenario in @('solo', 'fleet', 'empty', 'untrusted', 'ordering', 'czech')) {
        $culture = if ($scenario -ceq 'czech') { 'cs-CZ' } else { 'en-US' }
        [cultureinfo]::CurrentCulture = [cultureinfo]::GetCultureInfo($culture)
        $script:Trusted = $scenario -cne 'untrusted'
        $machines = @($machine)
        $sessions = @($session)
        $selector = ''
        if ($scenario -ceq 'fleet') {
            $machines += [pscustomobject]@{
                Slug = 'beta'; Machine = 'Beta'; Online = $false
                IncludeProfile = $false; IncludeResume = $false; SessionNodes = @('agent_bridge_mcp')
            }
            $machines += [pscustomobject]@{
                Slug = 'gamma'; Machine = 'Gamma'; Online = $true; IncludeProfile = $false; IncludeResume = $false
            }
            $sessions += [pscustomobject]@{ Node = 'agent_bridge_mcp'; Name = 'MCP'; Machine = 'Beta'; Kind = 'mcp' }
            $selector = 'input_select.agent_bridge_launch_machine'
        }
        if ($scenario -ceq 'ordering') {
            $machines = @(
                [pscustomobject]@{ Slug = 'a1'; Machine = 'A1'; IncludeProfile = $false; IncludeResume = $false; SessionNodes = @('agent_bridge_a1', 'agent_bridge_a_1') },
                [pscustomobject]@{ Slug = 'a_1'; Machine = 'A-1'; IncludeProfile = $false; IncludeResume = $false; Online = $false }
            )
            $sessions = @(
                [pscustomobject]@{ Node = 'agent_bridge_a1'; Name = 'A1'; Machine = 'A1'; Kind = 'copilot' },
                [pscustomobject]@{ Node = 'agent_bridge_a_1'; Name = 'A-1'; Machine = 'A1'; Kind = 'mcp' }
            )
        }
        if ($scenario -ceq 'empty') { $sessions = @() }
        if ($scenario -ceq 'czech') {
            $machines = @(
                [pscustomobject]@{ Slug = 'ch'; Machine = 'CH'; IncludeProfile = $false; IncludeResume = $false; SessionNodes = @('agent_bridge_ch', 'agent_bridge_d') },
                [pscustomobject]@{ Slug = 'd'; Machine = 'D'; IncludeProfile = $false; IncludeResume = $false }
            )
            $sessions = @(
                [pscustomobject]@{ Node = 'agent_bridge_ch'; Name = 'CH'; Machine = 'CH'; Kind = 'copilot' },
                [pscustomobject]@{ Node = 'agent_bridge_d'; Name = 'D'; Machine = 'CH'; Kind = 'mcp' }
            )
        }
        $machines = @($machines | Sort-Object Slug)
        $sessions = @($sessions | Sort-Object Node)
        Save-CopilotSessionDashboard -Sessions $sessions -Machines $machines -MachineSelector $selector -ReplyCardUrl $script:CardUrl
        if (-not $script:Captured.views -or $script:Captured.views[0].cards.Count -lt 2) { throw 'Recorder did not produce the full view.' }
        $cases.Add(@{         version = $version; scenario = $scenario; culture = $culture; machines = $machines; sessions = $sessions; selector = $selector
            trusted = $script:Trusted; cardUrl = $script:CardUrl; config = $script:Captured; inputJson = $script:InputJson })
    }
    $script:Trusted = $true
    $templateSession = [pscustomobject]@{ Node = 'agent_bridge_templatenode'; Name = '__BRIDGE_SESSION_NAME__'; Machine = '__BRIDGE_MACHINE_NAME__'; Kind = 'copilot' }
    Save-CopilotSessionDashboard -Sessions @($templateSession) -Machines @($machine) -ReplyCardUrl $script:CardUrl
    $key = if ($version) { $version } else { '0.0.0' }
    $template = $script:Captured.views[0].cards[-1]
    $canonical = ConvertTo-BridgePublicationJson $template
    if ($canonical -cne $lastTemplate) { $templatesByVersion[$key] = $template; $lastTemplate = $canonical }
}
$templateSession.Kind = 'mcp'
Save-CopilotSessionDashboard -Sessions @($templateSession) -Machines @($machine) -ReplyCardUrl $script:CardUrl
$templatesByVersion['mcp'] = $script:Captured.views[0].cards[-1]
$metadata = @{ commit = (& git -C $Source rev-parse HEAD).Trim(); dirty = [bool](& git -C $Source status --porcelain -- hooks frontend)
    cardHash = (Get-FileHash -LiteralPath (Join-Path $Source 'frontend\agent-bridge-reply-card.js') -Algorithm SHA256).Hash.ToLowerInvariant() }
foreach ($file in @($Templates, $Output)) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $file)) }
[IO.File]::WriteAllText($Templates, (ConvertTo-BridgePublicationJson @{ source = $metadata; sessions = $templatesByVersion }) + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($Output, (ConvertTo-BridgePublicationJson @{ source = $metadata; cases = $cases.ToArray() }) + "`n", [Text.UTF8Encoding]::new($false))
Write-Information "Recorded $($cases.Count) complete renderer contracts and session templates." -InformationAction Continue
