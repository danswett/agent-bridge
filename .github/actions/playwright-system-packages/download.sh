#!/usr/bin/env bash
# Downloads and offline-installs the system packages `npx playwright install --with-deps <browsers>`
# would install, so the subsequent browser install needs no apt update (#799, #689). Run it where
# node_modules holds that Playwright; its arguments are the browsers.
#
# Why: on main run 41927187905, pwa-ui's webkit 3/3 fetched its 181 packages (114 MB) from
# azure.archive.ubuntu.com at about 100 kB/s on two runners in turn and hit the job's limit inside
# the install, before any test ran; on main run 41952998347 pwa-e2e did the same with Chromium's
# and WebKit's packages. The runner's mirror list (/etc/apt/apt-mirrors.txt, from
# actions/runner-images) puts that mirror first and Canonical's HTTPS archive after it, but apt
# moves down the list only when a fetch fails, never when it is slow. Network attempts get
# 60 seconds; an Azure timeout (124) or DNS failure immediately leaves Azure. The
# Azure mirror is taken off the list and apt fetches the rest from https://archive.ubuntu.com,
# keeping what it has. Nothing else changes: no source, key or trust setting is added, apt checks
# every package against the signed index either way. Azure DNS failures leave immediately;
# connection/5xx errors get three attempts (5 s, then 20 s apart), including on Canonical.
# A non-forced cache hit uses 20-second attempts within a two-minute update budget; only
# classified network exhaustion may warn and continue to the mandatory offline install. If
# the mirror list isn't the Azure-then-Canonical one this expects, or Azure is still named after the
# edit, it fails rather than go on waiting on the same mirror.
#
# PLAYWRIGHT_APT_FORCE_FALLBACK=true (the action's force-apt-fallback, which ci.yml sets only in
# pwa-ui on a run by hand that asks for it; quality/README.md §4.7) skips the first download and takes
# this fallback before any package is downloaded, to prove it once. Its log says it was forced, not
# timed out, and how many packages came from https://archive.ubuntu.com. The WebKit legs are the
# proof, so a webkit download with none fails; any other, whose packages the runner may already
# have, only warns that it proves nothing. A forced download also fails if apt still
# fetches a package from Azure after it was taken off the list; a fallback after a real timeout
# makes neither check (test.sh holds both modes). Unset or anything but true, every
# line that runs is the same as without it.
#
# PLAYWRIGHT_APT_ETC and PLAYWRIGHT_APT_LIMIT exist for test.sh's stand-ins; the action sets neither.
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "::error::name the browsers whose system packages to download"; exit 1
fi
apt_etc=${PLAYWRIGHT_APT_ETC:-/etc/apt}
limit=${PLAYWRIGHT_APT_LIMIT:-60s}
mirrors="$apt_etc/apt-mirrors.txt"
force=false
[ "${PLAYWRIGHT_APT_FORCE_FALLBACK:-}" != true ] || force=true
cache_hit=false
if [ "${DEBS_CACHE_HIT:-}" = true ] && [ "$force" = false ]; then
  cache_hit=true; limit=${PLAYWRIGHT_APT_LIMIT:-20s}
fi
update_deadline=0
attempts=${PLAYWRIGHT_APT_ATTEMPTS:-3}
delay=${PLAYWRIGHT_APT_RETRY_DELAY:-5}
network_host=azure.archive.ubuntu.com
left_azure=false
if ! grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" && grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors"; then
  network_host=archive.ubuntu.com; left_azure=true
fi
network_failed=false

# Apt uses the same exit code for network and package errors; mixed errors must fail closed.
network_error() {
  local errors
  errors=$(grep -E '^[EW]:' "$1" | grep -vE '^[EW]: (Unable to fetch some archives|Some index files failed to download)' || true)
  [ -n "$errors" ] || return 1
  if grep -qF 'mirror+file:/etc/apt/apt-mirrors.txt/' <<< "$errors"; then
    grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors" || return 1
    [ "$left_azure" = true ] || grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" || return 1
  fi
  if [ "$left_azure" = true ] && grep -qF azure.archive.ubuntu.com <<< "$errors"; then return 1; fi
  ! grep -qviE "(^|[^[:alnum:].-])${network_host//./\\.}([^[:alnum:].-]|$)|^[EW]: Failed to fetch mirror\\+file:/etc/apt/apt-mirrors\\.txt/" <<< "$errors" &&
    ! grep -qviE '^[EW]: Failed to fetch .* (Temporary failure resolving|Could not resolve|Could not connect|Unable to connect|Connection (failed|timed out|refused|reset)|Network is unreachable|5[0-9]{2}([[:space:]]|$))' <<< "$errors"
}

retry() {
  local n=1 rc=0 log wait
  network_failed=false
  log=$(mktemp)
  while true; do
    if [ "$update_deadline" -gt 0 ] && [ "$SECONDS" -ge "$update_deadline" ]; then
      echo "::warning::apt update budget exhausted"
      rc=124; network_failed=true; break
    fi
    echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::apt attempt $n of $attempts: $*"
    rc=0; LC_ALL=C "$@" 2>&1 | tee "$log" || rc=$?
    if [ "$rc" -eq 0 ] && ! grep -qE '^[EW]:' "$log"; then break; fi
    [ "$rc" -ne 0 ] || rc=100
    if [ "$rc" -ne 124 ] || grep -qE '^[EW]:' "$log"; then network_error "$log" || break; fi
    if { [ "$left_azure" = false ] && { [ "$rc" -eq 124 ] || grep -qiE 'Temporary failure resolving|Could not resolve' "$log"; }; } || [ "$n" -ge "$attempts" ]; then
      network_failed=true; break
    fi
    wait=$((delay * 4 ** (n - 1)))
    if [ "$update_deadline" -gt 0 ] && [ "$((SECONDS + wait))" -ge "$update_deadline" ]; then
      echo "::warning::apt update budget has no time for another backoff"
      rc=124; network_failed=true; break
    fi
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::apt network failure (exit $rc, attempt $n of $attempts); retrying"
    sleep "$wait"; n=$((n + 1))
  done
  rm -f "$log"
  return "$rc"
}

leave_azure() {
  [ "$left_azure" = false ] || return 0
  if ! grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" || ! grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors"; then
    echo "::error::$mirrors doesn't list the Azure mirror and https://archive.ubuntu.com this step expects, so it can't leave the slow mirror"; exit 1
  fi
  sudo sed -i '/^http:\/\/azure\.archive\.ubuntu\.com\//d' "$mirrors"
  if grep -rqs '^[^#]*azure\.archive\.ubuntu\.com' "$mirrors" "$apt_etc/sources.list" "$apt_etc/sources.list.d/"; then
    echo "::error::apt's sources still name azure.archive.ubuntu.com after removing it from $mirrors"; exit 1
  fi
  left_azure=true; network_host=archive.ubuntu.com
}
fail() { echo "::error::apt $1 failed (exit $2); see the attempt log above"; exit "$2"; }

# install-deps --dry-run prints the apt-get command it would run; the packages follow
# --no-install-recommends. Every such line counts, should Playwright ever print one per browser.
list=$(npx playwright install-deps --dry-run "$@" | sed -n 's/.*--no-install-recommends \([^"]*\)".*/\1/p' | tr '\n' ' ')
read -ra pkgs <<< "$list"
if [ "${#pkgs[@]}" -eq 0 ]; then
  echo "::error::playwright install-deps --dry-run named no packages to download"; exit 1
fi

# timeout returns 137 if --kill-after had to KILL it; that still takes the timeout fallback.
bounded() {
  local rc=0 remaining
  if [ "$update_deadline" -gt 0 ]; then
    remaining=$((update_deadline - SECONDS)); [ "$remaining" -gt 0 ] || return 124
    sudo timeout --kill-after=2s "${remaining}s" timeout --kill-after=2s "$limit" "$@" || rc=$?
  else
    sudo timeout --kill-after=30s "$limit" "$@" || rc=$?
  fi
  [ "$rc" -eq 137 ] && rc=124; return "$rc"
}
if [ "$cache_hit" = true ]; then update_deadline=$((SECONDS + ${PLAYWRIGHT_APT_UPDATE_BUDGET:-120})); fi
if grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors"; then
  probe_rc=0
  probe_output=$(timeout --kill-after=2s 5s getent hosts azure.archive.ubuntu.com 2>&1) || probe_rc=$?
  echo "::notice::Azure DNS probe exit $probe_rc: ${probe_output:-no output}"
  if [ "$probe_rc" -ne 0 ]; then
    echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::Azure DNS probe failed (exit $probe_rc); fetching the rest from https://archive.ubuntu.com"
    leave_azure
  fi
fi
rc=0
retry bounded apt-get update || rc=$?
if [ "$rc" -ne 0 ]; then
  if [ "$left_azure" = false ] && [ "$network_failed" = true ]; then
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::Azure apt-get update failed or exceeded $limit; switching to https://archive.ubuntu.com"
    leave_azure
    rc=0; retry bounded apt-get update || rc=$?
  fi
  if [ "$rc" -ne 0 ]; then
    if [ "$cache_hit" = true ] && [ "$network_failed" = true ]; then
      echo "::warning::apt update failed (exit $rc); using cached packages for the mandatory offline install; see the attempt log above"
    else fail update "$rc"; fi
  fi
fi
update_deadline=0
# Never interrupt dpkg with the network timeout while it is modifying installed packages.
install_offline() {
  sudo apt-get install -y --no-install-recommends --no-download "${pkgs[@]}" || fail install "$?"
}
if [ "$cache_hit" = true ]; then install_offline; exit 0; fi
rc=0
if [ "$force" = true ]; then
  echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::force-apt-fallback: taking the Azure mirror off apt's list before downloading anything; nothing timed out"
else
  retry bounded apt-get install -y --download-only --no-install-recommends "${pkgs[@]}" || rc=$?
fi
if [ "$force" = true ] || { [ "$left_azure" = false ] && [ "$network_failed" = true ]; }; then
  if [ "$rc" -eq 124 ]; then
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::apt took over $limit to download Playwright's system packages; fetching the rest from https://archive.ubuntu.com"
  elif [ "$network_failed" = true ]; then
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::Azure DNS/network failure; fetching the rest from https://archive.ubuntu.com"
  fi
  leave_azure
  if [ "$force" = true ]; then
    got=$(mktemp)
    retry bounded apt-get install -y --download-only --no-install-recommends "${pkgs[@]}" | tee "$got"
    if grep -q '^Get:.*azure\.archive\.ubuntu\.com' "$got"; then
      echo "::error::apt still fetched packages from azure.archive.ubuntu.com after it was taken off $mirrors"; exit 1
    fi
    n=$(grep -c '^Get:[0-9]* https://archive\.ubuntu\.com/ubuntu' "$got" || true)
    if [ "$n" -gt 0 ]; then
      echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::force-apt-fallback: $n packages downloaded from https://archive.ubuntu.com"
    elif [[ " $* " == *" webkit "* ]]; then
      echo "::error title=${GITHUB_JOB:-playwright-system-packages}::force-apt-fallback: nothing was downloaded from https://archive.ubuntu.com, so this WebKit leg is no proof of the fallback"; exit 1
    else
      echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::force-apt-fallback: nothing was downloaded from https://archive.ubuntu.com, so this leg proves nothing about the fallback"
    fi
  else
    retry bounded apt-get install -y --download-only --no-install-recommends "${pkgs[@]}" || fail download "$?"
  fi
elif [ "$rc" -ne 0 ]; then
  fail download "$rc"
fi
install_offline
