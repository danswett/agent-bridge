# Advisory, line-based scan of test sources: Start-Sleep, Thread.Sleep, Task.Delay and
# wall-clock conditions in while/for/if statements. It is not a language parser.
# By default it scans tests/*.Tests, pwa/test and apple/Tests. Excludes non-source files,
# full-line comments, generated/dependency directories and reparse points; other comments
# and string literals may be reported. Explicit -Path is for focused offline fixtures.
param([string[]]$Path)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$extensions = @('.cs', '.ps1', '.js', '.mjs', '.ts', '.swift')
$excludedDirs = @('bin', 'obj', 'node_modules', '.git', 'test-results', 'playwright-report', '__snapshots__')
$patterns = @(
    '\bStart-Sleep\b',
    '\bThread\s*\.\s*Sleep\s*\(',
    '\bTask\s*\.\s*Delay\s*\(',
    '\b(?:while|for|if)\s*\([^\r\n]*(?:(?:DateTime|DateTimeOffset)\s*\.\s*(?:UtcNow|Now)\b[^\r\n]*[<>]|[<>][^\r\n]*(?:DateTime|DateTimeOffset)\s*\.\s*(?:UtcNow|Now)\b)'
)

function Warn([string]$message) {
    Write-Warning -Message "realtime-lint: $message" -WarningAction Continue
}
function Scan-File([string]$file) {
    try {
        $number = 0
        foreach ($line in [IO.File]::ReadLines($file)) {
            $number++
            if ($line -match '^\s*(//|#|/\*|\*)') { continue }
            foreach ($pattern in $patterns) {
                if ($line -match $pattern) {
                    Warn "${file}:${number}: $($Matches[0])"
                }
            }
        }
    } catch {
        Warn "could not read ${file}: $($_.Exception.Message)"
    }
}
function Scan-Path([string]$target) {
    try {
        $item = Get-Item -LiteralPath $target -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Warn "skipped reparse point: $target"
            return
        }
        if (-not $item.PSIsContainer) {
            if ($item.Extension -in $extensions) { Scan-File $item.FullName }
            return
        }
        foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) {
            if ($child.PSIsContainer -and $child.Name -in $excludedDirs) { continue }
            Scan-Path $child.FullName
        }
    } catch {
        Warn "could not scan ${target}: $($_.Exception.Message)"
    }
}

try {
    if (-not $Path) {
        $Path = @(
            Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Directory -Filter '*.Tests' |
                ForEach-Object { $_.FullName }
            Join-Path $root 'pwa/test'
            Join-Path $root 'apple/Tests'
        )
    }
    foreach ($target in $Path) { Scan-Path $target }
} catch {
    Warn "scan failed: $($_.Exception.Message)"
}
exit 0
