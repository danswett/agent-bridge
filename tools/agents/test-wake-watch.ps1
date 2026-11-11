#Requires -Version 7.4
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
$result = Invoke-Pester -Path (Join-Path $PSScriptRoot 'wake-watch.Tests.ps1') -Output Detailed -PassThru
if ($result.FailedCount -or $result.FailedContainersCount -or $result.TotalCount -eq 0) { exit 1 }
