# How long an update keeps the app away (#1167): time from the old app's exit to the new version's
# process start and its window. Runs Velopack's silent apply against a throwaway install of a
# published app, packed twice under a throwaway pack id, so nothing installed is touched.
#
#   pwsh build/measure-update-gap.ps1 -Publish out/app/publish -Work <empty scratch folder>
#
# Each run extracts the 1.0.0 portable package into -Work, starts the old app in its demo, hands
# Update.exe the 1.0.1 package with the arguments PackageHandoff gives it (--waitPid, --rootDir,
# --packageDir, restart arguments) and Velopack's --silent, which #1167 adds, ends the old app, and
# waits for the new version's window. The app restarts into its demo (--demo), so no host is
# started and nothing reaches a relay.
#
# The update hooks run the app's own Lifecycle code, which manages the default profile's host. On
# a machine where a person's host may run, they are kept from touching it: AGENT_BRIDGE_SESSION is
# set, and LifecycleGuard refuses (so the old version's hook skips its look for a running host,
# about 1 s when there is none). -RealHooks lets them run as they would for a person: only on a
# throwaway machine such as a CI runner.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Publish,
    [Parameter(Mandatory)][string]$Work,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [int]$Runs = 5,
    [switch]$RealHooks,
    [string]$Vpk = 'vpk'
)
$ErrorActionPreference = 'Stop'
$Publish = (Resolve-Path $Publish).Path
New-Item -ItemType Directory -Force $Work | Out-Null
$Work = (Resolve-Path $Work).Path
if (-not $RealHooks) { $env:AGENT_BRIDGE_SESSION = 'measure-update-gap' }
$packId = 'SlipgatesUpdateGapTest'

if (-not ('UpdateGap.Windows' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
namespace UpdateGap {
public static class Windows {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    // The ids of processes owning a visible top-level window.
    public static HashSet<uint> Owners() {
        var r = new HashSet<uint>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (IsWindowVisible(h)) { r.Add(p); } return true; }, IntPtr.Zero);
        return r;
    }
}
}
'@
}

# A visible window of a process running from $dir: the process and when it started.
function Find-Window([string]$dir) {
    foreach ($id in [UpdateGap.Windows]::Owners()) {
        try {
            $p = [Diagnostics.Process]::GetProcessById([int]$id)
            # The app itself, never Update.exe beside it, should it ever draw a window.
            if ($p.MainModule.FileName.Equals((Join-Path $dir 'current\AgentBridge.exe'), [StringComparison]::OrdinalIgnoreCase)) { return $p }
        } catch { }
    }
    return $null
}

function Get-From([string]$dir) {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) } catch { $false } }
}

# Ends every process this run started: the old app, Update.exe and whatever it started (the new
# version, a hook), each with its children, and anything else running from the install. The old
# app in its demo holds the demo's single-instance mutex (Program.cs), shared across scratch
# folders, so one left running would keep the next run's app from opening. Bounded: 15 s, then it
# fails rather than letting a run start against a survivor.
function Stop-Run([string]$dir, [Diagnostics.Process[]]$started) {
    foreach ($p in $started) {
        if ($p -and -not $p.HasExited) { try { $p.Kill($true) } catch { } }
    }
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (($left = @(Get-From $dir)).Count -and $deadline.Elapsed.TotalSeconds -lt 15) {
        $left | ForEach-Object { try { $_.Kill($true) } catch { } }
        Start-Sleep -Milliseconds 200
    }
    if (($left = @(Get-From $dir)).Count) { throw "could not end $(($left | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ') running from $dir" }
}

$extra = if ($Runtime -ne 'win-x64') { @('--runtime', $Runtime) } else { @() }
foreach ($v in '1.0.0', '1.0.1') {
    $out = Join-Path $Work "v$v"
    if (-not (Test-Path $out)) {
        & $Vpk pack -u $packId -v $v -p $Publish -e AgentBridge.exe -o $out --packTitle $packId --delta None @extra
        if ($LASTEXITCODE) { throw "vpk pack $v failed" }
    }
}
$portable = Get-ChildItem (Join-Path $Work 'v1.0.0') -Filter '*Portable.zip' | Select-Object -First 1
$package = Get-ChildItem (Join-Path $Work 'v1.0.1') -Filter "$packId-1.0.1*full.nupkg" | Select-Object -First 1
if (-not $portable -or -not $package) { throw 'vpk did not pack the portable 1.0.0 and the full 1.0.1 package' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path $Work 'root'
$handoff = Join-Path $Work 'handoff'
$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    Stop-Run $root @()
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($portable.FullName, $root)
    New-Item -ItemType Directory -Force $handoff | Out-Null
    $copy = Join-Path $handoff $package.Name
    Copy-Item $package.FullName $copy -Force

    $old = $null; $update = $null; $new = $null
    try {
        $old = Start-Process (Join-Path $root 'current\AgentBridge.exe') -ArgumentList '--demo' -PassThru
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Find-Window $root) -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 100 }
        if (-not (Find-Window $root)) { throw "run ${i}: the old version's window did not open" }

        $log = Join-Path $Work "update-$i.log"
        $apply = @('--silent', 'apply', '--package', "`"$copy`"", '--waitPid', "$($old.Id)", '--rootDir', "`"$root`"", '--packageDir', "`"$root\packages`"", '--log', "`"$log`"", '--', '--demo')
        $update = Start-Process (Join-Path $root 'Update.exe') -ArgumentList $apply -PassThru
        # As the app does: it starts Update.exe, then exits, and its window goes with it.
        Stop-Process -Id $old.Id -Force
        $gone = Get-Date
        while (-not $new -and ((Get-Date) - $gone).TotalSeconds -lt 120) {
            $new = Find-Window $root
            if ($new -and $new.Id -eq $old.Id) { $new = $null }
            if (-not $new) { Start-Sleep -Milliseconds 20 }
        }
        $shown = Get-Date
        if (-not $update.WaitForExit(120000)) { throw "run ${i}: Update.exe did not finish in 120 s" }
        $version = (Select-String -Path (Join-Path $root 'current\sq.version') -Pattern '<version>([^<]+)</version>').Matches[0].Groups[1].Value
        $results += [pscustomobject]@{
            Run = $i
            Installed = $version
            StartS = if ($new) { [math]::Round(($new.StartTime - $gone).TotalSeconds, 2) } else { $null }
            WindowS = if ($new) { [math]::Round(($shown - $gone).TotalSeconds, 2) } else { $null }
            UpdateExit = $update.ExitCode
        }
    }
    finally { Stop-Run $root @($old, $update, $new) }
    Start-Sleep -Seconds 2
}

function Median($values) {
    $s = @($values | Where-Object { $_ -ne $null } | Sort-Object)
    if (-not $s.Count) { return $null }
    if ($s.Count % 2) { return $s[[int][math]::Floor($s.Count / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$summary = "update gap ($Runtime, $Runs runs, hooks $(if ($RealHooks) { 'real' } else { 'kept from the host' })): median $(Median $results.StartS) s to the new process starting, $(Median $results.WindowS) s to its window"
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY $summary }
if ($results | Where-Object { $_.Installed -ne '1.0.1' -or $_.UpdateExit -ne 0 -or $null -eq $_.StartS }) { Write-Error 'a run did not install 1.0.1 and open its window'; exit 1 }
