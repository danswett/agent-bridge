$ErrorActionPreference = 'Stop'
$sender = Join-Path $PSScriptRoot '..\tools\agents\reply-session.ps1'
$mock = @'
$env:AGENT_HA_URL = 'https://unused.invalid/api'
$env:AGENT_HA_AGENT_TOKEN = 'test-only'
$script:reads = 0
function Invoke-RestMethod {
    param($Uri, $Method, $Headers, $ContentType, $Body, $TimeoutSec)
    if ($Method -eq 'Post') {
        if ($Uri.EndsWith('/agent_bridge_reply_payload')) {
            $script:stamp = (($Body | ConvertFrom-Json).payload | ConvertFrom-Json).at
        }
        return @{}
    }
    if ($Uri.EndsWith('_reply_payload')) { return @{ attributes = @{ at = $script:stamp } } }
    if ($Uri.EndsWith('_status')) { return @{ state = 'working'; last_changed = 'fixed' } }
    if ($Uri.EndsWith('_activity')) {
        $script:reads++
        if ($script:reads -eq 1) { return @{ attributes = @{} } }
        $outcome = if ($script:reads -eq 2) { 'queued' } else { 'delivered' }
        return @{ attributes = @{ reply_ack = @{ rid = 'test-queued'; at = 'new'; outcome = $outcome } } }
    }
    throw 'Unexpected mocked request'
}
'@
$path = [Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($sender)
$invoke = "`n& '$path' -Session agent_bridge_0123456789abcdef -Text test -Rid test-queued -Attempts 1 -SeenPolls 1 -AckSeconds 3; exit `$LASTEXITCODE"
$output = & pwsh -NoProfile -NonInteractive -Command ($mock + $invoke) 2>&1
if ($LASTEXITCODE -ne 0 -or "$output" -notmatch 'sent to agent_bridge_0123456789abcdef') {
    throw "A queued acknowledgement must wait for delivered, not fail or claim success: $output"
}
$output = & pwsh -NoProfile -NonInteractive -Command ($mock.Replace("'delivered'", "'queued'") + $invoke) 2>&1
if ($LASTEXITCODE -ne 3 -or "$output" -match 'sent to agent_bridge_0123456789abcdef') {
    throw "A queued-only acknowledgement must time out without claiming delivery: $output"
}
'PASS: queued waits for delivery; queued-only never claims success (mock transport only)'
