#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

# Local development output, including the .NET runtime; no system installation,
# signing identity, launch agent, credentials or host configuration is changed.
dotnet_command="${DOTNET_COMMAND:-dotnet}"
case "$(uname -m)" in
  arm64) runtime=osx-arm64 ;;
  x86_64) runtime=osx-x64 ;;
  *) echo "Unsupported Mac architecture" >&2; exit 2 ;;
esac
if [[ "$(uname -s)" != Darwin ]]; then echo "Run this build on macOS" >&2; exit 2; fi
output="${1:-out/macos-host}"
"$dotnet_command" publish src/AgentBridge.Host/AgentBridge.Host.csproj \
  -c Release -r "$runtime" --self-contained true -o "$output"
echo "Local host built in $output. See docs/macos-host.md for operator setup."
