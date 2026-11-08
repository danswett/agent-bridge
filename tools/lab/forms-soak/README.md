# Forms soak (screen-only)

Draws Copilot CLI `ask_user`-style forms from a stub MCP server, in consoles of fixed sizes,
and answers them with the host's own form reader, planner, screen check and injector. No model
call is needed for the form itself. Part of #1138; background in #1123 and #1193.

## What it tests, and what it does not

It tests the **screen reader and the injector**: `CopilotForm.Read`, `CopilotPlanner.Plan`,
`CopilotFormScreen.Diagnose` and `WorkerInjector`, the host's own classes, against a real
Copilot CLI TUI in a real console. The stub reports what the CLI was finally given, and that is
what each result is judged against.

It does **not** test `session.answer`, the host's transcript reading, its attempt ledger, its
gates, or the `answer-attempt` lines in `host.log`. A form raised before a session's first model
turn leaves no transcript, so the host lists no question for it (found by probe: `session.get`
showed `idle`, no question). Nor does it help to run one cheap model turn first: with a
transcript present, an MCP elicitation form was still not listed (the transcript holds no
elicitation event). `Invoke-FormsSoak.ps1` has a marked hook, `Invoke-FullPathAnswer`, for a
later full-path mode that goes through the local pipe; it needs a form the host can see.

### The host types a form field by field; the rig does not

Read in the code (`AnswerService.TypeFieldsAsync`, `KeyPlan`, `KeySend`, `WorkerInjector`):

- The injector worker types a plan's sends in order and re-checks the screen only for a send
  that carries `Expect` or `Checks` (rows it must still read exactly so, taken in the same
  attach); it checks nothing between sends that carry none.
- `CopilotPlanner.Plan` builds sends with no `Expect` or `Checks`.
- The host does not hand that plan to the injector whole. `AnswerService.TypeFieldsAsync` sends it
  field by field: it reads the screen, holds each write to the rows the form shows at that
  field, sends the field's Enter alone once the screen has settled, and stops, leaving the form
  part answered and saying so, if the screen changed.
- This rig checks the screen once, with `Diagnose` at field 0, then gives the injector the
  whole plan to type blind. It does not run the host's per-field path (that method is private
  to the host).

So a `delivered` for a multi-field shape (`choice-text`, `choice-yesno-text`, `tall`) shows the
Copilot form accepts that key sequence at that timing, and that the field-0 screen check
passes. It does not show that the host's per-field checks and waits hold on that form, and it
cannot surface a failure of them. A failure of the host's per-field checks is a gap this rig
does not cover. Closing it needs the rig to drive `AnswerService`'s form path, which means
fakes for its transcript and session sources.

The stub server is named `Copilot` because the form's heading is drawn from the MCP server's
name, and the host's screen check looks for "Copilot needs information."

## Results

| Class | Meaning |
| --- | --- |
| `delivered` | The CLI was given the answer that was typed |
| `refused-before-any-key` | The host's checks said no, or the screen could not be read; nothing typed |
| `unconfirmed` | Keys went and nothing proves what the CLI was given (also any rig error) |
| `wedged` | Keys went and the form is still waiting |
| `half-answered` | The CLI was given something other than the answer, or the injector stopped part way |

Shapes: `choice`, `choice-text`, `choice-yesno-text`, `number`, `long-title`, `tall`, `other`.
Default sizes: 120x30, 120x50, 200x50, 250x40. A `tall` form at a small size is expected to be
refused (the heading scrolls off the screen, #1123); the rig records it, and a refusal is not a
failure. Only `wedged` and `half-answered` make the exit code non-zero (1). A rig that could not
start exits 2.

`-OutDir` (default `forms-soak-results` in the current folder) gets `results.csv` (one row per
run) and `results.md` (screen rows for every run that was not delivered). Rows have the user
name, machine name, drive paths, tokens and email addresses removed; only the stub's own text
should remain. Check `results.md` before sharing it.

## Needs

- PowerShell 7.6 or later (it must run on .NET 10, as the host's assemblies do).
- Windows, with a console the injector can attach to (a hidden console window is created per run).
- `node` on the path.
- One or more Copilot CLI executables (`-CopilotPath`), each one a "CLI version" of the matrix.
  Each run starts the CLI with its own `COPILOT_HOME` (a `home` folder inside the run folder, with
  the run folder trusted), so it never reads or writes a person's own CLI config or sessions, and
  it has no stored login: **it needs `COPILOT_GITHUB_TOKEN` in the environment the rig is started
  in**. This rig never reads, prints or stores a token; the CLI gets it from the environment.
- `-HostDir`: a folder with `AgentBridge.Host.Delivery.dll`, `AgentBridge.Host.Core.dll`,
  `AgentBridge.Protocol.dll` and `agent-bridge-injector.exe`. Use a published host (for example
  `dotnet publish src/AgentBridge.Host -c Release -r win-x64`, with the injector beside it) or an
  installed app's folder. The running host does not need to be started.

## Run it

```powershell
pwsh -File tools\lab\forms-soak\Invoke-FormsSoak.ps1 -HostDir C:\soak\host `
  -CopilotPath C:\soak\cli\1.0.107\copilot.exe,C:\soak\cli\1.0.105\copilot.exe `
  -Sizes 120x30,200x50 -Shapes choice,tall -Iterations 3 -OutDir C:\soak\results
```

Parameters: `-HostDir` (or `AGENT_BRIDGE_HOST_DIR`), `-CopilotPath`, `-Shapes`, `-Sizes`,
`-Iterations` (0 = until stopped), `-PauseBetweenPassesSeconds`, `-OutDir`, `-NodePath`,
`-FormTimeoutSeconds`, `-SettleSeconds`, `-KeepRuns`, `-CopilotArguments`. Nothing is a fixed path.

## Unattended, as a scheduled task, on a dedicated machine

Use a machine nobody else works on, signed in as a user that stays signed in (the injector
needs an interactive session for its consoles). Create a task that runs at logon:

```powershell
$a = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument '-NoProfile -File C:\soak\repo\tools\lab\forms-soak\Invoke-FormsSoak.ps1 -HostDir C:\soak\host -CopilotPath C:\soak\cli\copilot.exe -Iterations 0 -PauseBetweenPassesSeconds 600 -OutDir C:\soak\results'
Register-ScheduledTask -TaskName FormsSoak -Action $a -Trigger (New-ScheduledTaskTrigger -AtLogOn) -RunLevel Limited
```

Do not run it elevated.
Put `COPILOT_GITHUB_TOKEN` in that user's environment, not in the task's command line or this
repository.

## Stop it

Create a file named `STOP` in `-OutDir`: the loop ends after the current run. To end it at once,
stop the task (`Stop-ScheduledTask FormsSoak`) or stop the `pwsh` process by its id. A run in
flight leaves at most one hidden console; end that tree by its id (`taskkill /PID <id> /T /F`).

## What it must never touch

- It starts one console per run and ends only that console's process tree, by id (`taskkill /PID`,
  never by name). It attaches the injector only to the console it started.
- It never looks at, attaches to, types into or ends any other process, and in particular not a
  person's Copilot sessions or a running Slipgates host. Run it on a machine with neither.
- It never starts the host, calls the relay or uses a Home Assistant. It makes no model call.
- What the rig itself writes goes under `-OutDir`: `results.csv`, `results.md`, and one folder per
  run under `work\` (the CLI's home and logs, its config, the stub log), of which the newest
  `-KeepRuns` (default 30) are kept. The CLI it starts is a real one: it also uses the network
  (GitHub, to start and for its own services) and runs as the current user. Its own state goes
  to the run's `COPILOT_HOME`, not the user's. Node and the CLI may still write to the usual
  system temp and cache folders.

`Test-Redaction.ps1` checks the redaction of names, paths, tokens, Bearer values, JWTs and
emails in the results (`pwsh tools\lab\forms-soak\Test-Redaction.ps1`; it exits 1 on a miss).
