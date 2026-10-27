# Spike S1, live check: deliver a prompt from the console-less injector into a real
# Copilot CLI session and confirm from the session's own transcript that it was
# submitted as a user message and answered.
#
# The CLI runs with a throwaway COPILOT_HOME (a copy of config.json with no tokens, the
# workspace trusted, and a junction to the shared pkg directory), so it loads none of
# the installed bridge's hooks or instructions and never appears on its dashboard. The
# junction is removed on its own before the temp directory, so the real pkg is untouched.
[CmdletBinding()]
param(
    [int]$TimeoutSec = 180,
    # Which injector types the prompt: this spike's, or the existing bridge's
    # (old-injector.ps1, run hidden as the daemon is).
    [ValidateSet('new', 'old')][string]$Injector = 'new',
    [switch]$Unicode,
    [string]$DecisionInjectPath = (Join-Path $HOME 'repos\agent-ha-bridge\hooks\decision-inject.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$exe = Get-ChildItem "$here\Injector\bin\Release" -Recurse -Filter s1-injector.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'build the injector first (run-s1.ps1)' }

$root = Join-Path $env:TEMP "s1-live-$([guid]::NewGuid().ToString('n').Substring(0, 8))"
$copilotHome = Join-Path $root 'copilot-home'
$ws = Join-Path $root 'ws'
New-Item -ItemType Directory -Force $copilotHome, $ws | Out-Null
$cfg = Get-Content (Join-Path $HOME '.copilot\config.json') -Raw | ConvertFrom-Json
$keep = [ordered]@{}
foreach ($k in 'lastLoggedInUser', 'loggedInUsers', 'staff', 'firstLaunchAt', 'askedSetupTerminals') {
    if ($cfg.PSObject.Properties[$k]) { $keep[$k] = $cfg.$k }
}
$keep.trustedFolders = @($ws)
$keep | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $copilotHome 'config.json') -Encoding utf8
$junction = Join-Path $copilotHome 'pkg'
New-Item -ItemType Junction -Path $junction -Target (Join-Path $HOME '.copilot\pkg') | Out-Null

$injectorLog = Join-Path $root 'injector.jsonl'
$cliPid = $null
$window = $null
$marker = "PINEAPPLE$(Get-Random -Minimum 1000 -Maximum 9999)"
$result = [ordered]@{ marker = $marker; injector = $Injector; unicode = [bool]$Unicode }
try {
    $env:COPILOT_HOME = $copilotHome
    $window = Start-Process conhost.exe -ArgumentList "cmd.exe /c cd /d `"$ws`" && copilot" -PassThru
    Remove-Item Env:\COPILOT_HOME

    # The CLI announces its session with an inuse.<pid>.lock, which is also how the
    # bridge finds the process to type into.
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $lock = $null
    while (-not $lock -and [DateTime]::UtcNow -lt $deadline) {
        $lock = Get-ChildItem (Join-Path $copilotHome 'session-state') -Recurse -Filter 'inuse.*.lock' -ErrorAction SilentlyContinue | Select-Object -First 1
        Start-Sleep -Milliseconds 500
    }
    if (-not $lock) { throw 'CLI did not start a session' }
    $cliPid = [int]($lock.Name -replace '^inuse\.(\d+)\.lock$', '$1')
    $sessionDir = $lock.DirectoryName
    $result.cliPid = $cliPid
    $result.version = (Get-ChildItem (Join-Path $HOME '.copilot\pkg\win32-x64') | Sort-Object Name | Select-Object -Last 1).Name
    Start-Sleep -Seconds 6   # let the input box come up

    $prompt = if ($Unicode) {
        # Smart quotes are string delimiters to PowerShell itself, hence the code points.
        "Reply with exactly the single word $marker and nothing else. (Ignore this: café – 世界 ✓ $([char]0x201C)smart$([char]0x201D) 🚀)"
    } else { "Reply with exactly the single word $marker and nothing else." }
    if ($Injector -eq 'new') {
        $psi = [Diagnostics.ProcessStartInfo]::new($exe)
        foreach ($a in 'send', '--pid', "$cliPid", '--text', $prompt, '--out', $injectorLog) { $psi.ArgumentList.Add($a) }
        $p = [Diagnostics.Process]::Start($psi); $p.WaitForExit()
        $result.injectorOutcome = (Get-Content $injectorLog | Select-Object -Last 1 | ConvertFrom-Json).result
    }
    else {
        $oldOut = Join-Path $root 'old-injector.txt'
        $psi = [Diagnostics.ProcessStartInfo]::new('pwsh.exe')
        foreach ($a in '-NoProfile', '-File', (Join-Path $here 'old-injector.ps1'), '-DecisionInjectPath', $DecisionInjectPath,
            '-TargetPid', "$cliPid", '-Text', $prompt, '-Out', $oldOut) { $psi.ArgumentList.Add($a) }
        $psi.CreateNoWindow = $true; $psi.UseShellExecute = $false
        $p = [Diagnostics.Process]::Start($psi); $p.WaitForExit()
        $result.injectorOutcome = (Get-Content $oldOut | Select-Object -Last 1)
    }

    $events = Join-Path $sessionDir 'events.jsonl'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $user = $null; $assistant = $null
    while ([DateTime]::UtcNow -lt $deadline -and -not $assistant) {
        if (Test-Path $events) {
            foreach ($line in Get-Content $events) {
                $e = $line | ConvertFrom-Json
                if ($e.type -eq 'user.message' -and "$($e.data.content)" -like "*$marker*") { $user = $e.data.content }
                if ($user -and $e.type -eq 'assistant.message' -and "$($e.data.content)" -like "*$marker*") { $assistant = $e.data.content }
            }
        }
        Start-Sleep -Seconds 1
    }
    $result.userMessageExact = ($user -ceq $prompt)
    $result.userMessage = $user
    $result.sent = $prompt
    $result.assistantMessage = $assistant

    # End it the way the bridge's End session does: type /exit.
    $p = Start-Process $exe -ArgumentList "send --pid $cliPid --text `"/exit`" --out `"$injectorLog`"" -PassThru -Wait
    $exited = $false
    for ($i = 0; $i -lt 20 -and -not $exited; $i++) { Start-Sleep -Milliseconds 500; $exited = -not (Get-Process -Id $cliPid -ErrorAction SilentlyContinue) }
    $result.exitedOnSlashExit = $exited
}
finally {
    foreach ($id in @($cliPid, $window.Id)) {
        if ($id) { Get-Process -Id $id -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 1
    if (Test-Path $junction) { [IO.Directory]::Delete($junction, $false) }
    if ((Test-Path (Join-Path $HOME '.copilot\pkg\win32-x64')) -and -not (Test-Path $junction)) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$result | ConvertTo-Json -Depth 5
