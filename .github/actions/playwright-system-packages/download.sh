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
# PLAYWRIGHT_APT_ETC and PLAYWRIGHT_APT_LIMIT exist for test.sh's stand-ins; the action sets neither.
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "::error::name the browsers whose system packages to download"; exit 1
fi
apt_etc=${PLAYWRIGHT_APT_ETC:-/etc/apt}
limit=${PLAYWRIGHT_APT_LIMIT:-5m}
mirrors="$apt_etc/apt-mirrors.txt"

# install-deps --dry-run prints the apt-get command it would run; the packages follow
# --no-install-recommends. Every such line counts, should Playwright ever print one per browser.
list=$(npx playwright install-deps --dry-run "$@" | sed -n 's/.*--no-install-recommends \([^"]*\)".*/\1/p' | tr '\n' ' ')
read -ra pkgs <<< "$list"
if [ "${#pkgs[@]}" -eq 0 ]; then
  echo "::error::playwright install-deps --dry-run named no packages to download"; exit 1
fi

sudo apt-get update
rc=0
sudo timeout "$limit" apt-get install -y --download-only --no-install-recommends "${pkgs[@]}" || rc=$?
if [ "$rc" -eq 124 ]; then
  if ! grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" || ! grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors"; then
    echo "::error::$mirrors doesn't list the Azure mirror and https://archive.ubuntu.com this step expects, so it can't leave the slow mirror"; exit 1
  fi
  echo "::warning title=${GITHUB_JOB:-Playwright's system packages}::apt took over $limit to download Playwright's system packages; fetching the rest from https://archive.ubuntu.com"
  sudo sed -i '/^http:\/\/azure\.archive\.ubuntu\.com\//d' "$mirrors"
  if grep -rqs '^[^#]*azure\.archive\.ubuntu\.com' "$mirrors" "$apt_etc/sources.list" "$apt_etc/sources.list.d/"; then
    echo "::error::apt's sources still name azure.archive.ubuntu.com after removing it from $mirrors"; exit 1
  fi
  sudo apt-get install -y --download-only --no-install-recommends "${pkgs[@]}"
elif [ "$rc" -ne 0 ]; then
  exit "$rc"
fi
