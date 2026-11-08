Set-StrictMode -Version Latest

# Takes names, paths and credentials out of text before it is written to a results file.
function Get-Redacted([string]$text) {
    $t = $text
    foreach ($v in @($env:USERNAME, $env:COMPUTERNAME, $env:USERDOMAIN) | Where-Object { $_ -and $_.Length -gt 2 }) {
        $t = $t -replace [regex]::Escape($v), '<redacted>'
    }
    $t = $t -replace '[A-Za-z]:\\[^\s]*', '<path>'
    $t = $t -replace '(gh[pousr]_|github_pat_)\w+', '<token>'
    $t = $t -replace 'eyJ[\w-]+\.[\w-]+\.[\w-]*', '<token>'
    $t = $t -replace '(?i)\bBearer\s+[\w.~+/=-]+', 'Bearer <token>'
    $t = $t -replace '(?i)\b(authorization|token|secret|password|passwd|api[_-]?key)(\s*[:=]\s*)\S+', '$1$2<token>'
    $t -replace '[\w.+-]+@[\w-]+\.[\w.-]+', '<email>'
}
