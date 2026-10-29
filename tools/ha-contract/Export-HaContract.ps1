<#
    Records agent-ha-bridge's per-machine Home Assistant contract as fixtures, by running
    agent-ha-bridge's own functions rather than restating them.

    The HA connector (docs/ha-connector.md) must publish exactly what the PowerShell daemon
    publishes, so the dashboard DSWETT-HOME generates keeps drawing a migrated machine. The
    surest description of "exactly" is the daemon's own code: this script loads the functions
    that build topics, entity ids and payloads from an agent-ha-bridge checkout, replaces the
    one function that would send anything (Invoke-HomeAssistantService) with a recorder, calls
    them with fixed inputs, and writes what they would have published.

    Nothing is sent anywhere. Functions are loaded from the source's syntax tree, one by one,
    so no file's top-level code runs (that code reads ~/.agent-ha-bridge, which must never be
    touched from here); only decision-mqtt.ps1's top level, which is variable assignments, is
    evaluated, and that is checked first. The recorder runs agent-ha-bridge's own
    Assert-HomeAssistantServiceTarget on every call before recording it.

    Run from the agent-bridge repository root:

        pwsh -NoProfile -File tools/ha-contract/Export-HaContract.ps1 `
            -Source C:\Users\me\repos\agent-ha-bridge

    and commit tests/AgentBridge.HomeAssistant.Tests/Fixtures/ha-contract.json. The source's
    commit is recorded in the fixture, so a later regeneration shows what changed upstream.
#>
#Requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Output = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Source = (Resolve-Path -LiteralPath $Source).Path
$commit = (& git -C $Source rev-parse HEAD).Trim()
$dirty = [bool](& git -C $Source status --porcelain -- hooks claude codex)

function Import-SpecFunction {
    # Defines the named functions from one source file in this script's scope, without
    # running anything else in that file.
    param([string]$Path, [string[]]$Name)
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source $Path), [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$Path does not parse: $($errors[0])" }
    foreach ($n in $Name) {
        $definition = @($ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $n }, $false))
        if ($definition.Count -ne 1) { throw "$Path defines $n $($definition.Count) times" }
        . ([scriptblock]::Create($definition[0].Extent.Text.Replace("function $n", "function script:$n")))
    }
}

# decision-mqtt.ps1 is loaded whole, because its top level is its configuration. Checked to be
# only assignments, so a future change that adds work there is refused rather than run.
$mqttPath = Join-Path $Source 'hooks\decision-mqtt.ps1'
$mqttAst = [System.Management.Automation.Language.Parser]::ParseFile($mqttPath, [ref]$null, [ref]$null)
foreach ($statement in $mqttAst.EndBlock.Statements) {
    if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { continue }
    if ($statement -is [System.Management.Automation.Language.AssignmentStatementAst]) { continue }
    throw "decision-mqtt.ps1 has top-level code that is not an assignment: $($statement.Extent.Text)"
}

Import-SpecFunction 'hooks\decision-bridge-common.ps1' @(
    'Get-BridgeMachineSlug', 'Remove-CopilotTemplateMarkup', 'Assert-HomeAssistantServiceTarget',
    'Get-BridgeThoughtLine', 'Get-CopilotSafeSessionKey', 'Get-CopilotSessionDisplay')
$script:DecisionMaxFormFields = 12
. $mqttPath
Import-SpecFunction 'hooks\daemon-activity.ps1' @('Get-DaemonBackgroundAgentCount', 'Get-DaemonBackgroundShellCount', 'Get-DaemonCompactionCount', 'Add-DaemonCardText', 'Add-DaemonAnswerStamp')
Import-SpecFunction 'hooks\daemon-sessions.ps1' @('Add-DaemonTuningAttributes')
Import-SpecFunction 'hooks\daemon-usage.ps1' @('Get-BridgeUsageLabel', 'ConvertTo-BridgeUsageInstant', 'Get-BridgeUsageWindow', 'New-BridgeUsageRecord')
Import-SpecFunction 'claude\hooks\claude-session.ps1' @('Get-ClaudeSessionDisplay')
Import-SpecFunction 'codex\hooks\codex-session.ps1' @('Get-CodexSessionDisplay')

# The daemon's configuration the loaded functions read (hooks/agent-bridge-daemon.ps1).
$script:DaemonConfig = @{ ResponseMaxChars = 6000; ReasoningMaxChars = 4000 }
# Add-DaemonCardText asks the agent table whether reasoning is inline (hooks/daemon-agents.ps1:
# Copilot, Claude and Codex are; the default entry, for anything else, is not).
function script:Get-DaemonEntryKind { param($Entry) [string]$Entry.Kind }
function script:Get-DaemonAgent { param([string]$Kind) @{ InlineReasoning = ($Kind -in @('copilot', 'claude', 'codex')) } }

$script:Recorded = [System.Collections.Generic.List[object]]::new()
function script:Invoke-HomeAssistantService {
    param([string]$Domain, [string]$Service, [hashtable]$Data, [hashtable]$Headers, [int]$TimeoutSec = 15)
    Assert-HomeAssistantServiceTarget -Data $Data
    if ("$Domain.$Service" -ne 'mqtt.publish') { throw "the contract only publishes; $Domain.$Service was called" }
    $script:Recorded.Add([ordered]@{ topic = [string]$Data.topic; payload = [string]$Data.payload; retain = [bool]$Data.retain; qos = [int]$Data.qos })
}
$headers = @{ Authorization = 'Bearer not-a-token' }

function ConvertTo-Fixture {
    # A publication as a fixture: the payload parsed when it is JSON (key order is not part of
    # the contract; Home Assistant parses it), and the clock-dependent stamps replaced by a
    # marker the tests replace the same way.
    param($Publication)
    $payload = [string]$Publication.payload
    $value = $payload
    $json = $false
    if ($payload.StartsWith('{') -or $payload.StartsWith('[')) {
        # -DateKind String: a timestamp is recorded as the text that was published, not
        # reparsed into this machine's time zone.
        $value = $payload | ConvertFrom-Json -AsHashtable -Depth 20 -DateKind String
        if ($value -is [System.Collections.IDictionary] -and $value.Contains('updated')) { $value['updated'] = '<now>' }
        $json = $true
    }
    [ordered]@{ topic = $Publication.topic; retain = $Publication.retain; qos = $Publication.qos; json = $json; payload = $value }
}

function Get-Published {
    param([scriptblock]$Action)
    $script:Recorded.Clear()
    & $Action | Out-Null
    , @($script:Recorded | ForEach-Object { ConvertTo-Fixture $_ })
}

$machine = 'DSWETT-DEV-VM1'
$slug = Get-BridgeMachineSlug -MachineName $machine
$copilotId = '6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20'
$claudeId = 'c1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f607'
$codexId = '019a2b3c-4d5e-7f60-8192-a3b4c5d6e7f8'
$sessionName = 'Copilot: Fixture session'
$node = Get-CopilotMqttNodeId -SessionId $copilotId

# A scratch folder inside this checkout (never the system temp), for the Copilot workspace file
# Get-CopilotSessionDisplay reads.
$scratch = Join-Path $PSScriptRoot '.scratch'
if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
[void](New-Item -ItemType Directory -Path $scratch)
try {
    $script:DecisionBridgeConfig = @{ SessionStateRoot = $scratch }
    function New-Workspace([string]$Id, [AllowNull()][string]$Name) {
        $dir = Join-Path $scratch (Get-CopilotSafeSessionKey -SessionId $Id)
        [void](New-Item -ItemType Directory -Path $dir -Force)
        if ($Name) { Set-Content -LiteralPath (Join-Path $dir 'workspace.yaml') -Value "id: $Id`nname: $Name`n" -Encoding utf8 }
    }
    New-Workspace $copilotId 'Fixture session'
    New-Workspace '7b2d0f1e-0000-4000-8000-000000000001' ('Fix the build ' + ('x' * 150))
    New-Workspace '7b2d0f1e-0000-4000-8000-000000000002' "Rename {{ states('sensor.secret') }} please"
    New-Workspace '7b2d0f1e-0000-4000-8000-000000000003' $null

    $display = [ordered]@{
        copilot = @(
            @($copilotId, '7b2d0f1e-0000-4000-8000-000000000001', '7b2d0f1e-0000-4000-8000-000000000002', '7b2d0f1e-0000-4000-8000-000000000003') | ForEach-Object {
                $title = $null
                $yaml = Join-Path (Join-Path $scratch $_) 'workspace.yaml'
                if (Test-Path -LiteralPath $yaml) { $title = ((Get-Content -LiteralPath $yaml) | Where-Object { $_ -like 'name: *' }).Substring(6) }
                [ordered]@{ sessionId = $_; title = $title; name = (Get-CopilotSessionDisplay -SessionId $_ -WorkingDirectory 'C:\work\repo').Name }
            })
        claude = @(
            @(@($claudeId, 'C:\Users\me\repos\agent-bridge'), @($claudeId, '/home/me/{{ x }}'), @($claudeId, '')) | ForEach-Object {
                [ordered]@{ sessionId = $_[0]; workingDirectory = $_[1]; name = (Get-ClaudeSessionDisplay -SessionId $_[0] -WorkingDirectory $_[1]).Name }
            })
        codex = @(
            @(@($codexId, 'C:\Users\me\repos\agent-bridge'), @($codexId, ('C:\' + ('d' * 130))), @($codexId, '')) | ForEach-Object {
                [ordered]@{ sessionId = $_[0]; workingDirectory = $_[1]; name = (Get-CodexSessionDisplay -SessionId $_[0] -WorkingDirectory $_[1]).Name }
            })
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}

$tuning = [pscustomobject]@{ Model = 'gpt-6.1-sol'; Effort = ''; Context = ''; BackgroundAgents = @('agent-1', 'agent-2'); BackgroundShells = @(); Compactions = 2 }
$statusAttributes = Add-DaemonTuningAttributes -Attributes @{
    session = $sessionName; machine = $machine; process_id = 4242; updated = '<now>'
} -Tuning $tuning
$bareStatus = Add-DaemonTuningAttributes -Attributes @{
    session = $sessionName; machine = $machine; process_id = 4242; updated = '<now>'
} -Tuning ([pscustomobject]@{ Model = ''; Effort = ''; Context = ''; Compactions = 0 })
$shellStatus = Add-DaemonTuningAttributes -Attributes @{
    session = $sessionName; machine = $machine; process_id = 4242; updated = '<now>'
} -Tuning ([pscustomobject]@{ Model = 'opus'; BackgroundShells = @('shell-1'); Compactions = 1 })

function New-CardDetail([string]$Kind, [bool]$Verbose, [string]$Response, [string]$Message, [bool]$Thinking, [string]$Reasoning) {
    $entry = [pscustomobject]@{ Kind = $Kind; LastResponse = $Response; LastMessage = $Message; LastMessageIsThinking = $Thinking; LastReasoning = $Reasoning }
    $detail = @{ session = $sessionName; machine = $machine; verbose = $Verbose; updated = '<now>'; driver = 'human'; history = @('Reading your message', 'Running: view', 'Done.') }
    Add-DaemonCardText -Entry $entry -Detail $detail -VerboseOn $Verbose
    Add-DaemonAnswerStamp -Entry $entry -Detail $detail
    $detail
}

function New-Card([string]$Kind, [bool]$Verbose, [string]$Response, [string]$Message, [bool]$Thinking, [string]$Reasoning) {
    [ordered]@{
        kind = $Kind; verbose = $Verbose; response = $Response; message = $Message; thinking = $Thinking; reasoning = $Reasoning
        detail = (New-CardDetail $Kind $Verbose $Response $Message $Thinking $Reasoning)
    }
}

$longResponse = 'A' * 6100
$cards = [ordered]@{
    inlineQuiet = New-Card 'copilot' $false 'Done.' 'Thinking hard' $true 'Thinking hard'
    inlineVerboseThinking = New-Card 'copilot' $true 'Done.' 'Thinking hard' $true 'Thinking hard'
    inlineVerboseText = New-Card 'claude' $true 'Done.' 'Done.' $false 'Earlier thought'
    separateVerbose = New-Card 'other' $true 'Done.' '' $false ('R' * 4100)
    separateQuiet = New-Card 'other' $false 'Done.' '' $false 'A thought'
    longResponse = New-Card 'copilot' $false $longResponse '' $false ''
    empty = New-Card 'copilot' $false '' '' $false ''
}

# The capability flags a machine publishes (hooks/daemon-sessions.ps1, Get-DaemonLaunchCapabilities),
# as a host that serves no launch controls, no Detailed activity switch and no fleet reports them.
$readOnlyCapabilities = [ordered]@{
    newSession = $false; profile = $false; resume = $false; agent = $false; tuning = $false; permissions = $false
    detailed = $false; resumeShare = $false; dev = $true; fleet = ''
}
$descriptors = @(
    [ordered]@{ name = $sessionName; machine = $machine; node = $node; kind = 'copilot' }
    [ordered]@{ name = 'Claude: agent-bridge'; machine = $machine; node = (Get-CopilotMqttNodeId -SessionId $claudeId); kind = 'claude' }
)

$copilotWindows = @(
    (Get-BridgeUsageWindow -Key 'premium' -Label 'Premium requests' -PercentUsed 42.345 -Used 127 -Limit 300 -Unit 'requests' -ResetsAt '2026-11-01T00:00:00Z')
)
$claudeWindows = @(
    (Get-BridgeUsageWindow -Key 'five_hour' -Label '5h limit' -PercentUsed 12 -ResetsAt '2026-10-29T23:00:00Z')
    (Get-BridgeUsageWindow -Key 'seven_day' -Label 'Weekly' -PercentUsed 61.04 -ResetsAt '2020-01-08T00:00:00Z' -StartedAt '2020-01-01T00:00:00Z')
)
$records = @(
    (New-BridgeUsageRecord -Client 'copilot' -Account 'octocat' -Plan 'business' -Source 'api' -Windows $copilotWindows -MeasuredAt '2026-10-29T19:00:00.0000000+00:00')
    (New-BridgeUsageRecord -Client 'claude' -AccountId 'a1b2c3d4e5f60718' -Plan 'max' -Source 'api' -Windows $claudeWindows -MeasuredAt '2026-10-29T19:00:01.0000000+00:00')
    (New-BridgeUsageRecord -Client 'codex' -Source 'none' -Problem 'The Codex usage could not be read: no rollout' -MeasuredAt '2026-10-29T19:00:02.0000000+00:00')
)

$guardCases = @(
    [ordered]@{ name = 'no target'; data = @{ topic = 't'; payload = 'p' } }
    [ordered]@{ name = 'one entity'; data = @{ entity_id = 'button.agent_bridge_x_stop' } }
    [ordered]@{ name = 'one entity in a list'; data = @{ entity_id = @('button.agent_bridge_x_stop') } }
    [ordered]@{ name = 'one entity in target'; data = @{ target = @{ entity_id = 'button.agent_bridge_x_stop' } } }
    [ordered]@{ name = 'two entities'; data = @{ entity_id = @('button.a_stop', 'button.b_stop') } }
    [ordered]@{ name = 'comma list'; data = @{ entity_id = 'button.a_stop,button.b_stop' } }
    [ordered]@{ name = 'all'; data = @{ entity_id = 'all' } }
    [ordered]@{ name = 'area'; data = @{ area_id = 'office' } }
    [ordered]@{ name = 'device'; data = @{ target = @{ device_id = 'abc' } } }
    [ordered]@{ name = 'label'; data = @{ label_id = 'x' } }
    [ordered]@{ name = 'floor'; data = @{ floor_id = 'x' } }
    [ordered]@{ name = 'body and target'; data = @{ entity_id = 'button.a_stop'; target = @{ entity_id = 'button.a_stop' } } }
    [ordered]@{ name = 'empty'; data = @{ entity_id = ' ' } }
    [ordered]@{ name = 'malformed'; data = @{ entity_id = 'button' } }
    [ordered]@{ name = 'not a string'; data = @{ entity_id = 5 } }
)

$fixture = [ordered]@{
    source = [ordered]@{
        repository = 'danswett/agent-ha-bridge'
        commit = $commit
        dirty = $dirty
        generator = 'tools/ha-contract/Export-HaContract.ps1'
        note = 'Recorded by running agent-ha-bridge''s own functions with a recording transport. Regenerate; do not edit by hand.'
    }
    constants = [ordered]@{
        discoveryPrefix = $script:CopilotMqttConfig.DiscoveryPrefix
        topicRoot = $script:CopilotMqttConfig.TopicRoot
        stateMaxChars = $script:CopilotMqttConfig.StateMaxChars
        maxFields = $script:CopilotMqttMaxFields
    }
    slugs = @(@('DSWETT-DEV-VM1', 'DSWETT-HOME', 'dswett-arm8.redmond.corp.microsoft.com', 'A--B__C', '!!!', 'ÄÖÜ-Laptop', ('x' * 30), ('ab_' * 9)) | ForEach-Object {
        [ordered]@{ machineName = $_; slug = Get-BridgeMachineSlug -MachineName $_ }
    })
    nodes = @(@($copilotId, $claudeId, $codexId, 'abc', '---', 'ABCDEF0123456789ABCDEF', 'session one') | ForEach-Object {
        [ordered]@{ sessionId = $_; node = Get-CopilotMqttNodeId -SessionId $_ }
    })
    machine = [ordered]@{
        machineName = $machine
        slug = $slug
        node = Get-CopilotMqttMachineNode -Slug $slug
        topicRoot = Get-CopilotMqttMachineTopicRoot -Slug $slug
        device = Get-CopilotMqttMachineDevice -Slug $slug -MachineName $machine
        entityIds = [ordered]@{
            online = Get-BridgeMachineEntityId -Domain 'binary_sensor' -Key 'online' -Slug $slug
            sessions = Get-CopilotMqttGlobalEntityId -Slug $slug
            update = Get-BridgeMachineEntityId -Domain 'update' -Key 'update' -Slug $slug
            installUpdate = Get-BridgeMachineEntityId -Domain 'button' -Key 'install_update' -Slug $slug
            detailedActivity = Get-BridgeMachineEntityId -Domain 'input_boolean' -Key 'detailed_activity' -Slug $slug
            usageCopilot = Get-BridgeMachineEntityId -Domain 'sensor' -Key 'usage_copilot' -Slug $slug
        }
        topics = @(Get-CopilotMqttMachineTopic -Slug $slug)
        usageTopics = @(Get-CopilotMqttUsageTopics -Slug $slug)
        forgetTopics = @(Get-BridgeMachineForgetTopic -Slug $slug -SessionNodes @($node))
    }
    session = [ordered]@{
        sessionId = $copilotId
        sessionName = $sessionName
        node = $node
        topics = Get-CopilotMqttTopics -SessionId $copilotId
        entityIds = Get-CopilotMqttEntityIds -SessionId $copilotId
        discoveryTopics = @(Get-CopilotMqttSessionDiscoveryTopic -Node $node)
        stateTopics = @(Get-CopilotMqttSessionStateTopic -Node $node)
    }
    display = $display
    thoughtLines = @(@('**Checking the config**', "## Planning`nthen more", '   ', ('word ' * 40), "plain thought`nsecond") | ForEach-Object {
        [ordered]@{ text = $_; line = Get-BridgeThoughtLine -Text $_ }
    })
    statusAttributes = [ordered]@{ tuned = $statusAttributes; bare = $bareStatus; shell = $shellStatus }
    cards = $cards
    capabilities = $readOnlyCapabilities
    publications = [ordered]@{
        sessionPublish = Get-Published { Publish-CopilotMqttSession -SessionId $copilotId -SessionName $sessionName -Machine $machine -Headers $headers }
        sessionRename = Get-Published { Update-CopilotMqttSessionName -SessionId $copilotId -SessionName 'Copilot: Renamed' -Machine $machine -Headers $headers }
        sessionRemove = Get-Published { Remove-CopilotMqttSession -SessionId $copilotId -Headers $headers }
        statusTuned = Get-Published { Set-CopilotMqttStatus -SessionId $copilotId -Status 'agents' -Attributes $statusAttributes -Headers $headers }
        activityShort = Get-Published { Set-CopilotMqttActivity -SessionId $copilotId -Summary 'Running: view' -Detail $cards.inlineVerboseThinking.detail -Headers $headers }
        activityLong = Get-Published { Set-CopilotMqttActivity -SessionId $copilotId -Summary ('S' * 300) -Detail $null -Headers $headers }
        onlineConfig = Get-Published { Publish-CopilotMqttMachineOnlineConfig -Slug $slug -MachineName $machine -ExpireAfter 180 -Headers $headers }
        heartbeat = Get-Published { Publish-CopilotMqttMachineHeartbeat -Slug $slug -Headers $headers }
        globalStatus = Get-Published { Publish-CopilotMqttGlobalStatus -Sessions $descriptors -Capabilities $readOnlyCapabilities -Resumable @() -Slug $slug -MachineName $machine -DiscoveryHeld '' -Headers $headers }
        usage = Get-Published { Publish-CopilotMqttUsage -Records $records -Slug $slug -MachineName $machine -Headers $headers }
        update = Get-Published { Publish-CopilotMqttUpdate -InstalledVersion '0.1.0' -LatestVersion '0.1.0' -Slug $slug -Headers $headers }
        machineRemove = Get-Published { Remove-CopilotMqttMachineEntities -Slug $slug -Headers $headers }
    }
    serviceTargetGuard = @($guardCases | ForEach-Object {
        $allowed = $true
        try { Assert-HomeAssistantServiceTarget -Data $_.data } catch { $allowed = $false }
        [ordered]@{ name = $_.name; data = $_.data; allowed = $allowed }
    })
}

$json = $fixture | ConvertTo-Json -Depth 30
$full = [IO.Path]::GetFullPath($Output)
[void](New-Item -ItemType Directory -Path (Split-Path $full) -Force)
[IO.File]::WriteAllText($full, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $full from agent-ha-bridge $commit$(if ($dirty) { ' (with local changes)' })"
