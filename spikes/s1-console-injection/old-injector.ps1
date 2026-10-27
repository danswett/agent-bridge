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
# The file compiles more than one type; take the block that defines the injector.
$m = [regex]::Matches($src, "-Source @'\r?\n(?<cs>.*?)\r?\n'@", 'Singleline') |
    Where-Object { $_.Groups['cs'].Value -match 'class ConsoleInjector' } | Select-Object -First 1
if (-not $m) { throw 'ConsoleInjector source not found' }
Add-Type -TypeDefinition $m.Groups['cs'].Value
$r = [CopilotCli.ConsoleInjector]::Send([uint32]$TargetPid, $Text, $true, 300)
Add-Content -LiteralPath $Out -Value $r -Encoding utf8
