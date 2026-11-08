# The only file in the Beta acceptance run that touches a credential (#1185). The throwaway GitHub
# test account's username, password and TOTP secret arrive as environment variables from the
# `beta-acceptance` Actions environment. This file checks they exist, masks each in the job log
# before anything can print it, and computes the current TOTP code. It never writes, prints or
# returns a secret except as the code a login form needs for the next 30 seconds.
#Requires -Version 7
Set-StrictMode -Version Latest

$script:AcceptSecretNames = 'ACCEPT_GH_USER', 'ACCEPT_GH_PASSWORD', 'ACCEPT_GH_TOTP_SECRET'

# The names of the secrets that are missing or empty; none means the login path can run.
function Get-MissingAcceptSecrets {
    @($script:AcceptSecretNames | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
}

# Masks each value (and the TOTP secret's spaced form) so a later echo shows ***.
function Protect-AcceptSecrets {
    foreach ($n in $script:AcceptSecretNames) {
        $v = [Environment]::GetEnvironmentVariable($n)
        if ([string]::IsNullOrWhiteSpace($v)) { continue }
        foreach ($m in @($v, ($v -replace '\s', ''))) { if ($m) { Write-Host "::add-mask::$m" } }
    }
}

function ConvertFrom-Base32([string]$text) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $bits = ''
    foreach ($c in ($text.ToUpperInvariant() -replace '[\s=-]', '').ToCharArray()) {
        $i = $alphabet.IndexOf($c)
        if ($i -lt 0) { throw 'the TOTP secret is not base32' }
        $bits += [Convert]::ToString($i, 2).PadLeft(5, '0')
    }
    $bytes = [byte[]]::new([math]::Floor($bits.Length / 8))
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($bits.Substring($i * 8, 8), 2) }
    , $bytes
}

# RFC 6238, HMAC-SHA1, 6 digits, 30 s: what GitHub's authenticator step expects.
function New-TotpCode([string]$Base32Secret, [DateTimeOffset]$At = [DateTimeOffset]::UtcNow) {
    $key = ConvertFrom-Base32 $Base32Secret
    $counter = [long][math]::Floor($At.ToUnixTimeSeconds() / 30)
    $msg = [BitConverter]::GetBytes($counter)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($msg) }
    $hmac = [Security.Cryptography.HMACSHA1]::new($key)
    try { $h = $hmac.ComputeHash($msg) } finally { $hmac.Dispose() }
    $o = $h[$h.Length - 1] -band 0x0f
    $bin = (([int]$h[$o] -band 0x7f) -shl 24) -bor ([int]$h[$o + 1] -shl 16) -bor ([int]$h[$o + 2] -shl 8) -bor [int]$h[$o + 3]
    ($bin % 1000000).ToString('000000')
}
