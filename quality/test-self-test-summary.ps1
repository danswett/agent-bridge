$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/self-test-summary.ps1"
$checks = @(1..12 | ForEach-Object { @{ name = "check | $_"; ms = $_ * 1000 } })
$result = [pscustomobject]@{ timings = [pscustomobject]@{ checks = $checks; totalMs = 80000 } }
$summary = ConvertTo-SelfTestSummary $result
if ($summary -notmatch '80.000 s' -or $summary -notmatch 'check &#124; 12' -or
    $summary -match 'check &#124; 2 \|') { throw 'Summary must contain only the ten slowest checks and the total' }
if ($summary.IndexOf('check &#124; 12') -gt $summary.IndexOf('check &#124; 11')) { throw 'Slowest checks must come first' }
if ((ConvertTo-SelfTestSummary ([pscustomobject]@{})) -notmatch 'unavailable') { throw 'Legacy results need an explicit unavailable summary' }
$unsafe = [pscustomobject]@{ timings = [pscustomobject]@{ totalMs = 1; checks = @(@{ name = "<img>`nnext"; ms = 1 }) } }
if ((ConvertTo-SelfTestSummary $unsafe) -match '<img>|[\r\n]next') { throw 'Check labels must not inject summary markup' }
'self-test summary canned-data checks passed'
