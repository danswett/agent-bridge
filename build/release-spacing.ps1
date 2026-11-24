# The owner's release spacing (docs/app-windows.md, "Spacing"): a release goes out only once the
# newest published release is 15 minutes old. Dot-sourced by publish-prerelease.ps1 and
# publish-pending.ps1, which read the releases, published_at included, from GitHub's API.
$ReleaseSpacing = [TimeSpan]::FromMinutes(15)

function Get-NewestRelease([object[]]$Releases, [DateTimeOffset]$Now) {
    # The release published last, of any version or kind (a draft isn't published), and its age;
    # nothing when none is out. A publication time it can't read throws: the caller holds.
    $newest = $null
    foreach ($r in @($Releases | Where-Object { $_ -and -not $_.draft })) {
        $at = $r.published_at
        $parsed = [DateTimeOffset]::MinValue
        if ($at -is [datetime]) { $parsed = [DateTimeOffset]$at }
        elseif ($at -isnot [string] -or -not [DateTimeOffset]::TryParse($at, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
            throw "couldn't read $($r.tag_name)'s publication time ('$at')"
        }
        if (-not $newest -or $parsed -gt $newest.At) { $newest = [pscustomobject]@{ Tag = $r.tag_name; At = $parsed; Age = $Now - $parsed } }
    }
    $newest
}
