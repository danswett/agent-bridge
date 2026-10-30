# The host's config.json

What a host may do on its machine is set by the person who runs it, in `config.json` in the
profile's folder, beside the device key: `%LOCALAPPDATA%\AgentBridge\<profile>\config.json`
on Windows, `~/.agent-bridge/<profile>/config.json` elsewhere (`default` unless
`--profile` says otherwise). `agent-bridge-host serve` reads it when it starts; a change
takes a restart. Nothing another device sends can change it.

Until the app has a settings screen, it is edited with the host's own commands, at the
machine (below), or by hand. The app will edit the same file; this page is the schema both
follow.

## Schema, version 1

```jsonc
{
  "version": 1,
  "permissions": {
    "remoteApproval": true
  },
  "push": { "enabled": true },
  "launch": {
    "enabled": true,
    "workspaces": [
      { "path": "C:\\Users\\me\\repos\\bridge", "label": "Bridge", "isolate": true },
      "C:\\Users\\me\\notes"
    ],
    "defaultWorkspace": "Bridge",
    "defaultAgent": "auto",
    "permissions": "ask",
    "worktrees": { "root": "~/repos/wt", "limit": 10, "idleHours": 12 },
    "profiles": [],
    "defaultProfile": null,
    "resumeCount": 12,
    "agents": {
      "copilot": {
        "path": null,
        "model": "gpt-6.1-sol", "effort": null, "context": null,
        "models": [], "efforts": [], "contexts": [],
        "extraArgs": []
      },
      "agency": { "path": null, "extraArgs": [] },
      "claude": { "model": "opus" },
      "codex": { }
    },
    "transfer": { "serve": true, "receive": true, "maxBytes": 26214400, "maxSourceBytes": null }
  },
  "homeAssistant": {
    "enabled": false,
    "baseUrl": "http://homeassistant.local:8123",
    "dashboardUrlPath": "agent-decisions",
    "detailedActivity": true,
    "machineName": null,
    "disposable": false,
    "agentUserIds": []
  }
}
```

Comments and trailing commas are allowed. Absent and `null` mean the default.

| Key | Default | Meaning |
|---|---|---|
| `version` | `1` | The schema. A file with a higher version, written by a newer Agent Bridge, is not guessed at: remote approval and launching are both off until the host is updated |
| `permissions.remoteApproval` | `true` | Copilot permission prompts may be approved or denied from another device ([host-answers.md](host-answers.md)) |
| `push.enabled` | `true` | Notifications (Web Push) to the account's phones. `false` sends none and takes no subscription: `hello` leaves `push` out, and `push.subscribe` fails saying the machine doesn't send notifications. For a machine that should be seen but not heard from, as the shadow host beside agent-ha-bridge ([host-shadow.md](host-shadow.md#on-the-phone--relay)) |
| `launch.enabled` | `true` | Launching from other devices at all. `false` leaves `sessions.launch` out of `hello` |
| `launch.workspaces` | `[]` | **The approved folders.** A new session starts only in one of these, and a session is resumed only if it ran in one of these folders itself (or in a worktree this host or agent-ha-bridge made for an isolated one), as agent-ha-bridge has it: not in a folder below one. Each is a path, or `{ "path", "label", "isolate" }`. `path` is absolute (`~` is the home folder); `label` defaults to the folder's name, and is what a client names; `isolate: true` starts each new session in a git worktree of its own ([host-launch.md](host-launch.md#isolation)). A folder that does not exist, or a system folder (Windows, Program Files, ProgramData, temp, a drive root), is not offered. At most 64 |
| `launch.defaultWorkspace` | first | The label a launch gets when it names none |
| `launch.defaultAgent` | `auto` | `auto` (the first installed of Agency, Copilot, Claude, Codex) or one of `agency`, `copilot`, `claude`, `codex` |
| `launch.permissions` | `ask` | What a launch that does not say gets: `ask` keeps permission prompts; `allowAll` launches without them. A client may choose either per launch |
| `launch.worktrees.root` | `~/repos/wt` | Where isolated sessions' worktrees go. Shared with agent-ha-bridge and every other session: this host only ever adds its own there |
| `launch.worktrees.limit` | `10` | Worktrees this host made, per repository, before isolated launches there are refused, counted after finished ones are removed. `0` is no cap |
| `launch.worktrees.idleHours` | `12` | How long after this host made a worktree it may be removed once finished: clean (no uncommitted, untracked or ignored files but two named build outputs), detached, nothing ahead of its base, unlocked, and no live session in it ([host-launch.md](host-launch.md#reclaiming-finished-worktrees)). `0` removes a finished one at the next isolated launch |
| `launch.profiles` | `[]` | Agency profiles to offer: empty offers every one Agency lists; otherwise those of them named here (all, if none of them is listed) |
| `launch.defaultProfile` | first | The profile a launch gets when it names none |
| `launch.resumeCount` | `12` | How many resumable sessions to offer, up to 50; `0` offers none |
| `launch.agents.<id>.path` | found | The agent's executable, when it is not where it is usually found (PATH, then its installer's folder). A path that does not exist means the agent is not installed |
| `launch.agents.<id>.model`, `effort`, `context` | none | What a launch that does not say is started with. None means the agent's own default. `agency` uses `copilot`'s |
| `launch.agents.<id>.models`, `efforts`, `contexts` | built in | The values to offer, replacing the built-in lists (and, for models, those the machine's sessions are using). At most 64 each |
| `launch.agents.<id>.extraArgs` | `[]` | Arguments added to every launch of that agent, as written |
| `launch.transfer.serve` | `true` | Hand this machine's ended sessions to the account's other machines, each on a command a person's device signed for it ([host-transfer.md](host-transfer.md)). `false` leaves `sessions.transfer.serve` out |
| `launch.transfer.receive` | `true` | Bring sessions here from the account's other machines (`sessions.transfer`) |
| `launch.transfer.maxBytes` | `26214400` (25 MiB) | The largest session bundle, compressed, this machine sends or takes: 1 MiB to 200 MiB. A transfer needs it on both machines |
| `launch.transfer.maxSourceBytes` | 10 × `maxBytes` | The most read from disk to pack one, and unpacked to install one: from `maxBytes` to 20 GiB |
| `homeAssistant.enabled` | `false` | The Home Assistant connector ([ha-connector.md](ha-connector.md)): this machine's sessions on the existing agent-ha-bridge dashboard. Runs only while agent-ha-bridge does not, and only with a token (`agent-bridge-host ha token set`, kept protected beside the device key, never in this file) |
| `homeAssistant.baseUrl` | none | The Home Assistant, `http` or `https`, with no credentials in it. Required when enabled |
| `homeAssistant.dashboardUrlPath` | `agent-decisions` | The dashboard agent-ha-bridge's writer generates. Only read, to know when an ended session's card has gone |
| `homeAssistant.detailedActivity` | `true` | What cards carry when this machine has no Detailed activity switch in Home Assistant |
| `homeAssistant.machineName` | the computer's | The name the entities are keyed on (`agent_bridge_<slug>`). Only for a rehearsal posing as another machine on a disposable Home Assistant |
| `homeAssistant.disposable` | `false` | Says `baseUrl` is a disposable Home Assistant. A development build writes to no other |
| `homeAssistant.agentUserIds` | `[]` | Home Assistant user ids whose presses are an agent's, as agent-ha-bridge's own setting: an answer or reply they send makes the next turn agent-driven (the card's purple edge). A list of strings |

How a file that is wrong is read: one that cannot be read or parsed turns remote approval,
launching and notifications off; a `permissions.remoteApproval` or `push.enabled` that is not
`true` or `false` does too. In `launch`, a workspace entry that is wrong (no path, a relative path, an `isolate`
that is not `true` or `false`) is left out and said in the log, and any other value that is
wrong turns launching off. In `homeAssistant`, any value that is wrong turns the connector off
and is said in the log. A setting someone may have meant as off is never read as on.

## Approving a workspace at the machine

```
agent-bridge-host workspace list
agent-bridge-host workspace add C:\Users\me\repos\bridge --label Bridge --isolate
agent-bridge-host workspace remove Bridge
```

`add` refuses a folder that does not exist, a system folder, one already approved, a label in
use, and `--isolate` outside a git repository. Every command takes `--profile <name>`.

`workspace add` and `remove`, `config set` and `unset`, `ha token set` and `clear`, and
`sign-in` are for a person. They refuse to run (exit code 4) inside an agent session: when
anything they run inside is an agent CLI (`copilot`, `claude`, `codex`, `agency`, or Node or Bun
running one), or when `AGENT_BRIDGE_SESSION` is set, as it is in every session the host
launches. They refuse too when the process tree they run in cannot be read in full (an
ancestor `/proc` will not show, or `ps` failing on macOS), since an agent could be above what
was read. Run them in a terminal of your own. `list`, `show` and `ha status` work anywhere. Why, and what this does not stop:
[local-pipe.md](protocol/local-pipe.md#who-may-manage-the-host).

The rest of the file:

```
agent-bridge-host config show
agent-bridge-host config set launch.defaultAgent copilot
agent-bridge-host config set launch.permissions allowAll
agent-bridge-host config set launch.agents.copilot.models '["gpt-6.1-sol","claude-opus-5.5"]'
agent-bridge-host config unset launch.permissions
```

A value is read as JSON when it is JSON (`true`, `12`, `["a","b"]`), and as text otherwise.
Only the keys above can be set. Each change is read back as `serve` would read it first, and
one that would leave a problem is not written; the file is replaced in one step. The
commands rewrite the file without its comments.

Then restart `serve`. It logs `launching sessions is on: 1 workspace(s) approved (Bridge
[worktrees])`, or why launching is off.
