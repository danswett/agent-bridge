# Checks Get-Redacted. Exits 1 on the first value that comes through unmasked. The values are
# made up, shaped like real ones.
. (Join-Path $PSScriptRoot 'Redaction.ps1')

$secret = 'abcDEF0123456789abcDEF0123456789'
$jwt = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.c2lnbmF0dXJlMTIz'
$cases = @(
    @{ In = "Authorization: Bearer $secret"; Must = $secret },
    @{ In = "sent Bearer $jwt to the server"; Must = $jwt },
    @{ In = "id $jwt"; Must = 'eyJzdWIi' },
    @{ In = 'token=ghp_0123456789abcdefghij0123456789abcdef'; Must = 'ghp_0123' },
    @{ In = 'github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz'; Must = 'github_pat_11ABC' },
    @{ In = "password: $secret"; Must = $secret },
    @{ In = 'see C:\Users\someone\file.txt'; Must = 'someone' },
    @{ In = "COPILOT_GITHUB_TOKEN=$secret"; Must = $secret },
    @{ In = "access_token=$secret"; Must = $secret },
    @{ In = "{`"token`":`"$secret`"}"; Must = $secret },
    @{ In = 'Authorization: Basic dXNlcjpwYXNz'; Must = 'dXNlcjpwYXNz' },
    @{ In = 'password: "my secret words"'; Must = 'words' },
    @{ In = 'see C:/Users/someone/x.txt'; Must = 'someone' },
    @{ In = 'see \\srv\share\someone'; Must = 'share' },
    @{ In = 'mail a.person@example.com now'; Must = 'a.person' }
)
$failed = 0
foreach ($c in $cases) {
    $out = Get-Redacted $c.In
    if ($out.Contains($c.Must)) { Write-Host "NOT MASKED: '$($c.In)' -> '$out'"; $failed++ }
}
if ($failed) { exit 1 }
Write-Host "All $($cases.Count) redaction cases pass."
