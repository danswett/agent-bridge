# Delivers text with the *existing* agent-ha-bridge injector (the C# class compiled by
# hooks/decision-inject.ps1), so the new injector's results can be compared with what
# the bridge does today on the same target. Run hidden, as the daemon is.
param(
    [Parameter(Mandatory)][string]$DecisionInjectPath,
    [Parameter(Mandatory)][int]$TargetPid,
    [Parameter(Mandatory)][string]$Text,
    [Parameter(Mandatory)][string]$Out
)
$src = Get-Content -Raw -LiteralPath $DecisionInjectPath
$m = [regex]::Match($src, "-Source @'\r?\n(?<cs>.*?)\r?\n'@", 'Singleline')
if (-not $m.Success) { throw 'ConsoleInjector source not found' }
Add-Type -TypeDefinition $m.Groups['cs'].Value
$r = [CopilotCli.ConsoleInjector]::Send([uint32]$TargetPid, $Text, $true, 300)
Add-Content -LiteralPath $Out -Value $r -Encoding utf8
