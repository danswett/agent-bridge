# Runs the R4 presence proof once, unelevated, on a machine with Windows Hello. See README.md.
# Exit: 0 held, 1 failed, 2 inconclusive, 3 refused to run here.
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { Write-Host 'Windows only.'; exit 3 }
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Run this from a normal (not elevated) terminal: the proof is about code at the app''s level.'
    exit 3
}
$root = Resolve-Path "$PSScriptRoot\..\.."
$tmp = Join-Path ([IO.Path]::GetTempPath()) "r4-presence-$PID"
$results = Join-Path $PSScriptRoot "RESULTS-$env:COMPUTERNAME.md"
$code = 3
try {
    Write-Host 'Building the harness and agent-bridge-injector...'
    dotnet build "$root\src\AgentBridge.Injector" -c Release -p:PublishAot=false -o "$tmp\injector" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'injector build failed' }
    dotnet build "$PSScriptRoot\R4Proof" -c Release -o "$tmp\proof" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'harness build failed' }
    & "$tmp\proof\R4Proof.exe" "$tmp\injector\agent-bridge-injector.exe" $results
    $code = $LASTEXITCODE
    if (Test-Path $results) { Write-Host "Send back: $results" }
}
catch { Write-Host $_; $code = 3 }
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
exit $code
