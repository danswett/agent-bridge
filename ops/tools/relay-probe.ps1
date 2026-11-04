#Requires -Version 7.2
<#
.SYNOPSIS
  Read-only, outside-in check of a relay: health, readiness, version, push key, CORS,
  TLS certificate and (for the custom domain) the DNS records its certificate renewal needs.

.DESCRIPTION
  What an operator runs first in an incident, after a deploy, or before decommissioning a
  stamp. It sends only anonymous GETs and one CORS preflight, the same requests any browser
  makes, so it is safe against production. It never signs in and prints no address: DNS
  answers are reported as "CNAME to <name>" or "no CNAME", never as IPs (the repository is
  public, and AGENTS.md keeps network details out of it).

  The checks mirror the availability alerts in ops/alerts.md (A-AVAIL, A-CERT), so a red
  alert can be confirmed or ruled out by hand in seconds.

.PARAMETER Target
  production (default), staging, old (the retired free-trial stamp), or all.

.PARAMETER Url
  Probe this base URL instead of a named target.

.PARAMETER ExpectVersion
  Fail unless /healthz reports this commit (full SHA or a prefix of at least 7).

.PARAMETER Json
  Write the results as JSON (for scripts and incident notes) instead of a table.

.EXAMPLE
  pwsh ops/tools/relay-probe.ps1
.EXAMPLE
  pwsh ops/tools/relay-probe.ps1 -Target all -Json
.EXAMPLE
  pwsh ops/tools/relay-probe.ps1 -Target staging -ExpectVersion 7c17d4a

.OUTPUTS
  Exit code 0: every check passed. 1: a check failed. 2: warnings only.
#>
[CmdletBinding(DefaultParameterSetName = 'Named')]
param(
    [Parameter(ParameterSetName = 'Named')]
    [ValidateSet('production', 'staging', 'old', 'all')]
    [string] $Target = 'production',

    [Parameter(ParameterSetName = 'Url', Mandatory)]
    [uri] $Url,

    [ValidatePattern('^[0-9a-f]{7,40}$')]
    [string] $ExpectVersion,

    [switch] $Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Kept in step with docs/relay-deploy.md. The custom domain's CNAME target and the PWA origin
# are what the certificate-renewal and CORS checks compare against.
$targets = [ordered]@{
    production = @{
        Url = 'https://relay.bad-duck.com'
        CnameTarget = 'agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io'
        Timeout = 15
    }
    staging = @{
        Url = 'https://agent-bridge-relay-staging.icyflower-4f71155d.westus2.azurecontainerapps.io'
        # Scales to zero between deploys, so the first request may wait for a cold start.
        Timeout = 90
    }
    old = @{
        Url = 'https://agent-bridge-relay.agreeablepebble-329880d4.westus2.azurecontainerapps.io'
        Timeout = 30
    }
}
$pwaOrigin = 'https://danswett.github.io'
$certWarnDays = 21    # matches A-CERT in ops/alerts.md
$latencyWarnMs = 1500

$selected = if ($PSCmdlet.ParameterSetName -eq 'Url') {
    [ordered]@{ custom = @{ Url = $Url.AbsoluteUri.TrimEnd('/'); Timeout = 30 } }
}
elseif ($Target -eq 'all') { $targets }
else { [ordered]@{ $Target = $targets[$Target] } }

$pacific = [TimeZoneInfo]::FindSystemTimeZoneById($(if ($IsWindows) { 'Pacific Standard Time' } else { 'America/Los_Angeles' }))
function Format-Pacific([DateTimeOffset] $t) {
    [TimeZoneInfo]::ConvertTime($t, $pacific).ToString('yyyy-MM-dd HH:mm') + ' PT'
}

$results = [Collections.Generic.List[object]]::new()
function Add-Result([string] $name, [string] $check, [ValidateSet('PASS', 'WARN', 'FAIL')] [string] $status, [string] $detail) {
    $results.Add([pscustomobject]@{ Target = $name; Check = $check; Status = $status; Detail = $detail })
}

function Invoke-Probe([string] $uri, [int] $timeout, [hashtable] $headers = @{}, [string] $method = 'GET') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Invoke-WebRequest -Uri $uri -Method $method -Headers $headers -TimeoutSec $timeout `
            -SkipHttpErrorCheck -MaximumRedirection 0 -UserAgent 'agent-bridge-ops-probe'
        $content = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
        return @{ Status = [int]$r.StatusCode; Body = $content; Headers = $r.Headers; Ms = $sw.ElapsedMilliseconds; Error = $null }
    }
    catch {
        return @{ Status = 0; Body = ''; Headers = @{}; Ms = $sw.ElapsedMilliseconds; Error = $_.Exception.Message }
    }
}

function Get-Header($headers, [string] $name) {
    foreach ($k in $headers.Keys) { if ($k -ieq $name) { return [string]($headers[$k] | Select-Object -First 1) } }
    return $null
}

function Test-Certificate([string] $name, [uri] $base) {
    $tcp = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $tcp.ConnectAsync($base.Host, 443).Wait([TimeSpan]::FromSeconds(10))) { throw 'connect timed out' }
        # Accept whatever is presented and report the policy errors, so an expired or
        # mismatched certificate is described rather than ending the probe.
        $script:sslErrors = [Net.Security.SslPolicyErrors]::None
        $ssl = [Net.Security.SslStream]::new($tcp.GetStream(), $false, { param($s, $c, $ch, $e) $script:sslErrors = $e; $true })
        try {
            $ssl.AuthenticateAsClient($base.Host)
            $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate)
            $days = [math]::Floor(($cert.NotAfter.ToUniversalTime() - [DateTime]::UtcNow).TotalDays)
            $issuer = ($cert.Issuer -split ',')[0] -replace '^CN=', ''
            $detail = "expires $(Format-Pacific ([DateTimeOffset]$cert.NotAfter)) ($days days), issuer $issuer, $($ssl.SslProtocol)"
            if ($script:sslErrors -ne [Net.Security.SslPolicyErrors]::None) { Add-Result $name 'tls certificate' 'FAIL' "$($script:sslErrors); $detail" }
            elseif ($days -lt $certWarnDays) { Add-Result $name 'tls certificate' 'WARN' "renewal overdue? $detail" }
            else { Add-Result $name 'tls certificate' 'PASS' $detail }
        }
        finally { $ssl.Dispose() }
    }
    catch { Add-Result $name 'tls certificate' 'FAIL' $_.Exception.Message }
    finally { $tcp.Dispose() }
}

# DNS over HTTPS, so the answer is the public one (not a corporate or home resolver's) and the
# script works on Linux too. Only names are reported.
function Resolve-Doh([string] $qname, [string] $type) {
    $r = Invoke-RestMethod -Uri "https://cloudflare-dns.com/dns-query?name=$qname&type=$type" -Headers @{ Accept = 'application/dns-json' } -TimeoutSec 10
    if ($r.PSObject.Properties['Answer']) { @($r.Answer) } else { @() }
}

function Test-Dns([string] $name, [uri] $base, [string] $expectedTarget) {
    try {
        $cname = Resolve-Doh $base.Host 'CNAME' | Where-Object type -EQ 5 | Select-Object -First 1
        if (-not $cname) {
            # A proxied (orange-cloud) Cloudflare record answers with Cloudflare's addresses and no
            # CNAME: the managed certificate can no longer renew, and the proxy cuts WebSockets.
            Add-Result $name 'dns cname' 'FAIL' "no CNAME for $($base.Host) (proxied, or record removed); certificate renewal will fail"
        }
        elseif ($cname.data.TrimEnd('.') -ine $expectedTarget) {
            Add-Result $name 'dns cname' 'FAIL' "CNAME to $($cname.data.TrimEnd('.')), expected $expectedTarget"
        }
        else { Add-Result $name 'dns cname' 'PASS' 'CNAME to the production app (DNS only)' }

        $txt = Resolve-Doh "asuid.$($base.Host)" 'TXT' | Where-Object type -EQ 16
        if ($txt) { Add-Result $name 'dns asuid txt' 'PASS' 'present (domain verification for renewal)' }
        else { Add-Result $name 'dns asuid txt' 'FAIL' "asuid.$($base.Host) TXT missing; certificate renewal will fail" }
    }
    catch { Add-Result $name 'dns' 'WARN' "DNS-over-HTTPS lookup failed: $($_.Exception.Message)" }
}

foreach ($entry in $selected.GetEnumerator()) {
    $name = $entry.Key
    $t = $entry.Value
    $base = [uri]$t.Url

    $h = Invoke-Probe "$($t.Url)/healthz" $t.Timeout
    if ($h.Status -ne 200) {
        Add-Result $name 'healthz' 'FAIL' $(if ($h.Error) { $h.Error } else { "HTTP $($h.Status) in $($h.Ms) ms" })
    }
    else {
        $body = $h.Body | ConvertFrom-Json
        $version = if ($body.PSObject.Properties['version']) { [string]$body.version } else { '' }
        $latency = if ($h.Ms -gt $latencyWarnMs -and $name -ne 'staging') { 'WARN' } else { 'PASS' }
        if ($body.ok -ne $true) { Add-Result $name 'healthz' 'FAIL' "ok=$($body.ok)" }
        else { Add-Result $name 'healthz' $latency "ok in $($h.Ms) ms; $($body.online) online" }

        $short = if ($version.Length -ge 7) { $version.Substring(0, 7) } else { $version }
        if ($ExpectVersion -and -not $version.StartsWith($ExpectVersion, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Result $name 'version' 'FAIL' "serving $(if ($short) { $short } else { 'no version' }), expected $ExpectVersion"
        }
        else { Add-Result $name 'version' 'PASS' $(if ($short) { "serving $short" } else { 'no version reported (spike build?)' }) }
    }

    $r = Invoke-Probe "$($t.Url)/readyz" $t.Timeout
    switch ($r.Status) {
        200 { Add-Result $name 'readyz' 'PASS' "ready in $($r.Ms) ms" }
        503 { Add-Result $name 'readyz' 'FAIL' 'draining (503): a revision is stopping; if it persists, no replica is serving' }
        404 { Add-Result $name 'readyz' 'WARN' 'no /readyz (spike build)' }
        default { Add-Result $name 'readyz' 'FAIL' $(if ($r.Error) { $r.Error } else { "HTTP $($r.Status)" }) }
    }

    $k = Invoke-Probe "$($t.Url)/v1/push/key" $t.Timeout
    $keyLength = if ($k.Status -eq 200) { [string](($k.Body | ConvertFrom-Json).key) | ForEach-Object Length } else { 0 }
    if ($keyLength -eq 87) { Add-Result $name 'push key' 'PASS' 'VAPID public key served (87 chars)' }
    else { Add-Result $name 'push key' 'FAIL' "HTTP $($k.Status), key length $keyLength; push subscriptions can't be made" }

    $c = Invoke-Probe "$($t.Url)/v1/auth/device" $t.Timeout @{
        Origin = $pwaOrigin; 'Access-Control-Request-Method' = 'POST'; 'Access-Control-Request-Headers' = 'content-type'
    } 'OPTIONS'
    $allow = Get-Header $c.Headers 'Access-Control-Allow-Origin'
    if ($c.Status -in 200, 204 -and $allow -eq $pwaOrigin) { Add-Result $name 'cors (pwa origin)' 'PASS' "preflight allows $pwaOrigin" }
    else { Add-Result $name 'cors (pwa origin)' 'FAIL' "preflight HTTP $($c.Status), allow-origin '$allow': the PWA can't sign in" }

    Test-Certificate $name $base
    if ($t.ContainsKey('CnameTarget')) { Test-Dns $name $base $t.CnameTarget }
}

$at = Format-Pacific ([DateTimeOffset]::UtcNow)
if ($Json) { [pscustomobject]@{ at = $at; results = $results } | ConvertTo-Json -Depth 4 }
else {
    "Relay probe at $at"
    $results | Format-Table -AutoSize -Wrap | Out-String -Width 200
}

if ($results.Status -contains 'FAIL') { exit 1 }
if ($results.Status -contains 'WARN') { exit 2 }
exit 0
