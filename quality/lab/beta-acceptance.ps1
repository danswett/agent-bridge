# The Beta acceptance run (#1185, #736): the real published Beta installer on a clean Windows
# runner, as a person would install it. One architecture per invocation; the workflow runs both.
# Steps 4/5 use a relay from the Beta's source, fake GitHub and an isolated host profile;
# the PWA opens the simulated session read-only. Feedback remains NOT RUN.
#
#   pwsh quality/lab/beta-acceptance.ps1 -Arch x64 -Out out/accept
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [Parameter(Mandatory)][string]$Out,
    [string]$Repo = 'danswett/agent-bridge',
    # A release tag to test instead of the newest Beta release; for rehearsals only.
    [string]$Tag)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:AGENT_BRIDGE_SESSION -or $env:AGENT_BRIDGE_TOKEN) { throw 'Beta acceptance requires a clean disposable runner, not an agent session' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$key = if ($Arch -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$steps = [ordered]@{}
foreach ($n in '1-download', '2-install-first-run', '3-simulated-agent', '4-pair', '5-drive-from-pwa', '5b-feedback', '6-uninstall') {
    $steps[$n] = [ordered]@{ result = 'NOT RUN'; detail = '' }
}
$state = @{ Version = $null; Setup = $null; Root = $null; Sim = $null; Commit = $null }

function Step([string]$name, [string[]]$Needs, [scriptblock]$body, [switch]$Required) {
    $unmet = @($Needs | Where-Object { $steps[$_].result -ne 'PASS' })
    if ($unmet) {
        $steps[$name].detail = "not run: step $($unmet -join ', ') did not pass"
        if ($Required) { $steps[$name].result = 'FAIL' }
        return
    }
    try {
        & $body
        $steps[$name].result = 'PASS'
    }
    catch {
        $steps[$name].result = 'FAIL'
        $steps[$name].detail = $_.Exception.Message
        Write-Host "::error title=Beta acceptance $Arch step $name::$($_.Exception.Message)"
    }
}

function Invoke-PairAndDrive([hashtable]$Context, [scriptblock]$Driver) {
    try {
        Step '4-pair' @('2-install-first-run') {
            foreach ($operation in 'start-relay', 'sign-in', 'start-host', 'presence') { & $Driver $operation $Context }
            $steps['4-pair'].detail = 'installed host signed in; relay presence confirmed with a fake account'
        } -Required
        Step '5-drive-from-pwa' @('4-pair', '3-simulated-agent') {
            & $Driver 'drive' $Context
            $steps['5-drive-from-pwa'].detail = 'PWA opened the simulated session and displayed its question; no answer or reply sent'
        } -Required
    }
    finally {
        try { & $Driver 'cleanup' $Context }
        catch { $steps['4-pair'].result = 'FAIL'; $steps['4-pair'].detail += "; cleanup failed: $($_.Exception.Message)" }
    }
}

function Start-AcceptanceProcess($c, [string]$File, [string[]]$Arguments, [switch]$Ready, [hashtable]$Environment = @{}) {
    $psi = [Diagnostics.ProcessStartInfo]::new($File)
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    foreach ($entry in $Environment.GetEnumerator()) { $psi.Environment[$entry.Key] = $entry.Value }
    $p = [Diagnostics.Process]::Start($psi)
    $c.Processes.Add($p)
    $c.Readers.Add($p.StandardError.ReadToEndAsync())
    if (-not $Ready) { $c.Readers.Add($p.StandardOutput.ReadToEndAsync()) }
    return $p
}

function Wait-AcceptanceExit($p, [int]$Seconds = 60) {
    if (-not $p.WaitForExit($Seconds * 1000)) { throw "acceptance $Operation process timed out after $Seconds s" }
    if ($p.ExitCode -ne 0) { throw "acceptance $Operation process exited with $($p.ExitCode)" }
}

function Invoke-AcceptanceDriver([string]$Operation, [hashtable]$c) {
    switch ($Operation) {
        'start-relay' {
            if (-not $state.Commit) { throw 'the Beta source commit was not resolved' }
            if (Test-Path -LiteralPath $c.Source) { throw 'relay source folder already exists' }
            $c.SourceOwned = $true
            git -C $c.Repo worktree add --detach $c.Source $state.Commit
            if ($LASTEXITCODE -ne 0) { throw 'could not check out the Beta source commit' }
            $project = Join-Path $c.Source 'tools/AgentBridge.AcceptRelay/AgentBridge.AcceptRelay.csproj'
            $p = Start-AcceptanceProcess $c 'dotnet' @('publish', $project, '-c', 'Release', '-o', $c.Build, '--source', 'https://packagefeedproxy.microsoft.io/nuget/v3/index.json')
            Wait-AcceptanceExit $p 180
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            try { $listener.Start(); $c.SitePort = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
            $p = Start-AcceptanceProcess $c 'dotnet' @((Join-Path $c.Build 'accept-relay.dll'), 'serve', '--port', '0') -Ready -Environment @{ Relay__AllowedOrigins__0 = "http://127.0.0.1:$($c.SitePort)" }
            $line = $p.StandardOutput.ReadLineAsync()
            if (-not $line.Wait(30000) -or $line.Result -notmatch '^READY relay=(http://127\.0\.0\.1:\d+)$') { throw 'relay did not report loopback readiness within 30 s' }
            $c.Relay = $Matches[1]
            $c.Readers.Add($p.StandardOutput.ReadToEndAsync())
        }
        'sign-in' {
            if (Test-Path -LiteralPath $c.Identity) { throw 'isolated identity folder already exists' }
            New-Item -ItemType Directory -Path $c.Identity | Out-Null
            $c.IdentityOwned = $true
            '{"version":1,"usage":{"publish":false},"push":{"enabled":false},"sessions":{"restoreOnStart":false},"delivery":{"readOnly":true}}' | Set-Content (Join-Path $c.Identity 'config.json')
            $hostFile = Get-ChildItem (Join-Path $state.Root 'current') -Filter 'agent-bridge-host.exe' -Recurse | Select-Object -First 1
            if (-not $hostFile) { throw 'the installed host executable is missing' }
            $c.HostExe = $hostFile.FullName
            Wait-AcceptanceExit (Start-AcceptanceProcess $c $c.HostExe @('sign-in', '--relay', $c.Relay, '--profile', $c.Profile))
        }
        'start-host' {
            $c.HostProcess = Start-AcceptanceProcess $c $c.HostExe @('serve', '--relay', $c.Relay, '--profile', $c.Profile, '--name', $c.Profile, '--copilot-home', (Join-Path $Out 'copilot-home'), '--claude-home', (Join-Path $Out 'empty-claude'), '--codex-home', (Join-Path $Out 'empty-codex'))
        }
        'presence' {
            Wait-AcceptanceExit (Start-AcceptanceProcess $c 'node' @((Join-Path $PSScriptRoot 'beta-pwa.mjs'), 'presence', $c.Relay, $c.Profile))
            if ($c.HostProcess.HasExited) { throw 'the installed host exited before presence was confirmed' }
        }
        'drive' {
            Wait-AcceptanceExit (Start-AcceptanceProcess $c 'node' @((Join-Path $PSScriptRoot 'beta-pwa.mjs'), 'drive', $c.Relay, $c.Profile, "$($c.SitePort)", $state.Sim.SessionId, $Out)) 90
        }
        'cleanup' {
            $errors = [Collections.Generic.List[string]]::new()
            foreach ($p in $c.Processes) {
                try { if (-not $p.HasExited) { $p.Kill($true); if (-not $p.WaitForExit(10000)) { throw 'process did not stop' } }; $p.Dispose() }
                catch { $errors.Add("process cleanup: $($_.Exception.Message)") }
            }
            try {
                if ($c.IdentityOwned) {
                    $reference = Join-Path $c.Identity 'device-key.json'
                    if (Test-Path -LiteralPath $reference) {
                        $keyRef = Get-Content -LiteralPath $reference -Raw | ConvertFrom-Json
                        if ($keyRef.Storage -eq 'tpm') {
                            $key = [Security.Cryptography.CngKey]::Open($keyRef.Name, [Security.Cryptography.CngProvider]::new('Microsoft Platform Crypto Provider'))
                            try { $key.Delete() } finally { $key.Dispose() }
                        }
                    }
                    Remove-Item -LiteralPath $c.Identity -Recurse -Force
                }
            } catch { $errors.Add("identity cleanup: $($_.Exception.Message)") }
            if ($c.SourceOwned) {
                git -C $c.Repo worktree remove $c.Source
                if ($LASTEXITCODE -ne 0) { $errors.Add('Beta source worktree cleanup failed') }
            }
            if ($errors.Count) { throw ($errors -join '; ') }
        }
        default { throw "unknown acceptance driver operation: $Operation" }
    }
}

function Save-Screenshot([string]$file) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = [Drawing.Bitmap]::new($b.Width, $b.Height)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $file), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# The app's own window and what UI Automation shows of its Get started rows. The rows are text
# elements named "<state>, step N of M, <title>. <hint>"; the section's own panel has no automation peer, so
# they are found by the heading and by that shape, not by the panel's AutomationId.
function Read-GetStarted([Diagnostics.Process]$app) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $A = [Windows.Automation.AutomationElement]
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $last = @()
    while ($deadline.Elapsed.TotalSeconds -lt 90) {
        $app.Refresh()
        if ($app.HasExited) { throw "the app exited with $($app.ExitCode) before showing a window" }
        if ($app.MainWindowHandle -ne 0) {
            $root = $A::FromHandle($app.MainWindowHandle)
            $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            $last = @($all | ForEach-Object { "$($_.Current.ControlType.ProgrammaticName) | $($_.Current.AutomationId) | $($_.Current.Name)" })
            $names = @($all | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text } | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
            $rows = @($names | Where-Object { $_ -match '^[^,]+, step \d+ of \d+, ' })
            if (($names -contains 'Get started') -and $rows.Count) { $last | Set-Content (Join-Path $Out 'ui-tree.txt'); return $rows }
        }
        Start-Sleep -Milliseconds 500
    }
    $last | Set-Content (Join-Path $Out 'ui-tree.txt')
    throw 'the Get started heading and rows did not appear in UI Automation within 90 s (ui-tree.txt has what it did show)'
}
Step '1-download' @() {
    $tags = if ($Tag) { @($Tag) } else { @(gh release list --repo $Repo --limit 40 --json tagName --jq '.[].tagName') }
    $manifest = $null
    foreach ($t in $tags) {
        $dir = Join-Path $Out "manifest-$t"
        gh release download $t --repo $Repo --pattern beta-release.json --dir $dir --clobber 2>$null
        if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $dir 'beta-release.json'))) {
            $manifest = Get-Content (Join-Path $dir 'beta-release.json') -Raw | ConvertFrom-Json
            break
        }
    }
    if (-not $manifest) { throw 'no Beta release with a beta-release.json was found' }
    if ($manifest.channel -ne 'beta') { throw "the manifest's channel is '$($manifest.channel)', not beta" }
    $d = $manifest.downloads.$key
    if (-not $d) { throw "the manifest has no $key download" }
    $state.Version = $manifest.version
    $setup = Join-Path $Out $d.fileName
    # The public URL a person gets, not the release API: the download page's own path.
    Invoke-WebRequest -Uri $d.url -OutFile $setup -MaximumRetryCount 3
    $file = Get-Item $setup
    if ($file.Length -ne $d.sizeBytes) { throw "size $($file.Length) differs from the manifest's $($d.sizeBytes)" }
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $d.sha256.ToLowerInvariant()) { throw "sha256 $hash differs from the manifest's $($d.sha256)" }
    $sig = Get-AuthenticodeSignature $setup
    if ($sig.Status -notin 'Valid', 'NotSigned') { throw "the installer's signature is $($sig.Status)" }
    # Only a fully verified installer is ever recorded for step 2 to run.
    $state.Setup = $setup
    $ref = gh api "repos/$Repo/git/ref/tags/$t" --jq .object.sha
    if ($LASTEXITCODE -ne 0 -or $ref -notmatch '^[0-9a-f]{40}$') { throw 'could not resolve the Beta tag reference' }
    $state.Commit = git -C (Join-Path $PSScriptRoot '../..') rev-parse --verify "$ref^{commit}"
    if ($LASTEXITCODE -ne 0 -or $state.Commit -notmatch '^[0-9a-f]{40}$') { throw 'Beta tag commit is absent; checkout must fetch full history and tags' }
    $steps['1-download'].detail = "v$($manifest.version) $($d.fileName) $($file.Length) bytes, sha256 verified, signature $($sig.Status)"
}

$installDir = Join-Path $env:LOCALAPPDATA 'AgentBridgeApp'
# The product runs next: it must not inherit the job's tokens.
foreach ($n in 'GH_TOKEN', 'GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_URL') {
    [Environment]::SetEnvironmentVariable($n, $null)
}

Step '2-install-first-run' @('1-download') {
    if (Test-Path $installDir) { throw "$installDir already exists: the runner is not clean" }
    $state.Root = $installDir
    $p = Start-Process $state.Setup -ArgumentList '--silent' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup.exe --silent exited with $($p.ExitCode)" }
    $exe = Join-Path $installDir 'current\AgentBridge.exe'
    if (-not (Test-Path $exe)) { throw "Setup finished but $exe does not exist" }
    $installed = (Select-String -Path (Join-Path $installDir 'current\sq.version') -Pattern '<version>([^<]+)</version>').Matches[0].Groups[1].Value
    if ($installed -ne $state.Version) { throw "installed $installed, the manifest says $($state.Version)" }
    # Velopack's Setup starts the app itself; take that process, or start it if it did not.
    Start-Sleep -Seconds 3
    $app = Get-Process AgentBridge -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $app) { $app = Start-Process $exe -PassThru }
    try {
        $rows = Read-GetStarted $app
        Save-Screenshot 'first-run.png'
        if (-not $rows.Count) { throw 'the Get started section has no rows' }
        $rows | Set-Content (Join-Path $Out 'get-started-rows.txt')
        $steps['2-install-first-run'].detail = "installed $installed; Get started rows: $($rows -join ' | ')"
    }
    catch { Save-Screenshot 'first-run-failed.png'; throw }
}

Step '3-simulated-agent' @('2-install-first-run') {
    $hook = Get-ChildItem (Join-Path $state.Root 'current') -Filter 'agent-bridge-host-hook.exe' -Recurse | Select-Object -First 1
    if (-not $hook) { throw 'the installed app has no agent-bridge-host-hook.exe' }
    $home3 = Join-Path $Out 'copilot-home'
    $sim = & (Join-Path $PSScriptRoot 'agent-sim.ps1') -Hook $hook.FullName -CopilotHome $home3 -Question 'Beta acceptance: proceed?'
    $bad = @($sim.Hooks | Where-Object Exit -ne 0)
    if ($bad) { throw "the hook exited non-zero for: $($bad.Hook -join ', ')" }
    $ask = $sim.Hooks | Where-Object Hook -eq 'ask_user'
    if ($ask.Output -notmatch 'allow') { throw "the hook's ask_user reply was '$($ask.Output)', not an allow" }
    if (-not (Select-String -Path $sim.Transcript -Pattern 'Beta acceptance: proceed\?' -Quiet)) { throw 'the transcript lacks the question' }
    $state.Sim = $sim
    $steps['3-simulated-agent'].detail = "session $($sim.SessionId): transcript written, hook replies as contracted. The host reading it is checked in step 5."
}

$profile = 'beta-accept-' + [guid]::NewGuid().ToString('N')
$pair = @{
    Profile = $profile; Identity = Join-Path $env:LOCALAPPDATA "AgentBridge/$profile"; IdentityOwned = $false
    Source = Join-Path $Out 'relay-source'; SourceOwned = $false; Repo = Join-Path $PSScriptRoot '../..'
    Build = Join-Path $Out 'relay-build'; Processes = [Collections.Generic.List[Diagnostics.Process]]::new()
    Readers = [Collections.Generic.List[object]]::new()
}
Invoke-PairAndDrive $pair ${function:Invoke-AcceptanceDriver}

if (-not $state.Root) { $steps['6-uninstall'].detail = 'not run: nothing was installed' }
else { Step '6-uninstall' @() {
    Get-Process AgentBridge -ErrorAction SilentlyContinue | Stop-Process -Force
    $update = Join-Path $installDir 'Update.exe'
    $p = Start-Process $update -ArgumentList '--uninstall', '--silent' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Update.exe --uninstall exited with $($p.ExitCode)" }
    # Velopack removes the folder from a helper process just after Update.exe returns.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path $installDir) -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 500 }
    $left = @()
    if (Test-Path $installDir) { $left += "folder $installDir" }
    $left += Get-Process AgentBridge, Update -ErrorAction SilentlyContinue | ForEach-Object { "process $($_.ProcessName) $($_.Id)" }
    $run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
    $left += $run.PSObject.Properties | Where-Object { $_.Value -is [string] -and $_.Value -like '*AgentBridgeApp*' } | ForEach-Object { "Run entry $($_.Name)" }
    # The host's logon task is ours alone (HostManager.TaskName); the uninstaller only logs a failure to delete it.
    $left += Get-ScheduledTask -TaskName 'AgentBridgeHost_*' -ErrorAction Stop | ForEach-Object { "scheduled task $($_.TaskName)" }
    $unKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $left += Get-ChildItem $unKey -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like '*AgentBridgeApp*' } | ForEach-Object { "Uninstall entry $($_.PSChildName)" }
    $menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $left += Get-ChildItem $menu -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -like '*Slipgates*' } | ForEach-Object { "shortcut $($_.FullName)" }
    if ($left.Count) { throw "left behind: $($left -join '; ')" }
    $steps['6-uninstall'].detail = 'install folder, processes, Run and Uninstall entries, the host logon task and shortcuts are gone'
} }

# What the person's data folder holds is kept as evidence, not judged: it is the user's data.
foreach ($f in Get-ChildItem (Join-Path $env:LOCALAPPDATA 'AgentBridge\app') -Filter '*.log' -ErrorAction SilentlyContinue) {
    Copy-Item $f.FullName (Join-Path $Out $f.Name)
}

$verdict = if ($steps.Values | Where-Object result -eq 'FAIL') { 'FAIL' } elseif ($steps.Values | Where-Object result -eq 'NOT RUN') { 'PARTIAL' } else { 'PASS' }
$result = [ordered]@{ arch = $Arch; version = $state.Version; verdict = $verdict; steps = $steps }
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Out 'result.json')
$table = ($steps.GetEnumerator() | ForEach-Object { "| $($_.Key) | $($_.Value.result) | $($_.Value.detail) |" }) -join "`n"
$summary = "### Beta acceptance, $Arch, v$($state.Version): $verdict`n`n| Step | Result | Detail |`n|---|---|---|`n$table`n"
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY $summary }
# Only a full pass is green: a run with steps NOT RUN must not look like a pass.
if ($verdict -ne 'PASS') { exit 1 }
