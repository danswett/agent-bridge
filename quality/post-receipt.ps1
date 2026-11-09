#requires -Version 7
<#
.SYNOPSIS
  Post independent-review receipts as the agent-bridge-receipts GitHub App.

.DESCRIPTION
  A receipt is a commit status, `independent-review` or `independent-review-2`, bound to the exact
  head the reviewer read (quality/README.md, "Who posts receipts"). This mints a one-hour
  installation token for the App, posts every receipt named by -Receipt in one batch, and revokes
  the token. The key stays where it lives (%USERPROFILE%\.agent-bridge-receipts\ on the machine the
  owner chose); nothing here copies, prints or logs the key or a token.

  The App can write commit statuses and read pull requests and metadata, nothing else, on
  danswett/agent-bridge only. It cannot merge, and it cannot post merge-rule or quality-gate:
  the main ruleset pins those to their own Apps.

  Refuses unless the head is 40 hex characters, the pull request is open, its current head is
  that exact head, and the comment link belongs to that pull request. A receipt for an older
  head is never posted. The caller says who reviewed (-Receipt context=model); this script does
  not judge a review, it only records it, as the danswett login did before.

  danswett stays a poster in quality/merge-rule.yml, so if the key, the App or this script is
  unavailable, post receipts the old way.

.EXAMPLE
  pwsh -NoProfile -File quality/post-receipt.ps1 -Pr 1444 -Head <40 hex> `
    -CommentUrl https://github.com/danswett/agent-bridge/pull/1444#issuecomment-6733141169 `
    -Receipt independent-review=claude-sonnet-5.5, independent-review-2=gemini-3.8-flash

  -DryRun checks the arguments and the key, signs a JWT, prints what would be posted, and stops
  before any network call.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [Parameter(Mandatory)][string]$Head,
    [Parameter(Mandatory)][string]$CommentUrl,
    # One or more context=model pairs, e.g. independent-review=claude-sonnet-5.5
    [Parameter(Mandatory)][string[]]$Receipt,
    [string]$Summary = 'read in full, no findings',
    [string]$Repo = 'danswett/agent-bridge',
    [string]$AppId = '5676335',
    [string]$KeyPath = $env:AGENT_BRIDGE_RECEIPTS_KEY_PATH,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$api = 'https://api.github.com'
$contexts = 'independent-review', 'independent-review-2'

if ($Head -cnotmatch '^[0-9a-f]{40}$') { throw 'Head must be the full 40-character lowercase SHA.' }
if ($Repo -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$') { throw 'Repo must be owner/name.' }
if ($Summary -notmatch '^[A-Za-z0-9 ,.;:()+/_-]{1,60}$') { throw 'Summary is 1 to 60 plain characters.' }
$commentPattern = '^https://github\.com/' + [regex]::Escape($Repo) + '/pull/' + $Pr + '#issuecomment-\d+$'
if ($CommentUrl -notmatch $commentPattern) { throw "CommentUrl must be a comment on $Repo pull request $Pr." }

$posts = foreach ($r in ($Receipt | ForEach-Object { $_ -split ',' })) {
    if ($r -notmatch '^(?<ctx>[a-z0-9-]+)=(?<model>[A-Za-z0-9._-]{1,40})$' -or $Matches.ctx -notin $contexts) {
        throw "Receipt '$r' must be independent-review=<model> or independent-review-2=<model>."
    }
    [pscustomobject]@{
        context = $Matches.ctx
        state = 'success'
        description = "Reviewed by $($Matches.model); $Summary"
        target_url = $CommentUrl
    }
}
if (@($posts.context | Select-Object -Unique).Count -ne @($posts).Count) { throw 'Name each receipt context once.' }
foreach ($p in $posts) { if ($p.description.Length -gt 140) { throw 'A receipt description is at most 140 characters.' } }

if (-not $KeyPath) {
    $keyDir = Join-Path $env:USERPROFILE '.agent-bridge-receipts'
    $KeyPath = (Get-ChildItem $keyDir -File -Filter *.pem -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $KeyPath -or -not (Test-Path $KeyPath)) {
    throw 'No App key found. Set AGENT_BRIDGE_RECEIPTS_KEY_PATH or put the .pem in %USERPROFILE%\.agent-bridge-receipts\ and run on that machine. Post receipts as danswett meanwhile.'
}

function ConvertTo-B64Url([byte[]]$Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-AppJwt {
    $rsa = [Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem((Get-Content $KeyPath -Raw))
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $header = ConvertTo-B64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"RS256","typ":"JWT"}'))
    $body = ConvertTo-B64Url ([Text.Encoding]::UTF8.GetBytes((@{ iat = $now - 60; exp = $now + 540; iss = $AppId } | ConvertTo-Json -Compress)))
    $sig = $rsa.SignData([Text.Encoding]::ASCII.GetBytes("$header.$body"), [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    "$header.$body." + (ConvertTo-B64Url $sig)
}

function Invoke-Gh([string]$Method, [string]$Path, [string]$Token, $Body) {
    $p = @{
        Method = $Method; Uri = "$api$Path"; SkipHttpErrorCheck = $true; StatusCodeVariable = 'sc'
        Headers = @{ Authorization = "Bearer $Token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'agent-bridge-receipts' }
    }
    if ($Body) { $p.Body = ($Body | ConvertTo-Json -Compress -Depth 4); $p.ContentType = 'application/json' }
    $data = Invoke-RestMethod @p
    [pscustomobject]@{ Status = $sc; Data = $data }
}

$jwt = New-AppJwt
if ($DryRun) {
    "jwt ok: $($jwt.Split('.').Count) parts. Would post on $Repo @ $($Head.Substring(0, 8)) (PR $Pr):"
    $posts | ForEach-Object { "  $($_.context) = $($_.state): $($_.description) -> $($_.target_url)" }
    exit 0
}

$inst = Invoke-Gh GET "/repos/$Repo/installation" $jwt
if ($inst.Status -ne 200) { throw "installation lookup failed: HTTP $($inst.Status)" }
$name = $Repo.Split('/')[1]
$tok = Invoke-Gh POST "/app/installations/$($inst.Data.id)/access_tokens" $jwt @{
    repositories = @($name); permissions = @{ statuses = 'write'; pull_requests = 'read'; metadata = 'read' }
}
if ($tok.Status -ne 201) { throw "token mint failed: HTTP $($tok.Status)" }
$token = $tok.Data.token

$failed = $false
try {
    $pull = Invoke-Gh GET "/repos/$Repo/pulls/$Pr" $token
    if ($pull.Status -ne 200) { throw "pull request read failed: HTTP $($pull.Status)" }
    if ($pull.Data.state -ne 'open') { throw "PR $Pr is $($pull.Data.state), not open; no receipt posted." }
    if ($pull.Data.head.sha -ne $Head) {
        throw "PR $Pr head is now $($pull.Data.head.sha.Substring(0, 8)), not $($Head.Substring(0, 8)); no receipt posted for a stale head."
    }
    foreach ($p in $posts) {
        $res = Invoke-Gh POST "/repos/$Repo/statuses/$Head" $token @{
            state = $p.state; context = $p.context; description = $p.description; target_url = $p.target_url
        }
        if ($res.Status -ne 201) { $failed = $true; "$($p.context): FAILED, HTTP $($res.Status)"; continue }
        "$($p.context): posted by $($res.Data.creator.login) on $($Head.Substring(0, 8)) (PR $Pr)"
    }
}
finally {
    $null = Invoke-Gh DELETE '/installation/token' $token
}
exit [int]$failed
