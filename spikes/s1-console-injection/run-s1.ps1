# Spike S1 harness: builds the WinExe injector, opens throwaway target consoles, and
# checks every delivery against what the targets actually read. Touches nothing but
# processes it started itself, and stops those by PID.
[CmdletBinding()]
param(
    [string]$ResultsDirectory = (Join-Path $env:TEMP "s1-$([guid]::NewGuid().ToString('n').Substring(0,8))"),
    # Read-only: the existing bridge's injector source, for a like-for-like comparison.
    [string]$DecisionInjectPath = (Join-Path $HOME 'repos\agent-ha-bridge\hooks\decision-inject.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null

dotnet build "$here\Injector\Injector.csproj" -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE) { throw "build failed" }
$exe = Get-ChildItem "$here\Injector\bin\Release" -Recurse -Filter s1-injector.exe | Select-Object -First 1 -ExpandProperty FullName
$injectorLog = Join-Path $ResultsDirectory 'injector.jsonl'
$started = [System.Collections.Generic.List[int]]::new()

function Invoke-Injector {
    param([string[]]$Arguments, [int]$TimeoutMs = 60000)
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($a in $Arguments + @('--out', $injectorLog)) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($TimeoutMs)) { $p.Kill(); return 'timeout' }
    $p.ExitCode
}

function Start-Target {
    param([string]$Name, [ValidateSet('conhost', 'default')][string]$ConsoleHost, [switch]$TreatControlCAsInput, [switch]$Utf8)
    $out = Join-Path $ResultsDirectory "$Name.jsonl"
    $pwshArgs = @('-NoProfile', '-File', "`"$here\target.ps1`"", '-Out', "`"$out`"")
    if ($TreatControlCAsInput) { $pwshArgs += '-TreatControlCAsInput' }
    if ($Utf8) { $pwshArgs += '-Utf8' }
    if ($ConsoleHost -eq 'conhost') {
        $p = Start-Process conhost.exe -ArgumentList (@('pwsh.exe') + $pwshArgs -join ' ') -PassThru
    }
    else {
        $p = Start-Process pwsh.exe -ArgumentList ($pwshArgs -join ' ') -PassThru
    }
    $started.Add($p.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $out) {
            $first = Get-Content $out -TotalCount 1 | ConvertFrom-Json
            if ($first.kind -eq 'start') {
                $started.Add([int]$first.pid)
                return [pscustomobject]@{ Name = $Name; Pid = [int]$first.pid; ConsoleClass = $first.consoleClass; Out = $out }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "target $Name did not start"
}

function Get-TargetLines {
    param($Target, [int]$Expect, [int]$TimeoutSec = 15)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    do {
        $lines = @(Get-Content $Target.Out | ConvertFrom-Json | Where-Object kind -eq 'line' | ForEach-Object text)
        if ($lines.Count -ge $Expect) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    , $lines
}

function Get-InjectorRecords { if (Test-Path $injectorLog) { @(Get-Content $injectorLog | ConvertFrom-Json) } else { @() } }

$results = [ordered]@{}
try {
    $a = Start-Target -Name 'a-conhost' -ConsoleHost conhost
    $b = Start-Target -Name 'b-default' -ConsoleHost default
    $results.targets = @($a, $b) | Select-Object Name, Pid, ConsoleClass

    # 1. A single delivery with non-ASCII text, into each kind of console host.
    $text = "héllo – 世界 ✓ `"quoted`" \back\slash"
    foreach ($t in $a, $b) {
        $exit = Invoke-Injector @('send', '--pid', "$($t.Pid)", '--text', $text)
        $got = Get-TargetLines -Target $t -Expect 1
        $results["send-$($t.Name)"] = [ordered]@{ exit = $exit; exact = ($got.Count -ge 1 -and $got[0] -ceq $text); got = $got }
    }

    # 2. Alternating deliveries to two consoles from one process: ordering, isolation,
    #    handle leaks, per-delivery time.
    $before = (Get-InjectorRecords).Count
    $exit = Invoke-Injector @('stress', '--pids', "$($a.Pid),$($b.Pid)", '--count', '40', '--delay', '60')
    $rec = (Get-InjectorRecords)[$before]
    $gotA = Get-TargetLines -Target $a -Expect 21
    $gotB = Get-TargetLines -Target $b -Expect 21
    $expA = @(0..39 | Where-Object { $_ % 2 -eq 0 } | ForEach-Object { "stress-$_-to-$($a.Pid)" })
    $expB = @(0..39 | Where-Object { $_ % 2 -eq 1 } | ForEach-Object { "stress-$_-to-$($b.Pid)" })
    $results.stress = [ordered]@{
        exit = $exit; failures = $rec.failures; elapsedMs = $rec.elapsedMs; perSendMs = [math]::Round($rec.elapsedMs / 40, 1)
        handles = $rec.handles
        aInOrder = (($gotA | Select-Object -Skip 1) -join '|') -ceq ($expA -join '|')
        bInOrder = (($gotB | Select-Object -Skip 1) -join '|') -ceq ($expB -join '|')
    }

    # 3. Windowless children after an attach/free cycle.
    $before = (Get-InjectorRecords).Count
    $exit = Invoke-Injector @('children', '--pid', "$($a.Pid)")
    $results.children = (Get-InjectorRecords)[$before]

    # 4. Ctrl+C reaching the injector while it is attached, for each way of declining it.
    foreach ($h in 'none', 'before', 'after', 'null', 'null-after') {
        $c = Start-Target -Name "c-ctrl-$h" -ConsoleHost conhost -TreatControlCAsInput
        $before = (Get-InjectorRecords).Count
        $exit = Invoke-Injector @('ctrl', '--pid', "$($c.Pid)", '--handler', $h)
        $recs = @(Get-InjectorRecords | Select-Object -Skip $before)
        $results["ctrl-handler-$h"] = [ordered]@{ exit = $exit; survived = [bool]($recs | Where-Object phase -eq 'survived') }
        [void](Invoke-Injector @('send', '--pid', "$($c.Pid)", '--text', 'quit'))
    }

    # 5. The target's window closed while the injector is attached.
    foreach ($h in 'none', 'before', 'after') {
        $d = Start-Target -Name "d-close-$h" -ConsoleHost conhost
        $before = (Get-InjectorRecords).Count
        $exit = Invoke-Injector @('close', '--pid', "$($d.Pid)", '--handler', $h)
        $recs = @(Get-InjectorRecords | Select-Object -Skip $before)
        $results["close-handler-$h"] = [ordered]@{ exit = $exit; survived = [bool]($recs | Where-Object phase -eq 'survived') }
    }

    # 6. Non-ASCII text: new injector vs the existing bridge injector, into a target
    #    reading with the default console code page and one reading UTF-8.
    $u = Start-Target -Name 'u-utf8' -ConsoleHost conhost -Utf8
    $o = Start-Target -Name 'o-default' -ConsoleHost conhost
    $ou = Start-Target -Name 'ou-utf8' -ConsoleHost conhost -Utf8
    [void](Invoke-Injector @('send', '--pid', "$($u.Pid)", '--text', $text))
    $oldOut = Join-Path $ResultsDirectory 'old-injector.txt'
    foreach ($t in $o, $ou) {
        Start-Process pwsh -WindowStyle Hidden -Wait -ArgumentList @('-NoProfile', '-File', "`"$here\old-injector.ps1`"",
            '-DecisionInjectPath', "`"$DecisionInjectPath`"", '-TargetPid', $t.Pid, '-Text', "`"$($text.Replace('"', '\"'))`"", '-Out', "`"$oldOut`"")
    }
    $results.unicode = [ordered]@{
        sent = $text
        newInjectorUtf8Target = (Get-TargetLines -Target $u -Expect 1)[0]
        oldInjectorDefaultTarget = (Get-TargetLines -Target $o -Expect 1)[0]
        oldInjectorUtf8Target = (Get-TargetLines -Target $ou -Expect 1)[0]
        oldInjectorOutcomes = @(Get-Content $oldOut)
    }
    foreach ($t in $u, $o, $ou) { [void](Invoke-Injector @('send', '--pid', "$($t.Pid)", '--text', 'quit')) }
    foreach ($t in $a, $b) { [void](Invoke-Injector @('send', '--pid', "$($t.Pid)", '--text', 'quit')) }
}
finally {
    Start-Sleep -Milliseconds 500
    foreach ($id in $started) { Get-Process -Id $id -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue }
}

$results | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $ResultsDirectory 'summary.json')
"results: $ResultsDirectory"
