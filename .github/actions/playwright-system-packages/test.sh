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
printf '%s\n' "${APT_DNS_OUTPUT:-}"
exit "${APT_DNS_EXIT:-0}"
EOF
cat > "$work/bin/timeout" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$TIMEOUT_LOG"
# Model a cold download's elapsed seconds without waiting out production bounds.
if [ "${3:-}" = apt-get ] && [[ " $* " == *" --download-only "* ]] &&
  { [ "$(grep -c -- '--download-only' "$APT_LOG" || true)" -lt "${APT_DOWNLOAD_TIMEOUTS:-0}" ] ||
    [ "${2%s}" -lt "${APT_DOWNLOAD_SECONDS:-0}" ]; }; then
  shift 2; "$@"; exit 124
fi
if [ "${3:-}" = apt-get ] && [ -n "${APT_TIMEOUT_EXIT:-}" ]; then
  shift 2; "$@"; exit "$APT_TIMEOUT_EXIT"
fi
exec /usr/bin/timeout "$@"
EOF
# apt-get records each call. A download is slow (it outlasts the limit) while the mirror list
# still names the Azure mirror and APT_SLOW is set, so only leaving that mirror ends it. A
# download that finishes prints APT_GETS, apt's "Get:" lines for what it fetched, if set.
cat > "$work/bin/apt-get" <<'EOF'
#!/usr/bin/env bash
# Model a runner-trusted signature, its signed index hash/size, and archive hashes.
chain_index() {
  if [ -f "$PLAYWRIGHT_APT_LISTS/cache_Packages" ]; then echo "$PLAYWRIGHT_APT_LISTS/cache_Packages"
  else echo "$PLAYWRIGHT_APT_LISTS/image_Packages"; fi
}
archive_matches_index() {
  local digest size; read -r digest size < "$(chain_index)"
  [ -f "$PLAYWRIGHT_APT_ARCHIVES/cached.deb" ] &&
    [ "$(wc -c < "$PLAYWRIGHT_APT_ARCHIVES/cached.deb")" -eq "$size" ] &&
    printf '%s  %s\n' "$digest" "$PLAYWRIGHT_APT_ARCHIVES/cached.deb" | sha256sum --check --status
}
if [ "$1" = -o ] && [[ "$2" == Dir::Cache::archives=* ]]; then
  plan=${2#Dir::Cache::archives=}
  compgen -G "$plan/*.deb" > /dev/null && exit 99
  mkdir -p "$plan/partial"; : > "$plan/lock"
  shift 2
fi
echo "$*" >> "$APT_LOG"
case "$*" in
  *--print-uris*)
    [ -n "${plan:-}" ] || exit 99
    [ -z "${APT_VERIFY_ERROR:-}" ] || { echo "$APT_VERIFY_ERROR" >&2; exit 100; }
    if [ -n "${APT_VERIFY_URI:-}" ]; then printf '%s\n' "$APT_VERIFY_URI"; exit 0; fi
    if [ -n "${APT_CHAIN_SOURCE:-}" ]; then
      read -r hash size < "$(chain_index)"
      printf "'https://archive.ubuntu.com/cached.deb' cached.deb %s SHA256:%s\n" "$size" "$hash"
      exit 0
    fi
    hash=$(printf 'valid deb\n' | sha256sum); hash=${hash%% *}
    printf "'https://archive.ubuntu.com/cached.deb' cached.deb 10 SHA256:%s\n" "$hash"
    exit 0 ;;
  *--no-download*)
    if [ -n "${APT_CHAIN_SOURCE:-}" ]; then
      archive_matches_index || { echo 'E: archive hash or size mismatch' >&2; exit 100; }
      cp "$PLAYWRIGHT_APT_ARCHIVES/cached.deb" "$APT_CHAIN_INSTALLED"
    fi
    if [ -n "${APT_EXPECT_INDEX:-}" ] && [ "$(cat "$PLAYWRIGHT_APT_LISTS"/*_Packages)" != "$APT_EXPECT_INDEX" ]; then
      echo 'E: package candidates do not match the cached archives' >&2; exit 100
    fi
    if [ -n "${APT_NEW_VERSION:-}" ] && [ ! -f "$PLAYWRIGHT_APT_ARCHIVES/libgtk-4-1_${APT_NEW_VERSION}_amd64.deb" ]; then
      echo "E: Unable to fetch libgtk-4-1=$APT_NEW_VERSION from the cached archives" >&2; exit 100
    fi
    [ -z "${APT_CACHE_ERROR:-}" ] || printf '%s\n' "$APT_CACHE_ERROR" >&2
    exit "${APT_CACHE_EXIT:-0}" ;;
  update)
    if [ -n "${APT_UPDATE_ERROR:-}" ] &&
      [ "$(grep -c '^update$' "$APT_LOG")" -le "${APT_UPDATE_FAIL_COUNT:-99}" ] &&
      { [ "${APT_AZURE_ONLY:-}" != true ] || grep -q azure "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; }; then
      printf '%s\n' "$APT_UPDATE_ERROR" >&2; exit "${APT_UPDATE_EXIT:-100}"
    fi
    if [ "${APT_UPDATE_SLOW:-}" = all ] || { [ -n "${APT_UPDATE_SLOW:-}" ] && grep -q azure "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; }; then
      case "$APT_UPDATE_SLOW" in ignore) trap '' TERM; /usr/bin/sleep 30 ;; *) exec /usr/bin/sleep 30 ;; esac
    fi
    if [ -n "${APT_CHAIN_SOURCE:-}" ]; then
      # The trusted fixture is outside the cache; equality models signature verification.
      cmp -s "$APT_CHAIN_SOURCE/InRelease" "$APT_TRUSTED_RELEASE" ||
        { echo 'E: InRelease signature verification failed' >&2; exit 100; }
      read -r digest size _ < "$APT_CHAIN_SOURCE/InRelease"
      [ "$(wc -c < "$APT_CHAIN_SOURCE/Packages")" -eq "$size" ] &&
        printf '%s  %s\n' "$digest" "$APT_CHAIN_SOURCE/Packages" | sha256sum --check --status ||
        { echo 'E: signed Packages hash or size mismatch' >&2; exit 100; }
      cp "$APT_CHAIN_SOURCE/Packages" "$PLAYWRIGHT_APT_LISTS/image_Packages"
      cp "$APT_CHAIN_SOURCE/InRelease" "$PLAYWRIGHT_APT_LISTS/image_InRelease"
    fi
    exit "${APT_UPDATE_EXIT:-0}" ;;
  *--download-only*)
    if [ -n "${APT_SLOW:-}" ] && grep -q 'azure\.archive\.ubuntu\.com' "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; then
      exec /usr/bin/sleep 30
    fi
    if [ -n "${APT_ERROR:-}" ] && [ "$(grep -c -- '--download-only' "$APT_LOG")" -le "${APT_FAIL_COUNT:-99}" ]; then
      if [ "${APT_AZURE_ONLY:-}" != true ] || grep -q 'azure\.archive\.ubuntu\.com' "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; then
        printf '%s\n' "$APT_ERROR" >&2; exit 100
      fi
    fi
    if [ "${APT_DOWNLOAD_EXIT:-0}" -eq 0 ] && [ -n "${APT_NEW_VERSION:-}" ]; then
      : > "$PLAYWRIGHT_APT_ARCHIVES/libgtk-4-1_${APT_NEW_VERSION}_amd64.deb"
    fi
    if [ "${APT_DOWNLOAD_EXIT:-0}" -eq 0 ] && [ -n "${APT_CHAIN_SOURCE:-}" ]; then
      cp "$APT_CHAIN_SOURCE/cached.deb" "$PLAYWRIGHT_APT_ARCHIVES/cached.deb"
      archive_matches_index || { echo 'E: downloaded archive hash mismatch' >&2; exit 100; }
    fi
    if [[ " $* " != *" -qq "* ]]; then
      [ -z "${APT_GETS:-}" ] || printf '%s\n' "$APT_GETS"
    fi
    exit "${APT_DOWNLOAD_EXIT:-0}" ;;
esac
exit 99
EOF
cat > "$work/bin/sleep" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$SLEEP_LOG"
EOF
# curl serves files from CURL_FIXTURES/<host>/<path> and records each URL. CURL_DOWN_HOSTS time out (28), CURL_BAD_HOSTS serve other bytes,
# and the first CURL_FAIL_FIRST requests for any one URL time out. Nothing here touches the network.
cat > "$work/bin/curl" <<'EOF'
#!/usr/bin/env bash
url=""; out=""; args=""
while [ $# -gt 0 ]; do
  case "$1" in -o) out=$2; shift 2 ;; --max-time|--connect-timeout|--retry|--speed-limit|--speed-time) args+="$1=$2 "; shift 2 ;; -*) shift ;; *) url=$1; shift ;; esac
done
seen=$(grep -cxF "$url" "$CURL_LOG" || true); echo "$url" >> "$CURL_LOG"; echo "$args" >> "$CURL_ARGS"
host=${url#*://}; host=${host%%/*}; path=${url#*://"$host"}
case " ${CURL_DOWN_HOSTS:-} " in *" $host "*) exit 28 ;; esac
[ "$seen" -ge "${CURL_FAIL_FIRST:-0}" ] || exit 28
file="$CURL_FIXTURES/$host$path"
[ -f "$file" ] || exit 22
case " ${CURL_BAD_HOSTS:-} " in *" $host "*) size=$(wc -c < "$file"); file=$CURL_FIXTURES/corrupt; head -c "$size" /dev/zero | tr '\0' X > "$file" ;; esac
if [ -n "$out" ]; then cp "$file" "$out"; else cat "$file"; fi
EOF
chmod +x "$work/bin/npx" "$work/bin/sudo" "$work/bin/apt-get" "$work/bin/sleep" "$work/bin/getent" "$work/bin/timeout" "$work/bin/curl"
export PATH="$work/bin:$PATH" NPX_LOG="$work/npx.log" APT_LOG="$work/apt.log" SLEEP_LOG="$work/sleep.log"
export TIMEOUT_LOG="$work/timeout.log"
export PLAYWRIGHT_APT_ETC="$work/apt" PLAYWRIGHT_APT_LIMIT=1s
export HOME="$work/cache-fixture/home" GITHUB_OUTPUT="$work/cache-fixture/output"
export PLAYWRIGHT_APT_LISTS="$work/cache-fixture/lists" PLAYWRIGHT_APT_ARCHIVES="$work/cache-fixture/archives"
unset GITHUB_JOB
export PLAYWRIGHT_APT_ATTEMPTS=1 PLAYWRIGHT_APT_RETRY_DELAY=0
# The package prefetch and the signed-index date are off or fixed unless a case asks, and curl is always the stand-in.
export PLAYWRIGHT_APT_PREFETCH=false PLAYWRIGHT_APT_ROTATE=false PLAYWRIGHT_APT_CODENAME=noble CURL_LOG="$work/curl.log" CURL_ARGS="$work/curl-args.log" CURL_FIXTURES="$work/curl-fixtures"

azure=$'http://azure.archive.ubuntu.com/ubuntu/\tpriority:1'
canonical=$'https://archive.ubuntu.com/ubuntu/\tpriority:2'
download='install -y --download-only --no-install-recommends libasound2t64 libgtk-4-1 xvfb'
cached='install -y --no-install-recommends --no-download libasound2t64 libgtk-4-1 xvfb'
verify='-o Acquire::ForceHash=sha256 --print-uris --download-only install -y --no-install-recommends libasound2t64 libgtk-4-1 xvfb'
failures=0

# reset <mirror list lines...>: a fresh apt directory and logs, and the stand-ins' defaults.
reset() {
  rm -rf "$work/cache-fixture"
  mkdir -p "$PLAYWRIGHT_APT_LISTS" "$PLAYWRIGHT_APT_ARCHIVES" "$HOME/.cache/playwright-apt-lists" "$HOME/.cache/playwright-apt-debs"
  echo image > "$PLAYWRIGHT_APT_LISTS/image_Packages"; echo image-signature > "$PLAYWRIGHT_APT_LISTS/image_InRelease"
  echo cached > "$HOME/.cache/playwright-apt-lists/cache_Packages"; echo cached-signature > "$HOME/.cache/playwright-apt-lists/cache_InRelease"
  printf 'valid deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"; : > "$PLAYWRIGHT_APT_ARCHIVES/downloaded.deb"; : > "$GITHUB_OUTPUT"
  rm -rf "$PLAYWRIGHT_APT_ETC"; mkdir -p "$PLAYWRIGHT_APT_ETC/sources.list.d"
  printf '%s\n' "$@" > "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"
  printf 'Types: deb\nURIs: mirror+file:/etc/apt/apt-mirrors.txt\n' > "$PLAYWRIGHT_APT_ETC/sources.list.d/ubuntu.sources"
  : > "$NPX_LOG"; : > "$APT_LOG"; : > "$SLEEP_LOG"; : > "$TIMEOUT_LOG"
  export NPX_OUT='sudo -- sh -c "apt-get update&& apt-get install -y --no-install-recommends libasound2t64 libgtk-4-1 xvfb"'
  unset APT_SLOW APT_UPDATE_SLOW APT_UPDATE_EXIT APT_DOWNLOAD_EXIT APT_GETS PLAYWRIGHT_APT_FORCE_FALLBACK CHECK_BROWSERS
  unset APT_ERROR APT_FAIL_COUNT APT_AZURE_ONLY APT_DOWNLOAD_TIMEOUTS APT_DOWNLOAD_SECONDS
  unset APT_DNS_EXIT APT_DNS_OUTPUT APT_UPDATE_ERROR APT_UPDATE_FAIL_COUNT
  unset DEBS_CACHE_HIT DEBS_CACHE_EXACT_HIT APT_CACHE_ERROR APT_CACHE_EXIT APT_TIMEOUT_EXIT PLAYWRIGHT_APT_UPDATE_BUDGET APT_EXPECT_INDEX APT_NEW_VERSION
  unset APT_VERIFY_ERROR APT_VERIFY_URI
  unset APT_CHAIN_SOURCE APT_TRUSTED_RELEASE APT_CHAIN_INSTALLED
  unset CURL_DOWN_HOSTS CURL_BAD_HOSTS CURL_FAIL_FIRST PLAYWRIGHT_APT_PREFETCH_BUDGET PLAYWRIGHT_APT_FETCH_TRIES PLAYWRIGHT_APT_DEADLINE
  export PLAYWRIGHT_APT_PREFETCH=false PLAYWRIGHT_APT_ROTATE=false; rm -rf "$CURL_FIXTURES"; mkdir -p "$CURL_FIXTURES"; : > "$CURL_LOG"; : > "$CURL_ARGS"
}
# check <name> <expected exit> <expected apt-get calls, one per line> [expected mirror list lines...]
# download.sh is given CHECK_BROWSERS, chromium and webkit unless set.
check() {
  local name="$1" want="$2" calls="$3" code=0 browsers; shift 3
  # A successful download must now also leave the dependencies installed, offline.
  if [ "$want" -eq 0 ] && [[ "$calls" == *"$download"* ]]; then calls+=$'\n'"$cached"; fi
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

# A robust download (S2, #4104). On main's logs every failing job restored an exact-key archives cache that failed verification against the signed indexes,
# then downloaded past the job's limit. So: the key carries the signed indexes' dates (a moved index is a MISS, not a failed verify), each package is fetched
# with a short bounded retry and a mirror fallback and checked against the signed hash, and the final error says what failed.
serve() { mkdir -p "$CURL_FIXTURES/$1$(dirname "$2")"; printf '%s' "$3" > "$CURL_FIXTURES/$1$2"; }
index_fixtures() { local pocket; for pocket in noble noble-updates noble-security; do serve "$1" "/ubuntu/dists/$pocket/InRelease" "Origin: Ubuntu"$'\n'"Date: $2"$'\n'; done; }
index_hash() {
  local code=0; : > "$GITHUB_OUTPUT"
  GITHUB_RUN_ID=77 bash "$here/download.sh" --cache-key chromium > "$work/out" 2>&1 || code=$?
  [ "$code" -eq 0 ] || { echo "exit-$code"; return 0; }
  sed -n 's/^index-hash=//p' "$GITHUB_OUTPUT"
}
# deb <file> <content>: the package on both mirrors; prints its plan line (the signed size and hash) in the shape real apt prints on the runner image, whose sources are
# mirror+file:/etc/apt/apt-mirrors.txt: the UNRESOLVED mirror-list URI, never an http one (a fake that printed http URIs hid a prefetch that fetched nothing on a real runner).
deb() {
  local hash; hash=$(printf '%s' "$2" | sha256sum); hash=${hash%% *}
  serve azure.archive.ubuntu.com "/ubuntu/pool/main/$1" "$2"; serve archive.ubuntu.com "/ubuntu/pool/main/$1" "$2"
  printf "'mirror+file:/etc/apt/apt-mirrors.txt/pool/main/%s' %s %s SHA256:%s" "$1" "$1" "${#2}" "$hash"
}
expect() { if eval "$2"; then echo "ok: $1"; else echo "FAIL: $1"; failures=$((failures + 1)); fi; }
dl() { DL_CODE=0; bash "$here/download.sh" chromium webkit > "$work/out" 2>&1 || DL_CODE=$?; }
curls() { grep -c -- "$1" "$CURL_LOG" || true; }
index_cases() {
  local first second third
  reset "$azure" "$canonical"; index_fixtures azure.archive.ubuntu.com 'Mon, 24 Nov 2026 03:00:00 UTC'
  first=$(index_hash); second=$(index_hash)
  expect "the cache key carries a hash of the signed indexes' dates, the same for the same dates" '[ "$first" = "$second" ] && [[ "$first" =~ ^[0-9a-f]{64}$ ]]'
  serve azure.archive.ubuntu.com /ubuntu/dists/noble-updates/InRelease $'Origin: Ubuntu\nDate: Tue, 25 Nov 2026 03:00:00 UTC\n'
  third=$(index_hash)
  expect "a moved index changes the key, so the old cache is a miss instead of a failed verification" '[ -n "$third" ] && [ "$third" != "$first" ]'
  reset "$azure" "$canonical"; index_fixtures archive.ubuntu.com 'Mon, 24 Nov 2026 03:00:00 UTC'; export CURL_DOWN_HOSTS=azure.archive.ubuntu.com
  expect "the key is the same whichever mirror answered, and a mirror that is down is skipped" '[ "$(index_hash)" = "$first" ]'
  reset "$azure" "$canonical"; export CURL_DOWN_HOSTS='azure.archive.ubuntu.com archive.ubuntu.com'
  expect "with no index readable the key is unique to the run, never a shared stale one" '[ "$(index_hash)" = unknown-77 ]'
}
prefetch_cases() {
  local a b calls
  reset "$azure" "$canonical"
  a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  calls=$(printf 'update\n%s\n%s' "$verify" "$download")
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b"
  check "every planned package is fetched and verified before apt's own download, which then finds them" 0 "$calls" "$azure" "$canonical"
  expect "the verified packages are in apt's archives" '[ "$(cat "$PLAYWRIGHT_APT_ARCHIVES/liba_1_amd64.deb")" = "package a" ] && [ "$(cat "$PLAYWRIGHT_APT_ARCHIVES/libb_1_amd64.deb")" = "package b" ]'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" CURL_FAIL_FIRST=2
  check "a package that times out twice is fetched on the third try, each try bounded" 0 "$calls" "$azure" "$canonical"
  expect "the retries were on the same mirror and backed off" '[ "$(curls "azure.archive.ubuntu.com/ubuntu/pool/main/liba")" -eq 3 ] && [ "$(curls "//archive.ubuntu.com")" -eq 0 ] && [ -s "$SLEEP_LOG" ]'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" CURL_DOWN_HOSTS=azure.archive.ubuntu.com
  check "a mirror that is down falls back to the next one, and the rest stay on the mirror that worked" 0 "$calls" "$azure" "$canonical"
  expect "Azure was tried (three times) only for the first package; Canonical served both" '[ "$(curls "azure.archive.ubuntu.com/ubuntu/pool/main/liba")" -eq 3 ] && [ "$(curls "azure.archive.ubuntu.com/ubuntu/pool/main/libb")" -eq 0 ] && [ "$(curls "//archive.ubuntu.com/ubuntu/pool/main/libb")" -eq 1 ]'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" CURL_BAD_HOSTS=azure.archive.ubuntu.com
  check "a mirror serving the wrong bytes fails the signed hash and the next mirror is used" 0 "$calls" "$azure" "$canonical"
  expect "only bytes that match the signed hash reach apt's archives" '[ "$(cat "$PLAYWRIGHT_APT_ARCHIVES/liba_1_amd64.deb")" = "package a" ]'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" CURL_DOWN_HOSTS='azure.archive.ubuntu.com archive.ubuntu.com'
  check "a package no mirror serves is left to apt's own bounded download, which still runs" 0 "$calls" "$azure" "$canonical"
  expect "and the log names it" 'grep -q "left to apt.*liba_1_amd64.deb" "$work/out"'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" PLAYWRIGHT_APT_PREFETCH_BUDGET=0
  check "a spent prefetch budget hands everything to apt without a single fetch" 0 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"
  expect "no package was fetched and the log says why" '[ "$(wc -l < "$CURL_LOG")" -eq 0 ] && grep -q budget "$work/out"'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" PLAYWRIGHT_APT_FORCE_FALLBACK=true
  dl
  expect "a forced fallback proof leaves downloading to apt, so its Get: lines stay the proof" '[ "$(wc -l < "$CURL_LOG")" -eq 0 ]'
}
cause_cases() {
  reset "$azure" "$canonical"; export APT_UPDATE_EXIT=100; dl
  expect "an update apt rejected says so, not a network failure" '[ "$DL_CODE" -eq 100 ] && grep -q "^::error::apt update failed (exit 100): .*not a network failure" "$work/out"'
  reset "$canonical"; export APT_DOWNLOAD_TIMEOUTS=99; dl
  expect "a download that timed out names network or timeout as the cause" '[ "$DL_CODE" -eq 124 ] && grep -q "^::error::apt download failed (exit 124): network or timeout" "$work/out"'
  reset "$canonical"; export APT_DOWNLOAD_EXIT=100; dl
  expect "a download apt rejected names a package or signature problem, not the network" '[ "$DL_CODE" -eq 100 ] && grep -q "^::error::apt download failed (exit 100): .*not a network failure" "$work/out"'
  reset "$canonical"; export DEBS_CACHE_HIT=true DEBS_CACHE_EXACT_HIT=true APT_VERIFY_ERROR='E: bad plan' APT_DOWNLOAD_TIMEOUTS=99; dl
  expect "a timeout after a cache that failed verification says both, so the log reads as it happened" '[ "$DL_CODE" -eq 124 ] && grep -q "network or timeout" "$work/out" && grep -q "restored cache had failed verification" "$work/out"'
}
# The stall facts from main run 45351659153 (pwa-e2e): a cold download from a regional mirror that STALLED mid-download (no data), three attempts each waiting out
# the full timeout (31 minutes), so the cache was never saved. So a stall is dead in seconds, attempts are short and many, and each attempt asks another mirror.
stall_cases() {
  local a b calls
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b"
  check "a fetch gives up on a stalled connection quickly: no data for a while is dead, whatever the total limit" 0 "$(printf 'update\n%s\n%s' "$verify" "$download")" "$azure" "$canonical"
  expect "every fetch carries a speed floor and a short stall window as well as the total limit" 'grep -q -- "--speed-limit=" "$CURL_ARGS" && grep -q -- "--speed-time=" "$CURL_ARGS" && grep -q -- "--max-time=" "$CURL_ARGS"'
  expect "apt's own downloads give up on a silent connection too (a short Acquire timeout, not the attempt limit)" 'grep -q "Acquire::http::Timeout \"30\"" "$PLAYWRIGHT_APT_ETC/apt.conf.d/99playwright-stall" && grep -q "Acquire::https::Timeout \"30\"" "$PLAYWRIGHT_APT_ETC/apt.conf.d/99playwright-stall"'
  reset "$canonical"; export PLAYWRIGHT_APT_ATTEMPTS=6 PLAYWRIGHT_APT_RETRY_DELAY=5 APT_DOWNLOAD_TIMEOUTS=99; dl
  expect "six attempts, each backed off by at most 30 s, so a stalled mirror is not waited on for half an hour" '[ "$DL_CODE" -eq 124 ] && [ "$(grep -c -- "--download-only" "$APT_LOG")" -eq 6 ] && [ "$(wc -l < "$SLEEP_LOG")" -eq 5 ] && ! awk "\$1 > 30 { bad = 1 } END { exit !bad }" "$SLEEP_LOG"'
  expect "the unset defaults are short attempts and more of them" 'grep -q "attempts=\${PLAYWRIGHT_APT_ATTEMPTS:-6}" "$here/download.sh" && ! grep -q "PLAYWRIGHT_APT_LIMIT:-600s" "$here/download.sh"'
  reset "$canonical" $'http://us-east-2.ec2.archive.ubuntu.com/ubuntu/\tpriority:3'
  export PLAYWRIGHT_APT_ROTATE=true PLAYWRIGHT_APT_ATTEMPTS=3 APT_DOWNLOAD_TIMEOUTS=1
  check "a download attempt that timed out is retried on the NEXT mirror, not the same one again" 0 "$(printf 'update\n%s\n%s' "$download" "$download")" $'http://us-east-2.ec2.archive.ubuntu.com/ubuntu/\tpriority:3' "$canonical"
}
# plan_line <uri> <file> <content>: a plan line for a package whose URI is exactly <uri>.
plan_line() { local hash; hash=$(printf '%s' "$3" | sha256sum); hash=${hash%% *}; printf "'%s' %s %s SHA256:%s" "$1" "$2" "${#3}" "$hash"; }
uri_cases() {
  local a calls
  calls=$(printf 'update\n%s\n%s' "$verify" "$download")
  reset "$azure" "$canonical"; serve archive.ubuntu.com /ubuntu/pool/main/libx_1_amd64.deb 'package x'
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$(plan_line 'cdrom:[Ubuntu]/pool/main/libx_1_amd64.deb' libx_1_amd64.deb 'package x')"$'\n'"$(plan_line 'file:/var/cache/libx_2_amd64.deb' libx_2_amd64.deb 'package x2')"$'\n'"$(plan_line 'mirror+copy:/x/libx_3_amd64.deb' libx_3_amd64.deb 'package x3')"
  check "a plan URI with a scheme curl cannot fetch is left to apt at once, with no fetch, no retry and no sleep" 0 "$calls" "$azure" "$canonical"
  expect "nothing was fetched, nothing slept, and the log names them" '[ "$(wc -l < "$CURL_LOG")" -eq 0 ] && [ ! -s "$SLEEP_LOG" ] && grep -q "left to apt.*libx_1_amd64.deb.*libx_2_amd64.deb.*libx_3_amd64.deb" "$work/out"'
  reset "$azure" "$canonical"; serve packages.microsoft.com /ubuntu/24.04/prod/pool/main/m/msx_1_amd64.deb 'package ms'
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$(plan_line 'https://packages.microsoft.com/ubuntu/24.04/prod/pool/main/m/msx_1_amd64.deb' msx_1_amd64.deb 'package ms')"
  check "a package from a non-Ubuntu source is fetched from its own URI only, never from the Ubuntu mirrors" 0 "$calls" "$azure" "$canonical"
  expect "only packages.microsoft.com was asked, and the package arrived" '[ "$(curls "packages.microsoft.com")" -eq 1 ] && [ "$(curls "ubuntu.com")" -eq 0 ] && [ "$(cat "$PLAYWRIGHT_APT_ARCHIVES/msx_1_amd64.deb")" = "package ms" ]'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a" CURL_DOWN_HOSTS=azure.archive.ubuntu.com
  check "a mirror+file URI is mapped onto the mirror list and rotates when a mirror is down, as a real runner needs" 0 "$calls" "$azure" "$canonical"
  expect "it was fetched, from Canonical after Azure failed, and the summary counts it" '[ "$(cat "$PLAYWRIGHT_APT_ARCHIVES/liba_1_amd64.deb")" = "package a" ] && grep -q "fetched 1 of 1 planned packages" "$work/out"'
}
# The worst case of the whole step (review P2 on #4410). Every wait is bounded, but they ADD UP across update, prefetch and apt's retries, so the step has one deadline:
# no new attempt starts after PLAYWRIGHT_APT_DEADLINE seconds, and the prefetch budget is at most half of what is left. The arithmetic, from the defaults themselves:
#   the step ends by  deadline + the longest single attempt that may still start (apt: its limit + 30 s kill grace; a fetch: --max-time + 10 s connect) + the plan call (60)
#   + the offline install (180), and that plus the time the job's own work needs (900: npm ci, browsers, tests) must fit the SMALLEST timeout-minutes of a job that calls the action.
deadline_cases() {
  local deadline limit fetch longest worst job_limit
  deadline=$(sed -n 's/^deadline=\${PLAYWRIGHT_APT_DEADLINE:-\([0-9]*\)}.*/\1/p' "$here/download.sh")
  limit=$(grep -o 'PLAYWRIGHT_APT_LIMIT:-[0-9]*s' "$here/download.sh" | grep -o '[0-9]*' | sort -n | tail -n 1)
  fetch=$(sed -n 's/^fetch_timeout=\${PLAYWRIGHT_APT_FETCH_TIMEOUT:-\([0-9]*\)}.*/\1/p' "$here/download.sh")
  job_limit=$(for f in ci.yml full.yml; do [ ! -f "$here/../../workflows/$f" ] || awk '
    /^  [[:alnum:]_-]+:[[:space:]]*$/ { t = "" }
    /^    timeout-minutes:/ { t = $2 }
    /uses: \.\/\.github\/actions\/playwright-system-packages/ && t != "" { print t * 60 }' "$here/../../workflows/$f"; done | sort -n | head -n 1)
  : "${job_limit:=2100}"
  longest=$((limit + 30)); [ $((fetch + 10)) -le "$longest" ] || longest=$((fetch + 10))
  worst=$((deadline + longest + 60 + 180))
  expect "the step's defaults are readable (deadline ${deadline:-?} s, longest apt limit ${limit:-?} s, fetch ${fetch:-?} s)" '[ -n "$deadline" ] && [ -n "$limit" ] && [ -n "$fetch" ]'
  expect "worst case $worst s (deadline $deadline + longest attempt $longest + plan 60 + install 180) plus 900 s for the job's own work fits its smallest cap ($job_limit s)" '[ "$((worst + 900))" -le "$job_limit" ]'
  expect "the header shows the arithmetic" 'grep -q "worst case" "$here/download.sh"'
  reset "$canonical"; export PLAYWRIGHT_APT_DEADLINE=0 PLAYWRIGHT_APT_ATTEMPTS=6 APT_DOWNLOAD_TIMEOUTS=99; dl
  expect "no new attempt starts once the deadline is spent, and the error says so" '[ "$DL_CODE" -eq 124 ] && [ "$(grep -c -- "--download-only" "$APT_LOG")" -eq 1 ] && grep -q "^::error::apt download failed (exit 124): network or timeout.*deadline" "$work/out"'
  reset "$azure" "$canonical"; a=$(deb liba_1_amd64.deb 'package a'); b=$(deb libb_1_amd64.deb 'package b')
  export PLAYWRIGHT_APT_PREFETCH=true APT_VERIFY_URI="$a"$'\n'"$b" PLAYWRIGHT_APT_DEADLINE=0
  dl
  expect "the prefetch never outlasts the deadline: with none left it fetches nothing" '[ "$(wc -l < "$CURL_LOG")" -eq 0 ] && grep -q budget "$work/out"'
}
robust_cases() { index_cases; prefetch_cases; uri_cases; cause_cases; stall_cases; deadline_cases; }
if [ "${1:-}" = --robust ]; then
  robust_cases
  [ "$failures" -eq 0 ] || { echo "::error::$failures of download.sh's robust checks failed"; exit 1; }
  exit 0
fi
# Cached-list cases guard isolation only; archive, tampered-Packages and live-* cases are the sensors.
cache_auth_cases() {
  local damage hash size calls want
  for damage in tampered-InRelease tampered-Packages missing-InRelease invalid-signature size-mismatch archive-size missing-archive weak-digest live-signature live-missing live-Packages live-size; do
    reset "$canonical"; export DEBS_CACHE_HIT=true PLAYWRIGHT_APT_LIMIT=10s
    export APT_CHAIN_SOURCE="$work/cache-fixture/repository" APT_TRUSTED_RELEASE="$work/cache-fixture/runner-trusted-InRelease"
    export APT_CHAIN_INSTALLED="$work/cache-fixture/installed.deb"
    mkdir -p "$APT_CHAIN_SOURCE"; printf 'valid deb\n' > "$APT_CHAIN_SOURCE/cached.deb"
    hash=$(sha256sum "$APT_CHAIN_SOURCE/cached.deb"); printf '%s 10\n' "${hash%% *}" > "$APT_CHAIN_SOURCE/Packages"
    hash=$(sha256sum "$APT_CHAIN_SOURCE/Packages"); hash=${hash%% *}; size=$(wc -c < "$APT_CHAIN_SOURCE/Packages")
    printf '%s %s Packages\nrunner-trusted-signature\n' "$hash" "$size" > "$APT_CHAIN_SOURCE/InRelease"
    cp "$APT_CHAIN_SOURCE/InRelease" "$APT_TRUSTED_RELEASE"
    cp "$APT_CHAIN_SOURCE/Packages" "$HOME/.cache/playwright-apt-lists/cache_Packages"
    cp "$APT_CHAIN_SOURCE/InRelease" "$HOME/.cache/playwright-apt-lists/cache_InRelease"
    calls=$(printf 'update\n%s\n%s' "$verify" "$cached"); want=0
    case "$damage" in
      tampered-InRelease) echo forged >> "$HOME/.cache/playwright-apt-lists/cache_InRelease" ;;
      tampered-Packages)
        printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"
        hash=$(sha256sum "$HOME/.cache/playwright-apt-debs/cached.deb")
        printf '%s 10\n' "${hash%% *}" > "$HOME/.cache/playwright-apt-lists/cache_Packages"
        calls=$(printf 'update\n%s\n%s' "$verify" "$download") ;;
      missing-InRelease) rm "$HOME/.cache/playwright-apt-lists/cache_InRelease" ;;
      invalid-signature) echo unknown-key > "$HOME/.cache/playwright-apt-lists/cache_InRelease" ;;
      size-mismatch) sed -i 's/ 10$/ 11/' "$HOME/.cache/playwright-apt-lists/cache_Packages" ;;
      archive-size | weak-digest)
        if [ "$damage" = archive-size ]; then echo short > "$HOME/.cache/playwright-apt-debs/cached.deb"
        else export APT_VERIFY_URI="'https://archive.ubuntu.com/cached.deb' cached.deb 10 MD5Sum:1234"; fi
        calls=$(printf 'update\n%s\n%s' "$verify" "$download") ;;
      missing-archive)
        rm "$HOME/.cache/playwright-apt-debs/cached.deb"; calls=$(printf 'update\n%s' "$download") ;;
      live-signature) echo forged >> "$APT_CHAIN_SOURCE/InRelease" ;;
      live-missing) rm "$APT_CHAIN_SOURCE/InRelease" ;;
      live-Packages) printf '%064d 10\n' 0 > "$APT_CHAIN_SOURCE/Packages" ;;
      live-size)
        sed -i "1s/ $size / $((size + 1)) /" "$APT_CHAIN_SOURCE/InRelease"
        cp "$APT_CHAIN_SOURCE/InRelease" "$APT_TRUSTED_RELEASE" ;;
    esac
    if [[ "$damage" == live-* ]]; then want=100; calls=update; fi
    check "$damage cannot promote unauthenticated cached metadata or archives" "$want" "$calls" "$canonical"
    if [ -e "$PLAYWRIGHT_APT_LISTS/cache_Packages" ] ||
      { [ "$want" -eq 0 ] && ! cmp -s "$APT_CHAIN_INSTALLED" "$APT_CHAIN_SOURCE/cached.deb"; } ||
      { [ "$want" -ne 0 ] && [ -e "$APT_CHAIN_INSTALLED" ]; }; then
      echo "FAIL: $damage consumed cached metadata or installed an unauthenticated archive"
      failures=$((failures + 1))
    fi
  done
}
cache_ref_case() {
  local code=0 expected; reset "$canonical"
  GITHUB_REF='refs/heads/topic,comma' bash "$here/download.sh" --cache-key chromium > "$work/out" 2>&1 || code=$?
  expected=$(printf '%s' 'refs/heads/topic,comma' | sha256sum)
  if [ "$code" -eq 0 ] && [ "$(sed -n 's/^ref-hash=//p' "$GITHUB_OUTPUT")" = "${expected%% *}" ] &&
    grep -q 'key:.*packages-hash.*ref-hash' "$here/action.yml" && ! grep -q 'github.ref' "$here/action.yml"; then
    echo "ok: a comma-bearing ref is hashed before entering the cache key"
  else echo "FAIL: a raw ref can break the cache key"; failures=$((failures + 1)); fi
}
# S2 then S4 share one runner (ci.yml spikes-linux), and apt trusts a size-matching archive unhashed.
cache_copy_cases() {
  reset "$canonical"; export DEBS_CACHE_HIT=true APT_VERIFY_URI='Reading package lists...'
  check "a plan naming no archives copies no cached deb into apt's archives" 0 "$(printf 'update\n%s\n%s' "$verify" "$cached")" "$canonical"
  [ ! -e "$PLAYWRIGHT_APT_ARCHIVES/cached.deb" ] || { echo "FAIL: an unplanned cached deb entered apt's archives"; failures=$((failures + 1)); }
  reset "$canonical"; export DEBS_CACHE_HIT=true; printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/libgtk-4-1_9_amd64.deb"
  check "only planned, verified cached debs enter apt's archives" 0 "$(printf 'update\n%s\n%s' "$verify" "$cached")" "$canonical"
  [ ! -e "$PLAYWRIGHT_APT_ARCHIVES/libgtk-4-1_9_amd64.deb" ] || { echo "FAIL: an unverified cached deb entered apt's archives"; failures=$((failures + 1)); }
  printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"; : > "$APT_LOG"
  check "a later rejected cache leaves no unverified deb in apt's archives" 0 "$(printf 'update\n%s\n%s' "$verify" "$download")" "$canonical"
  [ ! -e "$PLAYWRIGHT_APT_ARCHIVES/libgtk-4-1_9_amd64.deb" ] && [ "$(cat "$PLAYWRIGHT_APT_ARCHIVES/cached.deb")" = 'valid deb' ] ||
    { echo "FAIL: a rejected cache left an unverified deb in apt's archives"; failures=$((failures + 1)); }
}
if [ "${1:-}" = --cache-auth ]; then
  cache_auth_cases
  cache_ref_case
  cache_copy_cases
  echo "cache-auth: $failures failure(s)"
  [ "$failures" -eq 0 ]; exit
fi

followup_cases() {
  reset "$azure" "$canonical"; export APT_DOWNLOAD_SECONDS=120
  unset PLAYWRIGHT_APT_LIMIT PLAYWRIGHT_APT_ATTEMPTS PLAYWRIGHT_APT_RETRY_DELAY
  check "Azure keeps a two-minute download within its default 180-second escape bound" \
    0 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"
  grep -qx -- "--kill-after=30s 180s apt-get $download" "$TIMEOUT_LOG" \
    || { echo "FAIL: Azure's production download bound is not 180 seconds"; failures=$((failures + 1)); }

  reset "$canonical"; export DEBS_CACHE_HIT=true DEBS_CACHE_EXACT_HIT=true APT_GETS='Get:1 noisy archive progress'
  printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"
  check "an invalid exact-key hit redownloads quietly after live verification" \
    0 "$(printf 'update\n%s\n-qq %s' "$verify" "$download")" "$canonical"
  ! grep -q '^Get:' "$work/out" && grep -q 'immutable exact-key cache' "$work/out" &&
    grep -qF 'DEBS_CACHE_EXACT_HIT: ${{ steps.debs.outputs.cache-hit }}' "$here/action.yml" &&
    grep -q 'cache-ready=true' "$GITHUB_OUTPUT" \
    || { echo "FAIL: invalid exact-key recovery lost its quiet output, explanation or replacement archives"; failures=$((failures + 1)); }

  reset "$canonical"; export DEBS_CACHE_HIT=true DEBS_CACHE_EXACT_HIT=false APT_GETS='Get:1 normal archive progress'
  printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"
  check "an invalid prefix hit retains normal authenticated download output" \
    0 "$(printf 'update\n%s\n%s' "$verify" "$download")" "$canonical"
  grep -q '^Get:1 normal archive progress' "$work/out" \
    || { echo "FAIL: prefix-hit downloads were silenced"; failures=$((failures + 1)); }

  reset "$canonical"; export DEBS_CACHE_HIT=true DEBS_CACHE_EXACT_HIT=true APT_ERROR='E: Hash Sum mismatch'
  printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"
  check "quiet exact-key recovery still reports fatal download errors without installing or saving" \
    100 "$(printf 'update\n%s\n-qq %s' "$verify" "$download")" "$canonical"
  grep -q '^E: Hash Sum mismatch' "$work/out" && grep -q '^::error::apt download failed' "$work/out" &&
    [ ! -s "$GITHUB_OUTPUT" ] \
    || { echo "FAIL: quiet download recovery hid an error or authorized saving"; failures=$((failures + 1)); }
}
if [ "${1:-}" = --followups ]; then
  followup_cases
  echo "followups: $failures failure(s)"
  [ "$failures" -eq 0 ]; exit
fi

if [ "${1:-}" != --cold-cache ]; then
reset "$azure" "$canonical"
check "a download that finishes in time fetches the packages Playwright names, from the mirror list as it stands" \
  0 "$(printf 'update\n%s' "$download")" "$azure" "$canonical"
grep -qxF 'playwright install-deps --dry-run chromium webkit' "$NPX_LOG" \
  || { echo "FAIL: Playwright was asked about $(cat "$NPX_LOG"), not the browsers given"; failures=$((failures + 1)); }
[ ! -e "$HOME/.cache/playwright-apt-lists/image_Packages" ] &&
  [ -f "$HOME/.cache/playwright-apt-debs/downloaded.deb" ] && grep -qx 'cache-ready=true' "$GITHUB_OUTPUT" \
  || { echo "FAIL: a successful cold update did not stage archives only for saving"; failures=$((failures + 1)); }

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
[ ! -s "$GITHUB_OUTPUT" ] && [ ! -e "$HOME/.cache/playwright-apt-lists/image_Packages" ] \
  || { echo "FAIL: a failed cold update staged or authorized a cache save"; failures=$((failures + 1)); }

reset "$azure" "$canonical"; export APT_UPDATE_SLOW=1
check "an update that times out switches to Canonical before its next update and download" \
  0 "$(printf 'update\nupdate\n%s' "$download")" "$canonical"

reset "$azure" "$canonical"; export APT_UPDATE_SLOW=ignore
check "an update killed after ignoring TERM switches to Canonical as a timeout" \
  0 "$(printf 'update\nupdate\n%s' "$download")" "$canonical"
reset "$azure" "$canonical"; export APT_DNS_EXIT=1 APT_DNS_OUTPUT='getent lookup failed'
check "a failed Azure name probe switches before the first update" \
  0 "$(printf 'update\n%s' "$download")" "$canonical"
grep -q 'Azure DNS probe failed (exit 1)' "$work/out" \
  || { echo "FAIL: the probe exit code was not logged"; failures=$((failures + 1)); }
grep -qF 'getent lookup failed' "$work/out" \
  || { echo "FAIL: the probe output was not logged"; failures=$((failures + 1)); }
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

# Network downloads use the default three attempts on Azure (six on Canonical) and 5/20 second backoff; sleep is recorded,
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
check "Canonical connection failures exhaust six attempts then fail loudly" \
  100 "$(printf 'update\n%s\n%s\n%s\n%s\n%s\n%s' "$download" "$download" "$download" "$download" "$download" "$download")" "$canonical"
grep -q '^::error::' "$work/out" \
  || { echo "FAIL: exhausted downloads did not emit an error"; failures=$((failures + 1)); }
for attempt in 1 2 3 4 5 6; do
  grep -q "attempt $attempt of 6" "$work/out" \
    || { echo "FAIL: missing download attempt $attempt log"; failures=$((failures + 1)); }
done
reset "$azure" "$canonical"; export APT_UPDATE_ERROR="W: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease Temporary failure resolving 'azure.archive.ubuntu.com'" APT_UPDATE_EXIT=0
check "an update reporting Azure DNS warnings despite exit zero still switches immediately" \
  100 "$(printf 'update\nupdate')" "$canonical"
reset "$azure" "$canonical"; export APT_UPDATE_ERROR='W: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease 503 Service Unavailable'
check "mirror-file server errors retry on Azure and Canonical even without a hostname" \
  100 "$(printf 'update\nupdate\nupdate\nupdate\nupdate\nupdate\nupdate\nupdate')" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'15\n30\n60\n15\n30\n60' ] \
  || { echo "FAIL: both update phases did not use 15/30/60 second backoff"; failures=$((failures + 1)); }
reset "$canonical"; export APT_UPDATE_ERROR='W: Failed to fetch mirror+file:/etc/apt/apt-mirrors.txt/dists/noble/InRelease 503 Service Unavailable'
check "a new invocation on a Canonical-only list retries update failures three times after the initial attempt" \
  100 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'15\n30\n60' ] \
  || { echo "FAIL: Canonical-only update did not use 15/30/60 second backoff"; failures=$((failures + 1)); }
for diagnostic in '429  Too Many Requests' '503 Service Unavailable' 'Connection failed'; do
  reset "$azure" "$canonical"; export APT_UPDATE_EXIT=0 APT_UPDATE_FAIL_COUNT=3
  export APT_UPDATE_ERROR="W: Failed to fetch https://packages.microsoft.com/ubuntu/24.04/prod/dists/noble/InRelease  $diagnostic"
  check "a Microsoft update warning ($diagnostic) recovers on attempt four without switching Ubuntu mirrors" \
    0 "$(printf 'update\nupdate\nupdate\nupdate\n%s' "$download")" "$azure" "$canonical"
  [ "$(cat "$SLEEP_LOG")" = $'15\n30\n60' ] \
    || { echo "FAIL: transient update recovery did not use 15/30/60 second backoff"; failures=$((failures + 1)); }
done
for hit in false true; do
  reset "$azure" "$canonical"; export DEBS_CACHE_HIT="$hit" PLAYWRIGHT_APT_FORCE_FALLBACK=true
  export APT_UPDATE_ERROR='E: Failed to fetch https://packages.microsoft.com/ubuntu/24.04/prod/dists/noble/InRelease  429  Too Many Requests'
  check "exhausted Microsoft update throttling fails closed with cache hit $hit" \
    100 "$(printf 'update\nupdate\nupdate\nupdate')" "$azure" "$canonical"
  [ "$(cat "$SLEEP_LOG")" = $'15\n30\n60' ] && [ ! -s "$GITHUB_OUTPUT" ] &&
    grep -q '^::error::apt update failed (exit 100)' "$work/out" \
    || { echo "FAIL: exhausted throttling was not bounded and fatal without a cache save"; failures=$((failures + 1)); }
done
reset "$canonical"; export DEBS_CACHE_HIT=true PLAYWRIGHT_APT_FORCE_FALLBACK=true
export APT_UPDATE_ERROR='E: Failed to fetch https://archive.ubuntu.com/ubuntu/dists/noble/InRelease 429 Too Many Requests'
check "exhausted Ubuntu update throttling also fails closed despite cached debs" \
  100 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
for fatal in 'Hash Sum mismatch' 'The following signatures could not be verified' '404 Not Found' 'Could not get lock /var/lib/apt/lists/lock'; do
  reset "$azure" "$canonical"; export DEBS_CACHE_HIT=true PLAYWRIGHT_APT_FORCE_FALLBACK=true
  export APT_UPDATE_ERROR=$'E: Failed to fetch https://packages.microsoft.com/ubuntu/24.04/prod/dists/noble/InRelease 429 Too Many Requests\nE: '"$fatal"
  check "Microsoft throttling mixed with $fatal fails immediately" 100 update "$azure" "$canonical"
  [ ! -s "$SLEEP_LOG" ] || { echo "FAIL: mixed fatal update errors were retried"; failures=$((failures + 1)); }
done
reset "$canonical"; export APT_UPDATE_ERROR='E: Failed to fetch https://packages.microsoft.com.evil.invalid/repo/InRelease 429 Too Many Requests'
check "an unrecognized update host is not admitted for throttling retries" 100 update "$canonical"
reset "$canonical"; export APT_ERROR='E: Failed to fetch https://packages.microsoft.com/ubuntu/pool/pkg.deb 429 Too Many Requests'
check "update retry classification does not broaden download classification" \
  100 "$(printf 'update\n%s' "$download")" "$canonical"
for transition in completed timeout completed-after-timeout timeout-with-diagnostics; do
  if (
    source <(sed -n '/^network_error()/,/^leave_azure()/p' "$here/download.sh" | sed '$d')
    update_deadline=0; attempts=3; delay=0; left_azure=false; network_host=azure.archive.ubuntu.com
    deadline=600; deadline_hit=false   # retry() reads the step's deadline; this subshell sources only its functions
    unset PLAYWRIGHT_APT_ATTEMPTS
    export PLAYWRIGHT_APT_RETRY_DELAY=0
    sleep() { :; }
    : > "$work/transition-attempts"
    fake_update() {
      echo "$n" >> "$work/transition-attempts"
      if [ "$n" -eq 1 ]; then
        echo 'W: Failed to fetch https://packages.microsoft.com/repo 429 Too Many Requests'
        return 100
      fi
      if [ "$transition" = timeout ] || { [ "$transition" = completed-after-timeout ] && [ "$n" -eq 2 ]; }; then
        return 124
      fi
      echo 'W: Failed to fetch http://azure.archive.ubuntu.com/repo Could not resolve azure.archive.ubuntu.com'
      [ "$transition" != timeout-with-diagnostics ] || return 124
      return 100
    }
    rc=0; retry update fake_update > "$work/transition-out" 2>&1 || rc=$?
    want_rc=100; want_fallback=true; want_attempt=2
    case "$transition" in
      completed-after-timeout) want_attempt=3 ;;
      timeout*) want_rc=124; want_fallback=false; want_attempt=4 ;;
    esac
    [ "$rc" -eq "$want_rc" ] && [ "$network_failed" = "$want_fallback" ] &&
      [ "$(tail -n 1 "$work/transition-attempts")" -eq "$want_attempt" ]
  ); then
    echo "ok: Microsoft throttling followed by $transition preserves the correct Azure fallback"
  else
    echo "FAIL: Microsoft throttling followed by $transition left the wrong retry or fallback state"
    cat "$work/transition-out"; failures=$((failures + 1))
  fi
done
reset "$canonical"; export APT_ERROR='E: Failed to fetch https://archive.ubuntu.com/ubuntu/pool/pkg.deb Connection failed' APT_FAIL_COUNT=2
check "a new invocation on a Canonical-only list recovers on its third download attempt" \
  0 "$(printf 'update\n%s\n%s\n%s' "$download" "$download" "$download")" "$canonical"
[ "$(cat "$SLEEP_LOG")" = $'5\n20' ] \
  || { echo "FAIL: Canonical-only download did not use 5/20 second backoff"; failures=$((failures + 1)); }
reset "$azure" "$canonical"; export DEBS_CACHE_HIT=true APT_EXPECT_INDEX=image APT_DOWNLOAD_EXIT=100
check "a restored archive cache verifies hashes against live indexes then installs without downloads" \
  0 "$(printf 'update\n%s\n%s' "$verify" "$cached")" "$azure" "$canonical"
[ "$(cat "$HOME/.cache/playwright-apt-lists/cache_Packages")" = cached ] && [ ! -s "$GITHUB_OUTPUT" ] \
  || { echo "FAIL: a warm install replaced the cached snapshot or authorized saving"; failures=$((failures + 1)); }
[ ! -e "$PLAYWRIGHT_APT_LISTS/cache_InRelease" ] \
  || { echo "FAIL: a cache hit promoted unverified metadata"; failures=$((failures + 1)); }
reset "$canonical"; export DEBS_CACHE_HIT=true
printf 'evil! deb\n' > "$HOME/.cache/playwright-apt-debs/cached.deb"
check "a corrupt cached deb is discarded before authenticated network fallback" \
  0 "$(printf 'update\n%s\n%s' "$verify" "$download")" "$canonical"
for uri in "'https://archive.ubuntu.com/cached.deb' missing.deb 10 SHA256:$(printf '0%.0s' {1..64})" \
  "'https://archive.ubuntu.com/cached.deb' cached.deb 10 MD5Sum:1234"; do
  reset "$canonical"; export DEBS_CACHE_HIT=true APT_VERIFY_URI="$uri"
  check "a missing cached archive or weak digest discards the cache before downloading" \
    0 "$(printf 'update\n%s\n%s' "$verify" "$download")" "$canonical"
done
reset "$canonical"; export DEBS_CACHE_HIT=true
export APT_VERIFY_ERROR='E: The following signatures could not be verified'
check "an apt planning error discards the cache before the normal authenticated download" \
  0 "$(printf 'update\n%s\n%s' "$verify" "$download")" "$canonical"
reset "$azure" "$canonical"; export DEBS_CACHE_HIT=true APT_CACHE_EXIT=100 APT_CACHE_ERROR='E: Hash Sum mismatch'
check "a cached install failure fails without network fallback" \
  100 "$(printf 'update\n%s\n%s' "$verify" "$cached")" "$azure" "$canonical"
reset "$azure" "$canonical"; export DEBS_CACHE_HIT=true PLAYWRIGHT_APT_FORCE_FALLBACK=true CHECK_BROWSERS=chromium
check "the forced proof downloads despite a cache hit" \
  0 "$(printf 'update\n%s' "$download")" "$canonical"
fi
reset "$canonical"; export APT_TIMEOUT_EXIT=124; unset PLAYWRIGHT_APT_LIMIT PLAYWRIGHT_APT_ATTEMPTS PLAYWRIGHT_APT_RETRY_DELAY
check "Canonical update timeouts use four sixty-second attempts then fail loudly" \
  124 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
[ "$(grep -c -- '^--kill-after=30s 60s apt-get update$' "$TIMEOUT_LOG")" = 4 ] \
  || { echo "FAIL: all four update timeout attempts were not bounded at 60 seconds"; failures=$((failures + 1)); }
[ "$(cat "$SLEEP_LOG")" = $'15\n30\n60' ] \
  || { echo "FAIL: update timeout retries did not use 15/30/60 second backoff"; failures=$((failures + 1)); }
export PLAYWRIGHT_APT_LIMIT=1s
for hit in false true; do
  reset "$canonical"; export DEBS_CACHE_HIT="$hit" APT_DOWNLOAD_TIMEOUTS=2 APT_DOWNLOAD_SECONDS=120
  [ "$hit" != true ] || rm "$HOME/.cache/playwright-apt-debs/cached.deb"
  unset PLAYWRIGHT_APT_LIMIT
  check "an incomplete cache ($hit) finishes a two-minute cold download on Canonical after two timeouts" \
    0 "$(printf 'update\n%s\n%s\n%s' "$download" "$download" "$download")" "$canonical"
  [ "$(grep -c -- '^--kill-after=30s 180s apt-get install .*--download-only' "$TIMEOUT_LOG")" = 3 ] &&
    [ "$(cat "$SLEEP_LOG")" = $'5\n20' ] \
    || { echo "FAIL: download retries inherited the update limit or lost bounded backoff"; failures=$((failures + 1)); }
  update_limit=60s
  grep -q " $update_limit apt-get update$" "$TIMEOUT_LOG" \
    || { echo "FAIL: the download limit changed the update's own bound"; failures=$((failures + 1)); }
done
reset "$canonical"; export DEBS_CACHE_HIT=true APT_DOWNLOAD_TIMEOUTS=6
rm "$HOME/.cache/playwright-apt-debs/cached.deb"
unset PLAYWRIGHT_APT_LIMIT
check "six download timeouts remain fatal on an incomplete cache without installing or saving" \
  124 "$(printf 'update\n%s\n%s\n%s\n%s\n%s\n%s' "$download" "$download" "$download" "$download" "$download" "$download")" "$canonical"
grep -q '^::error::apt download failed (exit 124)' "$work/out" && [ ! -s "$GITHUB_OUTPUT" ] \
  || { echo "FAIL: exhausted downloads did not fail loudly without authorizing a cache save"; failures=$((failures + 1)); }
export PLAYWRIGHT_APT_LIMIT=1s
if [ "${1:-}" != --cold-cache ]; then
reset "$azure" "$canonical"; export DEBS_CACHE_HIT=true APT_DNS_EXIT=1
export APT_UPDATE_ERROR="E: Failed to fetch https://archive.ubuntu.com/ubuntu/dists/noble/InRelease Temporary failure resolving 'archive.ubuntu.com'"
check "a cache hit cannot bypass broken update DNS" \
  100 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
[ ! -e "$PLAYWRIGHT_APT_LISTS/cache_InRelease" ] && [ ! -s "$GITHUB_OUTPUT" ] \
  || { echo "FAIL: failed update promoted cached metadata or authorized saving"; failures=$((failures + 1)); }
reset "$canonical"; export DEBS_CACHE_HIT=true APT_UPDATE_ERROR='E: Failed to fetch https://archive.ubuntu.com/ubuntu/dists/noble/InRelease Connection failed'
rm "$HOME/.cache/playwright-apt-lists/cache_Packages" "$HOME/.cache/playwright-apt-lists/cache_InRelease"
check "a debs hit without cached lists still cannot mask a failed update" 100 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
[ ! -s "$GITHUB_OUTPUT" ] || { echo "FAIL: an incomplete hit authorized saving after a failed update"; failures=$((failures + 1)); }
reset "$azure" "$canonical"; export APT_DNS_EXIT=1
export APT_UPDATE_ERROR="E: Failed to fetch https://archive.ubuntu.com/ubuntu/dists/noble/InRelease Temporary failure resolving 'archive.ubuntu.com'"
check "without a cache hit exhausted update DNS failures remain fatal" \
  100 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
reset "$canonical"; export DEBS_CACHE_HIT=true APT_TIMEOUT_EXIT=124
check "a cache hit cannot bypass exhausted update timeouts" \
  124 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
export PLAYWRIGHT_APT_LIMIT=1s
for fatal in 'Hash Sum mismatch' 'The following signatures could not be verified' 'Unable to locate package missing' 'Could not get lock /var/lib/apt/lists/lock'; do
  reset "$canonical"; export DEBS_CACHE_HIT=true PLAYWRIGHT_APT_FORCE_FALLBACK=true
  export APT_UPDATE_ERROR=$'E: Failed to fetch https://archive.ubuntu.com/ubuntu/dists/noble/InRelease Connection failed\nE: '"$fatal"
  check "a cache hit never masks mixed update errors ($fatal)" 100 update "$canonical"
done
reset "$canonical"; export DEBS_CACHE_HIT=true APT_UPDATE_EXIT=0 PLAYWRIGHT_APT_FORCE_FALLBACK=true
export APT_UPDATE_ERROR='W: GPG error: https://archive.ubuntu.com noble InRelease: The following signatures could not be verified'
check "a cache hit cannot ignore an exit-zero signature warning" 100 update "$canonical"
reset "$canonical"; export DEBS_CACHE_HIT=true APT_CACHE_EXIT=100 APT_CACHE_ERROR='E: Unable to fetch some archives'
check "offline install failure on a hit is fatal without network fallback" \
  100 "$(printf 'update\n%s\n%s' "$verify" "$cached")" "$canonical"
fi
reset "$azure" "$canonical"; export APT_CACHE_EXIT=100 APT_CACHE_ERROR='E: Sub-process /usr/bin/dpkg returned an error code (1)'
check "an offline install failure after downloading packages fails the step" \
  100 "$(printf 'update\n%s\n%s' "$download" "$cached")" "$azure" "$canonical"
if grep -qx 'cache-ready=true' "$GITHUB_OUTPUT" && grep -qF 'value: ${{ steps.packages.outputs.cache-ready }}' "$here/action.yml"; then
  echo "ok: successfully downloaded archives are reported ready after a fatal install failure, for the save action"
else echo "FAIL: a later install failure prevented saving downloaded archives"; failures=$((failures + 1)); fi
if [ "${1:-}" != --cold-cache ]; then
reset "$azure" "$canonical"; export APT_TIMEOUT_EXIT=124
export APT_UPDATE_ERROR=$'E: Failed to fetch http://azure.archive.ubuntu.com/ubuntu/dists/noble/InRelease Connection failed\nE: Hash Sum mismatch'
check "a timeout with mixed integrity errors never retries or switches mirrors" 124 "update" "$azure" "$canonical"
reset "$azure" "$canonical"; export DEBS_CACHE_HIT=true PLAYWRIGHT_APT_FORCE_FALLBACK=true APT_DNS_EXIT=1
export APT_UPDATE_ERROR="E: Failed to fetch https://archive.ubuntu.com/ubuntu/dists/noble/InRelease Temporary failure resolving 'archive.ubuntu.com'"
check "a forced proof cannot use a cache hit to ignore update DNS failures" \
  100 "$(printf 'update\nupdate\nupdate\nupdate')" "$canonical"
! grep -q 'npx playwright install --with-deps' "$here/../../workflows/ci.yml" \
  || { echo "FAIL: a downstream browser install would run apt update again"; failures=$((failures + 1)); }
[ "$(cat "$here/action.yml" "$here/../playwright-system-packages-save/action.yml" | grep -c 'path: ~/.cache/playwright-apt-debs$')" = 2 ] &&
  ! grep -q 'actions/cache/save' "$here/action.yml" &&
  grep -q 'actions/cache/save' "$here/../playwright-system-packages-save/action.yml" &&
  ! grep -q 'playwright-apt-lists' "$here/action.yml" &&
  grep -q 'key:.*packages-hash.*ref-hash' "$here/action.yml" &&
  grep -q 'restore-keys:' "$here/action.yml" &&
  { ! grep -q 'playwright-system-packages-save' "$here/../../workflows/ci.yml" || grep -qF "if: always() && steps.system-packages.outputs.cache-ready == 'true'" "$here/../../workflows/ci.yml"; } \
  || { echo "FAIL: versioned archives-only caches must restore and save after successful cold runs"; failures=$((failures + 1)); }
awk '
  /^  [[:alnum:]_-]+:/ { actions=0 }
  /uses: \.\/\.github\/actions\/playwright-system-packages$/ { actions++ }
  /npx playwright install / { if (actions-- <= 0) bad=1; installs++ }
  END { exit bad || installs != 4 }
' "$here/../../workflows/ci.yml" "$here/../../workflows/full.yml" \
  || { echo "FAIL: each of the four browser installs must follow its system-package action"; failures=$((failures + 1)); }

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
# The package prefetch and the signed-index date are off or fixed unless a case asks, and curl is always the stand-in.
export PLAYWRIGHT_APT_PREFETCH=false PLAYWRIGHT_APT_ROTATE=false PLAYWRIGHT_APT_CODENAME=noble CURL_LOG="$work/curl.log" CURL_ARGS="$work/curl-args.log" CURL_FIXTURES="$work/curl-fixtures"

# force-apt-fallback (ci.yml's pwa-ui on a run by hand): APT_SLOW stays set, so a download tried
# before leaving the Azure mirror would time out and show up as a second call.
from_canonical=$'Get:1 https://archive.ubuntu.com/ubuntu noble/main amd64 libgtk-4-1 amd64 4.14.2 [3170 kB]\nGet:2 https://archive.ubuntu.com/ubuntu noble/main amd64 xvfb amd64 2:21.1.12 [877 kB]'
from_azure='Get:3 http://azure.archive.ubuntu.com/ubuntu noble/main amd64 libasound2t64 amd64 1.2.11 [390 kB]'

reset "$azure" "$canonical"; export PLAYWRIGHT_APT_FORCE_FALLBACK=true APT_SLOW=1 APT_GETS="$from_canonical"
check "a forced fallback leaves the Azure mirror before downloading anything, without waiting on it" \
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

reset "$azure" "$canonical"
code=0; bash "$here/download.sh" --cache-key chromium > "$work/out" 2>&1 || code=$?
first_key=$(sed -n 's/^packages-hash=//p' "$GITHUB_OUTPUT")
export NPX_OUT='sudo -- sh -c "apt-get install -y --no-install-recommends libgtk-4-1 xvfb"'
: > "$GITHUB_OUTPUT"
bash "$here/download.sh" --cache-key chromium >> "$work/out" 2>&1 || code=$?
second_key=$(sed -n 's/^packages-hash=//p' "$GITHUB_OUTPUT")
expected=$(printf '%s\n' libasound2t64 libgtk-4-1 xvfb | sha256sum); expected=${expected%% *}
if [ "$code" -eq 0 ] && [ "$first_key" = "$expected" ] && [ "$first_key" != "$second_key" ] &&
  [ -n "$second_key" ] && [ ! -s "$APT_LOG" ] && [ ! -s "$TIMEOUT_LOG" ]; then
  echo "ok: cache identity hashes the exact Playwright package list without apt or DNS"
else
  echo "FAIL: cache identity did not hash the exact package list without network"; failures=$((failures + 1))
fi

cache_auth_cases
cache_ref_case
cache_copy_cases
followup_cases
robust_cases
fi
if [ "$failures" -ne 0 ]; then
  echo "::error::$failures of download.sh's checks failed"; exit 1
fi
