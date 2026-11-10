#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflow = Join-Path $root '.github' 'workflows' 'halts-upload.yml'
if (-not (Test-Path $workflow)) { throw 'The reviewed no-build halt upload workflow is missing.' }
$text = (Get-Content -Raw $workflow) -replace "`r`n", "`n"
function Require([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }

Require ($text -match '(?m)^permissions: \{\}$') 'The workflow must deny permissions by default.'
Require ($text -match '(?m)^    if: github.ref == ''refs/heads/main''$') 'Manual runs must refuse branches other than main.'
Require ($text -match '(?ms)^  push:\r?\n    branches: \[main\]\r?\n    paths: \[ops/halts.json\]\r?\n  workflow_dispatch:') 'Only main policy pushes and manual dispatch may trigger uploads.'
Require (@([regex]::Matches($text, '(?m)^  [a-z-]+:\r?\n    if:')).Count -eq 1) 'There must be one job.'
Require ($text -match '(?m)^      contents: write$') 'Only the upload job may have release-write permission.'
Require ($text -notmatch 'uses:|actions/checkout|git checkout|dotnet |npm |persist-credentials: true') 'The upload must not check out code, persist credentials, or build.'
Require ($text -match '(?m)^  cancel-in-progress: false$') 'A later push must not interrupt an upload.'
$match = [regex]::Match($text, '(?ms)^        run: \|\r?\n(.*)\z')
Require $match.Success 'The workflow must expose its upload step for offline execution.'
$upload = [scriptblock]::Create(($match.Groups[1].Value -replace '(?m)^          ', ''))
$validator = Get-Content -Raw (Join-Path $root 'build' 'validate-halt-list.ps1')

# Execute the real workflow body; only GitHub is replaced, and no credentials or network are used.
function Test-Upload([string]$Name, [string]$Policy, [string]$Failure = '', [string]$ErrorPattern = '') {
    $dir = Join-Path ([IO.Path]::GetTempPath()) "halts-upload-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $dir | Out-Null
    $calls = [System.Collections.Generic.List[string]]::new()
    $state = @{ Lists = 0; Uploaded = $false }
    function gh {
        $command = $args -join ' '
        $calls.Add($command)
        if ($command -like 'api *git/ref/heads/main*') { return ('a' * 40) }
        if ($command -like 'api *contents/*') {
            Require ($command -like "*?ref=$('a' * 40)*") 'Policy and validator must come from the same current main commit.'
            if ($Failure -eq 'fetch') { throw 'policy fetch failed' }
            if ($command -like '*contents/build/validate-halt-list.ps1*') { return $validator }
            if ($command -like '*contents/ops/halts.json*') { return $Policy }
        }
        if ($command -like 'release list *') {
            Require ($command -match '--exclude-drafts --limit 1') 'Selection must include prereleases, exclude drafts, and choose one newest release.'
            $state.Lists++
            if ($Failure -eq 'list') { throw 'release list failed' }
            if ($Failure -eq 'empty') { return }
            if ($Failure -eq 'race' -and $state.Lists -gt 1) { return 'v0.1.43' }
            return 'v0.1.42'
        }
        if ($command -like 'release upload *') {
            Require ($command -like 'release upload v0.1.42 halts.json --clobber *') 'The uploaded asset must be halts.json with replacement enabled.'
            Require ((Get-Content -Raw 'halts.json').Trim() -eq $Policy.Trim()) 'Upload must preserve the validated policy.'
            if ($Failure -eq 'upload') { throw 'upload failed' }
            $state.Uploaded = $true
            return
        }
        if ($command -like 'api *releases/tags/v0.1.42*') {
            $digest = 'sha256:' + (Get-FileHash 'halts.json' -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($Failure -eq 'digest') { $digest = 'sha256:' + ('0' * 64) }
            return $digest
        }
        throw "Unexpected GitHub call: $command"
    }
    Push-Location $dir
    try {
        $errorMessage = ''
        try { & $upload } catch { $errorMessage = $_.Exception.Message }
        if ($ErrorPattern) { Require ($errorMessage -match $ErrorPattern) "$Name did not fail clearly: $errorMessage" }
        else { Require (-not $errorMessage) "$Name failed: $errorMessage" }
        if ($Failure -in @('fetch', 'list', 'empty') -or $Name -like 'invalid*') {
            Require (-not $state.Uploaded) "$Name uploaded despite invalid or unavailable input."
        }
        if ($Name -like 'invalid*') {
            Require ($state.Lists -eq 0) 'Invalid policy must fail before release selection.'
        }
        if (-not $ErrorPattern) { Require $state.Uploaded 'The valid policy was not uploaded.' }
        Write-Host "PASS: $Name"
    }
    finally {
        Pop-Location
        Remove-Item -LiteralPath $dir -Recurse
    }
}

$previousRepo = $env:GITHUB_REPOSITORY
try {
    $env:GITHUB_REPOSITORY = 'example/repo'
    Test-Upload 'valid policy replaces the newest published prerelease asset' '{"v":1,"halted":["0.1.42"]}'
    Test-Upload 'invalid extra property prevents any release access' '{"v":1,"halted":[],"extra":true}' -ErrorPattern 'exactly v and halted'
    Test-Upload 'invalid version prevents any release access' '{"v":1,"halted":["0.1.042"]}' -ErrorPattern 'invalid halted version'
    Test-Upload 'missing source prevents upload' '{"v":1,"halted":[]}' -Failure fetch -ErrorPattern 'policy fetch failed'
    Test-Upload 'no release fails clearly without upload' '{"v":1,"halted":[]}' -Failure empty -ErrorPattern 'No published release'
    Test-Upload 'release lookup failure is not an empty success' '{"v":1,"halted":[]}' -Failure list -ErrorPattern 'release list failed'
    Test-Upload 'upload failure is not reported as success' '{"v":1,"halted":[]}' -Failure upload -ErrorPattern 'upload failed'
    Test-Upload 'digest mismatch fails verification' '{"v":1,"halted":[]}' -Failure digest -ErrorPattern 'digest'
    Test-Upload 'a racing publication requires a fresh run' '{"v":1,"halted":[]}' -Failure race -ErrorPattern 'newer release'
}
finally { $env:GITHUB_REPOSITORY = $previousRepo }
