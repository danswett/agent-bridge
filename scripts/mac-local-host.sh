#!/bin/bash
# Operator shortcut for the unsigned host built by build-macos-host.sh.
set -euo pipefail

usage() {
  echo 'Usage: mac-local-host.sh configure /absolute/path/to/workspace | start' >&2
  exit 2
}

case "${1:-}" in
  configure)
    [[ $# -eq 2 && "$2" == /* && -d "$2" ]] || usage ;;
  start)
    [[ $# -eq 1 ]] || usage ;;
  *) usage ;;
esac

checkout=$(cd "$(dirname "$0")/.." && pwd -P)
host="$checkout/out/macos-host/agent-bridge-host"
if [[ ! -x "$host" ]]; then
  echo 'Build the local host first: bash scripts/build-macos-host.sh' >&2
  exit 1
fi

if [[ $1 == configure ]]; then
  "$host" workspace add "$2" --label 'Bridge Sessions' --profile mac-local
  "$host" config set launch.permissions ask --profile mac-local
  "$host" config set launch.defaultAgent copilot --profile mac-local
  "$host" config set launch.enabled true --profile mac-local
  exec "$host" sign-in --relay https://relay.bad-duck.com --profile mac-local
fi

echo 'Starting This Mac in the foreground. Keep this Terminal open; Ctrl-C stops the host.'
exec "$host" serve --profile mac-local --name 'This Mac'
