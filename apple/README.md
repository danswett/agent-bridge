# Agent Bridge Apple clients

Initial, non-production Swift 6 shared protocol package for macOS, iOS and iPadOS clients.

Run `swift test` from `apple/` on a machine with Swift 6.

The existing .NET host and Bridge Protocol v1 remain authoritative. The Apple clients must not duplicate agent control logic. The native macOS app will connect to the local Unix socket with 4-byte little-endian framing; remote clients must use the existing authenticated Noise XX over P-256 channel and approved device pairing. Never send commands directly to the relay without that channel.

This first slice includes only JSON envelope/session decoding and unit tests. It does not yet contain app targets, working local transport, remote pairing, notifications, or session controls. It does not modify installed hooks, agents, relay or Home Assistant.

Next: local macOS transport and subscription tests, then Swift/.NET cryptographic interoperability, then adaptive SwiftUI clients.
