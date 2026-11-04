#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${DEVELOPMENT_TEAM:?Set DEVELOPMENT_TEAM to the Apple development team for this app.}"
output="${1:-out/ios-push-development}"
xcodegen generate
xcodebuild -quiet -project AgentBridgeApple.xcodeproj -scheme AgentBridgeMobile \
  -configuration Debug -destination 'generic/platform=iOS' -derivedDataPath "$output" \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" CODE_SIGN_STYLE=Automatic \
  CODE_SIGN_ENTITLEMENTS=Config/AgentBridgeMobilePushDevelopment.entitlements build
app="$output/Build/Products/Debug-iphoneos/AgentBridgeMobile.app"
codesign --verify --strict "$app"
signed="$(mktemp -d)"
trap 'rm -rf "$signed"' EXIT
codesign --display --entitlements :- "$app" > "$signed/entitlements.plist"
security cms -D -i "$app/embedded.mobileprovision" > "$signed/profile.plist"
for source in entitlements profile; do
  key=aps-environment
  if [[ "$source" == profile ]]; then key="Entitlements.$key"; fi
  if [[ "$(plutil -extract "$key" raw -o - "$signed/$source.plist")" != development ]]; then
    echo "The push build needs aps-environment=development in its signature and profile." >&2
    exit 1
  fi
done
echo "Signed sandbox push build: $app"
