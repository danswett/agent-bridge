#Requires -Version 7.4
<#
.SYNOPSIS
  Warns where a test waits in real time or reads the real clock (#929): Task.Delay, DateTime.UtcNow
  and Stopwatch in a test file. Warnings only, for now.

.DESCRIPTION
  A test that sleeps, or judges the wall clock, is slow, and flakes on a loaded runner; a fake clock
  (TimeProvider) says the same in no time. Connection's fake-clock sweep replaces today's uses
  (#929). Until it lands this lint only warns: it never fails a run or changes an exit code, and
  the warnings are its whole output. It turns into an error only once the sweep has landed.

  quality/run.ps1 dot-sources it and warns, line by line, about the changed test files, with one
  line counting the uses elsewhere. Run it on its own to list every use in the repository:

    pwsh quality/realtime-lint.ps1

  Each warning is "warning: <path>:<line>: <kind> in a test; ...", sorted by path (ordinal), line
  and kind, so two runs over the same tree print the same lines.

  Test files are those git tracks, plus the changed files run.ps1 hands it (a new, untracked one):
    C#          tests/**/*.cs, but not bin/, obj/ or generated *.g.cs and *.Designer.cs:
                Task.Delay, DateTime.UtcNow and Stopwatch, case-sensitive
    PowerShell  build/test-*.ps1, quality/test-*.ps1, *.Tests.ps1 and tests/**/*.ps1:
                [Task]::Delay, [DateTime]::UtcNow and Stopwatch, in any case
  Not JavaScript: these are .NET APIs, and a test in Node has its own timers and fake timers. Not
  production code (src/, tools/, the build scripts that aren't tests), and not node_modules.
  A line that is all comment (//, /*, * or #) is skipped; a comment after code on a line is not.

  The allowed files (Get-RealTimeTestAllowed) are those whose point is measuring wall time, each
  with its reason. It is not a baseline: today's uses in tests are all reported.
#>

# Path (or folder, ending in /) and why the real clock is right there. Keep it this narrow: a test
# that only waits for something belongs to the sweep, not here.
function Get-RealTimeTestAllowed {
    [ordered]@{
        'tests/AgentBridge.App.Tests/Speed.cs' = 'Speed.IsLinear judges how work grows with input size (#554): the wall clock is what it measures'
        'tests/AgentBridge.Benchmarks/'        = 'BenchmarkDotNet benchmarks (quality/perf.md): timing is the point'
        'tests/AgentBridge.PerfHook/'          = 'perf-hook (quality/perf.md) times the published hook as an agent runs it'
    }
}

# csharp, powershell, or $null for a file that isn't a test this lint reads.
function Get-RealTimeTestLanguage([string] $Path) {
    $p = $Path -replace '\\', '/'
    if ($p -match '(^|/)(bin|obj|node_modules)/') { return $null }
    if ($p -cmatch '^tests/.+\.cs$' -and $p -notmatch '\.(g|Designer)\.cs$') { return 'csharp' }
    if ($p -match '^(build|quality)/test-[^/]+\.ps1$' -or $p -match '\.Tests\.ps1$' -or $p -cmatch '^tests/.+\.ps1$') { return 'powershell' }
    $null
}

function Test-RealTimeTestAllowed([string] $Path) {
    foreach ($allowed in (Get-RealTimeTestAllowed).Keys) {
        if ($allowed.EndsWith('/') -and $Path.StartsWith($allowed, [StringComparison]::Ordinal)) { return $true }
        if ($Path -ceq $allowed) { return $true }
    }
    $false
}

# Every file git tracks under Root, relative to it, with forward slashes.
function Get-RealTimeTestCandidate([string] $Root) {
    $listed = (git -C $Root ls-files -z) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in $Root (exit $LASTEXITCODE)." }
    @($listed -split "`0" | Where-Object { $_ })
}

<#
.SYNOPSIS
  Every use of a real-time API in the test files among Path (relative to Root; by default every
  file git tracks), as { Path, Line, Kind }, sorted by path (ordinal), line and kind.
#>
function Find-RealTimeTestUse {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Root,
        [string[]] $Path
    )
    if ($null -eq $Path) { $Path = Get-RealTimeTestCandidate $Root }
    $rules = @{
        csharp     = @(
            @{ Kind = 'Task.Delay'; Pattern = [regex]::new('\bTask\.Delay\b') }
            @{ Kind = 'DateTime.UtcNow'; Pattern = [regex]::new('\bDateTime\.UtcNow\b') }
            @{ Kind = 'Stopwatch'; Pattern = [regex]::new('\bStopwatch\b') }
        )
        powershell = @(
            @{ Kind = 'Task.Delay'; Pattern = [regex]::new('\[(?:(?:System\.)?Threading\.Tasks\.)?Task\]::Delay\b', 'IgnoreCase') }
            @{ Kind = 'DateTime.UtcNow'; Pattern = [regex]::new('\[(?:System\.)?DateTime\]::UtcNow\b', 'IgnoreCase') }
            @{ Kind = 'Stopwatch'; Pattern = [regex]::new('\bStopwatch\b', 'IgnoreCase') }
        )
    }
    $comment = @{
        csharp     = [regex]::new('^\s*(?://|/\*|\*)')
        powershell = [regex]::new('^\s*#')
    }
    $uses = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($candidate in @($Path)) {
        $relative = "$candidate" -replace '\\', '/'
        if (-not $seen.Add($relative)) { continue }
        $language = Get-RealTimeTestLanguage $relative
        if (-not $language -or (Test-RealTimeTestAllowed $relative)) { continue }
        $full = Join-Path $Root $relative
        # A changed file the change deletes has nothing left to warn about.
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        $number = 0
        foreach ($text in [System.IO.File]::ReadLines($full)) {
            $number++
            if ($comment[$language].IsMatch($text)) { continue }
            foreach ($rule in $rules[$language]) {
                if ($rule.Pattern.IsMatch($text)) {
                    $uses.Add([pscustomobject]@{ Path = $relative; Line = $number; Kind = $rule.Kind })
                }
            }
        }
    }
    $uses.Sort([System.Comparison[object]] {
            param($x, $y)
            $order = [string]::CompareOrdinal($x.Path, $y.Path)
            if ($order -eq 0) { $order = $x.Line.CompareTo($y.Line) }
            if ($order -eq 0) { $order = [string]::CompareOrdinal($x.Kind, $y.Kind) }
            $order
        })
    $uses
}

function Format-RealTimeTestUse($Use) {
    "warning: $($Use.Path):$($Use.Line): $($Use.Kind) in a test; use a fake clock (TimeProvider) instead (#929)"
}

<#
.SYNOPSIS
  The lint's lines. With -Changed, a warning for each use in those files and one line counting the
  rest; without it, a warning for every use and a total. Nothing when there are none.
#>
function Get-RealTimeTestWarning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Root,
        # The files a change touches, relative to Root, as quality/run.ps1's selection lists them.
        [string[]] $Changed,
        # The files to read, relative to Root; by default every file git tracks.
        [string[]] $Path
    )
    if ($null -eq $Path) { $Path = Get-RealTimeTestCandidate $Root }
    $changedSet = $null
    if ($PSBoundParameters.ContainsKey('Changed')) {
        $changedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in @($Changed)) { if ($file) { $null = $changedSet.Add(("$file" -replace '\\', '/')) } }
        # A new test file isn't tracked yet, and still gets its warnings.
        $Path = @($Path) + @($changedSet)
    }
    $uses = @(Find-RealTimeTestUse -Root $Root -Path $Path)
    $listed = @(if ($null -ne $changedSet) { $uses | Where-Object { $changedSet.Contains($_.Path) } } else { $uses })
    foreach ($use in $listed) { Format-RealTimeTestUse $use }
    if ($uses.Count -eq 0) { return }
    $files = @($uses | ForEach-Object { $_.Path } | Select-Object -Unique).Count
    $until = "warnings only until Connection's fake-clock sweep lands (#929)"
    if ($null -ne $changedSet) {
        "$($listed.Count) real-time use(s) in the changed test files, $($uses.Count - $listed.Count) elsewhere in the tests; $until. pwsh quality/realtime-lint.ps1 lists them all."
    }
    else {
        "$($uses.Count) real-time use(s) in $files test file(s); $until."
    }
}

# Run on its own, not dot-sourced: every use in the repository, on stdout, and exit 0.
if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    Get-RealTimeTestWarning -Root (Split-Path -Parent $PSScriptRoot)
}
