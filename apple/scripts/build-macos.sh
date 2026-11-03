#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

# Runnable development builds need a provisioned app identity for the data
# protection Keychain. verify.sh deliberately only checks unsigned compilation.
if [[ "$(uname -s)" != Darwin ]]; then echo 'Run this build on macOS.' >&2; exit 2; fi
: "${DEVELOPMENT_TEAM:?Set DEVELOPMENT_TEAM to your local Apple development team.}"
output="${1:-out/macos-development}"
xcodegen generate
xcodebuild -quiet -project AgentBridgeApple.xcodeproj -scheme AgentBridgeMac \
  -destination 'platform=macOS' -derivedDataPath "$output" \
  -allowProvisioningUpdates build DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  CODE_SIGN_STYLE=Automatic CODE_SIGN_IDENTITY='Apple Development' \
  CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES

app="$output/Build/Products/Debug/AgentBridgeMac.app"
codesign --verify --strict "$app"
if [[ ! -f "$app/Contents/embedded.provisionprofile" ]]; then
  echo 'The build is missing its Mac development provisioning profile.' >&2; exit 1
fi
entitlements="$(mktemp)"
trap 'rm -f "$entitlements"' EXIT
codesign --display --entitlements :- "$app" > "$entitlements"
app_identity="$(/usr/libexec/PlistBuddy -c 'Print :com.apple.application-identifier' "$entitlements")"
keychain_group="$(/usr/libexec/PlistBuddy -c 'Print :keychain-access-groups:0' "$entitlements")"
if [[ "$app_identity" != *.dev.agentbridge.mac || "$keychain_group" != "$app_identity" ]]; then
  echo 'The build does not have its own provisioned Keychain access group.' >&2; exit 1
fi
echo "Signed local development app: $app"
