# Usage sources: live Claude and Codex usage without waiting for the agent (#197)

**Verdict: ask the agents, not the vendors.** Both agents can produce a fresh reading
without a model turn. The host can trigger that, and the agent then uses its own
credentials, refresh and locking. The host never holds or refreshes a token itself.

- **Claude.** Claude Code saves its last plan-usage answer in `~/.claude.json`
  (`cachedUsageUtilization`). The host reads that file and needs no credentials. When the
  reading is stale, the host runs
  `claude -p "/usage" --safe-mode --no-session-persistence`. `/usage` is a local command
  with a print-mode form. It runs no model turn, and it refreshes Claude Code's token
  through Claude Code's own lock. **Fallback:** today's read-only token path, only if the
  owner opts in (see Terms), and otherwise the last reading ages as it does now.
- **Codex.** The host sends `account/rateLimits/read` with
  `excludeResetCreditDetails: true` to Codex's own app-server. With no Codex running,
  that's a short-lived `codex app-server` over stdio. With the managed daemon running,
  the request goes to the daemon over its WebSocket control socket, so Codex never has a
  second credential writer. Codex uses its own `auth.json`/keyring handling and refresh,
  and no thread or turn is started. **Fallback:** the transcript reading the host uses
  today.
- **Neither read spends the allowance.** Probe only when the reading is stale: at most
  every 15 minutes while no session of that agent is open, and at most every 5 minutes
  while one is. Back off after failures, and never hard-kill a probe.
- **Rejected:** a host-side Claude token refresh (candidate a). It would mean copying an
  undocumented, changing lock-and-compare-and-swap protocol across three storage
  back-ends. Anthropic's terms also forbid third parties from collecting or intermediating
  Claude.ai tokens. The same terms raise a question about **today's** Claude reader,
  which the owner should decide (see [Terms](#terms)).
- **No live tests were run.** Everything below comes from installed code, public source
  and public docs. Experiments E1 to E14 must pass on a disposable VM before the build
  issue is merged.

## Question

The Claude and Codex rows go stale whenever those agents haven't run recently on the
machine that reads them (#197). Claude's OAuth token expires about hourly, and the
host deliberately never refreshes it. Codex has no endpoint in use: the host reads
rate-limit events from Codex's transcripts. Issue #197 asks four things:
1. A safe, live source for Claude.
2. A safe, live source for Codex.
3. What each source costs against the allowance and rate limits.
4. Any terms concerns.

## Method and sources

No token, credentials file or OS credential store was read, and no refresh or usage call
was made. No Claude Code or Codex session was started. The only commands run were
`claude --version` and `claude --help`.

| Source | Version | What was read |
|---|---|---|
| Claude Code, native Windows build at `~/.local/share/claude/versions/2.1.291` | 2.1.291, built 2026-10-05 PT | The JavaScript bundled in the binary, read as text (strings and minified code). Minified names change on every build, so the code is cited by stable string anchors (in `code` below), not by function names. |
| Claude Code docs | live, 2026-11-01 | [CLI reference](https://code.claude.com/docs/en/cli-reference), [commands](https://code.claude.com/docs/en/commands), [costs](https://code.claude.com/docs/en/costs), [status line](https://code.claude.com/docs/en/statusline), [security](https://code.claude.com/docs/en/security), [legal and compliance](https://code.claude.com/docs/en/legal-and-compliance) |
| Anthropic terms | live, 2026-11-01 | [Consumer Terms](https://www.anthropic.com/legal/consumer-terms) |
| Codex | `openai/codex` tag `rust-v0.166.0` (commit `6ff1e1c`, 2026-10-29), the version #154 found on the rehearsal VM | `codex-rs/` (paths below are relative to it) |
| Codex docs | live, 2026-11-01 | [Authentication](https://developers.openai.com/codex/auth), [CI/CD auth](https://learn.chatgpt.com/docs/auth/ci-cd-auth), [app-server](https://developers.openai.com/codex/app-server), [pricing](https://developers.openai.com/codex/pricing) |
| OpenAI terms | live, effective 2026-01-01 | [Terms of Use](https://openai.com/policies/terms-of-use/) |
| This repo | `main` at `ef23613` | `src/AgentBridge.Host.Core/Usage/*`, `docs/protocol/v1-usage.md`, #154, #172 |
| agent-ha-bridge | `main` at `ef5454e` | `hooks/daemon-usage.ps1` (`Get-BridgeClaudeAllowance`, `Get-BridgeCodexAllowance`) |

Codex isn't installed on the machine where this spike was written. Its findings come
from the tagged source, so E7 to E10 have to check the shipped binary against them.

## What the host does today

| Agent | Collector | Source | Why it goes stale |
|---|---|---|---|
| Claude | `ClaudeUsageCollector` | `GET api.anthropic.com/api/oauth/usage` using `claudeAiOauth.accessToken` from `~/.claude/.credentials.json`. An expired token is skipped, and a 401 counts as "nothing to say" | The token lives about an hour. The host never refreshes it, because a refresh rotates the refresh token and could sign Claude Code out (comment in `ClaudeUsage.cs`). agent-ha-bridge `Get-BridgeClaudeAllowance` behaves the same way |
| Codex | `CodexUsageCollector` | The newest `payload.rate_limits` in the five newest `~/.codex/sessions/**/*.jsonl` | Codex writes them only during turns (`token_count` events) |

---

## Claude

### Candidates

| | Candidate | Verdict |
|---|---|---|
| a | The host refreshes the token while no Claude Code runs, and writes the new tokens back in Claude Code's own format and location | **Rejected** (failure modes, terms) |
| b | Claude Code refreshes its own token through a cheap non-model invocation: `claude -p "/usage" …` | **Recommended** as the trigger |
| c1 | Claude Code's saved last reading, `cachedUsageUtilization` in `~/.claude.json` | **Recommended** as the source the host reads |
| c2 | Claude Code's `statusLine` input, which includes `rate_limits` | Live only while a session runs, and it takes over a user setting. Not recommended |
| c3 | `claude auth status` | Prints no usage and doesn't refresh. **Use it to detect a sign-out** |
| — | Today's direct read with the stored access token | Fallback only, owner opt-in (Terms) |

### Evidence

**C-1. Where Claude Code keeps its credentials (2.1.291).**
- Storage goes through one interface with three back-ends:
  - **File**: `~/.claude/.credentials.json`, or `$CLAUDE_CONFIG_DIR/.credentials.json`.
    It's written with mode `0600` (anchor `".credentials.json"`, `name:"plaintext"`). The
    record is `claudeAiOauth: {accessToken, refreshToken, expiresAt (ms), refreshTokenExpiresAt, scopes, subscriptionType, rateLimitTier, clientId}`.
  - **Windows Credential Manager** (`name:"windows-credman"`), through Bun's secrets API.
    The service is `Claude Code-credentials`, with a short hash suffix when
    `CLAUDE_CONFIG_DIR` is set, and the account is `claude-code-user`. A large value is
    split into numbered chunks, and the file is a fallback.
  - **macOS Keychain**, per the [security docs](https://code.claude.com/docs/en/security):
    "stored in the macOS Keychain when available. On Linux they're stored in a file with
    mode `0600`, and on Windows in a file…".
- **The Windows store is being rolled out behind a flag.** It's chosen when
  `cachedGrowthBookFeatures.tengu_windows_credman` is `true` in `~/.claude.json`, or when
  `CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` (anchor `tengu_windows_credman`). Once a user is
  in that cohort, `.credentials.json` stops being current. **Today's
  `ClaudeUsageCollector` would then go silent** with no error, because it reads only the
  file.

**C-2. How Claude Code refreshes.**
- **When:** within 5 minutes of `expiresAt` (`n+300000>=e`), or after a 401.
- **Locking:**
  - Claude Code takes a directory lock at `~/.claude/.oauth_refresh.lock` (anchor
    `".oauth_refresh.lock"`), plus a legacy lock at `<realpath of the config dir>.lock`.
  - Both are `proper-lockfile`-style: `mkdir`, an mtime heartbeat every 5 s, stale after
    60 s.
  - Claude Code retries the lock 5 times, 1 to 2 s apart. After that it can take over the
    lock from a holder whose heartbeat has stopped (`tengu_oauth_token_refresh_lock_dead_holder_takeover`).
- **Under the lock**, Claude Code re-reads storage. If another process already changed the
  access token, it adopts that token (`tengu_oauth_token_refresh_race_resolved`).
- **The request:** a `POST https://platform.claude.com/v1/oauth/token` with
  `grant_type: refresh_token`, the refresh token, the client id and the scopes.
  - If the response carries a `refresh_token`, it replaces the old one. If not, the old
    one is kept. That's rotation-ready, and the server's behaviour is checked in E2.
  - Claude Code may also fetch the profile and rewrite `oauthAccount` fields in
    `~/.claude.json`.
- **Saving is a compare-and-swap.** The new tokens are written only if the stored refresh
  token still equals the one that was posted, or is empty. Otherwise Claude Code adopts
  the newer write (`tengu_oauth_refresh_save_adopted_newer_write`), and it retries the
  write 3 times.
- **This is how a sign-out happens.** Suppose the token endpoint rejects the refresh token
  (`invalid_grant`), and nobody else has refreshed in the meantime. Claude Code then marks
  that token dead and writes `refreshToken: ""`, `accessToken: ""`, `expiresAt: 0` to
  storage (`tengu_oauth_refresh_token_cleared_on_disk`). From then on, that machine is
  signed out until `/login`.
- **A running Claude Code notices outside writes.** It watches the mtime of
  `.credentials.json` (or a version probe of the Windows store) and re-reads storage after
  a 401 (`tengu_oauth_401_recovered_from_disk`).

**C-3. How Claude Code reads usage, and what it saves.**
- **The request:** `GET /api/oauth/usage`, with a 5 s timeout and `refreshOAuth: true`.
  So this call refreshes the token itself when needed. Variants such as
  `?at_wall=1&skip_spend=1` also exist.
- **The response** is saved to `~/.claude.json` as
  `cachedUsageUtilization: {fetchedAtMs, accountUuid, utilization: <the response body>}`.
  - It's saved at most once every 60 s.
  - Claude Code trusts it for 1 hour.
  - It's dropped when the signed-in account changes.
  - The anchor is `cachedUsageUtilization`, with constants `60000` and `3600000` beside
    it.
  - The body is the same shape `ClaudeUsageCollector.Convert` already parses (`limits[]`,
    `seven_day_breakdown`).
- **Rate limits:** a snapshot less than 60 s old answers without calling the endpoint
  ("Usage read answered from a snapshot … endpoint not asked"). The
  [costs docs](https://code.claude.com/docs/en/costs) say the request "most often" fails
  because "the usage endpoint is rate limited", and that `/usage` then shows "the last
  usage bars it loaded on this machine within the past 60 minutes".

**C-4. `/usage` works in print mode.**
- The command table has two `/usage` entries. One is interactive. The other is
  `type:"local"`, `supportsNonInteractive:!0`, enabled only when not interactive.
- That second entry calls the usage read in C-3, so it saves `cachedUsageUtilization`. It
  then prints lines such as `Current session: N% used · resets …` and
  `Current week (all models): …`. It also attaches a structured `usageReport` with the
  same `rate_limits`.
- A `local` command sends no messages to a model. The
  [commands docs](https://code.claude.com/docs/en/commands) group `/usage` with `/status`
  as commands that "run immediately without interrupting the response".
- Flags (from `claude --help`, 2.1.291):
  - `--safe-mode`: "all customizations (CLAUDE.md, skills, installed plugins, hooks, MCP
    servers…) disabled… Auth, model selection… work normally". This keeps our own hooks,
    and agent-ha-bridge's, from seeing the probe as a session.
  - `--no-session-persistence`: "sessions will not be saved to disk".
  - **Not `--bare`**: "OAuth and keychain are never read".

**C-5. `claude auth status` is read-only.**
- Its handler reads only stored state. It prints `loggedIn`, `authMethod` and
  `subscriptionType` as JSON (the CLI reference documents
  `authMethod ∈ none|claude.ai|oauth_token|api_key|api_key_helper|third_party`), and it
  exits 1 when not logged in. The handler has no refresh call.
- After the dead-token clearing in C-2, `loggedIn` reads `false`. **That makes it the
  detector for that kind of sign-out**, and the host never has to open the credentials
  itself. Other failures, such as an account on hold, leave `loggedIn` `true`, so the
  probe's own result counts too. What runs at process start-up still needs checking
  (E1).

**C-6. The status line carries rate limits, but only during a session.**
- The [status line docs](https://code.claude.com/docs/en/statusline) list
  `rate_limits.five_hour.used_percentage`, `rate_limits.seven_day.used_percentage` and
  their `resets_at`.
- In the bundle they come from the rate-limit headers on model responses, so they're live
  only while Claude Code is making calls.
- Using them means owning the user's `statusLine` setting, which is visible and may
  already be in use.

### Failure modes

| Candidate | Failure | Claude Code starting mid-way | Sign-out risk and how to avoid it |
|---|---|---|---|
| **a. Host refresh** | Every back-end must be copied exactly: file, Windows store (flag-gated, chunked), Keychain. So must both locks with their heartbeat, the re-read under the lock, the compare-and-swap write and the `~/.claude.json` profile update. A format change in any Claude Code release breaks the copy silently | Claude Code waits about 5 to 10 s on the lock. It then takes the lock over if the host's heartbeat lapses, and refreshes with the old token | **High.** Two refreshes of one rotating token: the loser gets `invalid_grant`, and if that loser is Claude Code, it clears its storage (C-2). Avoiding this needs the full protocol above; "no Claude Code process running" isn't enough, because one can start at any moment. A crash between the token response and the write loses the rotated token |
| **b. `claude -p "/usage"` probe** | The CLI may be missing, not on `PATH`, or a different version from what was read here. The print form of `/usage` may change. A start-up error or rate limit only leaves the old reading | The probe *is* Claude Code, so the two contend on `.oauth_refresh.lock`, and one adopts the other's token (`race_resolved`). That's the same as a user running two Claude windows | **Low, with one rule:** never kill a probe while it may be refreshing. If it dies after the token endpoint answers and before the compare-and-swap write, the rotated token is lost and the next refresh signs the machine out (E4 shows this deliberately). So use a generous timeout (120 s), stop gracefully, and leave a running probe alone at host shutdown. Detect a sign-out with `claude auth status`, then stop probing and say so |
| **c1. Read `cachedUsageUtilization`** | Undocumented key. Absent until Claude Code has read usage once. Belongs to the account in `oauthAccount.accountUuid` (it's cleared on a switch) | None: read-only, through the `.claude.json` read the host already makes | **None:** no credential is touched |
| **c2. Status line** | Only while a session makes calls. Conflicts with the user's own status line | n/a | None |
| **Today: direct read** | Stale after about an hour, and blind to the Windows store (C-1) | n/a (read-only) | None, since it never refreshes |

### Allowance, rate limits and a safe pace

- **Allowance.** None of these reads is inference.
  - `/usage` in print mode is a `local` command, so no model call is made (E1 checks the
    plan percentages don't move).
  - `GET /api/oauth/usage` is the read that Claude Code's own `/usage` and its status bars
    make.
  - A probe start-up also makes non-inference calls, such as feature flags and telemetry.
    E1 lists them.
- **Rate limits.** The usage endpoint is rate-limited per account (costs docs). Claude Code
  copes with that by keeping a 60-minute fallback, never asking twice within 60 s, and
  saving the result.
- **Safe pace.**
  - Read `cachedUsageUtilization` at the host's existing pace (it's a local file).
  - Probe only when that reading is older than the idle interval (15 min), at most every
    15 min while no Claude session is open here and every 5 min while one is.
  - Run one probe per profile at a time.
  - After a failure or a rate-limited answer, back off: double the wait, up to 2 h.
  - Once usage is shared between hosts (#172), skip the probe when a peer's reading of
    the same account is fresher than the idle interval.
  - E5 measures the headroom before these numbers are relaxed.
- **A side effect that helps.** A probe that refreshes leaves a fresh access token in
  Claude Code's storage. agent-ha-bridge's read-only reader then works again too, without
  either bridge refreshing anything.

### Terms

- **[Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance), "Authentication and credential use":**
  > Anthropic does not permit third-party developers to offer Claude.ai login into their
  > own applications, or to route requests through Free, Pro, Max, or Ultra plan
  > credentials on behalf of their users. Moreover, developers may not collect, store, or
  > intermediate Claude.ai credentials or session tokens

  > Nor does it prevent an end user from signing in to the unmodified Claude Code binary
  > with their own Claude subscription

  These passages bear on each candidate as follows:
  - **Candidate a** collects and refreshes Claude.ai tokens in a third-party product. It's
    ruled out.
  - **Today's direct read** collects the access token and calls an Anthropic endpoint
    with it outside Claude Code. It routes no inference, and it runs on the user's own
    machine for the user's own account. But agent-bridge is a published product, and the
    sentence doesn't distinguish those cases. **Owner decision:** retire it once the
    recommended path is confirmed, or keep it as an owner opt-in (`usage.claude.directRead`,
    default off).
  - **Candidates b and c1** run the unmodified binary under the user's own sign-in, and
    read a file that binary writes. That's the case the second quote allows.
- **[Consumer Terms](https://www.anthropic.com/legal/consumer-terms) §3.7** forbid accessing the Services "through automated or
  non-human means, whether through a bot, script, or otherwise", "except when you are
  accessing our Services via an Anthropic API Key or where we otherwise explicitly permit
  it". The probe is scripted, but it uses Claude Code's documented non-interactive mode
  (`-p`), runs a local command, and makes no inference. That's a much weaker concern than
  a, but not none. Flag it to the owner, along with the pace above.
- **Copyright:** Claude Code is proprietary ("© Anthropic PBC. All rights reserved"). The
  build should rely on documented behaviour (`/usage`, `-p`, `auth status`, the flags)
  plus the one undocumented key it has to read, and it must not copy code from the bundle.

### Recommendation

1. Read Claude's allowance from `cachedUsageUtilization`:
   - It's in the profile's `.claude.json`, the file `UsageOptions.ClaudeAccountPath`
     already reads.
   - Accept it only when its `accountUuid` matches `oauthAccount.accountUuid`.
   - `measuredAt` is `fetchedAtMs`, and `source` is `cache`.
2. When that reading is stale (see pace), run, in a host-owned empty directory with no
   standard input:
   `claude -p "/usage" --safe-mode --no-session-persistence --output-format json`
   - Set `CLAUDE_CONFIG_DIR` for a non-default profile.
   - Then read the file again.
   - Never parse the text output when the file can be read instead.
3. Don't probe when `claude auth status` (checked at most hourly) says the machine isn't
   on a claude.ai login. When it says the machine is signed out, show "Claude Code is
   signed out on this machine" and stop probing until that changes.
4. **Fallback:**
   - First, the last reading ages, as now.
   - Then, if the owner opts in, today's read-only direct call with an unexpired stored
     token. It's file-only, as now, and is never used to refresh.

---

## Codex

### Candidates

| | Candidate | Verdict |
|---|---|---|
| a | The app-server protocol: start `codex app-server` (stdio), send `initialize` then `account/rateLimits/read` | **Recommended** when no Codex process is running |
| b | The same request through the managed daemon's WebSocket control socket (directly, or through `codex app-server proxy`) | **Recommended** when the daemon is running, once E9 passes (X-3). Otherwise don't probe |
| c | `/status` in the TUI | Interactive only. `codex exec` would start a turn. Rejected |
| d | `GET chatgpt.com/backend-api/wham/usage` with the token from `auth.json` | Rejected: it means reading credentials, keyring mode breaks it, the token needs refreshing, and the endpoint is undocumented |
| — | Transcripts (today) | Fallback |

### Evidence

**X-1. The request exists, is stable and is meant for this.**
- `account/rateLimits/read` is a v2 client request, not marked experimental
  (`app-server-protocol/src/protocol/common.rs:1386`). So are `account/read`
  (`:1526`) and the server notification `account/rateLimits/updated` (`:2071`).
- Its parameters were designed with passive readers in mind
  (`app-server-protocol/src/protocol/v2/account.rs:341-356`):
  - `excludeResetCreditDetails`: "Skip the separate reset-credit detail lookup for
    background usage polls."
  - `supportsLunaReserve` must stay false for passive readers. The backend client says
    "Opt in only for clients that can apply Reserve, not for passive account usage
    readers" (`backend-client/src/client/rate_limit_resets.rs:28`).
- **The response** (`account.rs:358-378`, `694-797`) has:
  - `rateLimits`, with `primary` and `secondary` windows: `usedPercent` (rounded to a
    whole number), `windowDurationMins` and `resetsAt` (Unix seconds), plus `planType`.
  - `rateLimitsByLimitId`, keyed by `limit_id` such as `codex`.
  - `accountId`, which is the ChatGPT **workspace**, not the user, and older backends may
    leave it out. Members of one Team or Business workspace each have their own limits,
    so `accountId` alone can't group readings. `account/read` adds the user's `email`
    (`account.rs:22-38`). Together, user and workspace make an identity for an
    `accountKey` (#172).
  - **The two requests aren't bound to each other.** If the login changes between them,
    the snapshot can belong to one user and the email to another. The handler does check
    the snapshot's account and user ids against the active login, but it exposes the
    result only by leaving `ordinaryUsageAllowed` non-null
    (`account_processor.rs:1245-1265`). The binding rule is in the recommendation.
- **The handler** (`app-server/src/request_processors/account_processor.rs:1171-1278`):
  - It requires ChatGPT auth, and otherwise returns "chatgpt authentication required to
    read rate limits".
  - It sends one `GET {chatgpt_base_url}/wham/usage`, or `/api/codex/usage`
    (`backend-client/src/client/rate_limit_resets.rs:124-129`), plus the reset-credit
    lookup unless that's excluded.
  - No thread, turn or model call is involved.
- **The TUI itself** sends this request on `/status`
  (`tui/src/chatwidget/slash_dispatch.rs:544`) and at start-up (`StartupPrefetch`,
  `tui/src/app/background_requests.rs:141`). The
  [pricing docs](https://developers.openai.com/codex/pricing) point users to `/status` to
  see remaining limits.
- **The handshake** is an `initialize` request with `clientInfo {name, title, version}`,
  then an `initialized` notification
  (`app-server-protocol/src/protocol/v1.rs:29-41`, `app-server-test-client/src/lib.rs:1593-1630`).
  The test client even has a `get-account-rate-limits` command (`lib.rs:432`, `:1692`)
  that does exactly this probe.
- `account/rateLimits/updated` is pushed only from `token_count` events, which means only
  during turns (`app-server/src/bespoke_event_handling.rs:1620-1645`). So subscribing
  wouldn't help when Codex is idle.

**X-2. Auth: Codex's own, with rotating refresh tokens.**
- The request uses Codex's `AuthManager`. Credentials live in `auth.json`, the OS
  keyring, `auto` mode or `ephemeral` mode (`cli_auth_credentials_store`,
  [auth docs](https://developers.openai.com/codex/auth)).
- **When it refreshes** (`login/src/auth/manager.rs:221-233`, `:3333-3351`): proactively
  within 5 minutes of the access token's JWT `exp`, or 8 days after `last_refresh`. It
  sends `POST https://auth.openai.com/oauth/token`.
- **Rotation:** the response may carry a new `refresh_token`. A reused one fails with
  `refresh_token_reused`: "your refresh token was already used. Please log out and sign in
  again" (`manager.rs:1705-1790`). So refresh tokens are single-use.
- **Running alongside other Codex processes:**
  - Each process takes the in-process refresh semaphore. Before refreshing, it does a
    "guarded reload": if the token on disk changed, it adopts that token
    (`manager.rs:3148-3195`, `reload_if_account_id_matches`).
  - Writes are serialised across processes by an `auth.lock` file lock
    (`login/src/auth/storage.rs:190-212`), and `persist_tokens` refuses to save when the
    stored **login** changed ("Account credentials changed during refresh",
    `manager.rs:1658-1700`).
  - **That's a login check, not a compare-and-swap on the refresh token.**
    `same_login_for_persistence` (`manager.rs:1647-1655`) compares the login owner,
    workspace and user ids. It compares refresh tokens only for legacy records without an
    owner id. So if two refreshes both succeed, the last writer wins.
  - **Reads aren't locked.** `FileAuthStorage::load` (`storage.rs:246`) reads without
    `auth.lock`, and `save` (`storage.rs:256-273`) truncates the file and then writes it.
    A process that reads while another writes can see a partial file.
  - The token exchange itself isn't serialised across processes. Two processes refreshing
    in the same instant can each send the same refresh token, and the loser gets
    `refresh_token_reused`, which Codex records as a permanent failure for that auth.
    Every refresh attempt starts with a guarded reload, so the loser's next attempt should
    adopt the winner's tokens. E8 must confirm that.
  - Codex expects this situation in general: "The CLI and extension share the same cached
    login details" (auth docs), and 0.166's managed daemon runs beside every TUI (#154).
    But each extra process that refreshes adds a writer, and the next point shows how to
    avoid adding one.
- **The managed daemon keeps the token fresh.** It refreshes the model catalogue every
  4 min 30 s (`app-server/src/models_refresh_worker.rs:10`), and each of those reads auth,
  which refreshes proactively. While the daemon runs, a probe rarely needs to refresh at
  all.
- **OpenAI's documented stance** ([CI/CD auth](https://learn.chatgpt.com/docs/auth/ci-cd-auth)):
  "the supported CI/CD strategy is not 'call the refresh API yourself.' It is 'run Codex
  and persist the updated `auth.json`.'"

**X-3. Where a probe should run, so it adds no second writer.**
- `codex app-server` over stdio is a standalone process. The managed daemon listens on
  `~/.codex/app-server-control/app-server-control.sock`
  (`app-server-transport/src/transport/mod.rs:56-75`), and a stdio app-server never
  binds or takes that socket. `codex app-server proxy` connects stdio to that socket
  instead (`cli/src/main.rs:641`).
- A standalone probe has its own `AuthManager`, so it can refresh and write `auth.json`.
  It's safe when it's the **only** Codex process. Beside a running Codex, it adds the
  unlocked read and last-writer-wins write described in X-2.
- So the probe should take one of three routes, depending on what's running:
  - **No Codex process:** a standalone probe. It's the only reader and writer, apart from
    a session that happens to start during its few seconds (E12).
  - **The managed daemon is running:** ask through the daemon. The daemon's own
    `AuthManager` answers, so no second writer exists.
    - **This route needs a different transport.** The control socket accepts WebSocket
      connections, not stdio's JSON lines
      (`app-server-transport/src/transport/unix_socket.rs:1-27`), and
      `codex app-server proxy` only copies bytes between stdio and that socket.
    - So the host either speaks WebSocket through the proxy's stdio, or connects to the
      Unix-domain socket itself (.NET's `SocketsHttpHandler.ConnectCallback` with a
      `UnixDomainSocketEndPoint`). Either way it does the upgrade, sends each message as
      a text frame, and ends with a WebSocket close.
    - It depends on E9 showing that this transport works and that a short client doesn't
      disturb the daemon.
  - **A Codex session without the daemon:** don't probe. The transcripts are fresh while
    it works.
- The host already ignores app-server processes in discovery
  (`ProcessInspector.IsCodexAppServer`, #154), and the proxy process needs the same
  treatment.
- Every standalone start refreshes the model catalogue online once
  (`message_processor.rs:407`, `models_refresh_worker.rs:41-58`). That's one more
  non-inference request per probe, and E7 lists them all.

**X-4. Ending the probe safely (still open).**
- **E13(a) to (c) have run on Linux.** [E13 on Linux](#e13-on-linux) corrects parts of
  this section and decides the shutdown rule.
- On Unix, closing standard input arms a 45 s watchdog that calls
  `process::exit(1)` (`app-server-transport/src/transport/stdio.rs:125-129`,
  `:168-182`). Closing too early could cut a refresh off between the token response and
  the save. Windows has no watchdog (`cfg!(unix)`).
- `account/rateLimits/read` gets its credentials through `auth()`
  (`manager.rs:2499-2513`). **When a proactive refresh is due,** `auth()` waits on the
  in-process refresh lock and adopts a refresh already in flight (`:3148-3195`), so that
  refresh has finished by the time the response arrives. **When none is due,** it
  returns the cached credentials straight away. A background refresh can then still be
  running after both responses arrive, for example when the start-up model-catalogue
  fetch's `auth()` finds a refresh due.
- So closing standard input after the responses removes the refresh caused by the
  probe's own request, but it doesn't guarantee that nothing is refreshing. The host
  can't see a background refresh from outside.
- **What could still be refreshing.** There are two kinds of refresh in a probe process.
  - **Proactive.** The model-catalogue fetch gets its credentials through `auth()`, so it
    refreshes only proactively, and a 401 there doesn't start a refresh
    (`model-provider/src/models_endpoint.rs:119-160`). If a refresh is already due when
    the usage request arrives, the request waits for it. The gap is the boundary: the
    token enters its 5-minute window after the usage read's `auth()` but before the
    process exits.
  - **Recovery after a 401.** At start-up the app-server also spawns workspace-routing
    discovery (`account_processor.rs:162-167`). When `/accounts/check` returns 401, that
    discovery runs `unauthorized_recovery()`, which refreshes **outside** the 5-minute
    window (`account_processor/workspace_routing.rs:401-445`). The usage read doesn't
    wait for it. `account/read` may wait, since it reports routing results, but whether
    that covers the refresh is unknown.
  - **That recovery has its own 15 s limit.** Discovery runs inside a 15-second timeout
    around an inline future (`workspace_routing.rs:302`, `:552-553`). A refresh still
    running at 15 s is cancelled by Codex itself, whether or not the probe has closed
    standard input. That's a hazard of any app-server start, including the user's own,
    on a slow token endpoint. Each probe is one more start, but discovery only recovers
    after a 401, meaning a rejected access token.
  - **EOF can also cancel it straight away, on every platform.** When the stdio
    connection closes, the processor loop exits (`app-server/src/lib.rs:1215-1218`)
    without joining detached start-up work, such as discovery
    (`account_processor.rs:163-168`, `message_processor.rs:882-888`). Runtime teardown
    then drops whatever refresh is running. So the 45 s watchdog isn't the only way EOF
    can cut a refresh off.
- **Consequence:** closing standard input is safe only if no refresh is running at that
  moment, and the host can't see that from outside. Any refresh a detached task started
  can be cut off at EOF.
- **Interim rule:** wait for all the responses, then a grace period of at least 20 s
  (longer than discovery's 15 s limit), then close standard input and let the process
  exit by itself. E13 decides the final mechanism:
  - keep the grace period, if E13 finds no refresh that can outlive it;
  - use the daemon route (X-3) whenever it's available, since it has no process to end;
  - if neither is safe, drop the standalone route: without the daemon, Codex falls back
    to transcripts.
- **The reporting timeout is separate from the process lifetime.** After 60 s with no
  answer, the host stops waiting and reports nothing new, but it leaves the process
  running. Only after a hard ceiling (10 min) does it treat the process as hung.

**X-5. Detecting a Codex sign-out.**
- `codex login status` only reports stored credentials (`cli/src/login.rs`). A refresh
  token that has been revoked or reused still prints "Logged in using ChatGPT", because
  permanent refresh failures live only in the failing process's memory.
- `account/read` discards the outcome of the refresh it's asked for with `refreshToken`
  (`account_processor.rs:1082-1104`). But when a workspace is selected, its
  routing-discovery path can fail with `RefreshTokenRejected`
  (`account_processor/workspace_routing.rs:418-432`). So it reports a permanent refresh
  failure in that case, and in no other.
- The verified signals are the probe's own `account/rateLimits/read` failing with an
  authentication error after a failed refresh, and, with a workspace selected,
  `account/read` failing with a rejected refresh token. E14 records the exact errors for
  a revoked token and for a reused one, with and without a selected workspace.
- After that error, stop probing. Start again after the 2-hour back-off or when a Codex
  session next runs here, whichever comes first.

### Failure modes

| Candidate | Failure | Codex starting or running mid-probe | Sign-out risk and how to avoid it |
|---|---|---|---|
| **a. Stdio app-server** | `codex` missing or a different version; protocol drift (the request is versioned v2 and stable); API-key login (an error, so keep transcripts); a slow start (the process is large); the backend down | If it runs beside Codex, it's a second `AuthManager` and a second writer. Codex reads `auth.json` without the lock while the probe may be truncating and rewriting it, so another Codex process can fail to load its auth; and the last writer wins (X-2). That's why it runs only when no Codex process does (X-3) | **Low when run alone.** A session starting during the probe's few seconds is the remaining window (E12). Two rules: (1) after the responses, wait the grace period before closing standard input. EOF tears the runtime down, dropping any refresh a detached task is running, and on Unix it also arms a 45 s exit (X-4). (2) One probe at a time. Detect a sign-out from the probe's own authentication error, not from `codex login status` (X-5) |
| **b. Through the daemon** | The daemon may not be running. Its control socket needs a WebSocket client, not JSON lines, and the socket's framing and lifetime are internal | The daemon's own `AuthManager` answers, so there's no second writer and no race | **Lowest for auth,** with no process to shut down. But it adds a client to a process that sessions depend on, and its effect on the daemon's idle exit is unknown (E9). Recommended whenever the daemon is running, once E9 passes |
| **d. Direct backend call** | Keyring mode has no file; a refresh is needed within about the token's life; undocumented path | n/a | Either it never refreshes (stale, like Claude today) or it refreshes (unsafe) |
| **Transcripts** | Move only during turns | n/a | None |

### Allowance, rate limits and a safe pace

- **Allowance:** none. `/wham/usage` is a read, and no turn is started. Each probe also
  makes a model-catalogue fetch and possibly a reset-credit count (excluded with the
  parameter above). E7 lists the rest.
- **Rate limits:** nothing is documented. The TUI makes this read on every `/status`
  and at every start. A pace no faster than a user typing `/status` a few times an hour is
  safe.
- **Safe pace:** the same rules as Claude.
  - Probe only when the newest reading, from transcripts or a probe, is older than 15 min.
  - At most every 15 min while no Codex session is open here, and every 5 min while one
    is.
  - One probe at a time. A 60 s reporting timeout that leaves the process running, and a
    doubling backoff up to 2 h.
  - Stand down for a peer's fresher reading once #172 lands.

### Terms

- **[OpenAI Terms of Use](https://openai.com/policies/terms-of-use/), "What you cannot do":**
  "Automatically or programmatically extract data or Output", and "circumvent any rate
  limits". The app-server is OpenAI's own published integration surface (IDE extensions
  and the [app-server docs](https://developers.openai.com/codex/app-server)), it's open
  source (Apache-2.0), and the request carries a parameter made for background polls. So
  candidate a is the use the vendor designed. A gentle pace keeps it clear of the
  rate-limit clause.
- Candidate d (calling the backend with Codex's token) runs against the CI/CD guide's
  "not 'call the refresh API yourself'" as soon as it needs a refresh. It's also the
  closest match for "programmatically extract".

### Recommendation

1. Add a Codex probe. It takes one of the routes in X-3, depending on what's running.
   With no Codex process it's a standalone `codex app-server` over stdio (JSON lines).
   With the managed daemon running it's a WebSocket connection to the daemon's control
   socket, after E9. It doesn't run beside a Codex session that has no daemon. Either
   way:
   - `CODEX_HOME` is set to the profile's home, and the working directory is owned by the
     host.
   - Send `initialize` (`clientInfo.name = "agent_bridge"`), then `initialized`, then
     `account/read` with `{ "refreshToken": false }`, then `account/rateLimits/read` with
     `{ "excludeResetCreditDetails": true }`, then `account/read` again.
   - Standalone: after the responses arrive, wait the grace period (at least 20 s), then
     close standard input and let the process exit by itself, until E13 settles the
     mechanism (X-4). Daemon: send a WebSocket close.
2. Map the result:
   - `rateLimitsByLimitId.codex`, or else `rateLimits`, gives the `primary` and
     `secondary` windows.
   - Labels come from `windowDurationMins`, as `CodexUsageCollector.Parse` does now.
   - `resetsAt` comes across as is, and `plan` is `planType`.
   - `accountKey` is the first 12 hex digits of
     `sha256("codex:" + lower(email) + ":" + accountId)`. It's set only when the identity
     is shown to be stable across the read:
     - both `account/read` answers give the same non-null email;
     - the snapshot has `accountId`, and its `ordinaryUsageAllowed` is non-null, which
       means the handler matched the snapshot to the active login when it answered;
     - **and the route rules out a switch and a switch back.** These checks don't catch a
       switch from A to B and back to A during the probe. The usage read would then be
       B's, checked as B's, while both emails say A. 0.166's stable API gives neither a
       user id on the snapshot nor a login generation, and `account/updated` carries only
       `authMode` and `planType` (`account.rs:618-621`). So:
       - **Standalone route:** a login change needs another Codex process, because the
         probe sends no login requests. The host checks the process list just before the
         first `account/read` and just after the second, and watches for sessions in
         between. If any other Codex process appears, there's no key.
       - **Daemon route:** another client can switch the daemon's login, and nothing
         visible shows it. Daemon-route readings have no key until Codex exposes a
         snapshot-bound user id. The backend already returns one, and the handler drops
         it (`account_processor.rs:1245-1265`). That's worth an upstream request.
     `account/updated` notifications don't count either way: routing discovery sends one
     to a newly initialized connection even when nothing has changed
     (`workspace_routing.rs:230-253`).
     Otherwise the reading has no key.
   - `source` is `api`.
3. Keep the transcript reading. The newer of the two readings wins. A transcript reading
   has no key, as today.
4. **A reading without a key must stay on its own.**
   - Today a missing key doesn't isolate a reading. `UsageMerge`
     (`src/AgentBridge.App.Core/Machines.cs:548`) and `mergeUsage` (`pwa/js/usage.js:71`)
     fall back to `account` and then to an empty string. So every keyless reading of an
     agent shares one row, and the newest replaces another user's allowance. Every Codex
     reading has worked that way so far, because none of them carries a key.
   - The build must keep keyless readings from different machines apart:
     - clients bucket a keyless reading by agent and machine, not by agent alone;
     - under #172, hosts never share a keyless reading with peers.
   - The cost: one person on two machines without a key sees two rows until the probe
     supplies a key.
5. **Fallback:** transcripts only, as today. That covers a missing Codex, an API-key
   login, a session running without the daemon, failures and back-off.

---

## Disposable-VM experiments

All of these run on a disposable VM (the `rehearsal-w` pattern, with a Linux VM for
E1, E2, E5 and E13 too). **The owner signs in there.** The rules:
- No experiment prints a token.
- Where a token's change must be seen, record only `sha256(token)[:8]` and the `expiresAt`
  or JWT `exp` claim.
- Capture traffic with `mitmdump` and a script that logs only method, host, path and
  status, never headers or bodies. Claude Code trusts the proxy through
  `NODE_EXTRA_CA_CERTS`, and Codex through `CODEX_CA_CERTIFICATE`.
- Delete the VM afterwards.

Pin versions: Claude Code 2.1.291 and latest, Codex 0.166.0 and latest.

| # | Experiment | Steps | Pass when |
|---|---|---|---|
| E1 | Claude probe, a fresh token | Sign in. Add a `SessionStart` hook that appends to a file. Note the plan percentages on claude.ai. In an empty directory, run `claude -p "/usage" --safe-mode --no-session-persistence --output-format json`. Repeat with `--output-format text` | Exit 0 in under 15 s. `cachedUsageUtilization.fetchedAtMs` moved. The hook file is unchanged. Nothing new under `~/.claude/projects`. The traffic shows no `/v1/messages`. Plan percentages unchanged. Record whether the JSON carries `usageReport`, and list every host and path called |
| E2 | Claude probe, an expired token | Leave the VM idle at least 75 min, so the access token has expired. Run the probe. Then start `claude` and send one short prompt | `expiresAt` advanced. Record whether the refresh-token hash changed (that's rotation confirmed). `.oauth_refresh.lock` came and went. The prompt works. `claude auth status` reports `loggedIn: true` |
| E3 | Claude concurrency | 20 times: with an interactive session idle and the token about to expire, fire the probe and a prompt in the session within the same second | No `loggedIn: false` and no empty `refreshToken` afterwards. Both succeed |
| E4 | Claude kill (negative) | Make `mitmdump` hold the token-endpoint response for 10 s, then kill the probe while it's held. Let Claude Code refresh next | Expected to **fail**, with Claude Code signed out. That confirms the never-kill rule. The owner signs in again |
| E5 | Claude pace | Probe every 5 min for 2 h, then every minute for 15 min | No rate limit at 5 min. Record where 429s or "rate limited" begin. Plan percentages unaffected |
| E6 | Claude on the Windows store | `CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1`, sign in, run E1 | `.credentials.json` isn't updated, the probe still saves `cachedUsageUtilization`, and today's direct reader goes silent (this confirms C-1) |
| E7 | Codex probe | ChatGPT sign-in. A script runs the five messages in the Codex recommendation over stdio | Exit 0 in under 15 s after standard input closes. Windows match `/status`. No new rollout under `~/.codex/sessions`. No `/responses` traffic. Record every host and path, whether `last_refresh` moved, and whether `accountId` and `ordinaryUsageAllowed` are present (they decide whether readings can be grouped) |
| E8 | Codex concurrency | With the managed daemon and a TUI session running, probe through the daemon every 30 s for 2 h, across at least one access-token expiry (record JWT `exp` to learn the lifetime). Then force the race once: two standalone app-servers refreshing at the same instant | Through the daemon: no `refresh_token_reused` in the Codex logs, TUI turns keep working, and `codex login status` stays signed in. The forced race: record which process loses, and whether its next request recovers through the guarded reload without signing out |
| E9 | Codex through the daemon (gates route b) | Connect to the running daemon's control socket with a WebSocket client: through `codex app-server proxy`'s stdio, and directly on the Unix-domain socket. Send the same five messages, then a WebSocket close. Do this 50 times. Also send the plain JSON lines once, to confirm that's refused | Both WebSocket paths answer, and the plain JSON lines don't. The daemon's process ID is unchanged, its sessions are unaffected, and its idle exit behaves as without the probe |
| E10 | Codex keyring mode | `cli_auth_credentials_store = "keyring"`, then E7 | Same result as E7, with no `auth.json` present |
| E11 | Invisible to the bridges | Run both probes with an agent-bridge dev host and agent-ha-bridge hooks installed on the VM | No session appears in either, and the host's worktree accounting ignores the probes |
| E12 | Codex half-written `auth.json` | Make the standalone probe refresh: expire the token, and slow the token response through `mitmdump` so the save lands while another process is reading. Start `codex` or `codex login status` in a loop meanwhile, 50 times | Record any load or parse failure in the reading process. If any occur, the build must not probe while another Codex process could start, beyond what X-3 already rules out |
| E13 | Codex outstanding refresh at EOF (**on the Linux VM**, since the 45 s watchdog is Unix-only) | Hold the token response for 50 s. (a) Close standard input as soon as the request is sent. (b) Close it only after the responses. (c) **Find a background refresh before trying to cut one off.** Two runs. (c1) Proactive: using the token lifetime from E8, start probes 50 times with the access token from 6 min to 4 min before its `exp`, so the 5-minute window opens during the probe. (c2) Routing recovery: with a fresh token and a selected workspace, have `mitmdump` answer `/accounts/check` with one 401, which makes routing discovery refresh outside the window. Hold the token response for 10 s, 20 s and 30 s in separate runs, and in each close standard input (i) as soon as the probe's responses arrive and (ii) after the 20 s grace period. In every run, log the time of every token-endpoint request and every probe response, including both `account/read` answers, plus the process exit time. If a token request is still open after the last probe response in (c1), hold it past the 45 s watchdog in a repeat run and record whether the token is lost. (d) Repeat (b) on the Windows VM | (a) Expected to **fail**: the process exits at 45 s, the rotated token is lost, and Codex signs out. (b) No loss. (c) For each run: either the caller and outcome of a refresh that outlived the responses, or the finding that none does. In particular, whether the second `account/read` waits for routing discovery's refresh. For (c2), record for each run whether the rotated token was saved, and which cancellation happened, if any: runtime teardown at EOF, discovery's 15 s timeout, or the watchdog. Together these decide whether the grace period holds, whether to use the daemon route, or whether to drop the standalone route (X-4). (d) The process exits by itself after EOF. The owner signs in again after each failure |
| E14 | Codex sign-out signal | (a) Revoked: revoke the session from ChatGPT's security settings, so the stored refresh token really is revoked, then probe. (b) Reused: expire the access token so the probe has to refresh, and have `mitmdump` answer that token request with HTTP 400 and `{"error":"refresh_token_reused"}`. That's the code Codex classifies (`manager.rs:1760-1790`), and nothing new gets saved. Run each with and without a selected workspace | Record the exact `account/rateLimits/read` and `account/read` errors for each. `codex login status` still says logged in, which confirms X-5 |

---

## Follow-up build issue

> **Title:** Usage: live Claude and Codex readings from the agents themselves
>
> Spike #197 (`spikes/usage-sources/RESULTS.md`) found that both agents can produce a
> fresh reading without a model turn, using their own credentials and refresh. The host
> triggers the read and never holds or refreshes a token itself. Readings then spread to
> every client through #172.
>
> **Before starting:** E1, E2, E4, E7, E8, E9, E12, E13 and E14 have passed on a
> disposable VM. The owner has decided whether today's direct Claude read is retired or
> becomes an opt-in.
>
> **Claude**
> - [ ] `ClaudeUsageCollector` reads `cachedUsageUtilization` from the profile's
>       `.claude.json`. It accepts the reading only when its `accountUuid` matches
>       `oauthAccount.accountUuid`. `measuredAt` is `fetchedAtMs`, and `source` is
>       `cache`. The parse is the existing `Convert`.
> - [ ] `ClaudeUsageProbe` runs
>       `claude -p "/usage" --safe-mode --no-session-persistence --output-format json`:
>       in a host-owned empty directory, with no standard input, `CLAUDE_CONFIG_DIR` set
>       for a non-default profile, and the executable found as launch finds it.
> - [ ] A probe runs only when the reading is older than the idle interval: at most every
>       15 min with no Claude session open, and every 5 min with one. Only one runs per
>       profile, and failures back off to 2 h.
> - [ ] A probe is never killed: 120 s timeout, graceful stop, and host shutdown leaves a
>       running probe alone.
> - [ ] `claude auth status` (at most hourly) gates the probe. Signed out gives the
>       failure "Claude Code is signed out on this machine" and no probes until it signs
>       in.
> - [ ] Today's direct token read is retired, or kept behind `usage.claude.directRead`
>       (default off), as the owner decided.
>
> **Codex**
> - [ ] `CodexUsageProbe` chooses its route by what's running (spike X-3):
>       - no Codex process: a standalone `codex app-server` over stdio, using JSON lines;
>       - the managed daemon running: a WebSocket client to the daemon's control socket,
>         directly or through `codex app-server proxy`, ending with a WebSocket close;
>       - a Codex session without the daemon: no probe.
> - [ ] It sends `initialize`, `initialized`, `account/read {refreshToken: false}`,
>       `account/rateLimits/read {excludeResetCreditDetails: true}` and
>       `account/read` again.
> - [ ] Standalone: it closes standard input only after the responses and the grace
>       period, or by whatever shutdown mechanism E13 chose (spike X-4). After 60 s with
>       no answer it stops waiting and reports nothing, but leaves the process running;
>       the process is treated as hung only after 10 min.
> - [ ] An authentication error from the rate-limit read, or a rejected refresh token
>       from `account/read` (spike X-5, error text from E14), gives the failure "Codex
>       needs signing in again on this machine". Probes stop until the 2-hour back-off
>       ends or a Codex session next runs here. `codex login status` isn't used for
>       this.
> - [ ] Mapping: `rateLimitsByLimitId.codex`, or else `rateLimits`, gives the windows.
>       Labels come from `windowDurationMins`, plus `resetsAt` and `planType`.
>       `accountKey` is the first 12 hex digits of
>       `sha256("codex:" + lower(email) + ":" + accountId)`. It's set only when the
>       identity held steady across the probe (spike Codex recommendation, step 2): the
>       same non-null email in both `account/read` answers, a non-null `accountId` and
>       `ordinaryUsageAllowed`, the standalone route, and no other Codex process seen from
>       just before the first `account/read` to just after the second. Daemon-route
>       readings have no key. `account/updated` notifications aren't used. Otherwise
>       there's no key. `source` is `api`.
> - [ ] Transcripts stay, and a transcript reading has no key. The newest reading
>       wins. An API-key login or a missing `codex` falls back to transcripts without an
>       error row.
> - [ ] Keyless readings stay on their own (spike Codex recommendation, step 4).
>       `UsageMerge` and `mergeUsage` bucket a reading with no `accountKey` and no
>       `account` by agent and machine. #172's peer sharing never takes a keyless
>       reading.
> - [ ] Same pace, single flight and back-off as Claude.
>
> **Both**
> - [ ] Probe processes are excluded from session discovery and worktree registrations.
> - [ ] While agent-ha-bridge's daemon runs for the user, probes use only the idle pace.
> - [ ] `docs/protocol/v1-usage.md`: the "Where a host gets each figure" table, the pace
>       section, and "Leaving the agents' credentials alone" are rewritten. `source:
>       cache` for Claude. A Codex `accountKey`. The keyless-reading rule in "What a
>       client shows".
> - [ ] `docs/parity.md` and `docs/status.md` are updated.
> - [ ] Independent review before merge, because this spawns credential-using agent
>       processes.
>
> **Tests (each fails without the change)**
> - [ ] A Claude reading comes from `cachedUsageUtilization`, with `fetchedAtMs` as
>       `measuredAt`, when no credentials file exists.
> - [ ] A saved Claude reading for another account is ignored.
> - [ ] A stale Claude reading starts exactly one probe. A fresh one starts none. A pass
>       while a probe is running starts no second probe.
> - [ ] A failed or rate-limited probe backs off (15 min, 30 min, … up to 2 h), and the
>       last reading stands.
> - [ ] A probe is not killed before its timeout, and host shutdown doesn't kill a
>       running probe.
> - [ ] A signed-out `auth status` gives the signed-out failure and stops the probes.
> - [ ] A scripted fake app-server answering `account/rateLimits/read` produces
>       `primary`/`secondary` windows, labels, resets, plan and `accountKey`.
> - [ ] Two users in one workspace (same `accountId`, different email) get different
>       `accountKey`s. A response with no `accountId`, no email or a null
>       `ordinaryUsageAllowed` gets no `accountKey`.
> - [ ] A login switch during the probe withholds the `accountKey`: the fake answers
>       the two `account/read` calls with different emails, or answers the usage read
>       with a null `ordinaryUsageAllowed`.
> - [ ] An unchanged login keeps its `accountKey` when the fake sends the
>       initialization `account/updated` during the probe.
> - [ ] A switch and a switch back (A, then B, then A) withholds the key: a fake whose
>       two `account/read` answers both say A, with a valid snapshot, gets no key when
>       the process watcher reports another Codex process during the probe. A
>       daemon-route reading never gets a key.
> - [ ] Two keyless Codex readings from different machines merge into two rows, not
>       one, in both `UsageMerge` and `mergeUsage`. A keyed reading still merges across
>       machines as before. A host doesn't take a keyless reading from a peer.
> - [ ] The Codex probe sends only `initialize`, `initialized`,
>       `account/rateLimits/read` (with `excludeResetCreditDetails: true` and no
>       `supportsLunaReserve`) and `account/read`, and never `thread/*` or `turn/*`.
> - [ ] The probe never closes standard input before the responses and the grace period.
>       A fake app-server that answers after the 60 s reporting timeout still has its
>       standard input open, and is still running, when it answers.
> - [ ] The route follows the process state: standalone with no Codex running, the
>       daemon with the daemon running, and nothing beside a session without the daemon.
> - [ ] The daemon route speaks WebSocket: against a fake WebSocket control socket it
>       upgrades, sends text frames and ends with a close frame.
> - [ ] An authentication error from the rate-limit read stops probing, even while
>       `codex login status` says logged in.
> - [ ] A hung fake app-server is given up on after the 10 min ceiling, and the
>       transcript reading stays.
> - [ ] The newer of a transcript reading and a probe reading wins, both ways round.
> - [ ] A probe's process doesn't appear as a session.
>
> **Live (disposable VM):** E1 to E14 from the spike, run against the build.

## Limitations

- **Nothing here was observed running.** Claude's findings come from one build
  (2.1.291) and Codex's from one tag (0.166.0). Both change weekly, so the build must
  check versions and fall back quietly.
- **Some of Claude's server behaviour is inferred from client code.** Whether every
  refresh rotates the refresh token, and the usage endpoint's rate limit, are unconfirmed
  (E2 and E5).
- **The Windows store finding (C-1) needs owner attention now.** It affects today's
  reader, not only the proposal. A machine in that cohort stops showing Claude with no
  error. Agents using a non-default `CLAUDE_CONFIG_DIR` add a hash to the store's service
  name.
- **macOS was read from the docs only.** The Windows bundle compiles out the Keychain
  branch, so the Keychain details are from the docs and not the code. S7 covers macOS.

## E13 on Linux

E13(a) to (c) ran on 2026-11-01 PT on a disposable Azure VM: Ubuntu 24.04, Codex 0.166.0
from npm, signed in to a ChatGPT Plus account with device auth. The VM was then
deleted. Every probe ran the five spike messages with no model turn and no `thread/*` or
`turn/*` request. E13(d), the Windows repeat, wasn't run.

**These results change X-4 and #200.**
- **Closing stdin before the responses loses rotations.** Wait for them all.
- **Once the probe has waited, Codex's own start-up timeouts decide whether a rotated
  token is lost, not the grace period.** They apply to every `codex app-server` start.
- **The grace period stays**, for two refresh paths these runs didn't exercise (see What
  this decides).

### How it was run

- **The probe** was a small script that started `codex app-server` (the native binary,
  with no child processes) and sent `initialize`, `initialized`, `account/read`,
  `account/rateLimits/read` and `account/read`, each after the previous answer. It logged
  the time of every send, response, notification and stdin close, the exit code and the
  process tree. Account identifiers were redacted on the VM.
- **`mitmdump` logged only method, host, path, status and times.** For
  `POST auth.openai.com/oauth/token`:
  - it forwarded the request at once and **held the response** for the stated time, so
    the server had already rotated the token when Codex was kept waiting;
  - it kept the token response root-only on the VM, to tell whether Codex saved the new
    token, and recorded only `sha256(token)[:8]`;
  - it could answer `/backend-api/wham/accounts/check` with 401s.
- **The access token lives 240 h (10 days)** (JWT `iat` to `exp`, after both sign-in and
  refresh). That's what E8 was to measure.
- **A proactive refresh therefore needs a clock change.** Runs (a), (b) and (c1) stopped
  chrony and set the VM's clock to a chosen number of seconds before the stored token's
  `exp`, then restored it. The servers keep their own time, so only Codex's
  5-minute-window decision moves. After a refresh, the next in-window probe waited until
  the new token, which was issued in real time, also looked fresh under the shifted clock.
  Without that wait, 24 of the first 25 in-window probes refreshed 4 to 6 times each,
  all because of the clock change. All 25 were re-run with the wait.
- 97 signed-in probe runs in total, with no turns. Traffic seen: `chatgpt.com/backend-api/` paths
  `wham/accounts/check`, `wham/usage`, `codex/models`, `ps/plugins/*` and
  `plugins/featured`, plus plugin assets on `*.oaiusercontent.com`, and
  `auth.openai.com/oauth/token`.

### What happened

**A new finding first: Codex refreshes during start-up, under a 20 s timeout.** When a
proactive refresh is due, the first `auth()` caller isn't the usage read. It's the cloud
config bundle load (`cloud-config/src/service.rs:129-189`). That load runs inside
`CLOUD_CONFIG_BUNDLE_TIMEOUT`, which is 20 s (`:35`), and `initialize` isn't answered until
it finishes. A token response slower than 20 s is dropped, and the rotation with it. The
attempt then repeated once, with another 20 s limit. stderr shows "timed out waiting for
cloud config bundle after 20s", and `initialize` was answered at 40 s.

| Run | Set-up | Token requests (s after start) | Responses | Exit | Token saved? |
|---|---|---|---|---|---|
| (a) | In the window (240 s before `exp`). Hold 50 s. Stdin closed when the rate-limit read was sent, at 40.2 s | 0.3 and 20.3 (cut off by the cloud-config 20 s limit), then 40.3 (the rate-limit read's own refresh) | `initialize` at 40.2; `account/read` failed: "failed to load workspace requirements: cloud config: request timed out"; the rate-limit read was never answered | **30.0 s after EOF, exit 0**: the processor's 30 s drain, not the 45 s watchdog | **No.** All three rotations were lost, and `auth.json` kept the token that had been sent |
| (a) check | The next probe, no hold, about 5 min later, with the token (a) had sent three times | One, at 0.3 | All answered | 0.02 s after EOF | **Yes.** The server accepted that token a fourth time |
| (b) | As (a), with stdin closed only after all the responses | 0.2 and 20.2 (cut off as in (a)), then 40.2 | `initialize` at 40.2; both `account/read`s failed (cloud config timed out); the rate-limit read failed at 90.6 s: "failed to fetch codex rate limits: application network policy is unavailable" | 0.06 s after EOF, exit 0 | **Yes**, the third rotation. The first two were lost. The probe got **no reading** |

**(c1) Proactive.** 50 probes, from 360 s to 240 s before `exp`, with stdin closed as soon
as the last response arrived:
- **25 probes started outside the window** (360 to 301 s before `exp`). None made a token
  request.
- **25 probes started inside it** (299 to 240 s, re-run as above). Each made exactly one
  token request, at 0.08 to 0.17 s (the start-up `auth()`). Every one was answered before
  the first response and saved, and the process exited 0 within 0.03 s of EOF.
- **6 more probes aimed the window opening at the gap** after the usage read's `auth()`
  and before exit, at 301 to 304 s:
  - At 301 s, the window opened during the rate-limit read. That read made the refresh,
    waited for it and answered after it (token request at 1.14 s, response at 1.53 s),
    and the token was saved.
  - The other five exited before the window opened, with no refresh.
- **No token request was open, or started, after the last probe response in any (c1)
  run.** So the repeat with a hold past the 45 s watchdog wasn't needed.

**(c2) Routing recovery.** Fresh token, real clock. The token response was held for 10, 20
or 30 s. Stdin was closed (i) on the last response or (ii) 20 s after it.
- **As specified (one 401 on `/accounts/check`), there is no refresh.** Recovery's first
  step only reloads `auth.json` and retries (`UnauthorizedRecovery`,
  `login/src/auth/manager.rs:1914-1919` and `:2063-2105`). The retry 30 ms later got 200.
  All six runs made no token request and answered normally.
- **A refresh needs two consecutive 401s.** So the six runs were repeated with two:

| Hold | Close | Token request | First `account/read` | Exit | Token saved? |
|---|---|---|---|---|---|
| 10 s | (i) on the responses / (ii) +20 s | 0.2 s, released 10.7 s | **Waited for discovery's refresh** and answered after it; all responses by 11.6 s | 0.02–0.03 s after EOF | **Yes**, both |
| 20 s | (i) / (ii) | 0.2 s, released 20.6 s | Failed at 15.1 s: "workspace routing discovery timed out" | 0.02–0.04 s after EOF | **No**, in both: the 15 s discovery timeout (`workspace_routing.rs:552`) dropped the refresh. In (ii) the process was still running when the held response arrived at 20.6 s, but nothing was waiting for it |
| 30 s | (i) / (ii) | 0.2 s → **401 `refresh_token_reused`** (still held 30 s) | Failed at 15.1 s, as above | 0.02–0.04 s after EOF | Nothing new to save. **The stored refresh token was now rejected** |

**The 30 s runs used the same stored refresh token as the two 20 s runs.** The server
accepted that token twice, rotating it each time, and both rotations were lost. It
rejected the third use, about 100 s after the first, with HTTP 401
`{"code": "refresh_token_reused", "message": "Your refresh token has already been used to
generate a new access token. Please try signing in again."}`. Both 30 s runs still
answered the rate-limit read, using the unexpired access token. Codex itself never
received the rejection, because discovery had timed out first. `codex login status` still
said "Logged in using ChatGPT", which confirms X-5.

### What this decides (X-4)

- **(a) failed as expected, but not the way expected.** The rotated tokens were lost.
  The cause was Codex's 20 s cloud-config limit and the 30 s RPC drain after EOF, and
  either cuts first. The 45 s watchdog never fired. Losing a rotation didn't sign Codex
  out at once in (a): the server accepted the old token again 5 minutes later. But (c2)
  shows the server's tolerance runs out: the same pattern ended in
  `refresh_token_reused`. **A lost rotation can sign Codex out**, and whether it does
  depends on server behaviour the client can't see.
- **(b) passed for EOF.** Closing stdin after the responses lost nothing that the probe
  could still save.
- **(c) No refresh outlived the probe's responses in any run.** Every refresh seen came from
  one of three places, and each finished, or was cancelled, before the responses:
  - the start-up `auth()`, which `initialize` waits for;
  - the usage read's own `auth()`, which the read waits for;
  - routing discovery, which `account/read` waits for. **The second `account/read` does
    wait for routing discovery's refresh** (c2, 10 s).
- **One refresher wasn't exercised.** The model-catalogue fetch takes its `auth()` in a
  detached task, so that a catalogue timeout can't drop a rotation
  (`model-provider/src/models_endpoint.rs:127-156`), and nothing the probe waits for
  joins it.
  - In every run the catalogue request went out at start-up, before the usage read, and
    any refresh it needed was already in flight or done.
  - A catalogue fetch whose `auth()` came after the usage read's, just as the window
    opened, would refresh outside every response. EOF would then drop it at runtime
    teardown. No run held the token response for that case.
- **The cloud-config background refresher also takes `auth()` on its own timer**
  (`cloud-config/src/service.rs:433-447`): 15 min after start, or 5 s after a start-up
  timeout. A short probe meets it only after a start-up timeout.
  - In (a) and (b) the start-up timeout came from the held token response, and the
    refresher's own 20 s limit cut its refresh off too.
  - **The start-up load also covers reading the cache and fetching the bundle, with
    retries** (`:129-189`). So a stalled bundle fetch with a fast token endpoint can also
    trigger the 5 s retry, without losing a rotation. A refresh by that retry could then
    cross the probe's responses.
  - **That slow-bundle, fast-token case is another shutdown case these runs didn't
    exercise.** The account here (Plus) may not fetch a managed bundle at all.
- **In the runs that waited for all their responses, the lost refreshes were cancelled by
  Codex's own timeouts:** 20 s for cloud config at start-up, and 15 s for routing
  discovery. There the stdin timing made no difference: in (c2), (i) and (ii) gave
  identical outcomes. Closing stdin early is different: in (a), the rate-limit read's
  refresh was cut off by the 30 s drain after EOF, and (b) saved that same rotation by
  waiting.
- **So keep the grace period.** It changed nothing in any run here, but it covers the
  two untested cases above, as long as the token endpoint answers within it. X-4's rule
  stands: wait for all the responses, then at least 20 s, then close stdin.
- **The remaining risk is a slow token endpoint**, more than 15 to 20 s, while a refresh is
  due. That loses the rotation on any app-server start, including the user's own. Each
  probe adds a start, so the pace limits keep their weight. The daemon route (X-3) adds
  no start and stays preferred whenever the daemon runs.

### Corrections

**X-4:**
- "When `/accounts/check` returns 401, that discovery runs `unauthorized_recovery()`,
  which refreshes": the first 401 only reloads from disk. A refresh needs a second
  consecutive 401.
- "A background refresh can then still be running after both responses arrive": not
  observed in any run where the probe waited for its responses. Each refresher seen is
  awaited by `initialize`, the usage read or `account/read`. Two candidates remain,
  neither exercised: the model catalogue's detached `auth()`, and the cloud-config retry
  after a slow bundle fetch.
- Add the start-up cloud-config refresh, bounded by 20 s, to "What could still be
  refreshing". It's the first caller when a refresh is due, and the most likely place to
  lose a rotation.
- "On Unix, closing standard input arms a 45 s watchdog": still true, but the process
  exited at the 30 s drain first, and within 0.06 s of EOF whenever nothing was in flight.
- "Interim rule": it becomes the rule. Wait for all the responses, then at least 20 s,
  then close stdin. The standalone route stays.

**#200 Codex:**
- **Shutdown.** "It closes standard input only after the responses and the grace period,
  or by whatever shutdown mechanism E13 chose." E13 keeps the grace period: close stdin
  at least 20 s after the last response, which is the second `account/read`. The test "a fake app-server that answers
  after the 60 s reporting timeout still has its standard input open" still stands.
- **Errors that aren't a sign-out.** The probe must treat these as transient and back
  off. They're what a slow token endpoint produces:
  - "failed to load workspace requirements: cloud config: request timed out";
  - "workspace routing discovery timed out";
  - "failed to fetch codex rate limits: application network policy is unavailable".
- **Sign-out detection** ("an authentication error from the rate-limit read"):
  - A rejected refresh token can stay hidden for up to 10 days. While the access token is
    valid the rate-limit read succeeds, and a held or slow rejection is cut off by the
    15 s discovery timeout before Codex sees it.
  - So the signal arrives late, at the next refresh, and E14's error texts still decide
    the failure message.
  - The rejection carries `refresh_token_reused` with HTTP 401, not the HTTP 400 that
    E14(b) planned to inject.
- **E8:** the access token lifetime is 240 h, so a 2-hour E8 run won't cross an expiry
  without the same clock change.

### Limitations of this run

- **One account, one plan (Plus), one region, one day.** The server's reuse tolerance
  (four uses over 5 minutes once, two uses then rejection another time) isn't documented,
  and this run doesn't establish its rule.
- **The proactive runs moved the VM's clock, not real time.** The backend saw real time
  throughout, so this only changes when Codex decides to refresh.
- **mitmdump changed the transport.** Codex reached the backend through the proxy and
  trusted its CA through `CODEX_CA_CERTIFICATE`. With no hold, a whole probe took 0.6 to
  6 s.

## Experiments on the Windows rehearsal VM (#200)

E1, E2, E4, E7, E8, E9, E12, E13(d) and E14 ran on 2026-11-01 and 2026-11-02 PT on VM-M,
the disposable Azure rehearsal VM of `docs/rehearsal-2026-10.md` (Windows 11 24H2 Pro,
x64), as its standard user `rehearsal`, beside the scripted shadow soak. The owner signed
the agents in there.

- **Versions:** Claude Code **2.1.316** and Codex **0.166.0** (`@openai/codex` from npm, its
  native `codex.exe`), both signed in to the owner's accounts: Claude Pro, ChatGPT Plus.
  Claude Code 2.1.291, the build the spike read, wasn't run. `mitmdump` 12.3.1.
- **Traffic** went through `mitmdump` on `127.0.0.1:8899` only for the probe runs that
  needed it. Its add-on logged method, host, path, status and time, never headers or
  bodies, and could hold the token response or answer chosen paths with errors. Tokens
  were compared only as in-memory hashes and never written out.
- **No probe started a turn.** Soak sessions were kept apart from probes in time, so the
  shadow comparison's counts saw no probe process. The soak is a separate piece of work
  (`docs/host-shadow.md`).
- **E3, E5, E6, E10 and E11 weren't run.** #200 doesn't need them first.
- **The owner deferred the Linux-only E13(a)–(c) to P5.** They don't block the Windows
  build (#200). They have since run once on Linux: see [E13 on Linux](#e13-on-linux).

| # | Result | Evidence |
|---|---|---|
| E1 | **Pass** | Below |
| E2 | **Pass, with the expiry simulated** and the probe's exit code not captured | Below |
| E4 | **Failed, as expected:** Claude Code signed out | Below |
| E7 | **Pass**, except that the windows weren't compared with `/status` | Below |
| E8 | **Pass, with the expiry replaced by forced refreshes;** the forced race had no loser | Below |
| E9 | **Pass, with one finding:** the daemon never acknowledges a WebSocket close | Below |
| E12 | **Pass:** no load or parse failure | Below |
| E13(d) | **Pass** | Below |
| E14(a) | **Simulated on the VM, not a real server-side revoke** | Below |
| E14(b) | **Pass** | Below |

### Claude

**E1, the probe with a fresh token: pass.**
- `claude -p "/usage" --safe-mode --no-session-persistence --output-format json`, in an
  empty directory, exited 0 in about 1.8 s. `--output-format text` also exited 0.
- `cachedUsageUtilization.fetchedAtMs` moved.
- **The `SessionStart` hook's file was unchanged by the probe.** The same hook fired when
  the next soak Claude session started, which shows it was live. It was then removed, and
  `settings.json` is byte-for-byte what it was before.
- Nothing new under `~/.claude/projects`.
- **No `/v1/messages`.** Every request went to one of these:
  - `api.anthropic.com`: `/api/oauth/usage`, `/api/claude_cli/bootstrap`,
    `/api/claude_code_penguin_mode`, `/mcp-registry/v0/servers`,
    `/api/oauth/account/settings`, `/api/claude_code_grove`,
    `/api/organizations/<id>/model_selector/cc`, `/api/eval/sdk-<key>` and
    `/api/event_logging/v2/batch`;
  - `http-intake.logs.us5.datadoghq.com`: `/api/v2/logs`.
- **The JSON carries no `usageReport` in 2.1.316.**
- **The owner's claude.ai reading matched the probe's:** current session 0%, weekly (all
  models) 0%, and no other weekly rows.

**E2, the probe with an expired token: pass, with the expiry simulated.**
- The VM wasn't left idle for 75 minutes. Instead, `expiresAt` in `.credentials.json` was
  set in the past, so the next probe had to refresh.
- `expiresAt` advanced, and **the refresh token changed (rotation confirmed)**.
  `.oauth_refresh.lock` came and went.
- A one-line Haiku prompt then answered, and `claude auth status` reported
  `loggedIn: true`.
- The probe's exit code wasn't captured.

**E4, killing the probe: failed, as expected.**
- `mitmdump` held the token response for 10 s, and the probe was killed while it was held.
- **The next refresh got HTTP 400**, and Claude Code cleared its credentials:
  `claude auth status` said `loggedIn: false`. That confirms the never-kill rule.
- **The probe still exited 0 with the cached figures**, so its exit code can't detect a
  sign-out. `claude auth status` has to.
- The owner signed in again; `loggedIn: true`, plan pro. No `.oauth_refresh.lock` was left
  behind.

### Codex

**E7, the probe: pass,** except that the windows weren't compared with `/status`.
- Exit 0.1 s after standard input closed.
- No new rollout under `~/.codex/sessions`. No `/responses`. No token request, and
  `last_refresh` didn't move.
- `accountId` and `ordinaryUsageAllowed` are present at the top level of the
  `account/rateLimits/read` result.
- Every request went to `chatgpt.com/backend-api/`: `wham/usage`, `wham/accounts/check`,
  `codex/models`, `plugins/featured`, `ps/plugins/installed`,
  `ps/plugins/suggested/codex` and `ps/plugins/list` (dozens of pages per start).

**E9, through the daemon: pass, with one finding.**
- **Starting the daemon.** `codex app-server daemon start` failed with OS error 5 (access
  denied) from a scheduled task, the way these runs reach the desktop user. It started
  when created through WMI (`Win32_Process.Create`) instead. `daemon version` reports a
  managed copy under `~/.codex/packages/app-server-daemon/current`, also 0.166.0.
- **The socket is `~/.codex/app-server-control/app-server-control.sock`.** A WebSocket
  client connected over it, and over `codex app-server proxy`'s stdio, sent the five
  messages.
- **Both paths answered every time:** 50 of 50 directly on the socket (about 0.4 s each),
  and 5 of 5 through the proxy.
- **The plain JSON lines got no answer in 8 s**, and the proxy then exited 0. That
  confirms they're refused.
- **The daemon's process ID was the same throughout**, including the two hours of E8, and
  the soak's Codex sessions in that time weren't affected.
- **Idle exit is unchanged.** With no clients, the daemon didn't exit within 12 minutes.
  After the probes it was still running 14 minutes after the last one.
- **The finding: the daemon never acknowledges a WebSocket close.** It answers every
  request, then drops the connection when the client sends a close frame.
  - Directly on the socket, .NET's `CloseAsync` throws at once ("the remote party closed
    the WebSocket connection without completing the close handshake").
  - Through the proxy, the close waited for the client's own 60 s timeout; the proxy then
    exited 0.
  - **#200 must treat an unacknowledged close as normal.** Once the responses are in, send
    the close and don't wait for its echo (or abort the socket after a short timeout).
    Through the proxy, close its standard input instead of waiting.

**E8, concurrency: pass, with the expiry replaced by forced refreshes.** The forced race
had no loser.
- **Through the daemon, for 2 hours:** 240 probes on the control socket, one every 30 s,
  while the soak ran two Codex TUI sessions (`codex-basic`, with a shell command and a
  compaction, and `codex-approvals`).
  - All 240 answered.
  - No `refresh_token_reused` in any file under `~/.codex` written during the run.
  - Both TUI sessions' turns all ended normally.
  - `codex login status` said logged in at every check, every 5 minutes.
  - The daemon's process ID didn't change.
- **No expiry fell inside the window.** The access token lives 240 h (JWT `exp` 10 days
  after `last_refresh`, as on Linux). Moving the clock, as the Linux run did, would have
  corrupted the soak's timing. So every 15 minutes a probe sent `account/read` with
  `refreshToken: true` through the daemon instead. That's 8 real refreshes by the
  daemon's own `AuthManager`, beside the TUI sessions, and each one rotated the stored
  token without error.
- **The forced race:** two standalone app-servers, started in the same millisecond
  through `mitmdump`, each sent `account/read` with `refreshToken: true`.
  - Their token requests went out 113 ms apart, with the same stored refresh token.
    **Both got HTTP 200.** The server accepted the second use, as it did on Linux.
  - Every request in both probes answered, and both exited 0, 0.1 s after EOF.
  - The later save won. The other process's rotation was simply discarded.
  - The probes after it worked: a plain one, and a refresh with the saved token (which
    rotated it again). `codex login status` stayed logged in.
  - **So nothing lost, and the guarded reload wasn't exercised.** A race that the server
    rejects with `refresh_token_reused` would need the reuse tolerance to have run out,
    as in the Linux (c2) runs.

**E12, a half-written `auth.json`: pass.**
- Three real refreshes by the standalone probe, each with the token response held 4 s.
- Meanwhile, 33,673 tight reads of `auth.json` all parsed, and 150 `codex login status`
  runs all said logged in.
- **None occurred, so nothing beyond X-3 is needed.** A failure is still possible: 0.166's
  `FileAuthStorage.save` truncates the file and rewrites it in place rather than replacing
  it, so a reader could in principle see it empty or half-written. Upstream main now
  replaces it atomically.

**E13(d), the Windows repeat of (b): pass.**
- The token response was held for 50 s, and standard input was closed only after the
  responses.
- `account/read` waited for the held refresh; `account/rateLimits/read` answered at once.
- The rotated token was saved, and the process exited by itself 0 s after EOF.

**E14(a), revoked: simulated on the VM, not a real server-side revoke.** ChatGPT has no
per-device revoke, and the owner chose not to sign out every device. Instead, `auth.json`
was backed up on the VM (owner-only, never printed), only its `refresh_token` value was
garbled, and `last_refresh` was set 9 days back. Then:
- **Run A, as planned: no error at all.** 0.166 refreshes only within 5 minutes of the
  access token's JWT `exp` (10 days away). It reads `last_refresh` only when `exp` can't
  be parsed.
- **Run B, forcing a refresh** (`account/read` with `refreshToken: true`): the token
  request got HTTP 401. **`account/read` then answered without an error, with `account`
  null and `workspaceRouting` null.** The rate-limit read still succeeded on the valid
  access token.
- **Run C, as B, with `mitmdump` also answering `wham/usage` and `wham/accounts/check`
  with 401** (what a revoked access token would get):
  - `account/read`: error -32603, "Your access token could not be refreshed. Please log
    out and sign in again."
  - `account/rateLimits/read`: error -32603, "failed to fetch codex rate limits: GET
    https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized; …"
- `codex login status` said "Logged in using ChatGPT" throughout, which confirms X-5.
- The backup was restored and checked, then deleted.
- "Without a selected workspace" couldn't be run: this personal account always has
  routing.

**E14(b), reused: pass.**
- `mitmdump` answered the token request with HTTP 400 and
  `{"error":"refresh_token_reused"}`, and `wham/usage` and `wham/accounts/check` with 401.
- `account/read`: error -32603, "Your access token could not be refreshed because your
  refresh token was already used. Please log out and sign in again."
- `account/rateLimits/read`: the same 401 error as E14(a) run C.
- `codex login status` stayed logged in, and `auth.json` was unchanged.
- The same caveat as E14(a): this account always has a workspace.

### What this changes for #200

- **Claude sign-out:** gate on `claude auth status`, as planned. E4 shows the probe exits 0
  on cached figures after a sign-out.
- **Codex sign-out:** treat any of these as signed out: E14's -32603 texts, a 401 from the
  rate-limit read, or `account/read` answering with `account` null (E14(a) run B). Don't
  trust `codex login status`.
- **The daemon route** works on Windows. Don't wait for the close echo (E9). The host may
  not be able to start the daemon from a scheduled task, so it should use the daemon only
  when one is already running, which is what the spike recommends anyway.

### An incident during these runs

A check script run on the VM defined a function named `H`. In PowerShell `h` is a built-in
alias for `Get-History`, and aliases win over functions, so the call went to
`Get-History`. That printed the argument, the Claude access token, in an error message in
the run's output. No refresh token was exposed, and nothing reached the repository.
- The two run-command status files on the VM that held it were scrubbed. The owner let
  the token expire, and the E4 sign-in replaced it.
- **The fix, for every check script since:**
  - never name a function or variable after a PowerShell alias (`h`, `r`, `%`, `?`, `gc`
    and so on);
  - a script that handles a credential file reads only the fields it needs (such as
    `expiresAt`), and never echoes a parsed object, an error's text or `$_` from a parse
    that could include the content.
