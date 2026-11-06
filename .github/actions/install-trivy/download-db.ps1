# The vulnerability database for install-trivy's vulnerability-db input (quality/README.md §3.3,
# container-scan), from the first repository that serves it. Trivy's own --db-repository list,
# which already names both of these, moves on to the next repository only for a temporary error
# (408, 429 or 5xx) or BLOB_UNKNOWN (shouldTryOtherRepo in pkg/oci/artifact.go, v0.76.1). A bare
# 404 for a layer is neither, so when mirror.gcr.io returned one on main (run 42031392354) Trivy
# never asked ghcr.io. Here any failure moves on, one repository per Trivy run. None left is an
# error, and the scans run with --skip-db-update, so without a database they fail, never pass
# unscanned. --download-db-only scans nothing, so no exit here is a vulnerability finding.
function Save-TrivyDb {
    param(
        [string]$Trivy = 'trivy',
        [string]$CacheDir = $env:TRIVY_CACHE_DIR,
        [string[]]$Repository = @('mirror.gcr.io/aquasec/trivy-db:2', 'ghcr.io/aquasecurity/trivy-db:2')
    )
    if (-not $CacheDir) { throw 'Save-TrivyDb needs a cache directory: -CacheDir or TRIVY_CACHE_DIR' }
    # A failed repository is the next one's cue, not a reason to stop.
    $PSNativeCommandUseErrorActionPreference = $false
    $db = Join-Path $CacheDir 'db' 'trivy.db'
    foreach ($repo in $Repository) {
        & $Trivy image --download-db-only --no-progress --cache-dir $CacheDir --db-repository $repo
        $exit = $LASTEXITCODE
        if ($exit -eq 0 -and (Test-Path -LiteralPath $db -PathType Leaf)) {
            Write-Host "Vulnerability database from $repo"
            return
        }
        Write-Warning "No vulnerability database from $repo (exit $exit)"
    }
    throw "No vulnerability database from any of $($Repository -join ', '), so no scan can run"
}
