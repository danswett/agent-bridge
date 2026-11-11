#!/usr/bin/env bash
# download.sh against stand-ins for npx, sudo and apt-get: nothing here downloads, installs or
# touches the runner's own apt configuration. The real timeout does the timing, cut to a second.
# Run by ci.yml's light job (quality/catalog.yml, playwright-apt).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"

# npx prints install-deps --dry-run's command, as Playwright 1.56 does, and records its arguments.
cat > "$work/bin/npx" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$NPX_LOG"
printf '%s\n' "$NPX_OUT"
EOF
# sudo runs the command as it is: the stand-ins and the test's apt directory need no root.
cat > "$work/bin/sudo" <<'EOF'
#!/usr/bin/env bash
exec "$@"
EOF
cat > "$work/bin/getent" <<'EOF'
#!/usr/bin/env bash
exit "${APT_DNS_EXIT:-0}"
EOF
# apt-get records each call. A download is slow (it outlasts the limit) while the mirror list
# still names the Azure mirror and APT_SLOW is set, so only leaving that mirror ends it. A
# download that finishes prints APT_GETS, apt's "Get:" lines for what it fetched, if set.
cat > "$work/bin/apt-get" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$APT_LOG"
case "$*" in
  update)
    if [ -n "${APT_UPDATE_ERROR:-}" ] && { [ "${APT_AZURE_ONLY:-}" != true ] || grep -q azure "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; }; then
      printf '%s\n' "$APT_UPDATE_ERROR" >&2; exit "${APT_UPDATE_EXIT:-100}"
    fi
    if [ "${APT_UPDATE_SLOW:-}" = all ] || { [ -n "${APT_UPDATE_SLOW:-}" ] && grep -q azure "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; }; then
      case "$APT_UPDATE_SLOW" in ignore) trap '' TERM; sleep 30 ;; *) exec sleep 30 ;; esac
    fi
    exit "${APT_UPDATE_EXIT:-0}" ;;
  *--download-only*)
    if [ -n "${APT_SLOW:-}" ] && grep -q 'azure\.archive\.ubuntu\.com' "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; then
      exec sleep 30
    fi
    if [ -n "${APT_ERROR:-}" ] && [ "$(grep -c -- '--download-only' "$APT_LOG")" -le "${APT_FAIL_COUNT:-99}" ]; then
      if [ "${APT_AZURE_ONLY:-}" != true ] || grep -q 'azure\.archive\.ubuntu\.com' "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; then
        printf '%s\n' "$APT_ERROR" >&2; exit 100
      fi
    fi
    [ -z "${APT_GETS:-}" ] || printf '%s\n' "$APT_GETS"
    exit "${APT_DOWNLOAD_EXIT:-0}" ;;
esac
exit 99
EOF
cat > "$work/bin/sleep" <<'EOF'
#!/usr/bin/env bash
if [ "$1" = 30 ]; then exec /usr/bin/sleep "$@"; fi
echo "$*" >> "$SLEEP_LOG"
EOF
chmod +x "$work/bin/npx" "$work/bin/sudo" "$work/bin/apt-get" "$work/bin/sleep" "$work/bin/getent"
export PATH="$work/bin:$PATH" NPX_LOG="$work/npx.log" APT_LOG="$work/apt.log" SLEEP_LOG="$work/sleep.log"
export PLAYWRIGHT_APT_ETC="$work/apt" PLAYWRIGHT_APT_LIMIT=1s
unset GITHUB_JOB
export PLAYWRIGHT_APT_ATTEMPTS=1 PLAYWRIGHT_APT_RETRY_DELAY=0

azure=$'http://azure.archive.ubuntu.com/ubuntu/\tpriority:1'
canonical=$'https://archive.ubuntu.com/ubuntu/\tpriority:2'
download='install -y --download-only --no-install-recommends libasound2t64 libgtk-4-1 xvfb'
failures=0

# reset <mirror list lines...>: a fresh apt directory and logs, and the stand-ins' defaults.
reset() {
  rm -rf "$PLAYWRIGHT_APT_ETC"; mkdir -p "$PLAYWRIGHT_APT_ETC/sources.list.d"
  printf '%s\n' "$@" > "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"
  printf 'Types: deb\nURIs: mirror+file:/etc/apt/apt-mirrors.txt\n' > "$PLAYWRIGHT_APT_ETC/sources.list.d/ubuntu.sources"
  : > "$NPX_LOG"; : > "$APT_LOG"; : > "$SLEEP_LOG"
  export NPX_OUT='sudo -- sh -c "apt-get update&& apt-get install -y --no-install-recommends libasound2t64 libgtk-4-1 xvfb"'
  unset APT_SLOW APT_UPDATE_SLOW APT_UPDATE_EXIT APT_DOWNLOAD_EXIT APT_GETS PLAYWRIGHT_APT_FORCE_FALLBACK CHECK_BROWSERS
  unset APT_ERROR APT_FAIL_COUNT APT_AZURE_ONLY
  unset APT_DNS_EXIT APT_UPDATE_ERROR
}
# check <name> <expected exit> <expected apt-get calls, one per line> [expected mirror list lines...]
# download.sh is given CHECK_BROWSERS, chromium and webkit unless set.
check() {
  local name="$1" want="$2" calls="$3" code=0 browsers; shift 3
  read -ra browsers <<< "${CHECK_BROWSERS:-chromium webkit}"
  bash "$here/download.sh" "${browsers[@]}" > "$work/out" 2>&1 || code=$?
  local problems=()
  [ "$code" -eq "$want" ] || problems+=("exit $code, wanted $want")
  [ "$(cat "$APT_LOG")" = "$calls" ] || problems+=("apt-get was called with: $(cat "$APT_LOG")")
  if [ "$#" -gt 0 ] && [ "$(cat "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt")" != "$(printf '%s\n' "$@")" ]; then
    problems+=("the mirror list is now: $(cat "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt")")
  fi
  if [ "${#problems[@]}" -gt 0 ]; then
    echo "FAIL: $name (${problems[*]})"; cat "$work/out"; failures=$((failures + 1))
  else
    echo "ok: $name"
  fi
}

reset "$azure" "$canonical"
check "a download that finishes in time fetches the packages Playwright names, from the mirror list as it stands" \
  0 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"
grep -qxF 'playwright install-deps --dry-run chromium webkit' "$NPX_LOG" \
  || { echo "FAIL: Playwright was asked about $(cat "$NPX_LOG"), not the browsers given"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export APT_SLOW=1
check "a download still running at the limit leaves the Azure mirror and finishes from Canonical's HTTPS archive" \
  0 "$(printf 'update\n%s\n%s' "$download" "$download")" "$canonical"
grep -q '^::warning' "$work/out" || { echo "FAIL: leaving the Azure mirror wasn't announced"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export APT_SLOW=1 APT_DOWNLOAD_EXIT=100
check "a download from Canonical's archive that fails after leaving the Azure mirror fails the step" \
  100 "$(printf 'update\n%s\n%s' "$download" "$download")" "$canonical"

reset "$azure" "$canonical"; export APT_DOWNLOAD_EXIT=100
check "an apt failure that isn't the limit fails the step, and the mirror list is left alone" \
  100 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"

reset "$azure" "$canonical"; export APT_UPDATE_EXIT=100
check "a failed apt-get update fails the step before anything is downloaded" 100 "update" "$azure" "$canonical"

reset "$azure" "$canonical"; export APT_UPDATE_SLOW=1
check "an update that times out switches to Canonical before its next update and download" \
  0 "$(printf 'update\nupdate\n%s' "$download")" "$canonical"

reset "$azure" "$canonical"; export APT_UPDATE_SLOW=ignore
check "an update killed after ignoring TERM switches to Canonical as a timeout" \
  0 "$(printf 'update\nupdate\n%s' "$download")" "$canonical"
reset "$azure" "$canonical"; export APT_DNS_EXIT=1
check "a failed Azure name probe switches before the first update" \
  0 "$(printf 'update\n%s' "$download")" "$canonical"
reset "$azure" "$canonical"; export APT_UPDATE_SLOW=all
check "a Canonical update that also times out fails without trying Azure again" \
  124 "$(printf 'update\nupdate')" "$canonical"
reset "$azure" "$canonical"; export APT_DNS_EXIT=1 APT_UPDATE_EXIT=100
check "a failed Canonical update after an Azure DNS probe failure fails the step" \
  100 "update" "$canonical"
reset "$azure" "$canonical"; export APT_UPDATE_ERROR="E: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease Could not resolve 'azure.archive.ubuntu.com'" APT_AZURE_ONLY=true
check "an Azure DNS error during update switches immediately and refreshes Canonical before download" \
  0 "$(printf 'update\nupdate\n%s' "$download")" "$canonical"
reset "$azure" "$canonical"; export NPX_OUT='nothing to install'
check "Playwright naming no packages fails the step before apt runs" 1 "" "$azure" "$canonical"

reset "$azure"; export APT_SLOW=1
check "a slow download with no Canonical archive on the mirror list fails rather than keep waiting" \
  1 "$(printf 'update\n%s' "$download")" "$azure"

reset "$azure" "$canonical"; export APT_SLOW=1
echo 'deb http://azure.archive.ubuntu.com/ubuntu noble main' > "$PLAYWRIGHT_APT_ETC/sources.list"
check "a slow download fails when another apt source still names the Azure mirror after the edit" \
  1 "$(printf 'update\n%s' "$download")" "$canonical"

# Retries: a transient failure of apt-get update or of the download is tried again; a persistent one
# still fails the step after the last attempt.
reset "$azure" "$canonical"; export PLAYWRIGHT_APT_ATTEMPTS=3 APT_UPDATE_EXIT=100
check "an unclassified update failure is not retried" \
  100 "update" "$azure" "$canonical"

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_ATTEMPTS=3 APT_DOWNLOAD_EXIT=100
check "a download failure without a network diagnostic fails without retrying or changing mirrors" \
  100 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_ATTEMPTS=3 APT_SLOW=1
check "a slow download is not retried: it leaves the Azure mirror after one try" \
  0 "$(printf 'update\n%s\n%s' "$download" "$download")" "$canonical"
export PLAYWRIGHT_APT_ATTEMPTS=1

# Network downloads use the default three attempts and 5/20 second backoff; sleep is recorded,
# not waited out. Errors must come from this download, not a previous attempt.
reset "$azure" "$canonical"; unset PLAYWRIGHT_APT_ATTEMPTS PLAYWRIGHT_APT_RETRY_DELAY
export APT_ERROR='E: Failed to fetch http://azure.archive.ubuntu.com/ubuntu/pool/pkg.deb Connection failed' APT_FAIL_COUNT=1
check "a connection failure then success retries without switching mirrors" \
  0 "$(printf 'update\n%s\n%s' "$download" "$download")" "$azure" "$canonical"
[ "$(cat "$SLEEP_LOG")" = 5 ] || { echo "FAIL: first download backoff was not 5 seconds"; failures=$((failures + 1)); }

reset "$azure" "$canonical"
export APT_ERROR="E: Failed to fetch http://azure.archive.ubuntu.com/ubuntu/pool/pkg.deb Temporary failure resolving 'azure.archive.ubuntu.com'" APT_AZURE_ONLY=true
check "an Azure DNS failure leaves Azure immediately and downloads once from Canonical" \
  0 "$(printf 'update\n%s\n%s' "$download" "$download")" "$canonical"
[ ! -s "$SLEEP_LOG" ] || { echo "FAIL: Azure DNS fallback waited for a retry"; failures=$((failures + 1)); }
grep -q '^::warning.*Azure DNS.*fetching the rest from https://archive\.ubuntu\.com' "$work/out" \
  || { echo "FAIL: immediate Azure DNS fallback was not announced"; failures=$((failures + 1)); }

reset "$azure" "$canonical"
export APT_ERROR='E: Failed to fetch http://azure.archive.ubuntu.com/ubuntu/pool/pkg.deb 503 Service Unavailable' APT_AZURE_ONLY=true
check "three server failures leave Azure and download from Canonical" \
  0 "$(printf 'update\n%s\n%s\n%s\n%s' "$download" "$download" "$download" "$download")" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'5\n20' ] || { echo "FAIL: download backoff was not 5 then 20 seconds"; failures=$((failures + 1)); }
grep -q '^::warning.*fetching the rest from https://archive\.ubuntu\.com' "$work/out" \
  || { echo "FAIL: DNS fallback was not announced"; failures=$((failures + 1)); }
! grep -q 'force-apt-fallback' "$work/out" \
  || { echo "FAIL: DNS fallback ran the forced proof"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export APT_DNS_EXIT=1 APT_ERROR='E: Failed to fetch https://archive.ubuntu.com/ubuntu/pool/pkg.deb Connection timed out'
check "Canonical connection failures exhaust three attempts then fail loudly" \
  100 "$(printf 'update\n%s\n%s\n%s' "$download" "$download" "$download")" "$canonical"
grep -q '^::error::' "$work/out" \
  || { echo "FAIL: exhausted downloads did not emit an error"; failures=$((failures + 1)); }
for attempt in 1 2 3; do
  grep -q "attempt $attempt of 3" "$work/out" \
    || { echo "FAIL: missing download attempt $attempt log"; failures=$((failures + 1)); }
done
reset "$azure" "$canonical"; export APT_UPDATE_ERROR="W: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease Temporary failure resolving 'azure.archive.ubuntu.com'" APT_UPDATE_EXIT=0
check "an update reporting Azure DNS warnings despite exit zero still switches immediately" \
  100 "$(printf 'update\nupdate')" "$canonical"
reset "$azure" "$canonical"; export APT_UPDATE_ERROR='W: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease 503 Service Unavailable'
check "mirror-file server errors retry on Azure and Canonical even without a hostname" \
  100 "$(printf 'update\nupdate\nupdate\nupdate\nupdate\nupdate')" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'5\n20\n5\n20' ] \
  || { echo "FAIL: both update phases did not use 5/20 second backoff"; failures=$((failures + 1)); }
reset "$canonical"; export APT_UPDATE_ERROR='W: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease 503 Service Unavailable'
check "a new invocation on a Canonical-only list retries update failures three times" \
  100 "$(printf 'update\nupdate\nupdate')" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'5\n20' ] \
  || { echo "FAIL: Canonical-only update did not use 5/20 second backoff"; failures=$((failures + 1)); }
reset "$canonical"; export APT_ERROR='E: Failed to fetch https://archive.ubuntu.com/ubuntu/pool/pkg.deb Connection failed' APT_FAIL_COUNT=2
check "a new invocation on a Canonical-only list recovers on its third download attempt" \
  0 "$(printf 'update\n%s\n%s\n%s' "$download" "$download" "$download")" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'5\n20' ] \
  || { echo "FAIL: Canonical-only download did not use 5/20 second backoff"; failures=$((failures + 1)); }

for error in '404 Not Found' 'Hash Sum mismatch' 'Unable to locate package missing'; do
  reset "$azure" "$canonical"; export APT_ERROR="E: $error"
  check "a missing or corrupt package ($error) fails without retry or fallback" \
    100 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"
done
reset "$azure" "$canonical"
export APT_ERROR=$'E: Temporary failure resolving azure.archive.ubuntu.com\nE: Hash Sum mismatch'
check "a corrupt package alongside a DNS failure still refuses retry and fallback" \
  100 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"
export PLAYWRIGHT_APT_ATTEMPTS=1 PLAYWRIGHT_APT_RETRY_DELAY=0

# force-apt-fallback (ci.yml's pwa-ui on a run by hand): APT_SLOW stays set, so a download tried
# before leaving the Azure mirror would time out and show up as a second call.
from_canonical=$'Get:1 https://archive.ubuntu.com/ubuntu noble/main amd64 libgtk-4-1 amd64 4.14.2 [3170 kB]\nGet:2 https://archive.ubuntu.com/ubuntu noble/main amd64 xvfb amd64 2:21.1.12 [877 kB]'
from_azure='Get:3 http://azure.archive.ubuntu.com/ubuntu noble/main amd64 libasound2t64 amd64 1.2.11 [390 kB]'

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=true APT_SLOW=1 APT_GETS="$from_canonical"
check "a forced fallback leaves the Azure mirror before downloading anything, with no five-minute wait" \
  0 "$(printf 'update\n%s' "$download")" "$canonical"
grep -q '^::notice.*force-apt-fallback: taking the Azure mirror off.*nothing timed out' "$work/out" \
  || { echo "FAIL: a forced fallback didn't say it was forced"; failures=$((failures + 1)); }
grep -q '^::notice.*force-apt-fallback: 2 packages downloaded from https://archive\.ubuntu\.com' "$work/out" \
  || { echo "FAIL: a forced fallback didn't count the packages from Canonical's archive"; failures=$((failures + 1)); }
! grep -q '^::warning' "$work/out" \
  || { echo "FAIL: a forced fallback that proved itself warned: $(grep '^::warning' "$work/out")"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=true APT_GETS="$from_canonical"$'\n'"$from_azure"
check "a forced fallback fails if apt still fetches a package from the Azure mirror after leaving it" \
  1 "$(printf 'update\n%s' "$download")" "$canonical"
grep -q '^::error::apt still fetched packages from azure\.archive\.ubuntu\.com' "$work/out" \
  || { echo "FAIL: a forced fallback that still fetched from Azure didn't say so"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=true
check "a forced WebKit download that fetched nothing from Canonical's archive fails as no proof" \
  1 "$(printf 'update\n%s' "$download")" "$canonical"
grep -q '^::error.*force-apt-fallback: nothing was downloaded.*this WebKit leg is no proof' "$work/out" \
  || { echo "FAIL: a forced WebKit leg with nothing from Canonical didn't say it is no proof"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=true CHECK_BROWSERS=chromium
check "a forced Chromium download that fetched nothing from Canonical's archive only warns" \
  0 "$(printf 'update\n%s' "$download")" "$canonical"
grep -q '^::warning.*force-apt-fallback: nothing was downloaded.*proves nothing' "$work/out" \
  || { echo "FAIL: a forced Chromium leg with nothing from Canonical didn't warn"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=false
check "force-apt-fallback false, the action's default, downloads from the mirror list as it stands" \
  0 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"

# The proof's checks are the forced run's alone: a real timeout's fallback is as before.
reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=false APT_SLOW=1 APT_GETS="$from_azure"
check "a fallback after a real timeout neither counts Canonical's packages nor checks for Azure's" \
  0 "$(printf 'update\n%s\n%s' "$download" "$download")" "$canonical"
! grep -q 'force-apt-fallback' "$work/out" \
  || { echo "FAIL: a fallback after a real timeout ran the forced run's checks"; failures=$((failures + 1)); }

if [ "$failures" -ne 0 ]; then
  echo "::error::$failures of download.sh's checks failed"; exit 1
fi
