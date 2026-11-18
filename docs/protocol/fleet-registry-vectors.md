# Shared session registry vectors

[S-N2c](https://github.com/danswett/agent-bridge/issues/2969) has one canonical
UTF-8 fixture, [fleet-registry-fields.json](vectors/fleet-registry-fields.json).
C# transport tests copy that file unchanged to their output; PWA tests read it
directly. Do not maintain consumer-specific copies. Apple consumption is **parked**
under [the Apple gate](https://github.com/danswett/agent-bridge/issues/1882);
no Apple conformance or retirement clearance is claimed.

Each schema case shallowly overrides the base record and, when supplied, its first
session. Sign the resulting record, not the template. The fixture machine is the
existing `host-sign` roster scenario's host; the unrostered case must not acquire
membership from relay metadata or a display name.

The relay replays every lifetime operation in order, selecting base sessions by
index. Reads assert replacement/empty/disconnect behavior and expired JSON erasure;
clock rewind and older puts assert the retained watermark. Offline machine identity
and last-seen metadata survive expiry. These relay-owned operations do not make a
stateless signature reader a session cache. See [the lifetime contract](fleet-registry-lifetime.md).
