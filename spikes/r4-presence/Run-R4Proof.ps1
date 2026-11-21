# Runs the R4 presence proof once, unelevated, on a machine with Windows Hello. See README.md.
# Exit: 0 held, 1 failed, 2 inconclusive, 3 refused to run here.
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
if (-not $IsWindows) { Write-Host 'Windows only.'; exit 3 }
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Run this from a normal (not elevated) terminal: the proof is about code at the app''s level.'
    exit 3
}
$root = Resolve-Path "$PSScriptRoot\..\.."
# All bin and obj go here (the projects' own folders stay untouched); deleted at the end.
$out = Join-Path $PSScriptRoot '.out'
$results = Join-Path $PSScriptRoot "RESULTS-$env:COMPUTERNAME.md"
# Offline: restore only from this machine's package cache, no nuget.org and no audit call.
$cache = ((dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', '').Trim()
$offline = @("--source", $cache, "-p:NuGetAudit=false", "--artifacts-path", $out, "--nologo", "-v", "q")
$code = 3
try {
    Write-Host "Building the harness and agent-bridge-injector offline from $cache ..."
    foreach ($project in "$root\src\AgentBridge.Injector", "$PSScriptRoot\R4Proof") {
        dotnet restore $project @offline
        if ($LASTEXITCODE -ne 0) { throw "Offline restore of $project failed: a package is not in $cache. Nothing was run; send this message back." }
    }
    dotnet build "$root\src\AgentBridge.Injector" -c Release -p:PublishAot=false --no-restore --artifacts-path $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'injector build failed' }
    dotnet build "$PSScriptRoot\R4Proof" -c Release --no-restore --artifacts-path $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'harness build failed' }
    & "$out\bin\R4Proof\release\R4Proof.exe" "$out\bin\AgentBridge.Injector\release\agent-bridge-injector.exe" $results
    $code = $LASTEXITCODE
    if (Test-Path $results) { Write-Host "Send back: $results" }
}
catch { Write-Host $_; $code = 3 }
finally {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}
exit $code
