# Agent sign-in token: storage and rotation rules (#1049)

Trust's rules for slices 3 and 4 of #1049, and for the answer to slice 1 (can the CLI
read the token from a file or helper). Design only. The token is a fine-grained PAT with
an 8-day maximum, read by the Copilot CLI from `COPILOT_GITHUB_TOKEN` once at launch.

## 1. Where it lives

- One owner-only file per host, outside session folders, the profile bundle area and
  diagnostics. On Windows it is DPAPI-protected for the host's user.
- Only the host process reads it. A session, an agent or a child process never does.
- It is set through the host's own command or an app route, never by editing an
  environment variable on the machine.

## 2. Passing it to a session

- Only into the launched CLI's environment at launch.
- Never into launch arguments (visible in process lists), session files, prompts, logs,
  traces, `host-state`, notifications or the relay.
- If slice 1 finds that the CLI can read the token from a file or helper, prefer that: the
  host passes the path, not the token, and a rotation needs no restart.

## 3. Syncing between machines

- The relay never holds the token in clear.
- Cross-device carriage uses the key path in #759 (roster-admitted devices, a fresh random
  key on removal), so it waits for the signed roster (#55).
- Until then the token is set per host from the app over the existing authenticated host
  channel, one host at a time.

## 4. Accepting a new token

- Only from a person-roster device, never from a session or an agent.
- Checked for shape without echoing it. The app shows the last four characters and the
  expiry date only.
- Replaced atomically. The old token stays until the new one passes one real call. A
  failed check changes nothing.

## 5. Never in clear

Logs, errors, notices, diagnostics bundles, answer ledgers and relay traffic. A notice such
as "token expires in N days" carries a date, never token text. A canary test plants a token
and scans the profile, logs, bundles and relay traffic for it, as the answer-words canary
does (#1184).

## 6. Rolling restart (slice 4)

- #719's Install between turns (docs/app-windows.md) restarts the app for an update at a gap
  in agent work; it does not restart sessions. This restart of sessions that still hold an
  old token is new behaviour specified here and built in slice 4, and it reuses that gap rule
  rather than a second timing path.
- The host never types into a waiting question or approval to restart. A session with a
  waiting question or approval is skipped until it is answered (the #954 ledger), idle
  sessions go first and coordinators last.

## 7. Expiry notice (slice 2)

A 401 from the CLI raises "token expired" with no token text. The early warning uses the
stored expiry date, from day 5.

## Review

Slice 3 changes how a token is stored and sent, so it is a sensitive change and needs two
model families.
