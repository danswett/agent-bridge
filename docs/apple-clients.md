# Native Apple clients

The implementation lives in [`apple/`](../apple/README.md), with reproducible XcodeGen targets for macOS, iPhone and iPad and a shared Swift protocol package.

The current slice is a read-only local Mac session workbench. It negotiates `hello`, subscribes to sessions, replaces snapshots at the subscribe response boundary and resubscribes after a channel sequence gap. Its asynchronous socket reader handles interleaved responses/events, bounded frames, cancellation, timeouts and disconnects. Profile endpoint resolution follows [local-pipe.md](protocol/local-pipe.md).

The iOS/iPadOS target builds and renders the same adaptive workbench with explicitly labelled offline samples. It does not yet connect remotely. Relay sign-in, Keychain persistence and a vector-tested Noise implementation are the next prerequisite; the existing PWA and .NET transport remain the interoperability references. No host lifecycle management or session mutations are exposed by this slice.

Run `bash apple/scripts/verify.sh` for unit/socket tests, formatting checks and both native builds. See the Apple README for verified environments and remaining limitations. This foundation is not P5 or P8 feature parity or release readiness.
