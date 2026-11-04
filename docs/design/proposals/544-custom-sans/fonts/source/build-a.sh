#!/usr/bin/env bash
# Proposal #544, option A: rebuilds Slipgates Sans A (NOT APPROVED) into ../ttf and ../woff2.
# The same toolchain and pinned Iosevka as design/fonts/agent-bridge-mono/build.sh; see its
# README for the Windows notes. About four minutes on twelve threads.
set -euo pipefail
IOSEVKA_TAG=v34.9.1
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if ! command -v ttfautohint >/dev/null; then
  mkdir -p "$work/bin"
  printf '#!/bin/sh\nexec python3 -m ttfautohint "$@"\n' > "$work/bin/ttfautohint"
  chmod +x "$work/bin/ttfautohint"
  export PATH="$work/bin:$PATH"
fi

git clone --quiet --depth 1 --branch "$IOSEVKA_TAG" https://github.com/be5invis/Iosevka "$work/iosevka"
cp "$here/private-build-plans.toml" "$work/iosevka/private-build-plans.toml"
cd "$work/iosevka"
npm ci --no-audit --no-fund --silent
npm run build -- ttf::SlipgatesSansA --jCmd="$(nproc 2>/dev/null || echo 4)"
python3 "$here/postprocess-a.py" "$work/iosevka/dist/SlipgatesSansA" "$here/.."
