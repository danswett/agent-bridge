#!/usr/bin/env bash
# Rebuilds Agent Bridge Mono from source into ttf/ and woff2/ beside this script.
# Needs: git, Node.js 18+, Python 3 with fonttools and brotli, and ttfautohint on PATH
# (`pip install ttfautohint-py` provides it as `python3 -m ttfautohint`).
# Takes about four minutes on four cores.
set -euo pipefail
IOSEVKA_TAG=v34.9.1   # pinned: a newer Iosevka can change glyph shapes
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
npm run build -- ttf::AgentBridgeMono --jCmd="$(nproc 2>/dev/null || echo 4)"
python3 "$here/postprocess.py" "$work/iosevka/dist/AgentBridgeMono" "$here"
