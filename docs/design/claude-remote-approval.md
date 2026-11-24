# Answering a Claude Code permission prompt from a device (#1003)

Status: **design plus PR1 (the hook-side contract); sensitive.** Nothing here is registered or on by default: PR1 adds the hook-side code and
its tests only. Part of the program C "fleet tools into Slipgates" work; #1003 is the No-RDP gap (a Claude permission prompt sends the person to
the machine). The host side (the registration, the marker, the app and push) is PR2.

## The mechanism

Claude Code has a `PermissionRequest` hook that runs when it is about to ask the person for a tool permission, and may print a decision
(`hookSpecificOutput.decision.behavior` `allow` or `deny`). A **real decision**, not keystrokes typed into a terminal (the Codex approval path types
keys and is always `unconfirmed`). The hook would record the prompt for the host, wait a bounded time for a decision file the host writes after a
person answers on a device, and print that decision. With no usable decision it prints nothing and Claude shows its own prompt at the terminal.

## Threat model

- **Who may approve.** Only a **person's** decision is taken (the decision file's `principal` is `person`). An agent's decision is refused,
  logged by its reason only, and ignored. Nothing is ever approved automatically: no default, no timeout-allow, no "remember".
- **Which request.** The event carries no request id, so the id is a hash of the session, tool and input (16 hex digits). A decision for another id
  is refused; a decision file older than two minutes or dated more than five seconds ahead is refused; an unknown verdict or unreadable file is
  refused. A changed command is a different request, so approving one command cannot approve another.
- **What is shown.** The device shows the host's bounded, escaped preview of the command (the existing escaped-approval rules, PR2). The hook's
  decision carries no command text and the log carries reasons only.
- **Default off.** The existing `permissions.remoteApproval` defaults **on** (it governs Copilot approvals today), so this uses its own setting,
  **`permissions.claudeApproval`, default off** (PR2), registered only when on. Off means no hook, and the terminal prompt as today.
- **Timeout.** The hook's wait is shorter than the hook timeout Claude is given, so the hook ends itself with no output rather than being
  cancelled. What Claude does when a hook is cancelled is not documented; the design does not rely on it.
- **Not a new privilege.** The person who answers could already answer at the machine; this moves the same decision to a signed-in device of the
  account, through the relay, as the Copilot approvals do. It does not widen what an agent can do.

## What the documentation says and does not say (read, not run)

From the Claude Code hooks reference (read through an assistant, not on a machine): `PermissionRequest` exists, receives `session_id`, `tool_name`,
`tool_input` and `permission_mode` (no tool-use id), and prints the decision above (`deny` may carry a `message`). It runs when Claude is about to
prompt, MCP tools included. **Not documented, so not assumed:** the Claude Code version that introduced it (so whether v2.1.330 honours it is
**unconfirmed**); whether it fires for a prompt that a **PreToolUse hook forced with `permissionDecision: "ask"`** (the #4241 hidden prompts came
from a plugin's PreToolUse hook, so this slice may not see them); whether it fires for `AskUserQuestion` (a separate hook already handles it);
what happens when a hook times out; and whether it fires under `bypassPermissions` (by its wording it does not, since no prompt occurs).
A live check on a build is needed before PR2 turns anything on; none was done here.

## PR1 (this change): the hook-side contract only

`ClaudeApproval` (`AgentBridge.Hooks`): the request id; the decision file's path (`<runtime>\agent-bridge-claude\<session>.decision.json`) and
fields (`request`, `verdict` `allow|deny`, `principal`, `at`); the verdict check above; the exact hook output; and the bounded wait on a fake
clock. It is not called by `HookRunner`, not registered, and writes nothing. Tests: `ClaudeApprovalTests`.

## PR2 (not started)

The host registers the hook only when `permissions.claudeApproval` is on; records the pending approval (tool, bounded escaped preview) for the
session; lists it as answerable (not `atMachine`) when the preview is readable; writes the decision file when a person's device answers; the app and
push show it; `host-answers.md`'s Claude row changes. It needs the live check above first.
