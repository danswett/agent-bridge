#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "$(uname -s)" != Darwin ]]; then echo 'Run the AppKit menu checks on macOS.' >&2; exit 2; fi
scratch="$(mktemp -d "${TMPDIR:-/tmp}/agent-bridge-menu-tests.XXXXXX")"
trap 'rm -f "$scratch/menu-check"; rmdir "$scratch"' EXIT
xcrun swiftc -parse-as-library -target "$(uname -m)-apple-macos15.0" \
  Apps/MacAttachmentMenu.swift Tests/Native/MacAttachmentMenuChecks.swift \
  -o "$scratch/menu-check"
"$scratch/menu-check"
