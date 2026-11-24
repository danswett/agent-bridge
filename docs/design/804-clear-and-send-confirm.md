# Clear and send, slice 3: the signed person confirm, the policy and the draft source

Design for [#804](https://github.com/danswett/agent-bridge/issues/804), slice 3. Slice 1 (#3181, the guarded
person-only clear) is merged and dormant; slice 2 (#4328) shows a person what is in a refused reply's box.
This note decides what makes the clear reachable, and stops at the one choice that departs from the merged
foundation. No code yet. **Security-sensitive: an independent review is required.** It builds on the design
note in #804's comments (§1 to §7); where it differs, this one says so.

## 1. What slice 3 adds

1. **A wire field** on `session.reply`: `clear`, a signed command from a person's device.
2. **A verifier** (`ReplyClearConfirmations`) that turns `clear` into the host-only
   `ReplyRequest.ClearConfirmation` the foundation already consumes.
3. **A policy**, `replies.clearAndSend`, default **off**, and the hello capability `sessions.replyClear`.
4. **A production `ClearDraftSource`**: the lossless draft the foundation requires. See section 5.
5. `ComposerInput.clearable` becomes true only when all of its conditions hold (section 4).

Nothing in this slice makes a clear happen for an agent, the local pipe, MCP, Lite or Home Assistant, and none of
them can carry a `clear`.

## 2. The wire field

`ReplyRequest.clear`: a `SignedCommand` (the same type #836 uses for `confirm`):

| Field | Value |
|---|---|
| `action` | `session.reply:clear` |
| `target.machine`, `target.session` | this machine, the reply's session |
| `target.shown` | the `input.digest` the person saw (SHA-256 of the whole escaped box) |
| `request` | this reply's own `request` UUID |
| lifetime | at most 5 minutes, signed on the press |

It is read like `confirm`: a host that does not know it ignores it and refuses the reply as today, so older hosts
fail closed. It never travels on the local pipe, MCP or Lite; those channels cannot hand a verified confirmation to
`ReplyService` (the type is host-only, as for answers).

## 3. The verifier, shared with #836

`EscapedApprovals` already holds the checks a person's confirmation needs. They move, unchanged, into one
`PersonConfirmations` helper used by both, so there is one place that decides "a person's approved device signed this":

- through the relay, on a `SecureChannel`, from the device that signed it (`c.Principal.Device == peer`), never this machine;
- the relay lists the device as `pwa` or `app`; an independently pinned person key (Trust);
- the device is in the approved list: `permissions.escapedApprovalDevices`, otherwise the folder-trust devices while folder trust is on;
- the MAC is for this machine, the action is `session.reply:clear`, the session and `request` are the reply's, the lifetime is short.

What is new is the target check, made **inside the typing slot**: `target.shown` must equal the digest of the box as
the host reads it then. A box changed since the person saw it is refused again, with the new `input`. The #836 suite
is generalised and must still pass.

`ClearConfirmation` is the function the foundation calls with that digest; the verifier returns `null` (ok) or the
refusal. It is rechecked at the fence before the keys and again before the reply, as the foundation already does.

## 4. Policy, capability and `clearable`

- `replies.clearAndSend`: `confirm` or `off`. **Default `off`.** It stays off until the owner's open item in #804 §6
  (a check of the clear keys on a disposable test machine) has passed; then it may default to `confirm` by a separate
  decision. Settings shows it, as for escaped approvals.
- `sessions.replyClear` is offered in hello to a device only when it could succeed (advisory, as `approveEscaped`).
  Every request is checked again.
- `input.clearable` is true only when: the session is Copilot, the policy is `confirm`, the box is complete (slice 2's
  reader), the reply is person-class (`Steer`), the channel is a device the verifier would accept, and a draft source
  is available (section 5). Otherwise it is false and the reason says why.

## 5. The draft source (needs a ruling)

The foundation refuses to delete a draft it cannot return whole, and requires `ClearDraftSource`: the original
composer text, because "display-normalized screen text alone cannot authorize deletion".

**The finding.** The host's only view of the box is the screen rows. The reader trims each row's outer spaces, and the
console pads a row to the box width, so a draft's **trailing spaces cannot be told from padding** and its leading
spaces only from the prompt glyph. Interior spaces survive. No other source of an unsent draft exists: Copilot does
not persist it, and the host does not see the keys typed at the machine. So a production source cannot be
byte-lossless today; it can only be exact to **outer whitespace**.

**Options** (one is needed before the code is written):

- **A. Screen-derived source, outer whitespace named as the one loss (recommended).** `ClearDraftSource` returns the box
  text as the reader sees it, including leading spaces after the prompt and excluding trailing padding. The digest, the
  confirm sheet and `cleared` all use that same text, so the person confirms what is deleted *as shown*. The docs and
  the sheet say that trailing spaces are not kept. Everything else the foundation requires (single row, 500 characters,
  fully visible, two empty reads) stays. Cost: this changes the foundation's wording ("every space"), so it is an owner
  CHANGE, not mine to make.
- **B. Do not ship a source.** `clearable` stays false everywhere; slice 3 lands the verifier, policy and wire field dormant.
  Safe, but "Clear and send" still never works, which does not help the refusals that bite today.
- **C. A cell-exact console read** that can see trailing spaces. A larger host change (injector and reader), untested on
  the real CLI, and its own review. Not for this slice.

## 6. Size and order

Over 400 whole lines, so two PRs, each tests first:

- **3a**: the wire field and its vectors, `PersonConfirmations` factored out of `EscapedApprovals`, the verifier, the
  policy and capability (default off). No producer of `clearable` yet. About 300 lines.
- **3b**: the draft source (per the ruling), the wiring in `SessionService` that supplies `ClearConfirmation` and
  `ClearDraftSource` to a person-class reply on a relay channel, and `clearable` going true. About 250 lines.

## 7. Tests (each fails without its piece)

- The verifier: the wrong device, an agent kind, a key not pinned, a machine, the pipe, MCP, Lite or HA, the wrong
  action, session, request or digest, expired, too long, this machine. The #836 cases still pass through the shared helper.
- A `clear` on a channel that is not a person's relay channel is never accepted (no `confirm` path through the pipe or Lite).
- Policy `off`: `clear` is refused with the reason and nothing is typed or cleared; the hello does not offer the capability.
- A box changed between view and clear is refused with the new `input` (digest checked in the slot).
- `clearable` is false for Claude, Codex, an incomplete box, an agent or unclassified reply, policy `off`, and a missing source.
- Protocol vectors for `clear` (re-signed by the PWA); the foundation's clear tests are unchanged.
- Lite stays content-free: the refusal carries `composerNotEmpty` and no box text.

## 8. Out of scope

The PWA and Windows app sheet (slice 4); Claude and Codex clear keys; shell mode; the keeper path (#2135), which stacks
on this. No live key probe is made; the owner's test-machine check (#804 §6) is still open.
