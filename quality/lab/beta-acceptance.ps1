# The Beta acceptance run (#1185, #736): the real published Beta installer on a clean Windows
# runner, as a person would install it. One architecture per invocation; the workflow runs both.
# Steps are named so a failure says which one broke. This slice does steps 1, 2 and 6
# (download and verify, install and first run, the simulated agent, uninstall and leftovers);
# pairing and driving from the PWA are NOT RUN until built and until the test account's secrets
# exist, and the feedback step is NOT RUN. None is ever reported PASS unrun.
#
#   pwsh quality/lab/beta-acceptance.ps1 -Arch x64 -Out out/accept
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [Parameter(Mandatory)][string]$Out,
    [string]$Repo = 'danswett/agent-bridge',
    # A release tag to test instead of the newest Beta release; for rehearsals only.
    [string]$Tag
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$key = if ($Arch -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$steps = [ordered]@{}
foreach ($n in '1-download', '2-install-first-run', '3-simulated-agent', '4-pair', '5-drive-from-pwa', '5b-feedback', '6-uninstall') {
    $steps[$n] = [ordered]@{ result = 'NOT RUN'; detail = '' }
}
$state = @{ Version = $null; Setup = $null; Root = $null; Sim = $null }

function Step([string]$name, [scriptblock]$body) {
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

function Save-Screenshot([string]$file) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = [Drawing.Bitmap]::new($b.Width, $b.Height)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $file), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# The app's own window and what UI Automation shows of its Get started rows.
function Read-GetStarted([Diagnostics.Process]$app) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 90) {
        $app.Refresh()
        if ($app.HasExited) { throw "the app exited with $($app.ExitCode) before showing a window" }
        if ($app.MainWindowHandle -ne 0) {
            $root = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            $cond = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'GetStarted')
            $gs = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
            if ($gs) {
                $texts = $gs.FindAll([Windows.Automation.TreeScope]::Descendants,
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))
                return @($texts | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'the Get started section did not appear within 90 s'
}

Step '1-download' {
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
    $state.Setup = Join-Path $Out $d.fileName
    # The public URL a person gets, not the release API: the download page's own path.
    Invoke-WebRequest -Uri $d.url -OutFile $state.Setup -MaximumRetryCount 3
    $file = Get-Item $state.Setup
    if ($file.Length -ne $d.sizeBytes) { throw "size $($file.Length) differs from the manifest's $($d.sizeBytes)" }
    $hash = (Get-FileHash $state.Setup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $d.sha256.ToLowerInvariant()) { throw "sha256 $hash differs from the manifest's $($d.sha256)" }
    $sig = Get-AuthenticodeSignature $state.Setup
    $steps['1-download'].detail = "v$($manifest.version) $($d.fileName) $($file.Length) bytes, sha256 verified, signature $($sig.Status)"
}

$installDir = Join-Path $env:LOCALAPPDATA 'AgentBridgeApp'
Step '2-install-first-run' {
    if (-not $state.Setup) { throw 'step 1 did not produce an installer' }
    if (Test-Path $installDir) { throw "$installDir already exists: the runner is not clean" }
    $p = Start-Process $state.Setup -ArgumentList '--silent' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup.exe --silent exited with $($p.ExitCode)" }
    $exe = Join-Path $installDir 'current\AgentBridge.exe'
    if (-not (Test-Path $exe)) { throw "Setup finished but $exe does not exist" }
    $state.Root = $installDir
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

Step '3-simulated-agent' {
    if (-not $state.Root) { throw 'step 2 did not install the app' }
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

# Steps 4 and 5 need the throwaway GitHub test account (#1185): its secrets come from the
# beta-acceptance environment. With any missing they stay NOT RUN, never PASS. The login and PWA
# driving are not built in this slice, so they stay NOT RUN either way, and say which.
. (Join-Path $PSScriptRoot 'accept-secrets.ps1')
Protect-AcceptSecrets
$missing = Get-MissingAcceptSecrets
$why = if ($missing) { "missing secrets: $($missing -join ', ')" } else { 'the device-flow login and PWA driver are not built yet' }
foreach ($n in '4-pair', '5-drive-from-pwa') { $steps[$n].detail = $why }

Step '6-uninstall' {
    if (-not (Test-Path $installDir)) { throw 'nothing was installed to uninstall' }
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
    $unKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $left += Get-ChildItem $unKey -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like '*AgentBridgeApp*' } | ForEach-Object { "Uninstall entry $($_.PSChildName)" }
    $menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $left += Get-ChildItem $menu -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -like '*Slipgates*' } | ForEach-Object { "shortcut $($_.FullName)" }
    if ($left.Count) { throw "left behind: $($left -join '; ')" }
    $steps['6-uninstall'].detail = 'install folder, processes, Run and Uninstall entries and shortcuts are gone'
}

# What the person's data folder holds is kept as evidence, not judged: it is the user's data.
foreach ($f in Get-ChildItem (Join-Path $env:LOCALAPPDATA 'AgentBridge\app') -Filter '*.log' -ErrorAction SilentlyContinue) {
    Copy-Item $f.FullName (Join-Path $Out $f.Name)
}

$verdict = if (($steps.Values | Where-Object result -eq 'FAIL')) { 'FAIL' } else { 'PARTIAL' }
$result = [ordered]@{ arch = $Arch; version = $state.Version; verdict = $verdict; steps = $steps }
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Out 'result.json')
$table = ($steps.GetEnumerator() | ForEach-Object { "| $($_.Key) | $($_.Value.result) | $($_.Value.detail) |" }) -join "`n"
$summary = "### Beta acceptance, $Arch, v$($state.Version): $verdict`n`n| Step | Result | Detail |`n|---|---|---|`n$table`n"
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY $summary }
if ($verdict -eq 'FAIL') { exit 1 }
