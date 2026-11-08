#Requires -Version 7
$ErrorActionPreference = 'Stop'

pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-provision.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-run.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-beta-acceptance.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
