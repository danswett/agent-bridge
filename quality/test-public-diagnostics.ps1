#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflow = Get-Content -Raw (Join-Path $root '.github/workflows/ci.yml')
$stages = [regex]::Matches($workflow, '(?ms)^      - name: Stage named (?<installed>installed )?self-test text, never WER dumps or archives\r?\n.*?^        run: \|\r?\n(?<body>.*?)(?=^      - )')
if ($stages.Count -ne 2) { throw 'Both public diagnostic staging steps must be tested.' }
function Require([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
$failed = 0
$passed = 0
$previousTemp = $env:RUNNER_TEMP
Push-Location $root
try {
    foreach ($stage in $stages) {
        $installed = $stage.Groups['installed'].Success
        $stageBody = [scriptblock]::Create(($stage.Groups['body'].Value -replace '(?m)^          ', ''))
        foreach ($case in 'scrubbing', 'oversized', 'invalid-json', 'invalid-utf8', 'dump', 'linked-directory', 'stale-output') {
            $scratch = Join-Path ([IO.Path]::GetTempPath()) "public-diagnostics-$([guid]::NewGuid().ToString('N'))"
            $env:RUNNER_TEMP = $scratch
            $source = Join-Path $scratch 'selftest'
            $logs = if ($installed) { Join-Path $source 'installed' } else { $source }
            $out = Join-Path $scratch $(if ($installed) { 'public-installed-selftest' } else { 'public-selftest' })
            $link = $null
            try {
                New-Item -ItemType Directory -Path $logs -Force | Out-Null
                $private = 'private-free-text C:\Users\private-user\hidden.txt Authorization: Bearer private-credential-fixture'
                $names = @('result.json', 'app-exit-cause.json', 'app.stderr.log', 'app.log', 'app-crash-analysis.txt')
                if ($installed) { $names += 'setup.log', 'distribution-evidence.json' }
                foreach ($name in $names) {
                    $text = if ($name.EndsWith('.json')) {
                        @{ failed = $true; exitCode = -1; events = @(@{ eventId = 1000; message = $private })
                            'private-free-text-key' = $private; stderr = $private } | ConvertTo-Json -Depth 6
                    } else { "EXCEPTION_CODE_STR: 0xc0000005`n$private" }
                    [IO.File]::WriteAllText((Join-Path $logs $name), $text)
                }
                if ($installed) { [IO.File]::WriteAllText((Join-Path $source 'installer-input.json'), '{"version":"private-free-text"}') }
                [IO.File]::WriteAllText((Join-Path $logs 'unselected.dmp'), 'MDMP')
                switch ($case) {
                    oversized { [IO.File]::WriteAllText((Join-Path $logs 'app.log'), ('x' * 65537)) }
                    invalid-json { [IO.File]::WriteAllText((Join-Path $logs 'result.json'), '{broken') }
                    invalid-utf8 { [IO.File]::WriteAllBytes((Join-Path $logs 'app.log'), [byte[]]@(255)) }
                    dump { [IO.File]::WriteAllText((Join-Path $logs 'app.log'), 'MDMPfake') }
                    linked-directory {
                        $target = Join-Path $scratch 'linked-input'
                        Move-Item -LiteralPath $source -Destination $target
                        $link = $source
                        $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
                        New-Item -ItemType $linkType -Path $link -Target $target | Out-Null
                    }
                    stale-output {
                        New-Item -ItemType Directory -Path $out | Out-Null
                        [IO.File]::WriteAllText((Join-Path $out 'stale.dmp'), 'MDMP')
                    }
                }
                $refused = $false
                try { & $stageBody | Out-Null } catch { $refused = $true }
                if ($case -ne 'scrubbing') {
                    Require $refused "$case must refuse staging, so the success-gated upload cannot run."
                } else {
                    Require (-not $refused) 'Valid diagnostic text must stage successfully.'
                    $files = @(Get-ChildItem -LiteralPath $out -Recurse -File)
                    Require ($files.Count -eq ($names.Count + [int]$installed)) 'Only the named text files may be staged.'
                    foreach ($file in $files) {
                        $public = [IO.File]::ReadAllText($file.FullName)
                        Require ($file.Length -le 65536) 'Every public diagnostic must be size-capped.'
                        Require ($public -notmatch 'private-|C:\\Users\\|Authorization:|Bearer ') 'Runner paths, arbitrary keys, user text and credentials must not be public.'
                        if ($file.Extension -eq '.json') { $null = $public | ConvertFrom-Json -ErrorAction Stop }
                    }
                    $resultPath = Join-Path $out $(if ($installed) { 'installed/result.json' } else { 'result.json' })
                    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
                    Require ($result.failed -eq $true -and $result.exitCode -eq -1) 'Scrubbing must preserve typed failure evidence.'
                    $crashPath = Join-Path $out $(if ($installed) { 'installed/app-crash-analysis.txt' } else { 'app-crash-analysis.txt' })
                    Require ((Get-Content -Raw -LiteralPath $crashPath) -match 'EXCEPTION_CODE_STR: 0xc0000005') 'The numeric crash exception code must remain useful.'
                }
                $passed++
                Write-Host "PASS: installed=$installed $case"
            } catch {
                $failed++
                Write-Host "FAIL: installed=$installed $case - $($_.Exception.Message)"
            } finally {
                if ($link -and (Test-Path -LiteralPath $link)) { Remove-Item -LiteralPath $link -Force }
                Remove-Item -LiteralPath $scratch -Recurse -Force
            }
        }
    }
} finally {
    Pop-Location
    $env:RUNNER_TEMP = $previousTemp
}
Write-Host "$passed passed; $failed failed."
if ($failed) { exit 1 }
