# Answering a Claude Code permission prompt from a device (#1003)

Status: **design and contract only; sensitive. No code path in this change can produce an allow** (a deny, or no output, only), because the one thing an allow
needs, an authenticated channel for a device's answer (#3847), does not exist yet. Nothing here is registered or on by default: PR1 adds the hook-side code and
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
- **Which request, and no replay.** The event carries no request id, so the id is a hash of the session, tool and input (16 hex digits). Each prompt
  also gets a fresh one-time **nonce** the hook keeps in its own pending record; a decision must carry the **proof** for this prompt (a hash of
  the nonce with the request id, session, tool and the **full** input), must be dated **no earlier than the prompt began**, and is **consumed** (claimed
  by a move, deleted) the moment it is read, whether usable or not; any older file is removed before the prompt is recorded. So one answer settles
  one prompt and cannot be reused for the next identical one, and a changed command is a different prompt. A decision for another request or proof,
  older than the prompt, over two minutes old, dated more than five seconds ahead, over 4 KiB, with an unknown verdict or unreadable, is refused.
- **NOT AUTHENTICATED (stated plainly).** `principal: "person"` is text in a file in the user's profile (`agent-bridge-claude`, the folder family
  `host-hooks.md` says has no ACL on Windows, where an unauthenticated local file never drives an answer, #3847). Any process of the same user can read
  the pending record, including its nonce, and write a matching decision, an agent's own tool call included. The nonce stops **replay and
  cross-prompt reuse, not forgery**, and "an agent's decision is refused" holds only for a writer that labels itself an agent. **So this feature must
  stay off until the host answers through a channel an agent cannot write** (a host-only, ACL-protected or signed answer from a device that the relay
  has authenticated), and PR2 does not turn anything on before that is designed, tested and approved. Until then this contract is the hook side only.
- **What is shown.** The device shows the host's bounded, escaped preview of the command (the existing escaped-approval rules, PR2). **A truncated command is never answerable**: the id binds the full input, but a person would see only a prefix. The hook's
  decision carries no command text and the log carries reasons only.
- **Default off, checked in code.** `ClaudeApproval.Enabled(setting)` is false whatever the setting says while `AuthenticatedChannel` is false, and a test pins it.
- **Default off (setting).** The existing `permissions.remoteApproval` defaults **on** (it governs Copilot approvals today), so this uses its own setting,
  **`permissions.claudeApproval`, default off** (PR2), registered only when on. Off means no hook, and the terminal prompt as today.
- **Timeout.** The hook's wait (100 s, measured on a monotonic clock so a stepped wall clock cannot stretch it) is shorter than the hook timeout Claude is
  given (120 s, a test pins it), so the hook ends itself with no output rather than being cancelled. A read that fails is tried again. What Claude does when a hook is cancelled is not documented; the design does not rely on it.
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
fields (`request`, `proof`, `verdict`, `principal`, `at`); an `allow` verdict is refused as `unavailable` and only a `deny` is ever printed; the verdict check above; the exact hook output; and the bounded wait on a fake
clock. It is not called by `HookRunner`, not registered, and writes nothing. Tests: `ClaudeApprovalTests`.

## PR2 (not started)

The host registers the hook only when `permissions.claudeApproval` is on; records the pending approval (tool, bounded escaped preview) for the
session; lists it as answerable (not `atMachine`) when the preview is readable; writes the decision file when a person's device answers; the app and
push show it; `host-answers.md`'s Claude row changes. It needs the live check above first.
