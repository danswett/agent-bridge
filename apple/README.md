# Agent Bridge — native Apple clients

Swift 6 protocol core and SwiftUI application targets for macOS, iOS and iPadOS.

## Build

Install Xcode and XcodeGen. From this directory run:

```sh
swift test
xcodegen generate
open AgentBridgeApple.xcodeproj
```

Build the **AgentBridgeMac** or **AgentBridgeMobile** scheme in Xcode. Both targets use the local `AgentBridgeAppleCore` Swift package. Set your own bundle identifiers and signing team.

## Implemented

- Protocol v1 JSON envelope and session models, frame codec, session snapshot reducer and unit tests.
- SwiftUI navigation and session details on macOS, iPhone and iPad.
- macOS local Unix socket transport with length-prefixed messages and response-ID checking.
- macOS manual refresh using `hello` and paginated `sessions.list`.

## Not yet implemented or verified

- No macOS/iOS Xcode build has been run. The application code requires macOS compilation and testing.
- Mobile clients have **no remote connection** yet; the screen displays a disconnected explanation.
- No Noise XX / P-256 handshake, trusted device pairing, relay auth, APNs, notifications or session actions.
- No streaming socket reader or live subscription. The local socket client is a blocking proof of concept and must become cancellation-aware and handle interleaved events before production.
- macOS currently assumes `~/.agent-bridge/default/host.sock`; profile selection and the documented fallback socket path remain to be implemented.
- Host installation, update and migration remain out of scope for this slice.

The .NET host is authoritative for all agent operations. Never bypass the authenticated encrypted channel for remote actions. Do not modify existing hooks or live Home Assistant infrastructure.
