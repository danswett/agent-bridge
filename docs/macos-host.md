# macOS host: local development

The Mac host serves the existing local socket and encrypted relay protocol. When
`tmux` is installed on an absolute PATH entry, launch, reply, answer and stop use
one private managed tmux server. It controls only agent processes launched by the
current host run. Existing sessions remain visible where discovery supports them,
and are read-only to this backend. Restarting the host does not stop or adopt old
panes. See [managed delivery](macos-managed-delivery.md) for ownership and recovery.

Build with the .NET 10 SDK on this Mac:

```sh
bash scripts/build-macos-host.sh
```

`DOTNET_COMMAND` may name an isolated SDK executable. The output includes its own
runtime and can be run without installing .NET globally. These are unsigned local
development builds, not a signed installer or automatic update channel.

## Operator setup

Run these in **your own Terminal**, from the checkout. Host-management guard #67
refuses configuration and sign-in from inside an agent session. The `mac-local`
profile is separate from any existing default profile. Use an existing ordinary
workspace directory you approve in place of `/absolute/path/to/workspace`.

```sh
out/macos-host/agent-bridge-host workspace add /absolute/path/to/workspace --label Local --profile mac-local
out/macos-host/agent-bridge-host config set launch.enabled true --profile mac-local
out/macos-host/agent-bridge-host config set launch.defaultAgent copilot --profile mac-local
out/macos-host/agent-bridge-host sign-in --relay https://relay.bad-duck.com --profile mac-local
out/macos-host/agent-bridge-host serve --profile mac-local
```

Sign-in displays GitHub's device code. Complete that flow in your browser. Keep
`serve` running; Ctrl-C stops this host without killing its agent sessions. The
host reads configuration at startup. A CLI agent must itself be installed and
signed in before it can answer. An agent's first folder-trust prompt still needs
the operator's decision; the bridge does not silently approve it.

In the Mac client, choose local profile **mac-local**. On Mac, iPhone and iPad,
use relay **https://relay.bad-duck.com** and sign in to the **same GitHub account**.
No development certificate is needed for that public relay. Localhost relays are
only reachable on their own machine (including simulators sharing this Mac's
network); a physical iPhone or iPad must use a reachable relay.

The Mac's native Unix-socket management peer identity is not yet implemented.
Local session operations work, but host configuration/sign-in/restart through
the app remain withheld; use the operator CLI above. Hook takeover/migration,
Windows console adoption, Dev Box operations and Windows updates are not Mac
features. No legacy hooks, Home Assistant settings or daemon are changed here.

## Isolation and safety

The profile stores its keys, account, attachments, managed socket and registration
roots under `~/.agent-bridge/mac-local`, in owner-only directories. Unix credential
writes are private and atomic; linked profiles and credential files are refused.
Mac discovery defaults to the profile's own registration roots, not the installed
legacy bridge's spool. Current agent homes are read for transcripts.

For disposable diagnostics, `--data-root /absolute/private/root` selects a separate
profile root; `--registration-root /absolute/private/registrations` replaces all
default registration roots. `--copilot-home`, `--claude-home` and `--codex-home`
select transcript roots. These options never remove management guard #67 or change
the environment's HOME/CODEX_HOME. The native client uses the normal profile root;
custom-root diagnostics can connect to the socket path printed by the host.

G5 holds delivery if a PowerShell runtime could be running agent-ha-bridge for the
same user, or if process observation is incomplete. This deliberately holds even
an unrelated PowerShell shell until it exits. Never stop an existing legacy daemon
to get past that check from an agent session.

For tests on macOS, use a canonical temporary path (`TMPDIR=/private/tmp`) because
some existing worktree fixtures compare `/var` and `/private/var` paths literally.
The disposable real-agent acceptance test is opt-in and separate from synthetic
interop; successful fixture transport tests do not establish real agent delivery.
