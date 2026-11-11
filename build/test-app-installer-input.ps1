# Offline fixtures: no builds, installer execution or network.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'app-channels.ps1')
. (Join-Path $PSScriptRoot 'app-installer-input.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) "installer-input-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $scratch | Out-Null
$passed = 0
try {
    foreach ($runtime in 'win-x64', 'win-arm64') {
        $root = Join-Path $scratch $runtime
        $publish = Join-Path $root 'publish'
        $releases = Join-Path $root 'releases'
        $inputPath = Join-Path $root 'input'
        New-Item -ItemType Directory (Join-Path $publish 'ThirdPartyNotices'), $releases | Out-Null
        $files = @(foreach ($name in 'LICENSE.txt', 'SOURCE.txt', 'THIRD-PARTY-NOTICES.txt') {
            $path = Join-Path $publish $name
            Set-Content -LiteralPath $path -Value "fixture $name"
            @{ path = $name; sha256 = (Get-FileHash $path).Hash.ToLowerInvariant() }
        })
        @{
            schemaVersion = 1; version = '0.1.600'; runtime = $runtime; commit = 'a' * 40
            files = $files; packages = @(@{ id = 'fixture'; version = '1'; files = @('LICENSE.txt') })
        } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $publish 'ThirdPartyNotices/manifest.json')
        $channel = (Get-AppChannel $runtime).Channel
        foreach ($kind in 'Installer', 'Full', 'Portable') {
            Set-Content -LiteralPath (Join-Path $releases (Get-AppAssetName $channel $kind '0.1.600')) -Value "$runtime $kind"
        }
        $identity = @{ Version = '0.1.600'; Runtime = $runtime; Commit = 'a' * 40; RunId = '12345' }
        Write-AppInstallerInput -Publish $publish -Releases $releases -Out $inputPath @identity
        Assert-AppInstallerInput -InputPath $inputPath -Releases $releases @identity
        $passed++
        Write-Host "PASS: $runtime accepts the matching run, commit, version, notices and release bytes"

        foreach ($field in 'Commit', 'Runtime', 'Version', 'RunId') {
            $wrong = $identity.Clone()
            $wrong[$field] = switch ($field) {
                Commit { 'b' * 40 }
                Runtime { if ($runtime -eq 'win-x64') { 'win-arm64' } else { 'win-x64' } }
                Version { '0.1.601' }
                RunId { '12346' }
            }
            $reason = $null
            try { Assert-AppInstallerInput -InputPath $inputPath -Releases $releases @wrong }
            catch { $reason = $_.Exception.Message }
            if ($reason -notlike "*identity mismatch*$field*") { throw "$runtime/${field}: unexpected refusal '$reason'" }
            $passed++
            Write-Host "PASS: $runtime refuses a different $field before installation"
        }
        $manifestPath = Join-Path $inputPath 'installer-input.json'
        $manifest = Get-Content $manifestPath -Raw
        $refusals = [ordered]@{
            'missing identity' = '*missing notice file*installer-input.json'
            'missing release' = '*missing notice file*Setup.exe'
            'altered release' = '*release hash mismatch*Setup.exe'
            'missing notices' = '*missing notice file*manifest.json'
            'foreign notice commit' = '*notice commit mismatch'
        }
        foreach ($case in $refusals.Keys) {
            $setup = Join-Path $releases (Get-AppAssetName $channel 'Installer' '0.1.600')
            $setupBytes = [IO.File]::ReadAllBytes($setup)
            $notices = Join-Path $inputPath 'publish/ThirdPartyNotices/manifest.json'
            $noticeBytes = [IO.File]::ReadAllBytes($notices)
            switch ($case) {
                'missing identity' { Remove-Item -LiteralPath $manifestPath }
                'missing release' { Remove-Item -LiteralPath $setup }
                'altered release' { Add-Content -LiteralPath $setup -Value 'changed' }
                'missing notices' { Remove-Item -LiteralPath $notices }
                'foreign notice commit' {
                    (Get-Content $notices -Raw).Replace(('a' * 40), ('b' * 40)) | Set-Content $notices
                }
            }
            $reason = $null
            try { Assert-AppInstallerInput -InputPath $inputPath -Releases $releases @identity }
            catch { $reason = $_.Exception.Message }
            if ($reason -notlike $refusals[$case]) { throw "$runtime/${case}: unexpected refusal '$reason'" }
            Set-Content -LiteralPath $manifestPath -Value $manifest
            [IO.File]::WriteAllBytes($setup, $setupBytes)
            [IO.File]::WriteAllBytes($notices, $noticeBytes)
            $passed++
            Write-Host "PASS: $runtime refuses $case before installation"
        }
    }
    Write-Host "app-installer-input: $passed fixtures passed"
} finally {
    Remove-Item -LiteralPath $scratch -Recurse
}
