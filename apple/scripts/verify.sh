#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

swift test
swiftformat . --lint --swiftversion 6.0 --exclude .build,AgentBridgeApple.xcodeproj
xcodegen generate
xcodebuild -quiet -project AgentBridgeApple.xcodeproj -scheme AgentBridgeMac \
  -destination 'platform=macOS' -derivedDataPath "${TMPDIR:-/tmp}/agent-bridge-verify-mac" \
  build CODE_SIGNING_ALLOWED=NO
xcodebuild -quiet -project AgentBridgeApple.xcodeproj -scheme AgentBridgeMobile \
  -destination 'generic/platform=iOS Simulator' -derivedDataPath "${TMPDIR:-/tmp}/agent-bridge-verify-mobile" \
  build CODE_SIGNING_ALLOWED=NO
