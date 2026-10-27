# A stand-in for an agent CLI: reads lines from its console and records each one, so
# the harness can see exactly what an injection typed. It also records which console
# host it landed in, because conhost and Windows Terminal (ConPTY) are different code
# paths for the same API.
param(
    [Parameter(Mandatory)][string]$Out,
    [switch]$TreatControlCAsInput,
    [switch]$Utf8
)

if ($Utf8) { [Console]::InputEncoding = [Text.UTF8Encoding]::new($false) }

Add-Type -Namespace S1 -Name Win -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(System.IntPtr hwnd, System.Text.StringBuilder name, int max);
'@
$sb = [System.Text.StringBuilder]::new(256)
[void][S1.Win]::GetClassNameW([S1.Win]::GetConsoleWindow(), $sb, 256)
if ($TreatControlCAsInput) { [Console]::TreatControlCAsInput = $true }

$start = [ordered]@{ kind = 'start'; pid = $PID; consoleClass = $sb.ToString() } | ConvertTo-Json -Compress
Add-Content -LiteralPath $Out -Value $start -Encoding utf8

while ($true) {
    $line = [Console]::ReadLine()
    if ($null -eq $line) { break }
    $rec = [ordered]@{ kind = 'line'; text = $line } | ConvertTo-Json -Compress
    Add-Content -LiteralPath $Out -Value $rec -Encoding utf8
    if ($line -eq 'quit') { break }
}
