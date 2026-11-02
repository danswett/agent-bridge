# Agent Bridge — native Apple clients

Swift 6 core and native SwiftUI apps for macOS 15+, iOS 18+ and iPadOS 18+.

## Open and run

Install Xcode, XcodeGen and SwiftFormat (`brew install xcodegen swiftformat`). From this directory:

```sh
xcodegen generate
open AgentBridgeApple.xcodeproj
```

Select **AgentBridgeMac** or **AgentBridgeMobile**. The mobile target supports both iPhone and iPad. Choose your signing team for physical devices. Generated projects and build output are ignored; `project.yml` is the source of truth.

The Mac app connects to an already-running host's `default` profile. Use **Connection → Host profile** for another profile. The endpoint resolver matches the host's profile validation, UTF-8 socket path limit, SHA-256 fallback name and private runtime directory precedence. The app does not install, start, stop, migrate or reconfigure a host.

**Explore Sample Sessions** works offline on all three platforms. The connection footer explicitly labels sample mode, including on the phone's detail screen. For deterministic preview launches, add `--sample-sessions` to the scheme's launch arguments. Samples never mix with live sessions.

## Verified functionality

- `hello` protocol and `sessions.read` capability negotiation, followed by a persistent `sessions.subscribe` feed.
- Nonblocking macOS Unix socket reader, bounded frames and event queue, response correlation, timeout/cancellation cleanup, and connection generation isolation.
- Replacement snapshots begin at the matching subscribe response; old-feed messages before it cannot enter the snapshot. Unknown events participate in sequence checking. A gap triggers one resubscription and quarantines updates until the replacement starts.
- A subscription that never completes its snapshot times out. Disconnects are visible; cached rows are never marked live. Manual reconnect creates a new channel.
- Shared searchable session list, adaptive navigation, main-agent response/reasoning, current tool, question message, delegate summaries and read-only details. Main-agent text is never taken from a delegate. Session and delegate identity remain stable during updates.
- Host discovery holds and read-only reasons remain visible. All questions are currently answered at the machine; no action requests are sent.

## Verification

```sh
bash scripts/verify.sh
```

This runs Swift tests, SwiftFormat lint, generates the project, and builds macOS and iOS Simulator applications without signing. The Apple clients CI workflow runs the same command. Socket tests use uniquely named disposable sockets in `/tmp`, never installed profiles or live agents.

Local validation on November 2, 2026: Xcode 27.2 / Swift 6.4.2; 21 tests passed; both app builds passed. The macOS app was launched and inspected through accessibility; iPhone 18 Pro and iPad Pro 11-inch simulator sample layouts were rendered and inspected. Device signing, minimum-OS runtime compatibility, VoiceOver interaction, and interoperability with a running .NET host are not yet verified. The tests exercise a fixture speaking the documented protocol.

## Remaining work

1. Remote iOS/iPadOS and Mac connectivity: relay device-code sign-in, Keychain identity/token persistence, relay proof and roster, and Noise XX P-256/AES-GCM/SHA-256 channels. Require byte-identical results against `tests/AgentBridge.Transport.Tests/testdata/noise-xx-p256-vectors.json` before connecting to a real relay.
2. Authenticated remote session subscriptions, foreground/background recovery and trusted device lifecycle. The mobile app currently provides the shared offline interface only and reports remote pairing as unavailable.
3. Capability-gated answers/replies/launch, full session/delegate history, Markdown rendering, usage, notifications and APNs.
4. The complete shared design language (generated tokens and custom glyphs), accessibility/device testing, signed distribution and host installation/update integration.

The .NET host is authoritative for all operations. Never bypass its authorization or use an unencrypted remote shortcut. Existing hooks and live Home Assistant infrastructure are untouched.
