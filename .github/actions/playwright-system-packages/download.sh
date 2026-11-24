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
# 60 seconds for updates; downloads get 180 seconds on Azure and on Canonical alike. Azure timeout or DNS failure
# immediately leaves Azure. The Azure mirror is taken off the list and apt fetches the rest
# from https://archive.ubuntu.com,
# keeping what it has. Nothing else changes: no source, key or trust setting is added, apt checks
# every package against the signed index either way. Azure DNS failures leave immediately;
# Downloads get six attempts (5/20/30 s backoff, capped at 30), each on the NEXT listed mirror once Azure is left. Updates get an initial attempt plus three
# retries (15/30/60 s) for known-host network errors, including Microsoft's feed and HTTP 429.
# Retry budget is per mirror, NOT a total deadline: allowing each 30 s kill grace, Canonical
# update can take 4*(60+30)+105 = 465 s, downloads 6*(180+30)+115 = 1375 s at the very worst, and a connection that goes silent
# is dead after PLAYWRIGHT_APT_STALL_SECONDS (30 s, apt's Acquire::http(s)::Timeout), so a stalled mirror costs seconds, not an attempt's limit.
# The package-by-package fetch below runs first and is the fast path; this is what is left to apt after it.
# Azure attempts and its up-to-7 s DNS probe can add more. The job cap is the final bound;
# retries do not reserve test time. See quality/README.md for the budget and proof limits.
# Exhausted throttling or Microsoft-feed errors are fatal even on a cache hit.
# Cache only debs: every run authenticates live indexes with apt-get update. A non-forced hit
# checks archive hashes/sizes against those indexes before installing with --no-download. If
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
# PLAYWRIGHT_APT_* paths and limits exist for test.sh's stand-ins; the action sets neither.
set -euo pipefail

key_only=false
if [ "${1:-}" = --cache-key ]; then key_only=true; shift; fi
if [ "$#" -eq 0 ]; then
  echo "::error::name the browsers whose system packages to download"; exit 1
fi
list=$(npx playwright install-deps --dry-run "$@" | sed -n 's/.*--no-install-recommends \([^"]*\)".*/\1/p' | tr '\n' ' ')
read -ra pkgs <<< "$list"
if [ "${#pkgs[@]}" -eq 0 ]; then
  echo "::error::playwright install-deps --dry-run named no packages to download"; exit 1
fi
apt_etc=${PLAYWRIGHT_APT_ETC:-/etc/apt}
apt_archives=${PLAYWRIGHT_APT_ARCHIVES:-/var/cache/apt/archives}
cache_debs="$HOME/.cache/playwright-apt-debs"
limit=${PLAYWRIGHT_APT_LIMIT:-60s}
mirrors="$apt_etc/apt-mirrors.txt"

# The mirrors apt is given, in its order, then Canonical's HTTPS archive, each as <scheme>://<host>/ubuntu (no trailing slash).
mirror_bases() {
  local seen=' ' base
  { sed -n 's/^\(https\{0,1\}:\/\/[^[:space:]]*\).*/\1/p' "$mirrors" 2>/dev/null || true; echo https://archive.ubuntu.com/ubuntu/; } |
    while read -r base; do
      base=${base%/}
      case "$seen" in *" $base "*) continue ;; esac
      seen+="$base "; printf '%s\n' "$base"
    done
}
# What the signed indexes say today: a hash of the Date line of the release, -updates and -security InRelease files. The cache key carries it, so an index that
# moved is a cache MISS (a fresh download, saved under a fresh key) and never an exact-key hit whose archives fail verification against the new index, which an
# immutable cache key could not be repaired from. Verification itself is unchanged (see below). If no index can be read the key is unique to the run.
index_identity() {
  local codename pocket base date dates='' got=false hash
  codename=${PLAYWRIGHT_APT_CODENAME:-$(. /etc/os-release 2>/dev/null && printf '%s' "${VERSION_CODENAME:-}")}
  if [ -n "$codename" ]; then
    for pocket in "$codename" "$codename-updates" "$codename-security"; do
      date=''
      while read -r base; do
        date=$(curl --fail --silent --max-time "${PLAYWRIGHT_APT_INDEX_TIMEOUT:-15}" "$base/dists/$pocket/InRelease" 2>/dev/null </dev/null | sed -n 's/^Date: //p' | head -n 1) || date=''
        [ -z "$date" ] || break
      done < <(mirror_bases)
      [ -z "$date" ] || got=true
      dates+="$pocket=$date;"
    done
  fi
  if [ "$got" = true ]; then
    hash=$(printf '%s' "$dates" | sha256sum); echo "index-hash=${hash%% *}"
  else
    echo "::warning::could not read the signed indexes' dates; this run's archives cache key is unique to it"
    echo "index-hash=unknown-${GITHUB_RUN_ID:-0}"
  fi
}
if [ "$key_only" = true ]; then
  hash=$(printf '%s\n' "${pkgs[@]}" | sha256sum); echo "packages-hash=${hash%% *}" >> "$GITHUB_OUTPUT"
  hash=$(printf '%s' "${GITHUB_REF:-}" | sha256sum); echo "ref-hash=${hash%% *}" >> "$GITHUB_OUTPUT"
  index_identity | tee /dev/stderr | grep '^index-hash=' >> "$GITHUB_OUTPUT"
  exit 0
fi
force=false
[ "${PLAYWRIGHT_APT_FORCE_FALLBACK:-}" != true ] || force=true
cache_hit=false
cache_available=false
download_options=()
if [ "${DEBS_CACHE_HIT:-}" = true ]; then
  if compgen -G "$cache_debs/*.deb" > /dev/null; then
    cache_available=true
  else echo "::warning::apt cache lacks debs; treating it as a miss"; fi
fi
if [ "$cache_available" = true ] && [ "$force" = false ]; then cache_hit=true; fi
attempts=${PLAYWRIGHT_APT_ATTEMPTS:-6}
delay=${PLAYWRIGHT_APT_RETRY_DELAY:-5}
network_host=azure.archive.ubuntu.com
left_azure=false
if ! grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" && grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors"; then
  network_host=archive.ubuntu.com; left_azure=true
fi
network_failed=false

# Apt uses the same exit code for network and package errors; mixed errors must fail closed.
network_error() {
  local errors hosts status='5[0-9]{2}'
  errors=$(grep -E '^[EW]:' "$1" | grep -vE '^[EW]: (Unable to fetch some archives|Some index files failed to download)' || true)
  [ -n "$errors" ] || return 1
  if grep -qF 'mirror+file:/etc/apt/apt-mirrors.txt/' <<< "$errors"; then
    grep -q '^https://archive\.ubuntu\.com/ubuntu/' "$mirrors" || return 1
    [ "$left_azure" = true ] || grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors" || return 1
  fi
  if [ "$left_azure" = true ] && grep -qF azure.archive.ubuntu.com <<< "$errors"; then return 1; fi
  hosts="(^|[^[:alnum:].-])${network_host//./\\.}([^[:alnum:].-]|$)|^[EW]: Failed to fetch mirror\\+file:/etc/apt/apt-mirrors\\.txt/"
  if [ "$2" = update ]; then
    hosts+='|^[EW]: Failed to fetch https://packages\.microsoft\.com/'
    status='429|5[0-9]{2}'
  fi
  ! grep -qviE "$hosts" <<< "$errors" &&
    ! grep -qviE "^[EW]: Failed to fetch .* (Temporary failure resolving|Could not resolve|Could not connect|Unable to connect|Connection (failed|timed out|refused|reset)|Network is unreachable|($status)([[:space:]]|$))" <<< "$errors"
}

# Puts the next listed mirror first, so the next attempt asks a different one (a regional mirror that stalls is not asked again straight away).
rotate_mirrors() {
  local first rotated
  [ "$(grep -c '^https\{0,1\}://' "$mirrors")" -gt 1 ] || return 0
  first=$(grep -m 1 '^https\{0,1\}://' "$mirrors")
  rotated=$(mktemp)
  { grep -v -x -F -- "$first" "$mirrors" || true; printf '%s\n' "$first"; } > "$rotated"
  sudo cp -- "$rotated" "$mirrors"; rm -f "$rotated"
  echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::the next attempt asks the next mirror: $(grep -m 1 '^https\{0,1\}://' "$mirrors" | cut -f 1)"
}
retry() {
  local phase="$1" n=1 rc=0 log wait max="$attempts" strict=false
  shift
  if [ "$phase" = download ] && [ "$left_azure" = true ]; then limit=${PLAYWRIGHT_APT_LIMIT:-180s}; fi
  [ "$phase" != update ] || max=${PLAYWRIGHT_APT_ATTEMPTS:-4}
  if [ "$phase" = download ] && [ "$left_azure" = false ]; then max=${PLAYWRIGHT_APT_ATTEMPTS:-3}; fi
  network_failed=false
  log=$(mktemp)
  while true; do
    echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::apt attempt $n of $max: $*"
    rc=0; LC_ALL=C "$@" 2>&1 | tee "$log" || rc=$?
    if [ "$rc" -eq 0 ] && ! grep -qE '^[EW]:' "$log"; then break; fi
    [ "$rc" -ne 0 ] || rc=100
    if [ "$rc" -ne 124 ] || grep -qE '^[EW]:' "$log"; then network_error "$log" "$phase" || break; fi
    # A timeout cannot prove that the previously throttled feed recovered.
    if [ "$phase" = update ] && [ "$rc" -ne 124 ]; then strict=false; fi
    if [ "$phase" = update ] && grep -qE '^[EW]: Failed to fetch (https://packages\.microsoft\.com/|.* 429([[:space:]]|$))' "$log"; then
      strict=true
    fi
    if { [ "$strict" = false ] && [ "$left_azure" = false ] && { [ "$rc" -eq 124 ] || grep -qiE 'Temporary failure resolving|Could not resolve' "$log"; }; } || [ "$n" -ge "$max" ]; then
      network_failed=true; break
    fi
    wait=$((delay * 4 ** (n - 1)))
    [ "$wait" -le 30 ] || wait=30
    if [ "$phase" = update ]; then
      wait=$((${PLAYWRIGHT_APT_RETRY_DELAY:-15} * 2 ** (n - 1)))
      [ "$wait" -le 60 ] || wait=60
    fi
    [ "$phase" != download ] || [ "$left_azure" != true ] || [ "${PLAYWRIGHT_APT_ROTATE:-true}" != true ] || rotate_mirrors
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::apt network failure (exit $rc, attempt $n of $max); retrying"
    sleep "$wait"; n=$((n + 1))
  done
  rm -f "$log"
  # A different Ubuntu mirror or cached debs cannot heal a throttled Microsoft index.
  if [ "$strict" = true ] && [ "$rc" -ne 0 ]; then network_failed=false; fi
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
cache_failed=false
# What the final error blames: a timeout or network failure, or apt rejecting what it fetched (verification, a dependency, a signature), so the log is read the right way.
cause() {
  if [ "$1" -eq 124 ] || [ "$network_failed" = true ]; then
    printf 'network or timeout (the mirrors were too slow or unreachable), not a package or signature problem'
  else
    printf 'apt rejected the %s (a package, dependency or signature error), not a network failure' "$2"
  fi
}
fail() {
  local why; why=$(cause "$2" "${3:-packages}")
  [ "$cache_failed" = false ] || why+="; the restored cache had failed verification against the signed indexes and was discarded"
  echo "::error::apt $1 failed (exit $2): $why; see the attempt log above"; exit "$2"
}

# timeout returns 137 if --kill-after had to KILL it; that still takes the timeout fallback.
bounded() {
  local rc=0
  sudo timeout --kill-after=30s "$limit" "$@" || rc=$?
  [ "$rc" -eq 137 ] && rc=124; return "$rc"
}
# Never interrupt dpkg with the network timeout while it is modifying installed packages.
install_offline() {
  sudo apt-get install -y --no-install-recommends --no-download "${pkgs[@]}" || { rc_install=$?; network_failed=false; fail install "$rc_install" packages; }
}
if grep -q '^http://azure\.archive\.ubuntu\.com/' "$mirrors"; then
  probe_rc=0
  probe_output=$(timeout --kill-after=2s 5s getent hosts azure.archive.ubuntu.com 2>&1) || probe_rc=$?
  echo "::notice::Azure DNS probe exit $probe_rc: ${probe_output:-no output}"
  if [ "$probe_rc" -ne 0 ]; then
    echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::Azure DNS probe failed (exit $probe_rc); fetching the rest from https://archive.ubuntu.com"
    leave_azure
  fi
fi
# A connection with no data for this long is dead (curl: under 1 kB/s for it; apt: Acquire::http(s)::Timeout), whatever the attempt's total limit.
stall_seconds=${PLAYWRIGHT_APT_STALL_SECONDS:-30}
# Written before anything is fetched: apt waits this long for DATA on a connection, not for the whole file, so a mirror that stalls mid-download
# (main run 45351659153: Get:69 libmfx1, then nothing, three attempts each waiting out their full limit) fails at once and the retry asks another mirror.
sudo mkdir -p "$apt_etc/apt.conf.d"
printf 'Acquire::http::Timeout "%s";\nAcquire::https::Timeout "%s";\n' "$stall_seconds" "$stall_seconds" | sudo tee "$apt_etc/apt.conf.d/99playwright-stall" > /dev/null
rc=0
retry update bounded apt-get update || rc=$?
if [ "$rc" -ne 0 ]; then
  if [ "$left_azure" = false ] && [ "$network_failed" = true ]; then
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::Azure apt-get update failed or exceeded $limit; switching to https://archive.ubuntu.com"
    leave_azure
    rc=0; retry update bounded apt-get update || rc=$?
  fi
  if [ "$rc" -ne 0 ]; then
    fail update "$rc" indexes
  fi
fi
if [ "$cache_hit" = true ]; then
  plan=$(mktemp -d)
  trap 'sudo rm -rf -- "$plan"' EXIT
  cache_valid=true
  verified=()
  # Empty planning archives defeat apt's size-only reuse; live update authenticated the indexes.
  sudo apt-get -o Dir::Cache::archives="$plan/" -o Acquire::ForceHash=sha256 --print-uris --download-only install -y --no-install-recommends "${pkgs[@]}" > "$plan/uris" || cache_valid=false
  while read -r uri file size hash extra; do
    [[ "$uri" == \'* ]] || continue
    if [[ ! "$file" =~ ^[[:alnum:]][[:alnum:].+%_~:-]*\.deb$ ]] ||
      [[ ! "$size" =~ ^[0-9]+$ ]] || [[ ! "$hash" =~ ^SHA256:[[:xdigit:]]{64}$ ]] ||
      [ -n "${extra:-}" ] || [ ! -f "$cache_debs/$file" ]; then
      cache_valid=false; break
    fi
    [ "$(wc -c < "$cache_debs/$file")" -eq "$size" ] &&
      printf '%s  %s\n' "${hash#SHA256:}" "$cache_debs/$file" | sha256sum --check --status ||
      { cache_valid=false; break; }
    verified+=("$cache_debs/$file")
  done < "$plan/uris"
  sudo rm -rf -- "$plan"
  trap - EXIT
  if [ "$cache_valid" = true ]; then
    # Copy only after ALL planned archives pass: apt reuses size-matching files without hashing.
    [ "${#verified[@]}" -eq 0 ] || sudo cp -- "${verified[@]}" "$apt_archives/"
    install_offline
    exit 0
  fi
  cache_failed=true
  if [ "${DEBS_CACHE_EXACT_HIT:-}" = true ]; then
    echo "::warning::cached archives failed verification; immutable exact-key cache cannot be replaced; discarding it locally and using quiet authenticated downloads (delete the cache key to recover)"
    download_options=(-qq)
  else
    echo "::warning::cached archives failed verification; discarding cache and using authenticated network downloads"
  fi
  find "$cache_debs" -maxdepth 1 -type f -name '*.deb' -delete
  cache_available=false
fi
# (S2, #4104) Fetch each planned package on its own, with a short bounded try, a few tries and a mirror fallback, and keep only bytes that match the signed
# size and sha256 apt printed for it (the same signed index check as the cache's: the hash comes from the index apt-get update authenticated, so no mirror is
# trusted). A mirror that times out for one package is not asked first for the next (the one that worked is kept). Apt's own download then finds what was
# fetched; anything not fetched in time or never served is left to apt's bounded download below, so this only ever adds a fast path. Not in a forced
# fallback proof (apt's own Get: lines are the proof there) and not past PLAYWRIGHT_APT_PREFETCH_BUDGET seconds in all.
fetch_tries=${PLAYWRIGHT_APT_FETCH_TRIES:-3}
fetch_timeout=${PLAYWRIGHT_APT_FETCH_TIMEOUT:-60}
prefetch_budget=${PLAYWRIGHT_APT_PREFETCH_BUDGET:-600}
prefetch() {
  [ "${PLAYWRIGHT_APT_PREFETCH:-true}" = true ] && [ "$force" = false ] || return 0
  local plan work_dir uri file size hash extra rel base mapped bases=() urls=() left=() sticky=0 planned=0 fetched=0 i try idx nb started=$SECONDS spent=false got
  if [ "$prefetch_budget" -le 0 ]; then
    echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::the prefetch budget is spent before it began; apt downloads every package"; return 0
  fi
  plan=$(mktemp -d); work_dir=$(mktemp -d)
  if ! sudo apt-get -o Dir::Cache::archives="$plan/" -o Acquire::ForceHash=sha256 --print-uris --download-only install -y --no-install-recommends "${pkgs[@]}" > "$plan/uris"; then
    echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::could not plan the packages to fetch one by one; apt downloads them"
    sudo rm -rf -- "$plan" "$work_dir"; return 0
  fi
  mapfile -t bases < <(mirror_bases); nb=${#bases[@]}
  while read -r uri file size hash extra; do
    [[ "$uri" == \'* ]] || continue
    if [[ ! "$file" =~ ^[[:alnum:]][[:alnum:].+%_~:-]*\.deb$ ]] || [[ ! "$size" =~ ^[0-9]+$ ]] || [[ ! "$hash" =~ ^SHA256:[[:xdigit:]]{64}$ ]] || [ -n "${extra:-}" ]; then
      continue
    fi
    planned=$((planned + 1))
    if [ -f "$apt_archives/$file" ] && [ "$(wc -c < "$apt_archives/$file")" -eq "$size" ] && printf '%s  %s\n' "${hash#SHA256:}" "$apt_archives/$file" | sha256sum --check --status; then
      fetched=$((fetched + 1)); continue
    fi
    if [ "$spent" = true ] || [ $((SECONDS - started)) -ge "$prefetch_budget" ]; then spent=true; left+=("$file"); continue; fi
    rel=${uri#\'}; rel=${rel%\'}; urls=()
    # What apt prints is not always a URL curl can fetch: on the runner images apt's sources are mirror+file:/etc/apt/apt-mirrors.txt, and --print-uris prints that
    # UNRESOLVED URI (mirror+file:/etc/apt/apt-mirrors.txt/pool/main/...deb). It is mapped onto the mirror list (the same rotation), as is an http(s) URI that names
    # one of those mirrors; any other http(s) URI (another source's) is fetched from itself only; any other scheme is left to apt at once, with no try and no sleep.
    case "$rel" in
      mirror+file:*/apt-mirrors.txt/*) rel=${rel#mirror+file:*/apt-mirrors.txt/}; mapped=true ;;
      http://*/ubuntu/* | https://*/ubuntu/*)
        mapped=false
        for base in "${bases[@]}"; do [ "${rel%%/ubuntu/*}/ubuntu" != "$base" ] || { mapped=true; break; }; done
        [ "$mapped" = false ] || rel=${rel#*/ubuntu/} ;;
      http://* | https://*) mapped=false ;;
      *) left+=("$file"); continue ;;
    esac
    if [ "$mapped" = true ]; then
      for ((i = 0; i < nb; i++)); do urls+=("$(( (sticky + i) % nb ))|${bases[$(( (sticky + i) % nb ))]}/$rel"); done
    else urls=("-1|$rel"); fi
    got=false
    for base in "${urls[@]}"; do
      idx=${base%%|*}; base=${base#*|}
      for ((try = 1; try <= fetch_tries; try++)); do
        if curl --fail --silent --show-error --location --connect-timeout 10 --max-time "$fetch_timeout" --speed-limit 1024 --speed-time "$stall_seconds" -o "$work_dir/$file" "$base" 2>/dev/null </dev/null &&
          [ "$(wc -c < "$work_dir/$file")" -eq "$size" ] && printf '%s  %s\n' "${hash#SHA256:}" "$work_dir/$file" | sha256sum --check --status; then
          got=true; break 2
        fi
        rm -f "$work_dir/$file"
        [ "$try" -ge "$fetch_tries" ] || sleep $((try * 2))
      done
    done
    if [ "$got" = true ]; then
      sudo cp -- "$work_dir/$file" "$apt_archives/$file"; rm -f "$work_dir/$file"
      [ "$idx" -lt 0 ] || sticky=$idx
      fetched=$((fetched + 1))
    else left+=("$file"); fi
  done < "$plan/uris"
  sudo rm -rf -- "$plan" "$work_dir"
  echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::fetched $fetched of $planned planned packages one by one, each checked against the signed index"
  [ "$spent" = false ] || echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::the prefetch budget ($prefetch_budget s) ran out; the rest is left to apt"
  [ "${#left[@]}" -eq 0 ] || echo "::warning title=${GITHUB_JOB:-playwright-system-packages}::left to apt's own bounded download: ${left[*]}"
  return 0
}
prefetch
# Downloads have their own bound, independent of the update limit.
limit=${PLAYWRIGHT_APT_LIMIT:-180s}
rc=0
if [ "$force" = true ]; then
  echo "::notice title=${GITHUB_JOB:-playwright-system-packages}::force-apt-fallback: taking the Azure mirror off apt's list before downloading anything; nothing timed out"
else
  retry download bounded apt-get "${download_options[@]}" install -y --download-only --no-install-recommends "${pkgs[@]}" || rc=$?
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
    retry download bounded apt-get "${download_options[@]}" install -y --download-only --no-install-recommends "${pkgs[@]}" | tee "$got"
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
    retry download bounded apt-get "${download_options[@]}" install -y --download-only --no-install-recommends "${pkgs[@]}" || fail download "$?"
  fi
elif [ "$rc" -ne 0 ]; then
  fail download "$rc"
fi
if [ "$cache_available" = false ] && compgen -G "$apt_archives/*.deb" > /dev/null; then
  mkdir -p "$cache_debs"
  cp "$apt_archives"/*.deb "$cache_debs/"
  [ -z "${GITHUB_OUTPUT:-}" ] || echo 'cache-ready=true' >> "$GITHUB_OUTPUT"
fi
install_offline
