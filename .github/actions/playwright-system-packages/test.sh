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
# apt-get records each call. A download is slow (it outlasts the limit) while the mirror list
# still names the Azure mirror and APT_SLOW is set, so only leaving that mirror ends it.
cat > "$work/bin/apt-get" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$APT_LOG"
case "$*" in
  update) exit "${APT_UPDATE_EXIT:-0}" ;;
  *--download-only*)
    if [ -n "${APT_SLOW:-}" ] && grep -q 'azure\.archive\.ubuntu\.com' "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"; then
      exec sleep 30
    fi
    exit "${APT_DOWNLOAD_EXIT:-0}" ;;
esac
exit 99
EOF
chmod +x "$work/bin/npx" "$work/bin/sudo" "$work/bin/apt-get"
export PATH="$work/bin:$PATH" NPX_LOG="$work/npx.log" APT_LOG="$work/apt.log"
export PLAYWRIGHT_APT_ETC="$work/apt" PLAYWRIGHT_APT_LIMIT=1s
unset GITHUB_JOB

azure=$'http://azure.archive.ubuntu.com/ubuntu/\tpriority:1'
canonical=$'https://archive.ubuntu.com/ubuntu/\tpriority:2'
download='install -y --download-only --no-install-recommends libasound2t64 libgtk-4-1 xvfb'
failures=0

# reset <mirror list lines...>: a fresh apt directory and logs, and the stand-ins' defaults.
reset() {
  rm -rf "$PLAYWRIGHT_APT_ETC"; mkdir -p "$PLAYWRIGHT_APT_ETC/sources.list.d"
  printf '%s\n' "$@" > "$PLAYWRIGHT_APT_ETC/apt-mirrors.txt"
  printf 'Types: deb\nURIs: mirror+file:/etc/apt/apt-mirrors.txt\n' > "$PLAYWRIGHT_APT_ETC/sources.list.d/ubuntu.sources"
  : > "$NPX_LOG"; : > "$APT_LOG"
  export NPX_OUT='sudo -- sh -c "apt-get update&& apt-get install -y --no-install-recommends libasound2t64 libgtk-4-1 xvfb"'
  unset APT_SLOW APT_UPDATE_EXIT APT_DOWNLOAD_EXIT
}
# check <name> <expected exit> <expected apt-get calls, one per line> [expected mirror list lines...]
check() {
  local name="$1" want="$2" calls="$3" code=0; shift 3
  bash "$here/download.sh" chromium webkit > "$work/out" 2>&1 || code=$?
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

reset "$azure" "$canonical"; export NPX_OUT='nothing to install'
check "Playwright naming no packages fails the step before apt runs" 1 "" "$azure" "$canonical"

reset "$azure"; export APT_SLOW=1
check "a slow download with no Canonical archive on the mirror list fails rather than keep waiting" \
  1 "$(printf 'update\n%s' "$download")" "$azure"

reset "$azure" "$canonical"; export APT_SLOW=1
echo 'deb http://azure.archive.ubuntu.com/ubuntu noble main' > "$PLAYWRIGHT_APT_ETC/sources.list"
check "a slow download fails when another apt source still names the Azure mirror after the edit" \
  1 "$(printf 'update\n%s' "$download")" "$canonical"

if [ "$failures" -ne 0 ]; then
  echo "::error::$failures of download.sh's checks failed"; exit 1
fi
