# Offline fixtures; no GitHub calls, installer execution or publication.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$producer = Join-Path $PSScriptRoot 'release-manifest.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "release-manifest-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $scratch | Out-Null
$inputPath = Join-Path $scratch 'release.json'
$outputPath = Join-Path $scratch 'beta-release.json'
$repository = 'danswett/agent-bridge'
$base = "https://github.com/$repository/releases"
$passed = 0
$global:RmCalls = [Collections.Generic.List[string]]::new()
$global:RmPublished = $null
$global:RmGhFailure = $null

function gh {
    $a = @($args)
    $global:RmCalls.Add($a -join ' ')
    $global:LASTEXITCODE = 0
    if ($a[0] -cne 'release' -or $a[1] -cnotin 'download', 'upload' -or $a -contains '--clobber') {
        throw "stand-in gh: unexpected $($a -join ' ')"
    }
    if ($a[1] -ceq $global:RmGhFailure) { $global:LASTEXITCODE = 1; return }
    if ($a[1] -ceq 'download') {
        $dir = $a[[array]::IndexOf($a, '--dir') + 1]
        [IO.File]::WriteAllText((Join-Path $dir 'beta-release.json'), $global:RmPublished)
    } else {
        if ($null -ne $global:RmPublished) { $global:LASTEXITCODE = 1; return }
        $global:RmPublished = [IO.File]::ReadAllText($a[3])
    }
}

function New-Release {
    @{
        tag_name = 'v0.1.600'; draft = $false; prerelease = $true
        published_at = '2026-11-06T12:30:00Z'; html_url = "$base/tag/v0.1.600"
        assets = @(
            @{ name = 'Slipgates-win-Setup.exe'; state = 'uploaded'; size = 123
                digest = 'sha256:' + ('a' * 64)
                browser_download_url = "$base/download/v0.1.600/Slipgates-win-Setup.exe" }
            @{ name = 'Slipgates-win-arm64-Setup.exe'; state = 'uploaded'; size = 456
                digest = 'sha256:' + ('b' * 64)
                browser_download_url = "$base/download/v0.1.600/Slipgates-win-arm64-Setup.exe" }
            @{ name = 'RELEASES'; state = 'uploaded'; size = 42; browser_download_url = "$base/download/v0.1.600/RELEASES" }
        )
    }
}
function Write-Fixture($release) {
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath }
    $release | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $inputPath -Encoding utf8NoBOM
}
function Assert($condition, [string]$reason) {
    if (-not $condition) { throw $reason }
}

try {
    Write-Fixture (New-Release)
    & $producer -ReleaseJson $inputPath -Repository $repository -Out $outputPath
    $wire = Get-Content -LiteralPath $outputPath -Raw
    $manifest = $wire | ConvertFrom-Json -AsHashtable
    Assert ($manifest.version -ceq '0.1.600') 'version was lost'
    Assert ($wire -cmatch '"publishedAt"\s*:\s*"2026-11-06T12:30:00Z"') 'wire publication time is not the expected UTC timestamp'
    Assert ($manifest.schemaVersion -eq 1 -and $manifest.channel -ceq 'beta') 'schema version or channel changed'
    Assert ($manifest.tag -ceq 'v0.1.600' -and $manifest.releaseNotesUrl -ceq "$base/tag/v0.1.600") 'tag or release notes URL changed'
    Assert ($manifest.downloads.Count -eq 2) 'unrelated release assets became downloads'
    Assert ($manifest.downloads['win-x64'].fileName -ceq 'Slipgates-win-Setup.exe') 'x64 installer fileName changed'
    Assert ($manifest.downloads['win-arm64'].fileName -ceq 'Slipgates-win-arm64-Setup.exe') 'Arm64 installer fileName changed'
    Assert ($manifest.downloads['win-x64'].url -ceq "$base/download/v0.1.600/Slipgates-win-Setup.exe") 'x64 installer URL changed'
    Assert ($manifest.downloads['win-arm64'].url -ceq "$base/download/v0.1.600/Slipgates-win-arm64-Setup.exe") 'Arm64 installer URL changed'
    Assert ($manifest.downloads['win-arm64'].sizeBytes -eq 456) 'installer size was lost'
    Assert ($manifest.downloads['win-x64'].sha256 -ceq ('a' * 64) -and $manifest.downloads['win-arm64'].sha256 -ceq ('b' * 64)) 'installer checksums were lost or exchanged between architectures'
    $passed++
    Write-Host 'PASS: a complete published pre-release produces both architecture downloads'

    $culture = [Globalization.CultureInfo]::CurrentCulture
    try {
        foreach ($name in 'fi-FI', 'ar-SA') {
            [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($name)
            $release = New-Release
            $release.published_at = '2026-11-06T04:30:00-08:00'
            Write-Fixture $release
            & $producer -ReleaseJson $inputPath -Repository $repository -Out $outputPath
            $wire = Get-Content -LiteralPath $outputPath -Raw
            Assert ($wire -cmatch '"publishedAt"\s*:\s*"2026-11-06T12:30:00Z"') "$name changed the wire timestamp's calendar, separator or UTC conversion"
            $passed++
            Write-Host "PASS: $name preserves the invariant UTC wire timestamp"
        }
    } finally { [Globalization.CultureInfo]::CurrentCulture = $culture }

    foreach ($case in @(
        @{ Name = 'missing manifest uploads once'; Existing = $false; Remote = $null; Failure = $null; Error = $null; Calls = 1 }
        @{ Name = 'matching manifest is retained'; Existing = $true; Remote = 'matching'; Failure = $null; Error = $null; Calls = 1 }
        @{ Name = 'mismatched manifest is not replaced'; Existing = $true; Remote = '{}'; Failure = $null; Error = '*does not match*'; Calls = 1 }
        @{ Name = 'download failure does not replace a manifest'; Existing = $true; Remote = 'matching'; Failure = 'download'; Error = '*download failed*'; Calls = 1 }
        @{ Name = 'upload failure is surfaced'; Existing = $false; Remote = $null; Failure = 'upload'; Error = '*upload failed*'; Calls = 1 }
        @{ Name = 'asset added after metadata read is not clobbered'; Existing = $false; Remote = 'matching'; Failure = $null; Error = '*upload failed*'; Calls = 1 }
    )) {
        $release = New-Release
        Write-Fixture $release
        & $producer -ReleaseJson $inputPath -Repository $repository -Out $outputPath
        $expected = [IO.File]::ReadAllText($outputPath)
        $global:RmPublished = if ($case.Remote -ceq 'matching') { $expected } else { $case.Remote }
        $before = $global:RmPublished
        $global:RmGhFailure = $case.Failure
        $global:RmCalls.Clear()
        if ($case.Existing) { $release.assets += @{ name = 'beta-release.json'; state = 'uploaded' } }
        Write-Fixture $release
        $reason = $null
        try { & $producer -ReleaseJson $inputPath -Repository $repository -Out $outputPath -Publish }
        catch { $reason = $_.Exception.Message }
        if ($case.Error) {
            Assert ($reason -and $reason -like $case.Error) "$($case.Name): unexpected refusal '$reason'"
        } else {
            Assert (-not $reason) "$($case.Name): unexpected failure '$reason'"
        }
        Assert ($global:RmCalls.Count -eq $case.Calls) "$($case.Name): unexpected extra GitHub calls"
        if ($case.Existing) {
            Assert ($global:RmCalls[0].StartsWith('release download ')) "$($case.Name): tried to replace an existing asset"
        }
        $wanted = if ($case.Existing -or $case.Error) { $before } else { $expected }
        Assert ($global:RmPublished -ceq $wanted) "$($case.Name): published content was lost or changed"
        $passed++
        Write-Host "PASS: $($case.Name)"
    }

    $cases = @(
        @{ Name = 'draft'; Change = { param($r) $r.draft = $true }; Error = '*draft*' }
        @{ Name = 'stable release'; Change = { param($r) $r.prerelease = $false }; Error = '*pre-release*' }
        @{ Name = 'noncanonical version'; Change = { param($r) $r.tag_name = 'v0.1.0600' }; Error = '*Beta tag*' }
        @{ Name = 'missing Arm64'; Change = { param($r) $r.assets = @($r.assets[0]) }; Error = '*exactly one installer*' }
        @{ Name = 'legacy-only installer names'; Change = {
            param($r)
            foreach ($asset in $r.assets[0..1]) {
                $asset.name = $asset.name.Replace('Slipgates-', 'AgentBridgeApp-')
                $asset.browser_download_url = $asset.browser_download_url.Replace('Slipgates-', 'AgentBridgeApp-')
            }
        }; Error = '*exactly one installer*' }
        @{ Name = 'duplicate installer'; Change = { param($r) $r.assets += $r.assets[0] }; Error = '*exactly one installer*' }
        @{ Name = 'empty installer'; Change = { param($r) $r.assets[0].size = 0 }; Error = '*positive integer size*' }
        @{ Name = 'upload unfinished'; Change = { param($r) $r.assets[1].state = 'new' }; Error = '*not uploaded*' }
        @{ Name = 'wrong release URL'; Change = { param($r) $r.assets[0].browser_download_url = "$base/download/v0.1.599/Slipgates-win-Setup.exe" }; Error = '*URL does not belong*' }
        @{ Name = 'missing Arm64 digest'; Change = { param($r) $r.assets[1].digest = $null }; Error = '*SHA-256 digest*' }
        @{ Name = 'malformed digest'; Change = { param($r) $r.assets[0].digest = 'sha256:not-a-sha256' }; Error = '*SHA-256 digest*' }
        @{ Name = 'foreign release notes'; Change = { param($r) $r.html_url = 'https://example.com/release' }; Error = '*release notes URL*' }
        @{ Name = 'unpublished timestamp'; Change = { param($r) $r.published_at = $null }; Error = '*published_at*' }
    )
    foreach ($case in $cases) {
        $release = New-Release
        & $case.Change $release
        Write-Fixture $release
        $reason = $null
        try { & $producer -ReleaseJson $inputPath -Repository $repository -Out $outputPath }
        catch { $reason = $_.Exception.Message }
        Assert ($reason -and $reason -like $case.Error) "$($case.Name): unexpected refusal '$reason'"
        Assert (-not (Test-Path -LiteralPath $outputPath)) "$($case.Name): invalid metadata wrote a manifest"
        $passed++
        Write-Host "PASS: $($case.Name) is refused before a manifest is written"
    }
    Write-Host "release-manifest: $passed fixtures passed"
} finally {
    foreach ($path in @($inputPath, $outputPath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $scratch
    Remove-Variable RmCalls, RmPublished, RmGhFailure -Scope Global
}
