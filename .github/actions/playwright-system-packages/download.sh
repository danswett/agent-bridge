#!/usr/bin/env bash
# Downloads the system packages `npx playwright install --with-deps <browsers>` is about to
# install, so that install finds them already fetched (#799, #689). Run it from the folder whose
# node_modules holds that Playwright; its arguments are the browsers.
#
# Why: on main run 41927187905, pwa-ui's webkit 3/3 fetched its 181 packages (114 MB) from
# azure.archive.ubuntu.com at about 100 kB/s on two runners in turn and hit the job's limit inside
# the install, before any test ran; on main run 41952998347 pwa-e2e did the same with Chromium's
# and WebKit's packages. The runner's mirror list (/etc/apt/apt-mirrors.txt, from
# actions/runner-images) puts that mirror first and Canonical's HTTPS archive after it, but apt
# moves down the list only when a fetch fails, never when it is slow. So the download gets five
# minutes on the list as it stands; if it isn't done by then (timeout's 124, and only that), the
# Azure mirror is taken off the list and apt fetches the rest from https://archive.ubuntu.com,
# keeping what it has. Nothing else changes: no source, key or trust setting is added, apt checks
# every package against the signed index either way, and any other apt failure fails the step. If
# the mirror list isn't the Azure-then-Canonical one this expects, or Azure is still named after the
# edit, it fails rather than go on waiting on the same mirror.
#
# PLAYWRIGHT_APT_FORCE_FALLBACK=true (the action's force-apt-fallback, which ci.yml sets only in
# pwa-ui on a run by hand that asks for it; quality/README.md §4.7) skips the five minutes and takes
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
limit=${PLAYWRIGHT_APT_LIMIT:-5m}
mirrors="$apt_etc/apt-mirrors.txt"
force=false
[ "${PLAYWRIGHT_APT_FORCE_FALLBACK:-}" != true ] || force=true
attempts=${PLAYWRIGHT_APT_ATTEMPTS:-3}
delay=${PLAYWRIGHT_APT_RETRY_DELAY:-10}

# Runs a command up to $attempts times, waiting delay, 2*delay, ... seconds between tries, so a
# transient mirror error (a 5xx, a reset) costs seconds, not the job. timeout's 124 returns at
# once: a slow mirror is left for Canonical's archive below, not waited on again.
retry() {
  local n=1 rc=0
  while true; do
    if "$@"; then return 0; else rc=$?; fi
    if [ "$rc" -eq 124 ] || [ "$n" -ge "$attempts" ]; then return "$rc"; fi
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::apt step failed (exit $rc, attempt $n of $attempts); retrying"
    sleep $((delay * n)); n=$((n + 1))
  done
}

# install-deps --dry-run prints the apt-get command it would run; the packages follow
# --no-install-recommends. Every such line counts, should Playwright ever print one per browser.
list=$(npx playwright install-deps --dry-run "$@" | sed -n 's/.*--no-install-recommends \([^"]*\)".*/\1/p' | tr '\n' ' ')
read -ra pkgs <<< "$list"
if [ "${#pkgs[@]}" -eq 0 ]; then
  echo "::error::playwright install-deps --dry-run named no packages to download"; exit 1
fi

# timeout returns 137 rather than 124 when the command ignored TERM and --kill-after had to KILL it;
# that is still a timeout, so it is reported as 124 and gets the same one more try.
bounded() { local rc=0; sudo timeout --kill-after=30s "$limit" "$@" || rc=$?; [ "$rc" -eq 137 ] && rc=124; return "$rc"; }
update() { retry bounded apt-get update; }
# A hung mirror ends the update at the limit (124); one more go, then the step fails, not the job's 35 minutes.
update || { rc=$?; [ "$rc" -eq 124 ] || exit "$rc"; echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::apt-get update took over $limit; trying once more"; update; }
rc=0
if [ "$force" = true ]; then
  echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::force-apt-fallback: taking the Azure mirror off apt's list before downloading anything; nothing timed out"
else
  retry bounded apt-get install -y --download-only --no-install-recommends "${pkgs[@]}" || rc=$?
fi
if [ "$force" = true ] || [ "$rc" -eq 124 ]; then
  if ! grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" || ! grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors"; then
    echo "::error::$mirrors doesn't list the Azure mirror and https://archive.ubuntu.com this step expects, so it can't leave the slow mirror"; exit 1
  fi
  if [ "$rc" -eq 124 ]; then
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::apt took over $limit to download Playwright's system packages; fetching the rest from https://archive.ubuntu.com"
  fi
  sudo sed -i '/^http:\/\/azure\.archive\.ubuntu\.com\//d' "$mirrors"
  if grep -rqs '^[^#]*azure\.archive\.ubuntu\.com' "$mirrors" "$apt_etc/sources.list" "$apt_etc/sources.list.d/"; then
    echo "::error::apt's sources still name azure.archive.ubuntu.com after removing it from $mirrors"; exit 1
  fi
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
    retry bounded apt-get install -y --download-only --no-install-recommends "${pkgs[@]}"
  fi
elif [ "$rc" -ne 0 ]; then
  exit "$rc"
fi
