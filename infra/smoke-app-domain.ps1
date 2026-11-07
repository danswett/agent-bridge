<#
.SYNOPSIS
  Checks the PWA's public address after (or before) the move to app.slipgates.com (#393).
.DESCRIPTION
  Read-only: GETs and one CORS preflight. Run it against the new origin after the cutover and
  against the old one to confirm the redirect. docs/app-domain.md has the runbook.
.EXAMPLE
  pwsh infra/smoke-app-domain.ps1
  pwsh infra/smoke-app-domain.ps1 -Origin https://danswett.github.io/agent-bridge/app
#>
[CmdletBinding()]
param(
  [string] $Origin = 'https://app.slipgates.com',
  [string] $Relay = 'https://relay.bad-duck.com',
  # Only the relay's CORS answer for -Origin's site: the check that says the relay is ready, before the address exists.
  [switch] $RelayOnly
)
$ErrorActionPreference = 'Stop'
$Origin = $Origin.TrimEnd('/')
$siteOrigin = ([Uri]$Origin).GetLeftPart([UriPartial]::Authority)
$failures = 0

function Check([string] $name, [scriptblock] $test) {
  try {
    $detail = & $test
    Write-Host "ok    $name$(if ($detail) { " ($detail)" })"
  } catch {
    $script:failures++
    Write-Host "FAIL  $name - $($_.Exception.Message)"
  }
}

function Get-Page([string] $url) {
  Invoke-WebRequest -Uri $url -UseBasicParsing -MaximumRedirection 0 -SkipHttpErrorCheck
}

Check 'the relay accepts this origin (CORS preflight)' {
  $r = Invoke-WebRequest -Uri "$Relay/v1/push/key" -Method Options -UseBasicParsing -SkipHttpErrorCheck -Headers @{
    Origin = $siteOrigin; 'Access-Control-Request-Method' = 'GET' }
  $allowed = "$($r.Headers['Access-Control-Allow-Origin'])"
  if ($allowed -ne $siteOrigin) { throw "Access-Control-Allow-Origin is '$allowed'" }
}

if (-not $RelayOnly) {
Check 'the app loads at the root' {
  $r = Get-Page "$Origin/"
  if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
  if ($r.Content -notmatch 'Content-Security-Policy') { throw 'no Content-Security-Policy in the page' }
  if ($r.Content -notmatch 'manifest') { throw 'no manifest link' }
}

Check 'the manifest is JSON, scoped to the root' {
  $r = Get-Page "$Origin/manifest.webmanifest"
  if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
  if ("$($r.Headers['Content-Type'])" -notmatch 'json') { throw "content type $($r.Headers['Content-Type'])" }
  $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { $r.Content }
  $m = $text | ConvertFrom-Json
  if ($m.scope -ne './' -or $m.start_url -notmatch '^\./') { throw "scope $($m.scope), start_url $($m.start_url)" }
}

Check 'the service worker is JavaScript' {
  $r = Get-Page "$Origin/sw.js"
  if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
  if ("$($r.Headers['Content-Type'])" -notmatch 'javascript') { throw "content type $($r.Headers['Content-Type'])" }
}

if ($siteOrigin -eq 'https://app.slipgates.com') {
  Check 'the spikes stay under /s2/ and /s4/' {
    foreach ($p in 's2', 's4') {
      $r = Get-Page "$siteOrigin/$p/"
      if ($r.StatusCode -ne 200) { throw "/$p/ status $($r.StatusCode)" }
    }
  }
  Check '/app/ sends on to the root, keeping the deep link' {
    $r = Get-Page "$siteOrigin/app/"
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    $js = Get-Page "$siteOrigin/app/redirect.js"
    if ($js.Content -notmatch 'location\.replace') { throw 'no redirect script' }
  }
}

}

if ($failures) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host 'All checks passed.'
