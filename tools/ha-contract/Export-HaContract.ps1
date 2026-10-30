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
    [string]$Output = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-contract.json'),
    # Questions: how the daemon arms, clears and reads a session's answer controls.
    [string]$DecisionsOutput = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-decisions.json'),
    # Replies: how the daemon reads the reply card's payload and the reply box, and what it says.
    [string]$RepliesOutput = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-replies.json'),
    # Stops: End session's two presses, what the card says and what the status shows.
    [string]$StopsOutput = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-stops.json'),
    # Launch: the launch card, an agent's launch request, its refusals and outcomes.
    [string]$LaunchOutput = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures\ha-launch.json')
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

function Import-SpecAssignment {
    # Evaluates the named top-level $script: assignments from one source file, and nothing else
    # in it. Each must be a constant: anything that calls out is refused rather than run.
    param([string]$Path, [string[]]$Name)
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source $Path), [ref]$null, [ref]$null)
    foreach ($n in $Name) {
        $found = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq "`$script:$n" })
        if ($found.Count -ne 1) { throw "$Path assigns `$script:$n $($found.Count) times" }
        $right = $found[0].Right.Extent.Text
        if ($right -notmatch "^(\d+|'[^']*')$") { throw "$Path assigns `$script:$n something that is not a constant: $right" }
        . ([scriptblock]::Create($found[0].Extent.Text))
    }
}

Import-SpecFunction 'hooks\decision-bridge-common.ps1' @(
    'Get-BridgeMachineSlug', 'Remove-CopilotTemplateMarkup', 'Assert-HomeAssistantServiceTarget',
    'Get-BridgeThoughtLine', 'Get-CopilotSafeSessionKey', 'Get-CopilotSessionDisplay',
    'ConvertFrom-DecisionJson', 'Get-DecisionSchemaFieldChoices', 'Test-DecisionFieldIsText', 'Test-DecisionFieldIsMultiSelect',
    'Test-DecisionTakesTypedAnswer', 'Get-DecisionMultiSelectChecked', 'Get-DecisionMultiSelectCode',
    'Test-DecisionMultiSelectCodeSafe', 'Test-DecisionMultiSelectAllowsEmpty', 'Resolve-DecisionMultiSelectIndexes',
    'Get-DecisionMultiSelectMasks', 'Get-DecisionMultiSelectLabel', 'Get-DecisionMultiSelectSpelledChoices',
    'Get-DecisionMultiSelectCodes', 'Get-DecisionMultiSelectEmptyCode', 'Get-DecisionMultiSelectEmptyChoices',
    'Get-DecisionMultiSelectEmptyChoice', 'Test-DecisionMultiSelectEmptyChoice', 'Get-DecisionMultiSelectChoices',
    'Test-DecisionFieldsAnswerable', 'Get-DecisionSchemaFields')
Import-SpecAssignment 'hooks\decision-bridge-common.ps1' @(
    'DecisionMultiSelectMaxChoices', 'DecisionMultiSelectSeparator', 'DecisionMultiSelectMaxCodes',
    'DecisionMultiSelectMaxOptions', 'DecisionMultiSelectCodePattern', 'DecisionMultiSelectEmptyLabel',
    'DecisionMultiSelectEmptyCode')
Import-SpecFunction 'hooks\copilot-hooks.ps1' @('Get-CopilotMixedFormHint')
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
# Arming a question also drives its optimistic selects; nothing else is ever allowed through.
$script:RecordedServices = @('mqtt.publish')
function script:Invoke-HomeAssistantService {
    param([string]$Domain, [string]$Service, [hashtable]$Data, [hashtable]$Headers, [int]$TimeoutSec = 15)
    Assert-HomeAssistantServiceTarget -Data $Data
    if ("$Domain.$Service" -notin $script:RecordedServices) { throw "the contract does not call $Domain.$Service here" }
    if ("$Domain.$Service" -eq 'mqtt.publish') {
        $script:Recorded.Add([ordered]@{ topic = [string]$Data.topic; payload = [string]$Data.payload; retain = [bool]$Data.retain; qos = [int]$Data.qos })
    }
    else {
        $call = [ordered]@{ service = "$Domain.$Service"; entity_id = [string]$Data['entity_id'] }
        # Each call's own field: a select's option, a text's value.
        foreach ($field in @('option', 'value')) { if ($Data.ContainsKey($field)) { $call[$field] = [string]$Data[$field] } }
        $script:Recorded.Add($call)
    }
}
$headers = @{ Authorization = 'Bearer not-a-token' }

function ConvertTo-Fixture {
    # A publication as a fixture: the payload parsed when it is JSON (key order is not part of
    # the contract; Home Assistant parses it), and the clock-dependent stamps replaced by a
    # marker the tests replace the same way.
    param($Publication)
    if ($Publication.Contains('service')) { return $Publication }
    $payload = [string]$Publication.payload
    $value = $payload
    $json = $false
    if ($payload.StartsWith('{') -or $payload.StartsWith('[')) {
        # -DateKind String: a timestamp is recorded as the text that was published, not
        # reparsed into this machine's time zone.
        $value = $payload | ConvertFrom-Json -AsHashtable -Depth 20 -DateKind String
        if ($value -is [System.Collections.IDictionary] -and $value.Contains('updated')) { $value['updated'] = '<now>' }
        if ($value -is [System.Collections.IDictionary] -and $value.Contains('asked_at')) { $value['asked_at'] = '<now>' }
        # The heartbeat's time, which is all its attributes topic carries.
        if ($Publication.topic -like '*/online/attr' -and $value -is [System.Collections.IDictionary] -and $value.Contains('at')) { $value['at'] = '<now>' }
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
# A publisher that takes no machine name (Publish-CopilotMqttUpdate) names its device after the
# computer it runs on, which made a recording depend on where it was made: on any computer but
# the fixture's own, the update entity's device came out under that computer's name. The
# fixture's machine stands in for "this computer", as it does everywhere else here.
$specMachineDevice = ${function:Get-CopilotMqttMachineDevice}
function script:Get-CopilotMqttMachineDevice {
    param([string]$Slug, [string]$MachineName)
    if (-not $MachineName) { $MachineName = $machine }
    & $specMachineDevice -Slug $Slug -MachineName $MachineName
}
# The same for whatever asks this computer's slug without naming it (the launch outcomes' topic,
# through Get-CopilotMqttMachineTopicRoot): the fixture's machine, not the recorder's.
$specMachineSlug = ${function:Get-BridgeMachineSlug}
function script:Get-BridgeMachineSlug {
    param([string]$MachineName)
    if (-not $PSBoundParameters.ContainsKey('MachineName')) { $MachineName = $machine }
    & $specMachineSlug -MachineName $MachineName
}
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
    newSession = $false; profile = $false; resume = $false; agent = $false; tuning = $false; permissions = $false; launchRequest = 0
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
# Further Copilot logins (usage.copilotAccounts), each on the numbered sensor of its place, as
# Get-BridgeCopilotLoginAllowance tags them: one read, and one gh holds no token for.
$further = New-BridgeUsageRecord -Client 'copilot' -Account 'hubot' -Plan 'individual' -Source 'api' -Windows $copilotWindows -MeasuredAt '2026-10-29T19:00:03.0000000+00:00'
$further['sensor'] = 'copilot_2'
$records += $further
$unread = New-BridgeUsageRecord -Client 'copilot' -Account 'monalisa' -Source 'none' -Problem 'No Copilot credential was available for monalisa (gh has no token for monalisa - run gh auth login).' -MeasuredAt '2026-10-29T19:00:04.0000000+00:00'
$unread['sensor'] = 'copilot_4'
$records += $unread

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

function ConvertTo-Stable {
    # Hashtable key order changes from one process to the next, which turned every regeneration
    # into a diff of the whole file. Unordered tables are written with their keys sorted;
    # ordered ones and objects keep the order they were built in.
    param($Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [System.Collections.Hashtable]) {
        $sorted = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object { [string]$_ } -CaseSensitive)) { $sorted[$key] = ConvertTo-Stable $Value[$key] }
        return $sorted
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in @($Value.Keys)) { $copy[$key] = ConvertTo-Stable $Value[$key] }
        return $copy
    }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $copy = [ordered]@{}
        foreach ($p in $Value.PSObject.Properties) { $copy[$p.Name] = ConvertTo-Stable $p.Value }
        return $copy
    }
    if ($Value -is [System.Collections.IEnumerable]) { return , @($Value | ForEach-Object { ConvertTo-Stable $_ }) }
    $Value
}

$json = (ConvertTo-Stable $fixture) | ConvertTo-Json -Depth 30
$full = [IO.Path]::GetFullPath($Output)
[void](New-Item -ItemType Directory -Path (Split-Path $full) -Force)
[IO.File]::WriteAllText($full, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $full from agent-ha-bridge $commit$(if ($dirty) { ' (with local changes)' })"

# ---- Questions -------------------------------------------------------------------------------
# What the daemon publishes to arm a session's answer controls for a question, to clear them, and
# how it reads a multi-select slot back. Driven through Set-CopilotMqttDecision and
# Clear-CopilotMqttDecision, with fields built by Get-DecisionSchemaFields from ask_user schemas,
# exactly as the Copilot hook does (hooks/copilot-hooks.ps1).

$script:DecisionBridgeConfig = @{ DecisionQuestionMaxChars = 6000 }
$script:RecordedServices = @('mqtt.publish', 'select.select_option')
# The answer channels' snapshot is the daemon's own state file; arming here always has one.
function script:Initialize-CopilotDecisionBaseline { [pscustomobject]@{ Recorded = $true; Reason = '' } }
# What the last question left armed, which the daemon reads from the decision entity.
$script:ArmedSlots = -1
function script:Get-CopilotMqttArmedFieldCount { $script:ArmedSlots }
# Waits for Home Assistant to take a discovery config before driving the select; the call it
# makes is what is recorded.
function script:Set-CopilotMqttSelectOption {
    param([string]$EntityId, [string]$Option, [hashtable]$Headers)
    Invoke-HomeAssistantService -Domain 'select' -Service 'select_option' -Headers $Headers -Data @{ entity_id = $EntityId; option = $Option }
    $true
}
function script:Start-Sleep { }

function ConvertTo-FieldFixture {
    param($Field)
    [ordered]@{
        name = [string]$Field.Name; label = [string]$Field.Label
        options = @($Field.Options | ForEach-Object { [string]$_ })
        values = @($Field.Values)
        isText = [bool]$Field.IsText; multiSelect = [bool]$Field.MultiSelect
        defaultIndexes = @($Field.DefaultIndexes); allowsEmpty = [bool]$Field.AllowsEmpty
    }
}

$schemas = [ordered]@{
    single = '{"type":"object","properties":{"release":{"type":"string","title":"Release","enum":["cut_now","wait"],"enumNames":["Cut the release now","Wait for the fix"]}},"required":["release"]}'
    boolean = '{"type":"object","properties":{"push":{"type":"boolean","title":"Push now"}}}'
    text = '{"type":"object","properties":{"name":{"type":"string","title":"Name"}}}'
    form = '{"type":"object","properties":{"visibility":{"type":"string","title":"Visibility","oneOf":[{"const":"public","title":"Public"},{"const":"private","title":"Private"}]},"push":{"type":"boolean","title":"Push now"},"areas":{"type":"array","title":"Areas","items":{"type":"string","enum":["Auth","Search","Billing"]},"default":["Search"]},"notes":{"type":"string","title":"Anything else?"}},"required":["visibility","push"]}'
    manyMulti = '{"type":"object","properties":{"followups":{"type":"array","title":"Follow-ups","minItems":1,"items":{"type":"string","enum":["one","two","three","four","five","six","seven"]}}}}'
    unsafeMulti = '{"type":"object","properties":{"picks":{"type":"array","title":"Picks","items":{"type":"string","enum":["A","B","#1"]}}}}'
    emptyMulti = '{"type":"object","properties":{"extras":{"type":"array","title":"Extras","items":{"type":"string","enum":["Docs","(none)"]},"default":[]}}}'
}

$questions = [System.Collections.Generic.List[object]]::new()
function Add-Question {
    param([string]$Name, [string]$Question, [string[]]$Choices = @(), [object[]]$Fields = @(), [int]$Armed = -1, [switch]$AnswerOnTap, [string]$Schema)
    $script:ArmedSlots = $Armed
    $published = Get-Published {
        Set-CopilotMqttDecision -SessionId $copilotId -SessionName $sessionName -Machine $machine -Question $Question `
            -Choices $Choices -Fields $Fields -DecisionId 'decision-1' -Headers $headers -AnswerOnTap:$AnswerOnTap
    }
    $questions.Add([ordered]@{
        name = $Name; schema = $Schema; question = $Question; choices = @($Choices); answerOnTap = [bool]$AnswerOnTap; armedSlots = $Armed
        fields = @($Fields | ForEach-Object { ConvertTo-FieldFixture $_ })
        takesTypedAnswer = [bool](Test-DecisionTakesTypedAnswer -Fields $(if ($Fields.Count -ge 1) { $Fields } else { @() }) -Choices $Choices -AnswerOnTap:$AnswerOnTap)
        published = $published
    })
}

# As the Copilot hook does it: a lone field's options also ride as the selector's choices, and a
# form mixing dropdowns with a free-text field says which control takes the words.
function Add-SchemaQuestion {
    param([string]$Name, [string]$Message, [int]$Armed = -1)
    $schema = ConvertFrom-DecisionJson -Json $schemas[$Name]
    $fields = @(Get-DecisionSchemaFields -Schema $schema)
    [string[]]$choices = @()
    if ($fields.Count -eq 1) { $choices = @($fields[0].Options) }
    $text = @($fields | Where-Object { Test-DecisionFieldIsText -Field $_ }) | Select-Object -First 1
    if ($fields.Count -gt 1 -and $null -ne $text) { $Message = "$Message`n`n$(Get-CopilotMixedFormHint -Label ([string]$text.Label))" }
    Add-Question -Name $Name -Question $Message -Choices $choices -Fields $fields -Armed $Armed -Schema $schemas[$Name]
}

Add-Question -Name 'freeform' -Question 'What should the release be called?'
Add-Question -Name 'approval' -Question "Codex wants to run:`n`ngit push" -Choices @('Approve', 'Deny') -AnswerOnTap
Add-Question -Name 'longQuestion' -Question ('Q' * 6100)
Add-SchemaQuestion 'single' 'Cut the release now?'
Add-SchemaQuestion 'boolean' 'Push the branch?'
Add-SchemaQuestion 'text' 'What should it be called?'
Add-SchemaQuestion 'form' 'Set up the repository' -Armed 0
Add-SchemaQuestion 'manyMulti' 'Which follow-ups?' -Armed 6
Add-SchemaQuestion 'unsafeMulti' 'Which picks?'
Add-SchemaQuestion 'emptyMulti' 'Any extras?' -Armed 1

$clears = [ordered]@{}
foreach ($armed in @(4, 0, -1)) {
    $script:ArmedSlots = $armed
    $clears["armed$armed"] = Get-Published { Clear-CopilotMqttDecision -SessionId $copilotId -SessionName $sessionName -Machine $machine -Headers $headers }
}

# How each multi-select field's slot values are read back.
$multi = foreach ($pair in @(@('form', 'areas'), @('manyMulti', 'followups'), @('unsafeMulti', 'picks'), @('emptyMulti', 'extras'))) {
    $field = @(Get-DecisionSchemaFields -Schema (ConvertFrom-DecisionJson -Json $schemas[$pair[0]])) | Where-Object { $_.Name -eq $pair[1] }
    $offered = @(Get-DecisionMultiSelectChoices -Field $field)
    $probes = @($offered) + @('#1,1', '#9', '#0', '(none)', 'Choose...', 'A + B + C', '#1,', '')
    [ordered]@{
        schema = $pair[0]; field = $pair[1]
        choices = $offered
        codes = @(Get-DecisionMultiSelectCodes -Field $field)
        codeSafe = [bool](Test-DecisionMultiSelectCodeSafe -Field $field)
        emptyChoice = [string](Get-DecisionMultiSelectEmptyChoice -Field $field)
        resolve = @($probes | Select-Object -Unique | ForEach-Object {
            [ordered]@{
                value = $_
                indexes = @(Resolve-DecisionMultiSelectIndexes -Field $field -Value $_)
                empty = [bool](Test-DecisionMultiSelectEmptyChoice -Field $field -Choice $_)
            }
        })
    }
}

$decisions = [ordered]@{
    source = $fixture.source
    sessionId = $copilotId; sessionName = $sessionName; machine = $machine
    constants = [ordered]@{
        separator = $script:DecisionMultiSelectSeparator; emptyLabel = $script:DecisionMultiSelectEmptyLabel
        emptyCode = $script:DecisionMultiSelectEmptyCode; questionMaxChars = $script:DecisionBridgeConfig.DecisionQuestionMaxChars
        replyMaxChars = $script:CopilotMqttConfig.ReplyMaxChars
    }
    mixedHint = Get-CopilotMixedFormHint -Label 'Anything else?'
    questions = $questions
    clears = $clears
    multiSelect = @($multi)
}
$full = [IO.Path]::GetFullPath($DecisionsOutput)
[IO.File]::WriteAllText($full, ((ConvertTo-Stable $decisions) | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $full"

# ---- Replies ---------------------------------------------------------------------------------
# What the daemon does with a session's reply card payload and its reply box when nothing is being
# asked: Send-DaemonCardPayload and Send-DaemonReplyBoxText, through Invoke-DaemonReply, with the
# Home Assistant reads, the staging of attachments and the typing itself replaced by stand-ins that
# record what they were asked. The card notes (Set-DaemonTransientActivity) and every service call
# are the contract; the typed text is recorded for reference (the host stages files its own way).

Import-SpecFunction 'hooks\daemon-replies.ps1' @('Get-BridgeReplyPayload', 'New-BridgeAttachmentPrompt', 'Send-DaemonCardPayload', 'Send-DaemonReplyBoxText', 'Invoke-DaemonReply')
# What a failed delivery's card says to do about it (agent-ha-bridge #249): the daemon's own rule,
# which adds a hint only for a detail it recognises.
Import-SpecFunction 'hooks\decision-inject.ps1' @('Get-BridgeConsoleFailureHint')
Import-SpecFunction 'hooks\agent-bridge-daemon.ps1' @('Set-DaemonSessionProperty', 'Set-DaemonDriverPending', 'Update-DaemonDriverPendingStamp', 'Test-DaemonPayloadJustDelivered', 'Test-DaemonDriverPending')

function Get-SpecConfigConstant {
    # Values from the daemon's $script:DaemonConfig hashtable, each of which must be a constant.
    param([string]$Path, [string[]]$Name)
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source $Path), [ref]$null, [ref]$null)
    $assign = @($ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.AssignmentStatementAst] -and $a.Left.Extent.Text -eq '$script:DaemonConfig' }, $true))
    if ($assign.Count -ne 1) { throw "$Path assigns `$script:DaemonConfig $($assign.Count) times" }
    $table = $assign[0].Right.Find({ param($a) $a -is [System.Management.Automation.Language.HashtableAst] }, $true)
    $values = [ordered]@{}
    foreach ($n in $Name) {
        $pair = @($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq $n })
        if ($pair.Count -ne 1) { throw "`$script:DaemonConfig has $n $($pair.Count) times" }
        $text = $pair[0].Item2.Extent.Text
        if ($text -notmatch "^(\d+|'[^']*')$") { throw "`$script:DaemonConfig.$n is not a constant: $text" }
        $values[$n] = [scriptblock]::Create($text).Invoke()[0]
    }
    $values
}
$replyConfig = Get-SpecConfigConstant 'hooks\agent-bridge-daemon.ps1' @('ReplyCommitAttempts', 'ReplyCommitWaitMs', 'SubmitArmSeconds', 'DriverArmSeconds', 'ReplyBlankValue')
foreach ($key in $replyConfig.Keys) { $script:DaemonConfig[$key] = $replyConfig[$key] }
$fileLimit = (Get-Command Save-BridgeReplyFile -ErrorAction SilentlyContinue)
$replyAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source 'hooks\daemon-replies.ps1'), [ref]$null, [ref]$null)
$saveFile = $replyAst.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Save-BridgeReplyFile' }, $true)
$maxBytes = ($saveFile.Body.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'MaxBytes' }).DefaultValue.Extent.Text

$script:RecordedServices = @('mqtt.publish', 'text.set_value')
$script:DaemonLive = $null
$script:DaemonAgents = @{}
$script:States = @{}
$script:Notes = [System.Collections.Generic.List[object]]::new()
$script:Typed = [System.Collections.Generic.List[string]]::new()
$script:Deliver = $true
$script:FailImage = $false
$script:AfterTyping = $null
$script:DeletedImages = [System.Collections.Generic.List[string]]::new()
function script:Write-DaemonLog { param([string]$Message) }
function script:Get-DaemonEntityState { param([string]$EntityId, [hashtable]$Headers) if (-not $script:States.ContainsKey($EntityId)) { throw "no $EntityId" }; $script:States[$EntityId] }
function script:Get-HomeAssistantState { param([string]$EntityId, [hashtable]$Headers) if (-not $script:States.ContainsKey($EntityId)) { throw "no $EntityId" }; $script:States[$EntityId] }
function script:Get-BridgeDriverFromState { param($State) if ($State.PSObject.Properties['context'] -and $State.context.user_id -eq 'agent-user') { 'agent' } else { 'human' } }
function script:Set-DaemonTransientActivity {
    param([string]$SessionId, [string]$Summary, [hashtable]$Extra = @{}, [hashtable]$Headers)
    $shown = [ordered]@{}
    foreach ($key in @($Extra.Keys | Sort-Object)) { $shown[$key] = if ($key -eq 'at') { '<time>' } else { [string]$Extra[$key] } }
    $script:Notes.Add([ordered]@{ summary = $Summary; extra = $shown })
}
function script:Save-BridgeReplyAttachment { param([string]$ImageId, [string]$Name, [hashtable]$Headers) if ($script:FailImage) { '' } else { "C:\staged\$ImageId.png" } }
function script:Save-BridgeReplyFile { param([string]$Name, [string]$Base64, [int]$MaxBytes = 262144) "C:\staged\$Name" }
function script:Remove-BridgeHomeAssistantImage { param([string]$ImageId) $script:DeletedImages.Add($ImageId) }
function script:Remove-BridgeStaleAttachment { }
function script:Send-CopilotSessionPrompt {
    param([string]$SessionId, [string]$Text, [int]$ProcessId)
    $script:Typed.Add($Text)
    if ($script:AfterTyping) { & $script:AfterTyping }
    [pscustomobject]@{ Delivered = $script:Deliver; Detail = $(if ($script:Deliver) { 'typed' } else { 'the console refused the keys' }); ProcessId = 4242 }
}

$replyNode = Get-CopilotMqttNodeId -SessionId $copilotId
$payloadEntity = "sensor.${replyNode}_reply_payload"
$submitEntity = "button.${replyNode}_submit"
$boxEntity = "text.${replyNode}_reply"
function New-PayloadState([hashtable]$Attributes, [string]$Stamp = '2026-10-29T20:00:00.000Z') {
    [pscustomobject]@{ state = $Stamp; attributes = [pscustomobject]$Attributes }
}
function Invoke-ReplyCase {
    param([string]$Name, [hashtable]$States, [hashtable]$Entry = @{}, [bool]$Deliver = $true, [switch]$FailImage, [scriptblock]$AfterTyping, [ValidateSet('card', 'box')][string]$Path)
    $script:Recorded.Clear(); $script:Notes.Clear(); $script:Typed.Clear(); $script:DeletedImages.Clear()
    $script:States = $States; $script:Deliver = $Deliver; $script:FailImage = [bool]$FailImage; $script:AfterTyping = $AfterTyping
    $sessionEntry = [pscustomobject]@{ Name = $sessionName; Machine = $machine }
    foreach ($key in $Entry.Keys) { $sessionEntry | Add-Member -NotePropertyName $key -NotePropertyValue $Entry[$key] }
    $handled = $null
    if ($Path -eq 'card') { $handled = [bool](@(Send-DaemonCardPayload -SessionId $copilotId -Entry $sessionEntry -Headers $headers) | Select-Object -Last 1) }
    else { Send-DaemonReplyBoxText -SessionId $copilotId -Entry $sessionEntry -Headers $headers }
    $after = [ordered]@{}
    foreach ($property in @('LastReplyPayloadAt', 'LastSubmitAt', 'PendingSubmitAt', 'Driver', 'DriverPending')) {
        if ($sessionEntry.PSObject.Properties[$property]) { $after[$property] = [string]$sessionEntry.$property }
    }
    [ordered]@{
        name = $Name; path = $Path; handled = $handled
        notes = @($script:Notes); typed = @($script:Typed); deletedImages = @($script:DeletedImages)
        calls = @($script:Recorded | Where-Object { $_.Contains('service') }); entry = $after
    }
}

$hello = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('hello'))
$press = [pscustomobject]@{ state = '2026-10-29T20:00:05.000000+00:00'; context = [pscustomobject]@{ user_id = 'person' } }
$agentPress = [pscustomobject]@{ state = '2026-10-29T20:00:05.000000+00:00'; context = [pscustomobject]@{ user_id = 'agent-user' } }
$replyCases = [System.Collections.Generic.List[object]]::new()
$replyCases.Add((Invoke-ReplyCase -Name 'cardText' -Path card -States @{ $payloadEntity = (New-PayloadState @{ text = 'Ship it after the hotfix' }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardAttachments' -Path card -States @{ $payloadEntity = (New-PayloadState @{ text = 'What is this?'; images = @([pscustomobject]@{ id = '0123abcd'; name = 'shot.png' }); files = @([pscustomobject]@{ name = 'notes.txt'; b64 = $hello }) }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardAgent' -Path card -States @{ $payloadEntity = (New-PayloadState @{ text = 'Carry on'; driver = 'agent' }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardFailed' -Path card -Deliver $false -States @{ $payloadEntity = (New-PayloadState @{ text = 'Ship it' }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardImageFails' -Path card -FailImage -States @{ $payloadEntity = (New-PayloadState @{ text = 'Look'; images = @([pscustomobject]@{ id = '0123abcd'; name = 'shot.png' }) }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardIncomplete' -Path card -States @{ $payloadEntity = (New-PayloadState @{ text = 'Look'; images = @([pscustomobject]@{ name = 'shot.png' }) }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardTagged' -Path card -States @{ $payloadEntity = (New-PayloadState @{ text = 'For the form'; decision_id = 'decision-1' }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardBlank' -Path card -States @{ $payloadEntity = (New-PayloadState @{ text = '  ' }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'cardRepeat' -Path card -Entry @{ LastReplyPayloadAt = '2026-10-29T20:00:00.000Z' } -States @{ $payloadEntity = (New-PayloadState @{ text = 'Ship it' }) }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxText' -Path box -States @{ $submitEntity = $press; $boxEntity = [pscustomobject]@{ state = 'Ship it after the hotfix' } }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxFailed' -Path box -Deliver $false -States @{ $submitEntity = $press; $boxEntity = [pscustomobject]@{ state = 'Ship it' } }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxFollowUpKept' -Path box -AfterTyping { $script:States[$boxEntity] = [pscustomobject]@{ state = 'and then the docs' } } -States @{ $submitEntity = $press; $boxEntity = [pscustomobject]@{ state = 'Ship it' } }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxBlank' -Path box -States @{ $submitEntity = $press; $boxEntity = [pscustomobject]@{ state = ' ' } }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxBlankAgent' -Path box -States @{ $submitEntity = $agentPress; $boxEntity = [pscustomobject]@{ state = '' } }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxBlankGivenUp' -Path box -Entry @{ PendingSubmitAt = $press.state; PendingSubmitSince = '2026-10-29T00:00:00.0000000+00:00' } -States @{ $submitEntity = $press; $boxEntity = [pscustomobject]@{ state = '' } }))
$replyCases.Add((Invoke-ReplyCase -Name 'boxRepeat' -Path box -Entry @{ LastSubmitAt = $press.state } -States @{ $submitEntity = $press; $boxEntity = [pscustomobject]@{ state = 'Ship it' } }))

$replies = [ordered]@{
    source = $fixture.source
    sessionId = $copilotId; machine = $machine
    constants = [ordered]@{
        replyCommitAttempts = $replyConfig.ReplyCommitAttempts; replyCommitWaitMs = $replyConfig.ReplyCommitWaitMs
        submitArmSeconds = $replyConfig.SubmitArmSeconds; driverArmSeconds = $replyConfig.DriverArmSeconds
        replyBlank = $replyConfig.ReplyBlankValue; inlineFileMaxBytes = [int]$maxBytes
    }
    cases = $replyCases
}
$full = [IO.Path]::GetFullPath($RepliesOutput)
[IO.File]::WriteAllText($full, ((ConvertTo-Stable $replies) | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $full"
# ---- Stops -----------------------------------------------------------------------------------
# End session (Invoke-PendingStops, Clear-DaemonExpiredStopArms, Get-DaemonCardSummary): a press of
# a session that is not idle arms and asks for a second; the second, or one press of an idle
# session, ends it. The process, the shared prompt record and the ending itself are stand-ins;
# the card notes, the status publishes and the pinned summary are the contract.

Import-SpecFunction 'hooks\daemon-launch.ps1' @('Set-DaemonStopArm', 'Remove-DaemonStopArm', 'Test-DaemonStopConfirms', 'Test-DaemonStopArmed',
    'Get-DaemonStopConfirmHint', 'Get-DaemonCardSummary', 'Clear-DaemonExpiredStopArms', 'Invoke-PendingStops')
Import-SpecFunction 'hooks\daemon-activity.ps1' @('ConvertTo-DaemonActivityInstant')
$stopConfig = Get-SpecConfigConstant 'hooks\agent-bridge-daemon.ps1' @('StopConfirmSeconds', 'StopLapseRetrySeconds')
foreach ($key in $stopConfig.Keys) { $script:DaemonConfig[$key] = $stopConfig[$key] }
$script:RecordedServices = @('mqtt.publish')
$script:DaemonStopArmed = @{}
$script:DaemonLaunchedPids = @{}
$script:BridgeIsWindows = $true
$script:DaemonReconcileNow = $false
$script:DaemonStartedAt = [DateTimeOffset]::Parse('2026-10-29T19:00:00Z')
$script:StopWorks = $true
function script:Write-BridgeStopPrompt { param([string]$SessionId, [string]$Hint, $Until) $true }
function script:Read-BridgeStopPromptStore { [pscustomobject]@{ State = 'Ok' } }
function script:Clear-BridgeStopPromptStore { $true }
function script:Get-DaemonProcessIdentity { param([int]$ProcessId) 638000000000000000L }
function script:Stop-BridgeCopilotSession {
    param([string]$SessionId, [int]$ProcessId)
    [pscustomobject]@{ Stopped = $script:StopWorks; Detail = $(if ($script:StopWorks) { 'exited' } else { 'the session did not exit' }) }
}

$stopNode = Get-CopilotMqttNodeId -SessionId $copilotId
$stopEntity = "button.${stopNode}_stop"
$stopPress = [pscustomobject]@{ state = '2026-10-29T20:00:07.000000+00:00' }
function Invoke-StopCase {
    param([string]$Name, [string]$Status, [hashtable]$Entry = @{}, [bool]$Works = $true, [switch]$Armed, [switch]$Lapse, [int]$ProcessId = 4242)
    $script:Recorded.Clear(); $script:Notes.Clear(); $script:DaemonStopArmed.Clear()
    $script:StopWorks = $Works
    $script:States = @{ $stopEntity = $stopPress }
    $sessionEntry = [pscustomobject]@{ Name = $sessionName; Machine = $machine; Status = $Status }
    foreach ($key in $Entry.Keys) { $sessionEntry | Add-Member -NotePropertyName $key -NotePropertyValue $Entry[$key] -Force }
    if ($Armed) { [void](Set-DaemonStopArm -SessionId $copilotId -Status $Status -ProcessId $ProcessId) }
    $state = @{ $copilotId = $sessionEntry }
    if ($Lapse) {
        $script:DaemonStopArmed[$copilotId].At = [DateTimeOffset]::Now.AddSeconds(-60)
        Clear-DaemonExpiredStopArms -Headers $headers -State $state
    }
    else {
        Invoke-PendingStops -Headers $headers -State $state -Live @{ $copilotId = [pscustomobject]@{ ProcessId = $ProcessId } }
    }
    [ordered]@{
        name = $Name; status = $Status
        notes = @($script:Notes)
        statuses = @($script:Recorded | Where-Object { $_.Contains('topic') -and $_.topic -like '*/status/state' } | ForEach-Object { $_.payload })
        armed = $script:DaemonStopArmed.ContainsKey($copilotId)
    }
}

$stopCases = [System.Collections.Generic.List[object]]::new()
$stopCases.Add((Invoke-StopCase -Name 'idleOnePress' -Status 'idle'))
$stopCases.Add((Invoke-StopCase -Name 'workingFirstPress' -Status 'working'))
$stopCases.Add((Invoke-StopCase -Name 'waitingFirstPress' -Status 'waiting'))
$stopCases.Add((Invoke-StopCase -Name 'agentsFirstPress' -Status 'agents'))
$stopCases.Add((Invoke-StopCase -Name 'shellFirstPress' -Status 'shell'))
$stopCases.Add((Invoke-StopCase -Name 'workingConfirmed' -Status 'working' -Armed))
$stopCases.Add((Invoke-StopCase -Name 'endFails' -Status 'idle' -Works $false))
$stopCases.Add((Invoke-StopCase -Name 'repeatPress' -Status 'working' -Entry @{ LastStopAt = $stopPress.state }))
$stopCases.Add((Invoke-StopCase -Name 'lapsed' -Status 'working' -Armed -Lapse))

# The card while armed: the summary the daemon publishes over the session's own, and the hint.
[void](Set-DaemonStopArm -SessionId $copilotId -Status 'waiting' -ProcessId 4242)
$pinnedDetail = @{}
$pinned = [ordered]@{ summary = (Get-DaemonCardSummary -SessionId $copilotId -Summary 'Reading files' -Detail $pinnedDetail); hint = $pinnedDetail['hint'] }
$script:DaemonStopArmed.Clear()

$stops = [ordered]@{
    source = $fixture.source
    sessionId = $copilotId; machine = $machine
    constants = [ordered]@{
        confirmSeconds = $stopConfig.StopConfirmSeconds; lapseRetrySeconds = $stopConfig.StopLapseRetrySeconds
        confirmNote = $script:CopilotEndSessionConfirmNote; lapsedNote = $script:CopilotEndSessionLapsedNote
    }
    hints = [ordered]@{
        working = (Get-DaemonStopConfirmHint -Status 'working'); waiting = (Get-DaemonStopConfirmHint -Status 'waiting')
        agents = (Get-DaemonStopConfirmHint -Status 'agents'); shell = (Get-DaemonStopConfirmHint -Status 'shell')
        unclassified = (Get-DaemonStopConfirmHint -Status ''); other = (Get-DaemonStopConfirmHint -Status 'compacting')
    }
    pinned = $pinned
    cases = $stopCases
}
$full = [IO.Path]::GetFullPath($StopsOutput)
[IO.File]::WriteAllText($full, ((ConvertTo-Stable $stops) | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $full"

# ---- Launch ----------------------------------------------------------------------------------
# The launch card (Publish-CopilotMqttNewSession) and what a press of Launch does with it: the
# labels its selectors offer, the resumable list's labels (Get-BridgeResumableSessions), an
# agent's launch request read from the prompt payload (Get-DaemonLaunchRequest) and refused when
# its settings name something this machine does not offer (Resolve-DaemonRequestedLaunch), the
# outcome an agent reads (Set-DaemonLaunchOutcome), and the capability flags a machine serving
# the card reports (Get-DaemonLaunchCapabilities). Folders, profiles, the agents installed and
# the readers of each agent's sessions are stand-ins; nothing is started.

Import-SpecFunction 'hooks\decision-bridge-common.ps1' @('Get-BridgePermissionOptions', 'Get-BridgePermissionLabel', 'Test-BridgePermissionAllowsAll',
    'Get-BridgeTuningAxes', 'Get-BridgeTuningAxisLabel', 'Get-BridgeTuningAxisIcon')
Import-SpecAssignment 'hooks\decision-bridge-common.ps1' @('BridgeTuningDefaultOption', 'BridgePermissionAskOption', 'BridgePermissionAllowOption')
Import-SpecFunction 'hooks\session-launch.ps1' @('Get-BridgeLauncher', 'Get-BridgeLauncherLabel', 'Resolve-BridgeLauncher', 'Get-BridgeResumableSessions')
Import-SpecFunction 'hooks\daemon-launch.ps1' @('ConvertTo-DaemonInstant', 'Get-DaemonLaunchMember', 'Get-DaemonLaunchRequest', 'Format-DaemonInstant',
    'Set-DaemonLaunchOutcome', 'Resolve-DaemonRequestedLaunch', 'Get-DaemonResumeLauncher')
Import-SpecFunction 'hooks\daemon-sessions.ps1' @('Get-DaemonLaunchCapabilities')

function Get-SpecLiteral {
    # A top-level $script: assignment whose value is a literal table: evaluated only when nothing
    # in it calls out (no command, no method call), so a table that does work is refused.
    param([string]$Path, [string]$Name)
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source $Path), [ref]$null, [ref]$null)
    $found = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq "`$script:$Name" })
    if ($found.Count -ne 1) { throw "$Path assigns `$script:$Name $($found.Count) times" }
    $calls = @($found[0].Right.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] -or $a -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true))
    if ($calls.Count -gt 0) { throw "$Path assigns `$script:$Name something that calls out: $($calls[0].Extent.Text)" }
    [scriptblock]::Create($found[0].Right.Extent.Text).InvokeReturnAsIs()
}
$script:BridgeTuningAxes = Get-SpecLiteral 'hooks\decision-bridge-common.ps1' 'BridgeTuningAxes'

# The agents' labels and the order their sessions are read in, from the launcher table itself
# (session-launch.ps1). The table holds the readers too, which run commands, so only these two
# constants are taken from each entry.
$launcherAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source 'hooks\session-launch.ps1'), [ref]$null, [ref]$null)
$launcherTable = @($launcherAst.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$script:BridgeLaunchers' })
if ($launcherTable.Count -ne 1) { throw "session-launch.ps1 assigns `$script:BridgeLaunchers $($launcherTable.Count) times" }
$outer = $launcherTable[0].Right.Find({ param($a) $a -is [System.Management.Automation.Language.HashtableAst] }, $true)
$script:BridgeLaunchers = [ordered]@{}
foreach ($pair in $outer.KeyValuePairs) {
    $kind = $pair.Item1.Extent.Text.Trim("'", '"')
    $inner = $pair.Item2.Find({ param($a) $a -is [System.Management.Automation.Language.HashtableAst] }, $true)
    $entry = @{}
    foreach ($slot in @('Label', 'Kind', 'ResumeOrder', 'NeedsFirstMessage')) {
        $value = @($inner.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq $slot })
        if ($value.Count -eq 1) {
            $text = $value[0].Item2.Extent.Text
            if ($text -notmatch "^('[^']*'|\d+|\`$true|\`$false)$") { throw "`$script:BridgeLaunchers.$kind.$slot is not a constant: $text" }
            $entry[$slot] = [scriptblock]::Create($text).InvokeReturnAsIs()
        }
    }
    $script:BridgeLaunchers[$kind] = $entry
}

$script:LaunchSettings = @{ 'newSession.enabled' = $true; 'newSession.resumeCount' = 12; 'newSession.allowAllTools' = $false; 'updates.installedFromSource' = $true }
$script:Installed = @('agency', 'copilot', 'claude', 'codex')
$script:Profiles = @('default', 'work')
function script:Get-BridgeSetting { param([string]$Name, $Default) if ($script:LaunchSettings.ContainsKey($Name)) { $script:LaunchSettings[$Name] } else { $Default } }
function script:Get-BridgeAvailableLaunchers { @($script:Installed) }
function script:Get-BridgeAgencyProfiles { @($script:Profiles) }
function script:Test-DaemonSharingEnabled { param([string]$Name) $false }
function script:Write-DaemonLog { param([string]$Message) }

# The card's controls, published for a machine with two workspaces, Agency's two profiles, two
# resumable sessions, four agents, and Copilot's tuning lists; then for a machine with nothing
# configured, where each selector still has to offer something.
$launchWorkspaces = @([pscustomobject]@{ Label = 'agent-bridge'; Path = 'C:\repos\agent-bridge' }, [pscustomobject]@{ Label = 'scratch'; Path = 'C:\scratch' })
$launchResumable = @([pscustomobject]@{ Label = 'Fix the relay - agent-bridge'; SessionId = 'a1b2c3d4-0000-4000-8000-000000000001' },
    [pscustomobject]@{ Label = 'Claude: Write docs - scratch'; SessionId = 'a1b2c3d4-0000-4000-8000-000000000002' })
$launchAgents = @('Agency', 'Copilot', 'Claude', 'Codex')
$launchTuning = @{
    model = @('Agent default', 'gpt-5.5', 'claude-sonnet-5')
    effort = @('Agent default', 'low', 'medium', 'high')
    context = @('Agent default')
}
$controls = Get-Published { Publish-CopilotMqttNewSession -Workspaces $launchWorkspaces -Profiles $script:Profiles -Resumable $launchResumable -Agents $launchAgents -Tuning $launchTuning -Slug $slug -Headers $headers }
$emptyControls = Get-Published { Publish-CopilotMqttNewSession -Workspaces @() -Profiles @() -Resumable @() -Agents @() -Tuning @{} -Slug $slug -Headers $headers }
$longNote = 'Launch failed: ' + ('x' * 300)
$result = Get-Published { Set-CopilotMqttNewSessionResult -Text $longNote -Slug $slug -Headers $headers }
$clearPrompt = Get-Published { Clear-CopilotMqttNewSessionPrompt -Slug $slug -Headers $headers }

# The resumable list's labels: one agent's sessions, then two agents' (each label then names its
# agent), a blank summary, a repeated one and a long one.
$script:ResumeEntries = @{}
foreach ($kind in @($script:BridgeLaunchers.Keys)) {
    $script:BridgeLaunchers[$kind]['Resumable'] = [scriptblock]::Create("param(`$Limit, `$Found) @(`$script:ResumeEntries['$kind'])")
}
function New-ResumeEntry([string]$Id, [string]$Launcher, [string]$Summary, [string]$Folder, [int]$Minutes) {
    [pscustomobject]@{ SessionId = $Id; Launcher = $Launcher; Summary = $Summary; Folder = $Folder; Updated = [DateTimeOffset]::Parse('2026-10-29T20:00:00Z').AddMinutes(-$Minutes) }
}
function Get-ResumeLabels([hashtable]$Entries) {
    $script:ResumeEntries = $Entries
    foreach ($kind in @($script:BridgeLaunchers.Keys)) { if (-not $script:ResumeEntries.ContainsKey($kind)) { $script:ResumeEntries[$kind] = @() } }
    @(Get-BridgeResumableSessions | ForEach-Object { [ordered]@{ id = [string]$_.SessionId; launcher = [string]$_.Launcher; label = [string]$_.Label } })
}
$resumeCases = [ordered]@{
    oneAgent = Get-ResumeLabels @{
        claude = @((New-ResumeEntry 'c0000000-0000-4000-8000-000000000001' 'claude' 'Write the   docs' 'C:\repos\agent-bridge\' 5),
            (New-ResumeEntry 'c0000000-0000-4000-8000-000000000002' 'claude' '' '/Users/me/scratch' 10),
            (New-ResumeEntry 'c0000000-0000-4000-8000-000000000003' 'claude' 'No folder' '' 15))
    }
    twoAgents = Get-ResumeLabels @{
        copilot = @((New-ResumeEntry '6f000000-0000-4000-8000-000000000001' 'copilot' 'Fix the relay' 'C:\repos\agent-bridge' 1),
            (New-ResumeEntry '6f000000-0000-4000-8000-000000000002' 'copilot' 'Fix the relay' 'C:\repos\agent-bridge' 2),
            (New-ResumeEntry '6f000000-0000-4000-8000-000000000003' 'copilot' ('A very long summary ' * 8) 'C:\repos\agent-bridge' 3))
        claude = @((New-ResumeEntry 'c0000000-0000-4000-8000-000000000004' 'claude' 'Write docs' 'C:\scratch' 4))
    }
}

# An agent's launch request, as Get-DaemonLaunchRequest reads it from the prompt payload sensor
# for a press at 20:00:30: every kind it can be, and each way its settings are refused.
$launchPress = [DateTimeOffset]::Parse('2026-10-29T20:00:30.000+00:00')
$script:DaemonEntity = @{ NewPromptPayload = "sensor.agent_bridge_${slug}_new_prompt_payload" }
$script:DaemonLaunchRequestsHandled = @('handled-0001')
function Read-LaunchRequest([string]$Stamp, $Attributes, [string]$Updated = '2026-10-29T20:00:29.500+00:00') {
    $script:States = @{ $script:DaemonEntity.NewPromptPayload = [pscustomobject]@{ state = $Stamp; attributes = $Attributes; last_updated = $Updated } }
    $read = Get-DaemonLaunchRequest -Headers $headers -PressedAt $launchPress
    $out = [ordered]@{ kind = $read.Kind }
    if ($null -ne $read.Request) {
        $settings = [ordered]@{}
        foreach ($k in @($read.Request.Settings.Keys | Sort-Object)) { $settings[$k] = $read.Request.Settings[$k] }
        $out['request'] = [ordered]@{ id = $read.Request.Id; text = $read.Request.Text; settings = $settings; problem = $read.Request.Problem }
    }
    $out
}
$at = '2026-10-29T20:00:29.400Z'
$requestCases = [ordered]@{
    person = Read-LaunchRequest $at ([pscustomobject]@{ at = $at; text = 'Fix the build' })
    fresh = Read-LaunchRequest $at ([pscustomobject]@{ at = $at; text = '  Fix the build  '; request = 'req-0001-abcd'; settings = [pscustomobject]@{ agent = 'Claude'; workspace = ' scratch '; model = ''; effort = $null } })
    emptied = Read-LaunchRequest 'unknown' ([pscustomobject]@{ request = 'req-0002-abcd' })
    newer = Read-LaunchRequest $at ([pscustomobject]@{ request = 'req-0003-abcd' }) '2026-10-29T20:00:31.000+00:00'
    stale = Read-LaunchRequest $at ([pscustomobject]@{ request = 'req-0004-abcd' }) '2026-10-29T19:59:00.000+00:00'
    malformed = Read-LaunchRequest $at ([pscustomobject]@{ request = 'bad id!' })
    short = Read-LaunchRequest $at ([pscustomobject]@{ request = 'abc' })
    handled = Read-LaunchRequest $at ([pscustomobject]@{ request = 'handled-0001' })
    unknownSetting = Read-LaunchRequest $at ([pscustomobject]@{ request = 'req-0005-abcd'; settings = [pscustomobject]@{ colour = 'blue' } })
    notText = Read-LaunchRequest $at ([pscustomobject]@{ request = 'req-0006-abcd'; settings = [pscustomobject]@{ model = 5 } })
    notObject = Read-LaunchRequest $at ([pscustomobject]@{ request = 'req-0007-abcd'; settings = 'agent=Claude' })
    settingsOnly = Read-LaunchRequest $at ([pscustomobject]@{ settings = [pscustomobject]@{ agent = 'Codex' } })
}

# What a request's settings are refused for, and the note the card (and the outcome) carries.
$script:DefaultWorkspace = 'agent-bridge'
function script:Get-BridgeDefaultWorkspaceLabel { $script:DefaultWorkspace }
function script:Resolve-BridgeWorkspacePath { param([string]$Label) (@($launchWorkspaces | Where-Object Label -EQ $Label) | Select-Object -First 1 | ForEach-Object Path) }
function script:Get-BridgeDefaultAgencyProfile { 'default' }
function script:Resolve-BridgeAgencyProfile { param([string]$Name) if ($script:Profiles -contains $Name) { $Name } else { $null } }
function script:Get-BridgeTuningOptions { param([string]$Launcher, [string]$Axis) if ($Launcher -in @('copilot', 'agency')) { @($launchTuning[$Axis]) } else { @('Agent default') } }
function script:Get-BridgeDefaultTuning { param([string]$Launcher, [string]$Axis) '' }
function script:Resolve-BridgeTuningValue { param([string]$Launcher, [string]$Axis, [string]$Value) $Value }
function script:Complete-DaemonResumeChoice { param($Session, [string]$Label, $Workspaces, $Launchers, $Headers) [pscustomobject]@{ Session = $Session; Label = $Label; Launcher = (Get-DaemonResumeLauncher -Entry $Session) } }
function script:Get-DaemonOpeningPrompt { param([string]$Launcher, [string]$Prompt) $Prompt }
$launchControls = [pscustomobject]@{
    Workspaces = $launchWorkspaces; Launcher = 'copilot'; Launchers = $script:Installed; Agents = $launchAgents; Profiles = $script:Profiles
    Resumable = @([pscustomobject]@{ Label = 'Fix the relay - agent-bridge'; SessionId = '6f000000-0000-4000-8000-000000000001'; Launcher = 'copilot' })
}
function Resolve-Requested([hashtable]$Settings) {
    $script:Recorded.Clear()
    $script:DaemonLaunchRequest = [pscustomobject]@{ Id = 'req-0100-abcd'; Text = 'go'; Settings = $Settings; Problem = '' }
    $resolved = Resolve-DaemonRequestedLaunch -Controls $launchControls -Headers $headers
    $script:DaemonLaunchRequest = $null
    $note = @($script:Recorded | Where-Object { $_.topic -like '*/newsession/result' } | ForEach-Object { $_.payload }) | Select-Object -Last 1
    [ordered]@{ settings = $Settings; refused = ($null -eq $resolved); note = [string]$note }
}
$refusalCases = [ordered]@{
    unknownAgent = Resolve-Requested @{ agent = 'Gemini' }
    resumeAndWorkspace = Resolve-Requested @{ resume = 'Fix the relay - agent-bridge'; workspace = 'scratch' }
    unknownResume = Resolve-Requested @{ resume = 'Nothing like it' }
    resumeOtherAgent = Resolve-Requested @{ resume = 'Fix the relay - agent-bridge'; agent = 'Claude' }
    unknownWorkspace = Resolve-Requested @{ workspace = 'elsewhere' }
    unknownProfile = Resolve-Requested @{ agent = 'Agency'; profile = 'play' }
    profileNotAgency = Resolve-Requested @{ agent = 'Claude'; profile = 'work' }
    unknownModel = Resolve-Requested @{ model = 'gpt-1' }
    noEffort = Resolve-Requested @{ agent = 'Claude'; effort = 'high' }
    unknownPermissions = Resolve-Requested @{ permissions = 'allow all' }
    accepted = Resolve-Requested @{ agent = 'Copilot'; workspace = 'scratch'; model = 'gpt-5.5'; effort = 'Agent default'; permissions = 'Allow all' }
}

# The outcome an agent reads: a request started, then refused, each recorded newest first.
$script:DaemonLaunchOutcomes = @()
$outcome = Get-Published {
    Set-DaemonLaunchOutcome -Request 'req-0001-abcd' -Outcome 'starting' -Note 'Received; checking its settings before launching.' -Pressed '2026-10-29T20:00:30.0000000Z' -Headers $headers
    Set-DaemonLaunchOutcome -Request 'req-0001-abcd' -Outcome 'started' -Session '6f000000-0000-4000-8000-0000000000aa' -Headers $headers -Launched ([pscustomobject][ordered]@{
        agent = 'Copilot'; workspace = 'scratch'; resume = ''; profile = ''; model = 'gpt-5.5'; effort = ''; context = ''; permissions = 'Allow all'; resumed = $false })
}
foreach ($p in $outcome) { foreach ($r in @($p.payload.recent)) { $r['at'] = '<now>' } }

$launch = [ordered]@{
    source = $fixture.source
    machine = $machine; slug = $slug
    constants = [ordered]@{
        newSessionOption = $script:CopilotMqttNewSessionOption; tuningDefault = $script:BridgeTuningDefaultOption
        permissions = @(Get-BridgePermissionOptions); allowAll = (Get-BridgePermissionLabel -AllowAllTools $true); ask = (Get-BridgePermissionLabel -AllowAllTools $false)
        noteExpirySeconds = (Get-SpecConfigConstant 'hooks\agent-bridge-daemon.ps1' @('NoteExpirySeconds')).NoteExpirySeconds
        replyBlank = $script:DaemonConfig['ReplyBlankValue']
    }
    agents = [ordered]@{}
    controls = $controls; emptyControls = $emptyControls; result = $result; clearPrompt = $clearPrompt; outcome = $outcome
    resumeLabels = $resumeCases
    requests = $requestCases
    refusals = $refusalCases
    capabilities = (Get-DaemonLaunchCapabilities)
}
foreach ($kind in @($script:BridgeLaunchers.Keys)) {
    $launch.agents[$kind] = [ordered]@{ label = (Get-BridgeLauncherLabel -Launcher $kind); needsFirstMessage = [bool](Get-BridgeLauncher -Launcher $kind).NeedsFirstMessage }
}
$full = [IO.Path]::GetFullPath($LaunchOutput)
[IO.File]::WriteAllText($full, ((ConvertTo-Stable $launch) | ConvertTo-Json -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $full"
