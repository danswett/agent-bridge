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

function New-Release {
    @{
        tag_name = 'v0.1.600'; draft = $false; prerelease = $true
        published_at = '2026-11-06T12:30:00Z'; html_url = "$base/tag/v0.1.600"
        assets = @(
            @{ name = 'AgentBridgeApp-win-Setup.exe'; state = 'uploaded'; size = 123
                digest = 'sha256:' + ('a' * 64)
                browser_download_url = "$base/download/v0.1.600/AgentBridgeApp-win-Setup.exe" }
            @{ name = 'AgentBridgeApp-win-arm64-Setup.exe'; state = 'uploaded'; size = 456
                digest = 'sha256:' + ('b' * 64)
                browser_download_url = "$base/download/v0.1.600/AgentBridgeApp-win-arm64-Setup.exe" }
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
    $manifest = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json -AsHashtable
    Assert ($manifest.version -ceq '0.1.600' -and $manifest.publishedAt -eq '2026-11-06T12:30:00Z') 'version or publication time was lost'
    Assert ($manifest.downloads.Count -eq 2) 'unrelated release assets became downloads'
    Assert ($manifest.downloads['win-x64'].url -ceq "$base/download/v0.1.600/AgentBridgeApp-win-Setup.exe") 'x64 installer URL changed'
    Assert ($manifest.downloads['win-arm64'].url -ceq "$base/download/v0.1.600/AgentBridgeApp-win-arm64-Setup.exe") 'Arm64 installer URL changed'
    Assert ($manifest.downloads['win-arm64'].sizeBytes -eq 456) 'installer size was lost'
    Assert ($manifest.downloads['win-x64'].sha256 -ceq ('a' * 64) -and $manifest.downloads['win-arm64'].sha256 -ceq ('b' * 64)) 'installer checksums were lost or exchanged between architectures'
    $passed++
    Write-Host 'PASS: a complete published pre-release produces both architecture downloads'

    $cases = @(
        @{ Name = 'draft'; Change = { param($r) $r.draft = $true }; Error = '*draft*' }
        @{ Name = 'stable release'; Change = { param($r) $r.prerelease = $false }; Error = '*pre-release*' }
        @{ Name = 'noncanonical version'; Change = { param($r) $r.tag_name = 'v0.1.0600' }; Error = '*Beta tag*' }
        @{ Name = 'missing Arm64'; Change = { param($r) $r.assets = @($r.assets[0]) }; Error = '*exactly one installer*' }
        @{ Name = 'duplicate installer'; Change = { param($r) $r.assets += $r.assets[0] }; Error = '*exactly one installer*' }
        @{ Name = 'empty installer'; Change = { param($r) $r.assets[0].size = 0 }; Error = '*positive integer size*' }
        @{ Name = 'upload unfinished'; Change = { param($r) $r.assets[1].state = 'new' }; Error = '*not uploaded*' }
        @{ Name = 'wrong release URL'; Change = { param($r) $r.assets[0].browser_download_url = "$base/download/v0.1.599/AgentBridgeApp-win-Setup.exe" }; Error = '*URL does not belong*' }
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
}
