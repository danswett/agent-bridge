$ErrorActionPreference = 'Stop'
$file = Join-Path $PSScriptRoot 'dismiss.ps1'
$tokens = $null; $errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
if ($errors) { throw ($errors | Out-String) }
. $file
# Replace every desktop boundary: this test must never send input or show a window.
$script:retained = $false
function Get-Arm64Foreground {
    $cleared = $script:dismissed -gt 0 -and -not $script:retained
    [pscustomobject]@{ Handle = [IntPtr]$(if ($cleared) { 2 } else { 1 }); Class = 'Windows.UI.Core.CoreWindow'; Title = 'Microsoft account'; Process = $(if ($script:popup -and -not $cleared) { 'WWAHost' } else { 'explorer' }) }
}
function Close-Arm64Popup { $script:dismissed++ }
function Test-Arm64Foreground { $script:probed++; Write-Host "probe=$script:success"; return $script:success }
foreach ($popup in $false, $true) {
    foreach ($success in $false, $true) {
        $script:popup = $popup; $script:success = $success
        $script:dismissed = 0; $script:probed = 0; $failed = $false
        try { Invoke-Arm64PopupDismiss } catch {
            $failed = $true
            if ($_.Exception.Message -notmatch 'foreground before:.*foreground after:') { throw }
        }
        if ($failed -ne ($popup -and -not $success) -or $script:probed -ne 1 -or $script:dismissed -ne [int]$popup) {
            throw "Wrong popup/probe result: popup=$popup success=$success failed=$failed dismissed=$script:dismissed probed=$script:probed"
        }
    }
}
$script:popup = $script:success = $script:retained = $true
$script:dismissed = $script:probed = 0; $failed = $false
try { Invoke-Arm64PopupDismiss } catch {
    $failed = $true
    if ($_.Exception.Message -notmatch 'popup still owns foreground.*foreground before:.*foreground after:') { throw }
}
if (-not $failed -or $script:probed -ne 0 -or $script:dismissed -ne 1) { throw 'A retained popup must fail before the probe.' }
'PASS: script parsing, no-popup paths, dismissed and retained popups (desktop calls replaced).'
