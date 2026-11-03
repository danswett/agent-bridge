# Lift-Up Plan: Agent Bridge, whole product

> Platform: Mixed — Windows WinUI/.NET desktop; browser and installed PWA; SwiftUI macOS, iPhone and iPad; .NET host, transport, relay, hooks, and Home Assistant connector.
> Surveyed: 2026-11-02
> Baseline: codex/apple-full-bridge@f0bf103. Fetched origin/main@68972ac; 86 ahead, zero behind. Tracked files clean; one untracked prior Apple-only audit at preflight; no sync needed. HEAD unchanged on final recheck.
> Scope: Entire Agent Bridge product, including Windows and web as first-class clients. This combined-branch report supersedes the earlier Apple-only ranking; IDs below belong to this report.
> Coverage: Partial static audit across the full repository. Surveyed native and browser entry points, sign-in, session discovery/search and interaction, usage, settings, attachments, host delivery, hooks, connector upkeep, local peer/relay trust, update feeds, deployment layout and acceptance records. Not a line-by-line audit of every driver, a dependency vulnerability scan, a benchmark, or a new live deployment or device test.
> Attractiveness anchor: The repository’s own Agent Bridge workbench design — dense enough to scan a fleet, with readable conversation, stable controls and platform-native behavior.

**Decision.** Ship the sign-in destination check first (SEC-1). It fixes a reachable trust boundary in a small change and can be verified with fake relay responses. Keep REL-1 as the first live delivery acceptance: a signed-in device with a visible composer is different from a Mac process receiving a reply exactly once.

**How to read the evidence.** Items are ranked within each dimension. Proof quotes come from the pinned branch. Verified blocks reproduce competing-implementation searches and searches for the cited code; a match elsewhere is not evidence that the cited path already uses that behavior. Effort and impact are relative judgments. No performance gain, attack or production outage is asserted from a source reading alone.

## Performance

### PERF-1. Filter before sorting Windows search results

**Location:** `src/AgentBridge.App.Core/MainViewModel.Search.cs:12`, `src/AgentBridge.App.Core/MainViewModel.Search.cs:21`, `src/AgentBridge.App.Core/MainViewModel.Search.cs:26`

**Proof:**

`src/AgentBridge.App.Core/MainViewModel.Search.cs:12`
```
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { return []; }
        var several = machines.Count > 1;
        var listed = byId.Values.OrderByDescending(c => c.Session.UpdatedAt).ThenBy(c => c.Title, StringComparer.CurrentCulture).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var hits = new List<(SearchHit Hit, bool Prefix)>();
```

`src/AgentBridge.App.Core/MainViewModel.Search.cs:21`
```
            var haystack = string.Join(' ', new[] { card.Title, s.Repository, s.Branch, Words.LastSegment(s.Cwd), card.AgentName, m.Name });
            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { continue; }
            var detail = string.Join(" · ", new[] { several ? m.Name : null, s.Repository ?? Words.LastSegment(s.Cwd) }.Where(x => !string.IsNullOrEmpty(x)));
            hits.Add((new SearchHit(m.IsLocal ? null : m.Key, card.Id, card.Title, detail), card.Title.StartsWith(query!.Trim(), StringComparison.CurrentCultureIgnoreCase)));
```

`src/AgentBridge.App.Core/MainViewModel.Search.cs:26`
```
        return [.. hits.OrderByDescending(h => h.Prefix).Select(h => h.Hit).Take(most)];
    }
```

**Verified:**

```console
$ rg --sort path -n -- 'PriorityQueue|topMatches|byId\.Values\.Where' src/AgentBridge.App.Core
src/AgentBridge.App.Core/MainViewModel.Order.cs:140:        byId.Values.Where(Visible).Select(c => new OrderEntry(owner[c].Key, c.Session)).ToList(),
$ rg --sort path -n -F -- 'var several = machines.Count > 1;' src/AgentBridge.App.Core/MainViewModel.Search.cs
14:        var several = machines.Count > 1;
$ rg --sort path -n -F -- 'if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { continue; }' src/AgentBridge.App.Core/MainViewModel.Search.cs
22:            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { continue; }
$ rg --sort path -n -F -- 'return [.. hits.OrderByDescending(h => h.Prefix).Select(h => h.Hit).Take(most)];' src/AgentBridge.App.Core/MainViewModel.Search.cs
26:        return [.. hits.OrderByDescending(h => h.Prefix).Select(h => h.Hit).Take(most)];
```

**Do:** Apply the query before sorting; rank only matching rows, preserving prefix preference and deterministic tie-breaks. Compare allocations and elapsed time on the same large synthetic fleet before and after.

**Why:** The current path sorts every card before testing the query even though the default result set has only eight slots.

**Effort:** S · **Impact:** M

### PERF-2. Suspend the web card clock when the workbench is hidden

**Location:** `pwa/js/ui/views.js:51`

**Proof:**

`pwa/js/ui/views.js:51`
```
      this.schedule();
    });
    // Relative times move on without any event from the host.
    this.ticker = setInterval(() => { for (const c of this.#cards.values()) { c.refreshTimes(); } }, 15000);
    // What a move did, said to a screen reader.
```

**Verified:**

```console
$ rg --sort path -n -- 'visibilitychange|document\.hidden|IntersectionObserver' pwa/app.css pwa/index.html pwa/js pwa/sw.js
pwa/js/connection.js:58:      this.win.document?.addEventListener?.('visibilitychange', this.#wake);
pwa/js/connection.js:70:      this.win.document?.removeEventListener?.('visibilitychange', this.#wake);
pwa/js/fleet.js:51:    doc?.addEventListener?.('visibilitychange', () => {
pwa/js/main.js:149:    document.addEventListener('visibilitychange', () => {
pwa/js/main.js:488:  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { drawNotify(); } });
pwa/js/ui/delegates-view.js:198:    if (document.hidden || this.element.getClientRects().length === 0) { this.#schedule(POLL_MS); return; }
pwa/js/ui/usage-view.js:24:    doc?.addEventListener?.('visibilitychange', () => {
$ rg --sort path -n -F -- 'this.schedule();' pwa/js/ui/views.js
51:      this.schedule();
182:    if (this.order.sort !== sort) { this.schedule(); }
384:    if (this.filter) { this.schedule(); }
397:      this.schedule();
```

**Do:** Gate time-label refreshes on page visibility and active route. On return, refresh visible cards once before restarting the timer.

**Why:** The current interval visits all retained cards every fifteen seconds; being subscribed to host updates is independent of needing to repaint relative times.

**Effort:** S · **Impact:** M

### PERF-3. Build the order membership set once per host update

**Location:** `apple/State/BridgeStore.swift:682`

**Proof:**

`apple/State/BridgeStore.swift:682`
```
    private func registerSessions(_ sessions: [BridgeSession], host: HostStore) {
        let machineID = host.hello?["machine"]?["id"]?.string ?? host.id
        let newSessions = sessions.filter { !manualOrder.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }
        guard !newSessions.isEmpty else { return }
```

**Verified:**

```console
$ rg --sort path -n -- 'Set\(manualOrder\)|orderMembership' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
apple/State/BridgeStore.swift:694:        let kept = Set(manualOrder)
$ rg --sort path -n -F -- 'guard !newSessions.isEmpty else { return }' apple/State/BridgeStore.swift
685:        guard !newSessions.isEmpty else { return }
```

**Do:** Use a set for the membership check before sorting newly observed sessions. Keep the array for presentation order.

**Why:** Each incoming session scans up to 300 order entries; the set currently built later cannot reduce that work.

**Effort:** S · **Impact:** M

### PERF-4. Reuse ended-session rows on web refreshes

**Location:** `pwa/js/ui/views.js:196`, `pwa/js/ui/views.js:202`

**Proof:**

`pwa/js/ui/views.js:196`
```
    const { el, summary, list } = this.#ended;
    summary.textContent = `Ended \u00b7 ${ended.length}`;
    const now = Date.now();
    list.replaceChildren(...ended.map((e) => {
      const m = this.machine(e.hostId);
```

`pwa/js/ui/views.js:202`
```
      return h('li', { class: 'ended', dataset: { id: e.session.id, hostId: e.hostId } },
        h('span', { class: 'ended-title' }, e.session.title || 'Untitled session'),
        h('span', { class: 'ended-meta' }, [m.show ? m.name : null, when ? `ended ${when}` : null].filter(Boolean).join(' \u00b7 ')));
    }));
```

**Verified:**

```console
$ rg --sort path -n -- 'endedRows|endedById|endedListKey|#endedGroup' pwa/app.css pwa/index.html pwa/js pwa/sw.js
pwa/js/ui/views.js:156:    if (ended.length) { want.push(this.#endedGroup(ended)); }
pwa/js/ui/views.js:191:  #endedGroup(ended) {
$ rg --sort path -n -F -- 'const now = Date.now();' pwa/js/ui/views.js
198:    const now = Date.now();
$ rg --sort path -n -F -- 'h('"'"'span'"'"', { class: '"'"'ended-title'"'"' }, e.session.title || '"'"'Untitled session'"'"'),' pwa/js/ui/views.js
203:        h('span', { class: 'ended-title' }, e.session.title || 'Untitled session'),
```

**Do:** Update ended items by host and session identity and defer rebuilding closed history (new). Verify an unrelated live update does not reconstruct unchanged history nodes.

**Why:** Active cards are reused, but the ended group replaces all its list children whenever rendered.

**Effort:** M · **Impact:** M

### PERF-5. Avoid rebuilding two lists to find one selection

**Location:** `apple/Apps/SessionWorkbench.swift:15`, `apple/State/BridgeStore.swift:78`

**Proof:**

`apple/Apps/SessionWorkbench.swift:15`
```
    private var selected: SessionEntry? {
        (store.entries + store.endedEntries).first { $0.id == store.selectedSessionID }
    }
```

`apple/State/BridgeStore.swift:78`
```
    var entries: [SessionEntry] {
        if isSample {
            return SampleSessions.sessions.map { SessionEntry(session: $0, host: nil) }
        }
        return canonicalHosts.flatMap { host in host.sessions.map { SessionEntry(session: $0, host: host) } }
```

**Verified:**

```console
$ rg --sort path -n -- 'entries\.lazy|endedEntries\.first|sessionEntryByID' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
# (no matches)
$ rg --sort path -n -F -- 'private var selected: SessionEntry? {' apple/Apps/SessionWorkbench.swift
15:    private var selected: SessionEntry? {
$ rg --sort path -n -F -- 'if isSample {' apple/State/BridgeStore.swift
79:        if isSample {
123:        if isSample {
```

**Do:** Resolve the selected item by searching live hosts first and ended sessions only on a miss. Compare allocations when switching between sessions in a populated sidebar.

**Why:** Each lookup currently constructs the live and ended arrays and concatenates them before testing the first ID.

**Effort:** S · **Impact:** M

## Functionality

### FUNC-1. Let the web user cancel a pending device sign-in

**Location:** `pwa/js/main.js:353`, `pwa/js/main.js:360`, `pwa/index.html:91`, `pwa/js/signin.js:14`

**Proof:**

`pwa/js/main.js:353`
```
$('signin-button').onclick = async () => {
  const button = $('signin-button');
  button.disabled = true;
  $('signin-error').hidden = true;
```

`pwa/js/main.js:360`
```
    $('verify').href = flow.verificationUri;
    $('verify').textContent = flow.verificationUri.replace(/^https?:\/\//, '');
    $('code-box').hidden = false;
    const account = await flow.wait();
    accountStore.save(account);
```

`pwa/index.html:91`
```
        <div id="code-box" hidden>
          <p>Open <a id="verify" target="_blank" rel="noopener noreferrer">github.com/login/device</a> and enter this code:</p>
          <p class="code" id="code" aria-live="polite"></p>
          <p class="note" role="status">Waiting for GitHub…</p>
        </div>
```

`pwa/js/signin.js:14`
```
    // Polls until the code is entered, refused or expired. `signal` cancels.
    async wait(signal) {
      let interval = Number(s.interval) || 5;
```

**Verified:**

```console
$ rg --sort path -n -- 'cancelSignIn|cancel-signin|sign.?in.*AbortController|flow\.wait\(' pwa/app.css pwa/index.html pwa/js pwa/sw.js
pwa/js/main.js:363:    const account = await flow.wait();
$ rg --sort path -n -F -- 'button.disabled = true;' pwa/js/main.js
355:  button.disabled = true;
$ rg --sort path -n -F -- 'accountStore.save(account);' pwa/js/main.js
364:    accountStore.save(account);
$ rg --sort path -n -F -- '<div id="code-box" hidden>' pwa/index.html
91:        <div id="code-box" hidden>
$ rg --sort path -n -F -- 'async wait(signal) {' pwa/js/signin.js
15:    async wait(signal) {
```

**Do:** Expose a cancel action (new) while the code is pending and cancel both the wait and any in-flight request. Reject a late completion after cancellation.

**Why:** The flow accepts a signal, but the actual web button handler supplies none. Starting over currently requires waiting or leaving the page.

**Effort:** S · **Impact:** M

### FUNC-2. Launch the first session from a connected empty workbench

**Location:** `apple/Apps/SessionWorkbench.swift:102`, `apple/Apps/SessionWorkbench.swift:283`

**Proof:**

`apple/Apps/SessionWorkbench.swift:102`
```
                            Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect).disabled(store.isSample || store.isFixture)
                            Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
                                .disabled(!store.hosts.contains { $0.can("sessions.launch") || $0.capabilities.contains("launch.trust") || $0.attempts["launch"] != nil })
                        } label: { Image(systemName: "line.3.horizontal.decrease") }
                            .accessibilityLabel("Session filters and connections")
```

`apple/Apps/SessionWorkbench.swift:283`
```
            Button("Connection Settings") { showingConnection = true }
            if !store.isSample {
                Button("Explore Sample Sessions", action: store.showSamples)
            }
        }
```

**Verified:**

```console
$ rg --sort path -n -- 'Button\("Launch|showingLaunch = true' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
apple/Apps/SessionWorkbench.swift:103:                            Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
$ rg --sort path -n -F -- '.accessibilityLabel("Session filters and connections")' apple/Apps/SessionWorkbench.swift
106:                            .accessibilityLabel("Session filters and connections")
$ rg --sort path -n -F -- 'if !store.isSample {' apple/Apps/SessionWorkbench.swift
284:            if !store.isSample {
```

**Do:** Put a primary “Launch or Resume” action (new placement) in the connected empty state on Mac and iPad. Share the existing toolbar eligibility rule, including pending launches; verify an eligible empty host opens the chooser and a read-only host still explains its limits.

**Why:** The existing launcher is behind the filter icon. The empty screen offers settings and samples instead, so zero sessions can look like a failed connection.

**Effort:** S · **Impact:** L

### FUNC-3. Match multi-word session searches across fields in the web client

**Location:** `pwa/js/ui/views.js:87`, `src/AgentBridge.App.Core/MainViewModel.Search.cs:20`, `apple/Apps/SessionWorkbench.swift:27`

**Proof:**

`pwa/js/ui/views.js:87`
```
  #matches(hostId, s) {
    if (!this.filter) { return true; }
    const m = this.machine(hostId);
    return [s.title, s.repository, s.branch, s.cwd, s.agent, m.name].some((v) => typeof v === 'string' && v.toLowerCase().includes(this.filter));
  }
```

`src/AgentBridge.App.Core/MainViewModel.Search.cs:20`
```
            var s = card.Session;
            var haystack = string.Join(' ', new[] { card.Title, s.Repository, s.Branch, Words.LastSegment(s.Cwd), card.AgentName, m.Name });
            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { continue; }
            var detail = string.Join(" · ", new[] { several ? m.Name : null, s.Repository ?? Words.LastSegment(s.Cwd) }.Where(x => !string.IsNullOrEmpty(x)));
            hits.Add((new SearchHit(m.IsLocal ? null : m.Key, card.Id, card.Title, detail), card.Title.StartsWith(query!.Trim(), StringComparison.CurrentCultureIgnoreCase)));
```

`apple/Apps/SessionWorkbench.swift:27`
```
        let matched = store.entries.filter { entry in
            if !terms.isEmpty {
                let fields = [entry.session.title, entry.session.agent, entry.session.repository ?? "", entry.session.branch ?? "", entry.session.cwd ?? "", entry.machineName]
                return terms.allSatisfy { term in fields.contains { $0.localizedCaseInsensitiveContains(term) } }
            }
```

**Verified:**

```console
$ rg --sort path -n -- 'terms\.every|words\.every|queryTerms|split\(/\\s\+' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State pwa/app.css pwa/index.html pwa/js pwa/sw.js src/AgentBridge.App.Core
# (no matches)
$ rg --sort path -n -F -- '#matches(hostId, s) {' pwa/js/ui/views.js
87:  #matches(hostId, s) {
$ rg --sort path -n -F -- 'var s = card.Session;' src/AgentBridge.App.Core/MainViewModel.Search.cs
20:            var s = card.Session;
$ rg --sort path -n -F -- 'if !terms.isEmpty {' apple/Apps/SessionWorkbench.swift
28:            if !terms.isEmpty {
34:        if !terms.isEmpty {
```

**Do:** Match every query term against the available fields rather than requiring the entire phrase to occur in one field. Verify a machine name plus repository, while keeping recently ended items searchable.

**Why:** Windows searches separate words and Apple tests them across fields. The browser searches a single literal substring, so the same fleet query can miss a session that exists.

**Effort:** S · **Impact:** M

### FUNC-4. Offer opt-in notifications while the iPad is asleep

**Location:** `apple/Apps/SettingsNotificationsView.swift:27`

**Proof:**

`apple/Apps/SettingsNotificationsView.swift:27`
```
            #if os(iOS)
                Section {
                    Text("Notifications are available while Agent Bridge is open. Background push notifications are not configured.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
```

**Verified:**

```console
$ rg --sort path -n -- 'registerForRemoteNotifications|didRegisterForRemoteNotifications|aps-environment' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
# (no matches)
$ rg --sort path -n -F -- 'Section {' apple/Apps/SettingsNotificationsView.swift
8:            Section {
28:                Section {
34:                Section {
```

**Do:** Design an optional push delivery path (new) with generic payloads, revocable device registration and a signed build with the necessary entitlement. Keep foreground notifications working in local builds without a provider.

**Why:** The settings screen explicitly says notifications require the app to be open. A host waiting on an answer cannot currently alert an away user through this client path.

**Effort:** L · **Impact:** L

### FUNC-5. Support opt-in Copilot usage credentials on Mac

**Location:** `src/AgentBridge.Host.Core/Usage/CopilotCredentials.cs:65`

**Proof:**

`src/AgentBridge.Host.Core/Usage/CopilotCredentials.cs:65`
```
// macOS's keychain item is not read: reading it raises an authorization panel that the CLI's
// next token refresh invalidates, so the prompt cannot be made to stop by answering it
// (agent-ha-bridge #122). agent-ha-bridge reads it only when usage.keychain says to; this host
// does not take that setting yet (#52), so it never does.
public sealed class CopilotCredentials(UsageOptions options, ICommandRunner commands, ICredentialStore? store = null,
```

**Verified:**

```console
$ rg --sort path -n -- 'usage\.keychain|UsageKeychain|useMacKeychain' src/AgentBridge.Host.Core/Usage
src/AgentBridge.Host.Core/Usage/CopilotCredentials.cs:67:// (agent-ha-bridge #122). agent-ha-bridge reads it only when usage.keychain says to; this host
$ rg --sort path -n -F -- '// does not take that setting yet (#52), so it never does.' src/AgentBridge.Host.Core/Usage/CopilotCredentials.cs
68:// does not take that setting yet (#52), so it never does.
```

**Do:** Add an explicitly enabled Mac credential source (new) for usage reporting. On denial or token rotation, stop trying and tell the user how to recover; never trigger repeated background prompts.

**Why:** The current source intentionally skips this credential. A Mac user whose credential resides only there cannot use that route for usage reporting.

**Effort:** M · **Impact:** M

## Stability

### STAB-1. Reject unusable device-flow polling parameters

**Location:** `pwa/js/signin.js:13`, `pwa/js/signin.js:18`, `pwa/js/signin.js:25`, `src/AgentBridge.Transport/SignIn.cs:19`, `src/AgentBridge.Transport/SignIn.cs:23`

**Proof:**

`pwa/js/signin.js:13`
```
    expiresIn: Number(s.expiresIn) || 900,
    // Polls until the code is entered, refused or expired. `signal` cancels.
    async wait(signal) {
      let interval = Number(s.interval) || 5;
      const deadline = Date.now() + (Number(s.expiresIn) || 900) * 1000;
```

`pwa/js/signin.js:18`
```
      while (Date.now() < deadline) {
        await sleep(interval * 1000);
        if (signal?.aborted) { throw new Error('sign-in cancelled'); }
        const t = await fetch(`${base}/v1/auth/token`, {
```

`pwa/js/signin.js:25`
```
        });
        const j = await t.json().catch(() => ({}));
        if (t.status === 202) { if (j.interval) { interval = Number(j.interval) || interval; } continue; }
        if (!t.ok) { throw new Error(`sign-in failed: ${j.error ?? t.status}`); }
        return j;
```

`src/AgentBridge.Transport/SignIn.cs:19`
```
        show(s.GetProperty("userCode").GetString()!, s.GetProperty("verificationUri").GetString()!);
        var deviceCode = s.GetProperty("deviceCode").GetString()!;
        var interval = s.GetProperty("interval").GetInt32();
        var deadline = DateTime.UtcNow.AddSeconds(s.GetProperty("expiresIn").GetInt32());
```

`src/AgentBridge.Transport/SignIn.cs:23`
```
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var r = await http.PostAsJsonAsync(new Uri(relay, "/v1/auth/token"), new { deviceCode }, ct);
```

**Verified:**

```console
$ rg --sort path -n -- 'Number\.isFinite|Math\.min\(.*interval|Clamp\(.*interval|interval <= 60' pwa/app.css pwa/index.html pwa/js pwa/sw.js src/AgentBridge.Transport
pwa/js/answers.js:59:    case 'number': return typeof value === 'number' && Number.isFinite(value) ? null : 'expected a number';
pwa/js/answers.js:215:    case 'number': return { text: typeof d === 'number' && Number.isFinite(d) ? String(d) : typeof d === 'string' && d.trim() !== '' && Number.isFinite(Number(d)) ? d.trim() : '', bad: false };
pwa/js/answers.js:270:        if (Number.isFinite(n)) { fields[field.name] = n; } else { problems.push({ field: field.name, message: `"${label(field)}": expected a number` }); }
pwa/js/host-client.js:264:            this.store.revision = Number.isFinite(data.revision) ? data.revision : this.store.revision;
pwa/js/protocol.js:63:const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
pwa/js/relay.js:80:        this.blobs = m.blobs && Number.isFinite(m.blobs.max) && m.blobs.max > 0 ? { max: m.blobs.max } : null;
pwa/js/ui/time.js:5:  if (!Number.isFinite(t)) { return ''; }
pwa/js/ui/time.js:13:  if (!Number.isFinite(t)) { return ''; }
pwa/js/ui/time.js:28:  return Number.isFinite(t) ? new Date(t).toLocaleString() : '';
pwa/js/update.js:32:  const progress = typeof s.progress === 'number' && Number.isFinite(s.progress) ? Math.max(0, Math.min(100, Math.round(s.progress))) : null;
pwa/js/usage.js:16:const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
pwa/js/usage.js:137:  if (!Number.isFinite(percent)) { return ''; }
pwa/js/usage.js:151:  if (!Number.isFinite(at)) { return ''; }
pwa/js/usage.js:168:export const amount = (value) => (Number.isFinite(value) ? Math.round(value).toLocaleString('en-US') : '');
pwa/js/usage.js:175:  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) { return null; }
$ rg --sort path -n -F -- 'async wait(signal) {' pwa/js/signin.js
15:    async wait(signal) {
$ rg --sort path -n -F -- 'await sleep(interval * 1000);' pwa/js/signin.js
19:        await sleep(interval * 1000);
$ rg --sort path -n -F -- 'return j;' pwa/js/signin.js
29:        return j;
$ rg --sort path -n -F -- 'var interval = s.GetProperty("interval").GetInt32();' src/AgentBridge.Transport/SignIn.cs
21:        var interval = s.GetProperty("interval").GetInt32();
$ rg --sort path -n -F -- 'while (DateTime.UtcNow < deadline)' src/AgentBridge.Transport/SignIn.cs
23:        while (DateTime.UtcNow < deadline)
```

**Do:** Reject empty codes, invalid TTLs, nonpositive or excessive initial and updated intervals before sleeping (new validation). Exercise malformed success and pending responses on web and .NET.

**Why:** The web converts response fields with permissive defaults, and .NET feeds the supplied interval to a delay. Negative or extreme values can trigger rapid polling, exceptions or long stalls.

**Effort:** S · **Impact:** M

### STAB-2. Validate a token response before the web client enters its signed-in state

**Location:** `pwa/js/signin.js:25`, `pwa/js/signin.js:30`, `pwa/js/signin.js:41`, `pwa/js/main.js:362`, `pwa/js/main.js:367`

**Proof:**

`pwa/js/signin.js:25`
```
        });
        const j = await t.json().catch(() => ({}));
        if (t.status === 202) { if (j.interval) { interval = Number(j.interval) || interval; } continue; }
        if (!t.ok) { throw new Error(`sign-in failed: ${j.error ?? t.status}`); }
        return j;
```

`pwa/js/signin.js:30`
```
      }
```

`pwa/js/signin.js:41`
```
    try {
      const a = JSON.parse(storage.getItem(ACCOUNT) ?? 'null');
      return a && typeof a.token === 'string' ? a : null;
    } catch { return null; }
  },
```

`pwa/js/main.js:362`
```
    $('code-box').hidden = false;
    const account = await flow.wait();
    accountStore.save(account);
    $('code-box').hidden = true;
    $('signin').hidden = true;
```

`pwa/js/main.js:367`
```
    begin(account);
```

**Verified:**

```console
$ rg --sort path -n -- 'validateAccount|isRelayAccount|typeof .*accountId|!.*\.login' pwa/app.css pwa/index.html pwa/js pwa/sw.js
# (no matches)
$ rg --sort path -n -F -- 'return j;' pwa/js/signin.js
29:        return j;
$ rg --sort path -n -F -- '} catch { return null; }' pwa/js/signin.js
44:    } catch { return null; }
$ rg --sort path -n -F -- '$('"'"'signin'"'"').hidden = true;' pwa/js/main.js
366:    $('signin').hidden = true;
$ rg --sort path -n -F -- 'begin(account);' pwa/js/main.js
367:    begin(account);
617:  if (account) { begin(account); } else { showSignIn(); }
```

**Do:** Require a nonempty token, account identifier and login on success (new validation); accept only the expected HTTP success status. Fail before persistence or navigation and test empty, partial and non-JSON successful responses.

**Why:** A JSON parse failure becomes an empty object; the OK branch returns it, then the page saves it and switches to signed-in UI.

**Effort:** S · **Impact:** M

### STAB-3. Turn invalid upstream GitHub payloads into a defined relay error

**Location:** `src/AgentBridge.Relay/RelayApp.cs:156`, `src/AgentBridge.Relay/RelayApp.cs:164`, `src/AgentBridge.Relay/RelayApp.cs:196`

**Proof:**

`src/AgentBridge.Relay/RelayApp.cs:156`
```
            using var r = await gh.PostAsync($"{options.GitHubBaseUrl}/login/device/code",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = options.GitHubClientId }));
            if (!r.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            if (!j.TryGetProperty("device_code", out var code)) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
```

`src/AgentBridge.Relay/RelayApp.cs:164`
```
                userCode = j.GetProperty("user_code").GetString(),
                verificationUri = j.GetProperty("verification_uri").GetString(),
                interval = j.GetProperty("interval").GetInt32(),
                expiresIn = j.GetProperty("expires_in").GetInt32(),
            });
```

`src/AgentBridge.Relay/RelayApp.cs:196`
```
            using var ur = await gh.SendAsync(userReq);
            if (!ur.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
            var user = JsonDocument.Parse(await ur.Content.ReadAsStringAsync()).RootElement;
            var account = new Account(user.GetProperty("id").GetInt64().ToString(), user.GetProperty("login").GetString()!);
            await RevokeAsync(gh, githubToken, log);
```

**Verified:**

```console
$ rg --sort path -n -- 'TryParse.*Json|catch \(JsonException|BadGateway|github_error' src/AgentBridge.Relay
src/AgentBridge.Relay/RelayApp.cs:158:            if (!r.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
src/AgentBridge.Relay/RelayApp.cs:160:            if (!j.TryGetProperty("device_code", out var code)) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
src/AgentBridge.Relay/RelayApp.cs:185:                var error = j.TryGetProperty("error", out var e) ? e.GetString() : "github_error";
src/AgentBridge.Relay/RelayApp.cs:197:            if (!ur.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
src/AgentBridge.Relay/RelayHub.cs:211:                try { m = JsonDocument.Parse(data).RootElement; } catch (JsonException) { continue; }
src/AgentBridge.Relay/Roster.cs:553:            catch (JsonException) { metrics.Roster("load", "corrupt"); }
$ rg --sort path -n -F -- 'var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;' src/AgentBridge.Relay/RelayApp.cs
159:            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
182:            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
$ rg --sort path -n -F -- 'interval = j.GetProperty("interval").GetInt32(),' src/AgentBridge.Relay/RelayApp.cs
166:                interval = j.GetProperty("interval").GetInt32(),
$ rg --sort path -n -F -- 'await RevokeAsync(gh, githubToken, log);' src/AgentBridge.Relay/RelayApp.cs
200:            await RevokeAsync(gh, githubToken, log);
```

**Do:** Parse each upstream response into a bounded validated result (new) before accessing required fields. Return a consistent gateway error for malformed JSON or missing fields; test a successful HTTP response with an invalid body.

**Why:** Checking only device_code does not establish that interval or verification_uri exists. Later throwing getters and parses can bypass the intended upstream-error response.

**Effort:** M · **Impact:** M

### STAB-4. Keep pinned sessions when the order history rolls over

**Location:** `apple/State/BridgeStore.swift:684`, `apple/State/BridgeStore.swift:695`

**Proof:**

`apple/State/BridgeStore.swift:684`
```
        let newSessions = sessions.filter { !manualOrder.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }
        guard !newSessions.isEmpty else { return }
        let newIDs = newSessions.map { machineID + ":" + $0.id }
        manualOrder = Array((newIDs + manualOrder).prefix(300))
```

`apple/State/BridgeStore.swift:695`
```
        readVersions = readVersions.filter { kept.contains($0.key) }
        pinned.formIntersection(kept)
        UserDefaults.standard.set(manualOrder, forKey: preferenceScope + ".order")
        UserDefaults.standard.set(readVersions, forKey: preferenceScope + ".read")
        UserDefaults.standard.set(Array(pinned), forKey: preferenceScope + ".pins")
```

**Verified:**

```console
$ rg --sort path -n -- 'pinned.*prefix|prefix.*pinned|pinned\.formIntersection' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
apple/State/BridgeStore.swift:696:        pinned.formIntersection(kept)
$ rg --sort path -n -F -- 'guard !newSessions.isEmpty else { return }' apple/State/BridgeStore.swift
685:        guard !newSessions.isEmpty else { return }
$ rg --sort path -n -F -- 'pinned.formIntersection(kept)' apple/State/BridgeStore.swift
696:        pinned.formIntersection(kept)
```

**Do:** Separate explicit pins from the bounded recent-order history. Exercise one pinned session followed by 300 newer session IDs from other machines.

**Why:** Registration caps the history and intersects the pin set with its survivors, silently removing an intentional pin.

**Effort:** M · **Impact:** M

### STAB-5. Handle pasted tabs and line breaks in Windows search

**Location:** `src/AgentBridge.App.Core/MainViewModel.Search.cs:10`

**Proof:**

`src/AgentBridge.App.Core/MainViewModel.Search.cs:10`
```
    public IReadOnlyList<SearchHit> Search(string? query, int most = 8)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { return []; }
        var several = machines.Count > 1;
```

**Verified:**

```console
$ rg --sort path -n -- 'Split\(\(char\[\]\?\)null|char\.IsWhiteSpace|Regex\.Split' src/AgentBridge.App.Core
src/AgentBridge.App.Core/Delegates.cs:320:        r.Text = string.IsNullOrWhiteSpace(draft) ? ask : char.IsWhiteSpace(draft[^1]) ? draft + ask : $"{draft} {ask}";
src/AgentBridge.App.Core/HostManager.cs:132:        arg.Length > 0 && arg.All(c => !char.IsWhiteSpace(c) && c != '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
$ rg --sort path -n -F -- 'var several = machines.Count > 1;' src/AgentBridge.App.Core/MainViewModel.Search.cs
14:        var several = machines.Count > 1;
```

**Do:** Split on whitespace instead of literal spaces and use the normalized query for prefix matching. Verify that two words pasted with a tab match the same cards as space-separated input.

**Why:** The desktop tokenizer only treats a space as a separator; a pasted tab remains inside a single term.

**Effort:** S · **Impact:** S

## Reliability

### REL-1. Verify one iPad reply all the way to a Mac agent

**Location:** `docs/apple-parity.md:102`, `docs/apple-parity.md:107`

**Proof:**

`docs/apple-parity.md:102`
```
authorization. The signed app showed two connected machines and two live sessions;
selecting one showed the conversation, an editable reply with a staged image,
and the session inspector. This verifies physical sign-in, discovery, session
data and composer presentation. It does not establish delivery of that draft.
```

`docs/apple-parity.md:107`
```
Still pending: the remaining real .NET-host/relay interoperability cases covering
multi-machine reconnect, answer/reply/stop receipts, attachment delivery, launch,
folder trust, copy/resume and remote update requests; OS notification
authorization/delivery and Mac login registration; physical iPhone and minimum-supported-OS devices; and VoiceOver
plus large-text interaction testing. These are validation gaps for implemented
```

**Verified:**

```console
$ rg --sort path -n -- 'physical iPad|physical.*receipt|physical.*deliver|Shiva III' apple/README.md apple/Tests docs/apple-native-interop-acceptance.md docs/apple-parity.md tests/AgentBridge.AppleInterop.Tests tools/AgentBridge.AppleInterop
apple/README.md:95:simulators. A signed physical iPad completed GitHub authorization and displayed
docs/apple-parity.md:100:On a physical iPad, switching to Safari for device authorization exposed a lost
$ rg --sort path -n -F -- 'selecting one showed the conversation, an editable reply with a staged image,' docs/apple-parity.md
103:selecting one showed the conversation, an editable reply with a staged image,
$ rg --sort path -n -F -- 'folder trust, copy/resume and remote update requests; OS notification' docs/apple-parity.md
109:folder trust, copy/resume and remote update requests; OS notification
```

**Do:** Run a repeatable live acceptance case (new) in a disposable approved Mac workspace: launch a managed session, send a unique harmless reply from the physical iPad, observe it exactly once in the agent, and match the client receipt to the host outcome. Repeat with an interrupted receipt, retaining the original request identity and checking for duplicate input.

**Why:** The recorded device run reached the editable composer and inspector. It expressly stops before transmission; rendered UI and fixture receipts do not prove live delivery.

**Effort:** M · **Impact:** L

### REL-2. Finish or recover service-worker cache writes within the event lifetime

**Location:** `pwa/sw.js:46`, `pwa/sw.js:50`

**Proof:**

`pwa/sw.js:46`
```
  e.respondWith(fetch(e.request.url, { cache: 'no-cache', credentials: 'same-origin' })
    .then((r) => {
      if (r.ok) { const copy = r.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); }
      return r;
    })
```

`pwa/sw.js:50`
```
    })
    .catch(() => caches.match(e.request, { ignoreSearch: true })));
});
```

**Verified:**

```console
$ rg --sort path -n -- 'waitUntil\(|CACHE.*match|cache\.put|\.put\(e.request' pwa/app.css pwa/index.html pwa/js pwa/sw.js
pwa/sw.js:32:  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL.map((u) => new Request(u, { cache: 'reload' })))));
pwa/sw.js:38:  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k.startsWith(PREFIX) && k !== CACHE).map((k) => caches.delete(k)))));
pwa/sw.js:48:      if (r.ok) { const copy = r.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); }
pwa/sw.js:101:  e.waitUntil(self.registration.showNotification(title, options));
pwa/sw.js:110:  e.waitUntil((async () => {
$ rg --sort path -n -F -- 'return r;' pwa/sw.js
49:      return r;
$ rg --sort path -n -F -- '.catch(() => caches.match(e.request, { ignoreSearch: true })));' pwa/sw.js
51:    .catch(() => caches.match(e.request, { ignoreSearch: true })));
```

**Do:** Attach cache updates to the fetch event lifetime and handle quota failure without failing a successful network response. In an offline test, fetch an updated asset, terminate the worker, then reopen it.

**Why:** The response returns before the asynchronous cache write completes. Worker shutdown can leave the offline copy stale; write rejection also goes unobserved.

**Effort:** S · **Impact:** M

### REL-3. Preserve Windows settings if a write is interrupted

**Location:** `src/AgentBridge.App.Core/Stores.cs:70`, `src/AgentBridge.App.Core/Stores.cs:75`, `src/AgentBridge.App.Core/Stores.cs:120`, `src/AgentBridge.App.Core/Stores.cs:126`

**Proof:**

`src/AgentBridge.App.Core/Stores.cs:70`
```
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
```

`src/AgentBridge.App.Core/Stores.cs:75`
```
        // A nicety: the card still works without it.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
```

`src/AgentBridge.App.Core/Stores.cs:120`
```
    public static AppPreferences Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
```

`src/AgentBridge.App.Core/Stores.cs:126`
```
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
```

**Verified:**

```console
$ rg --sort path -n -- 'File\.Replace|WriteAllTextAsync|\.tmp|File\.Move' src/AgentBridge.App.Core
src/AgentBridge.App.Core/UsageCache.cs:133:            var temp = path + ".tmp";
src/AgentBridge.App.Core/UsageCache.cs:135:            File.Move(temp, path, overwrite: true);
$ rg --sort path -n -F -- 'Directory.CreateDirectory(Path.GetDirectoryName(path)!);' src/AgentBridge.App.Core/Stores.cs
72:            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
128:        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
$ rg --sort path -n -F -- '// A nicety: the card still works without it.' src/AgentBridge.App.Core/Stores.cs
75:        // A nicety: the card still works without it.
$ rg --sort path -n -F -- 'public static AppPreferences Load(string path)' src/AgentBridge.App.Core/Stores.cs
120:    public static AppPreferences Load(string path)
$ rg --sort path -n -F -- 'public void Save(string path)' src/AgentBridge.App.Core/Stores.cs
126:    public void Save(string path)
```

**Do:** Write a sibling temporary file and replace the previous file atomically. Exercise an interrupted preference save and confirm the last valid settings and fold state remain readable.

**Why:** Both paths write directly to their destination. A partially written JSON file is later treated as default state.

**Effort:** M · **Impact:** M

### REL-4. Distinguish failed Home Assistant probes from healthy quiet periods

**Location:** `src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs:245`, `src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs:303`

**Proof:**

`src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs:245`
```
            var sentinel = ServesAnswers ? SessionEntities.Reply(p.Node) : SessionEntities.Status(p.Node);
            HaState? state;
            try { state = await ha.GetStateAsync(sentinel, ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { continue; }
            var shortId = id[..Math.Min(8, id.Length)];
```

`src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs:303`
```
            HaState? decision;
            try { decision = await ha.GetStateAsync(SessionEntities.Decision(p.Node), ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { return; }
            // The selector gone on its own, the reply box still there: it alone is declared again.
```

**Verified:**

```console
$ rg --sort path -n -- 'probeFailed|probeFailure|upkeep.*(error|warn)|sentinel.*failed' src/AgentBridge.HomeAssistant
# (no matches)
$ rg --sort path -n -F -- 'HaState? state;' src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs
246:            HaState? state;
$ rg --sort path -n -F -- 'HaState? decision;' src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs
303:            HaState? decision;
```

**Do:** Count failed upkeep reads and surface a rate-limited diagnostic (new) without entity contents or tokens. Preserve the skip-and-retry behavior and verify recovery against a disposable connector.

**Why:** A failed state read silently skips that session. An operator cannot distinguish failure of these reads from a pass where no restoration was necessary using this branch alone.

**Effort:** S · **Impact:** M

### REL-5. Tell a hook read failure apart from a malformed event

**Location:** `src/AgentBridge.Hooks/HookRunner.cs:221`, `src/AgentBridge.Hooks/HookRunner.cs:226`, `src/AgentBridge.Hooks/HookRunner.cs:114`

**Proof:**

`src/AgentBridge.Hooks/HookRunner.cs:221`
```
                var room = max - (int)copy.Length;
                if (room <= 0) { return (copy.ToArray(), false); }
                var read = stdin.Read(buffer, 0, Math.Min(buffer.Length, room));
                if (read <= 0) { return (copy.ToArray(), true); }
                copy.Write(buffer, 0, read);
```

`src/AgentBridge.Hooks/HookRunner.cs:226`
```
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { return ([], true); }
    }
```

`src/AgentBridge.Hooks/HookRunner.cs:114`
```
                if (agent == "codex" && finished && whole) { (codexSession, codexEvent) = SpoolContract.CodexEvent(trimmed); }
                if (!alive) { reason = why; }
                else if (!finished) { reason = $"event not finished within {wait.TotalSeconds:0.###}s"; }
                else if (!whole) { reason = "event too large"; }
                else if (!SpoolContract.IsJson(trimmed)) { reason = "unusable event"; }
```

**Verified:**

```console
$ rg --sort path -n -- 'unreadable event|input read failed|stdin read failed|ReadFailure' src/AgentBridge.Hooks
# (no matches)
$ rg --sort path -n -F -- 'copy.Write(buffer, 0, read);' src/AgentBridge.Hooks/HookRunner.cs
225:                copy.Write(buffer, 0, read);
$ rg --sort path -n -F -- 'catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { return ([], true); }' src/AgentBridge.Hooks/HookRunner.cs
228:        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { return ([], true); }
$ rg --sort path -n -F -- 'if (!alive) { reason = why; }' src/AgentBridge.Hooks/HookRunner.cs
115:                if (!alive) { reason = why; }
```

**Do:** Return a distinct read-failure reason (new) from hook input handling. Preserve the same fail-open agent reply, omit payload contents from diagnostics and test a stream that throws after a partial read.

**Why:** The catch flags an empty buffer as complete. A failed read is then diagnosed as unusable JSON, which sends operators looking for a producer bug instead of an interrupted pipe.

**Effort:** S · **Impact:** M

## Security

### SEC-1. Pin the GitHub device-authorization destination across clients

**Location:** `pwa/js/signin.js:9`, `pwa/js/main.js:357`, `src/AgentBridge.App/SettingsView.xaml.cs:48`, `apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:94`, `pwa/index.html:91`, `src/AgentBridge.Transport/SignIn.cs:16`

**Proof:**

`pwa/js/signin.js:9`
```
  const s = await r.json();
  return {
    userCode: String(s.userCode ?? ''),
    verificationUri: String(s.verificationUri ?? ''),
    expiresIn: Number(s.expiresIn) || 900,
```

`pwa/js/main.js:357`
```
  try {
    const flow = await startDeviceFlow(BASE);
    $('code').textContent = flow.userCode;
    $('verify').href = flow.verificationUri;
    $('verify').textContent = flow.verificationUri.replace(/^https?:\/\//, '');
```

`src/AgentBridge.App/SettingsView.xaml.cs:48`
```
            var data = new DataPackage();
            data.SetText(code);
            Clipboard.SetContent(data);
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) { await Windows.System.Launcher.LaunchUriAsync(u); }
        }
```

`apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:94`
```
        guard !code.deviceCode.isEmpty, !code.userCode.isEmpty, code.expiresIn > 0,
              code.expiresIn <= 3600, code.interval > 0, code.interval <= 60,
              code.verificationUri.scheme == "https", code.verificationUri.host == "github.com",
              code.verificationUri.user == nil, code.verificationUri.password == nil
        else { throw RelayError.invalidMessage }
```

`pwa/index.html:91`
```
        <div id="code-box" hidden>
          <p>Open <a id="verify" target="_blank" rel="noopener noreferrer">github.com/login/device</a> and enter this code:</p>
          <p class="code" id="code" aria-live="polite"></p>
          <p class="note" role="status">Waiting for GitHub…</p>
        </div>
```

`src/AgentBridge.Transport/SignIn.cs:16`
```
        var start = await http.PostAsync(new Uri(relay, "/v1/auth/device"), null, ct);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<JsonElement>(ct);
        show(s.GetProperty("userCode").GetString()!, s.GetProperty("verificationUri").GetString()!);
        var deviceCode = s.GetProperty("deviceCode").GetString()!;
```

**Verified:**

```console
$ rg --sort path -n -- 'github\.com/login/device|verificationUri\.path|verificationUri\.port|Uri\.Host.*github' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State pwa/app.css pwa/index.html pwa/js pwa/sw.js src/AgentBridge.App src/AgentBridge.App.Core src/AgentBridge.Transport
pwa/index.html:92:          <p>Open <a id="verify" target="_blank" rel="noopener noreferrer">github.com/login/device</a> and enter this code:</p>
$ rg --sort path -n -F -- 'return {' pwa/js/signin.js
10:  return {
$ rg --sort path -n -F -- '$('"'"'code'"'"').textContent = flow.userCode;' pwa/js/main.js
359:    $('code').textContent = flow.userCode;
$ rg --sort path -n -F -- 'data.SetText(code);' src/AgentBridge.App/SettingsView.xaml.cs
49:            data.SetText(code);
$ rg --sort path -n -F -- 'else { throw RelayError.invalidMessage }' apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift
98:        else { throw RelayError.invalidMessage }
105:        guard (1 ... 3600).contains(code.expiresIn), (1 ... 60).contains(code.interval) else { throw RelayError.invalidMessage }
159:            guard !account.token.isEmpty, !account.accountId.isEmpty, !account.login.isEmpty else { throw RelayError.invalidMessage }
$ rg --sort path -n -F -- '<div id="code-box" hidden>' pwa/index.html
91:        <div id="code-box" hidden>
$ rg --sort path -n -F -- 'start.EnsureSuccessStatusCode();' src/AgentBridge.Transport/SignIn.cs
17:        start.EnsureSuccessStatusCode();
```

**Do:** Use a shared conformance table (new) for the exact supported device page. Reject other schemes, origins, ports, credentials and unrelated paths before copying a code, displaying a link or launching a browser; test Windows, PWA and Apple with the same malformed relay responses.

**Why:** The PWA links the relay-provided string directly, Windows accepts any absolute HTTPS destination, and Apple checks the GitHub host but not the device path. A malformed or compromised relay can misdirect the authorization step; this is a confirmed trust boundary, not evidence of exploitation. The host CLI is also handed the URI: share the validation where the .NET device flow receives it.

**Effort:** S · **Impact:** L

### SEC-2. Attempt upstream token revocation even when account lookup fails

**Location:** `src/AgentBridge.Relay/RelayApp.cs:193`, `src/AgentBridge.Relay/RelayApp.cs:198`, `src/AgentBridge.Relay/RelayApp.cs:230`

**Proof:**

`src/AgentBridge.Relay/RelayApp.cs:193`
```
            var githubToken = at.GetString()!;
            using var userReq = new HttpRequestMessage(HttpMethod.Get, $"{options.GitHubApiUrl}/user");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
            using var ur = await gh.SendAsync(userReq);
            if (!ur.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
```

`src/AgentBridge.Relay/RelayApp.cs:198`
```
            var user = JsonDocument.Parse(await ur.Content.ReadAsStringAsync()).RootElement;
            var account = new Account(user.GetProperty("id").GetInt64().ToString(), user.GetProperty("login").GetString()!);
            await RevokeAsync(gh, githubToken, log);
            var (token, expires) = tokens.Issue(account);
            // No login or id: logs carry no identities (design §5.10).
```

`src/AgentBridge.Relay/RelayApp.cs:230`
```
        async Task RevokeAsync(HttpClient gh, string githubToken, ILogger log)
        {
            if (string.IsNullOrEmpty(options.GitHubClientSecret)) { return; }
            using var del = new HttpRequestMessage(HttpMethod.Delete, $"{options.GitHubApiUrl}/applications/{options.GitHubClientId}/token")
```

**Verified:**

```console
$ rg --sort path -n -- 'finally|RevokeAsync\(' src/AgentBridge.Relay
src/AgentBridge.Relay/Blobs.cs:117:        finally { keyLock.Release(); }
src/AgentBridge.Relay/RelayApp.cs:200:            await RevokeAsync(gh, githubToken, log);
src/AgentBridge.Relay/RelayApp.cs:230:        async Task RevokeAsync(HttpClient gh, string githubToken, ILogger log)
src/AgentBridge.Relay/RelayHub.cs:61:            finally { sendLock.Release(); }
src/AgentBridge.Relay/RelayHub.cs:79:            finally { sendLock.Release(); }
src/AgentBridge.Relay/RelayHub.cs:100:            finally { sendLock.Release(); }
src/AgentBridge.Relay/RelayHub.cs:147:        finally
src/AgentBridge.Relay/Roster.cs:299:            finally { a.Io.Release(); }
src/AgentBridge.Relay/Roster.cs:443:            finally { a.Io.Release(); }
src/AgentBridge.Relay/Roster.cs:526:        finally
$ rg --sort path -n -F -- 'var githubToken = at.GetString()!;' src/AgentBridge.Relay/RelayApp.cs
193:            var githubToken = at.GetString()!;
$ rg --sort path -n -F -- 'await RevokeAsync(gh, githubToken, log);' src/AgentBridge.Relay/RelayApp.cs
200:            await RevokeAsync(gh, githubToken, log);
$ rg --sort path -n -F -- 'if (string.IsNullOrEmpty(options.GitHubClientSecret)) { return; }' src/AgentBridge.Relay/RelayApp.cs
232:            if (string.IsNullOrEmpty(options.GitHubClientSecret)) { return; }
```

**Do:** When revocation is configured, attempt it in a cleanup path after obtaining the GitHub token, including on account lookup or parsing failures. Keep cleanup bounded and never log the token.

**Why:** The non-success lookup returns before revocation and invalid user JSON throws before it; GitHub credentials can remain valid even though Agent Bridge did not issue an account token.

**Effort:** S · **Impact:** L

### SEC-3. Reduce the lifetime of a browser-readable relay credential

**Location:** `pwa/js/signin.js:39`, `pwa/js/signin.js:44`, `pwa/js/signin.js:46`, `src/AgentBridge.Relay/RelayOptions.cs:14`

**Proof:**

`pwa/js/signin.js:39`
```
export const accountStore = {
  load(storage = localStorage) {
    try {
      const a = JSON.parse(storage.getItem(ACCOUNT) ?? 'null');
      return a && typeof a.token === 'string' ? a : null;
```

`pwa/js/signin.js:44`
```
    } catch { return null; }
```

`pwa/js/signin.js:46`
```
  // The relay's token as issued. The product will keep a short-lived token and refresh it;
  // that needs relay support that does not exist yet.
  save(account, storage = localStorage) { storage.setItem(ACCOUNT, JSON.stringify(account)); },
  clear(storage = localStorage) { storage.removeItem(ACCOUNT); },
};
```

`src/AgentBridge.Relay/RelayOptions.cs:14`
```
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public int MaxFramesPerSecond { get; set; } = 200;
```

**Verified:**

```console
$ rg --sort path -n -- 'refreshToken|refresh_token|HttpOnly|TokenLifetime' pwa/app.css pwa/index.html pwa/js pwa/sw.js src/AgentBridge.Relay
src/AgentBridge.Relay/RelayOptions.cs:14:    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
src/AgentBridge.Relay/Tokens.cs:17:        lifetime = o.TokenLifetime;
$ rg --sort path -n -F -- 'export const accountStore = {' pwa/js/signin.js
39:export const accountStore = {
$ rg --sort path -n -F -- '} catch { return null; }' pwa/js/signin.js
44:    } catch { return null; }
$ rg --sort path -n -F -- '// that needs relay support that does not exist yet.' pwa/js/signin.js
47:  // that needs relay support that does not exist yet.
$ rg --sort path -n -F -- 'public int MaxFramesPerSecond { get; set; } = 200;' src/AgentBridge.Relay/RelayOptions.cs
15:    public int MaxFramesPerSecond { get; set; } = 200;
```

**Do:** Design a revocable, rotating browser session (new) with short-lived access credentials and a recovery path for installed web apps. Validate against the separately hosted relay/PWA arrangement before changing defaults.

**Why:** The browser stores the issued account object in localStorage; the default relay token lifetime is 30 days. Same-origin script execution or access to browser storage could expose a reusable credential.

**Effort:** L · **Impact:** L

### SEC-4. Reject insecure remote update feeds in installed Windows builds

**Location:** `src/AgentBridge.App/Platform.cs:31`, `src/AgentBridge.App/Platform.cs:36`

**Proof:**

`src/AgentBridge.App/Platform.cs:31`
```
    UpdateManager Manager()
    {
        var feed = File.Exists(FeedFile) ? File.ReadAllText(FeedFile).Trim() : "";
        var key = $"{feed}|{prereleases()}";
        if (manager is not null && source == key) { return manager; }
```

`src/AgentBridge.App/Platform.cs:36`
```
        IUpdateSource from = feed.Length == 0
            ? new GithubSource(Repository, null, prereleases(), null)
            : Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                ? new SimpleWebSource(feed)
                : new SimpleFileSource(new DirectoryInfo(feed));
```

**Verified:**

```console
$ rg --sort path -n -- 'IsLoopback|UriSchemeHttps|feed.*localhost|feed.*127\.0\.0\.1' src/AgentBridge.App
src/AgentBridge.App/SettingsView.xaml.cs:51:            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) { await Windows.System.Launcher.LaunchUriAsync(u); }
$ rg --sort path -n -F -- 'UpdateManager Manager()' src/AgentBridge.App/Platform.cs
31:    UpdateManager Manager()
$ rg --sort path -n -F -- '? new SimpleWebSource(feed)' src/AgentBridge.App/Platform.cs
39:                ? new SimpleWebSource(feed)
```

**Do:** Restrict remote feed overrides to HTTPS. Permit loopback HTTP only in an explicit development mode (new), retaining local-file support. Test release-channel behavior without fetching or installing an update.

**Why:** A local override can currently select cleartext HTTP. A network intermediary could alter the feed response; this review does not establish whether a malicious package would pass downstream checks.

**Effort:** S · **Impact:** M

### SEC-5. Confirm cleanup of private tmux input buffers

**Location:** `src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:242`

**Proof:**

`src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:242`
```
                        // -d removes a successfully pasted buffer. This also removes one
                        // left by cancellation, a changed prompt or an uncertain paste.
                        try { await commands.RunAsync(["delete-buffer", "-b", buffer]).ConfigureAwait(false); }
                        catch (Exception) { }
                    }
```

**Verified:**

```console
$ rg --sort path -n -- 'delete-buffer|list-buffers' src/AgentBridge.Host.Delivery/Mac
src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:244:                        try { await commands.RunAsync(["delete-buffer", "-b", buffer]).ConfigureAwait(false); }
$ rg --sort path -n -F -- 'catch (Exception) { }' src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs
245:                        catch (Exception) { }
```

**Do:** Distinguish an already-consumed buffer from a failed delete. Track uncertain cleanup for a bounded retry on the same verified tmux server, using only the buffer identifier and never logging its contents.

**Why:** Private reply text may remain in the managed server if both paste and cleanup fail. Current cleanup ignores exit status and exceptions. The existing delete already runs after an uncertain paste; only unconfirmed cleanup is in scope.

**Effort:** M · **Impact:** M

## Usability

### USE-1. Show the failing stage of sign-in where the user returns

**Location:** `apple/Apps/ConnectionView.swift:66`, `apple/State/BridgeStore.swift:310`

**Proof:**

`apple/Apps/ConnectionView.swift:66`
```
                        }
                    }
                    if let status = store.signInStatus {
                        Text(status).font(.callout).foregroundStyle(.secondary)
                    }
```

`apple/State/BridgeStore.swift:310`
```
                guard token == signInGeneration else { return }
                isSigningIn = false
                signInCode = nil
                signInStatus = error.localizedDescription
            }
```

**Verified:**

```console
$ rg --sort path -n -- 'signInStage|credentialStorageStatus|signInRecovery' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
# (no matches)
$ rg --sort path -n -F -- 'if let status = store.signInStatus {' apple/Apps/ConnectionView.swift
68:                    if let status = store.signInStatus {
$ rg --sort path -n -F -- 'signInCode = nil' apple/State/BridgeStore.swift
306:                signInCode = nil
312:                signInCode = nil
323:        signInCode = nil
```

**Do:** Render the last failed stage and a recovery action beside the account status (new). Distinguish browser approval, saving on this device, and connecting to the relay; avoid showing secrets.

**Why:** After browser approval, a generic error beside a fresh sign-in button does not tell the user which part succeeded or whether retrying GitHub would help.

**Effort:** M · **Impact:** L

### USE-2. Make web device-code entry a single deliberate action

**Location:** `pwa/index.html:91`, `pwa/js/main.js:357`, `pwa/js/main.js:362`

**Proof:**

`pwa/index.html:91`
```
        <div id="code-box" hidden>
          <p>Open <a id="verify" target="_blank" rel="noopener noreferrer">github.com/login/device</a> and enter this code:</p>
          <p class="code" id="code" aria-live="polite"></p>
          <p class="note" role="status">Waiting for GitHub…</p>
        </div>
```

`pwa/js/main.js:357`
```
  try {
    const flow = await startDeviceFlow(BASE);
    $('code').textContent = flow.userCode;
    $('verify').href = flow.verificationUri;
    $('verify').textContent = flow.verificationUri.replace(/^https?:\/\//, '');
```

`pwa/js/main.js:362`
```
    $('code-box').hidden = false;
```

**Verified:**

```console
$ rg --sort path -n -- 'clipboard\.writeText|Copy code|Copy Code' pwa/app.css pwa/index.html pwa/js pwa/sw.js
pwa/js/markdown.js:475:  button.setAttribute('aria-label', 'Copy code');
pwa/js/markdown.js:477:  // The button's name stays "Copy code"; what happened is announced separately.
pwa/js/markdown.js:508:      await clipboard.writeText(text);
$ rg --sort path -n -F -- '<div id="code-box" hidden>' pwa/index.html
91:        <div id="code-box" hidden>
$ rg --sort path -n -F -- '$('"'"'code'"'"').textContent = flow.userCode;' pwa/js/main.js
359:    $('code').textContent = flow.userCode;
$ rg --sort path -n -F -- '$('"'"'code-box'"'"').hidden = false;' pwa/js/main.js
362:    $('code-box').hidden = false;
```

**Do:** Add an explicit copy-and-open action (new) with confirmation and a selectable-code fallback when the browser denies clipboard access. Keep the destination visible.

**Why:** The web screen asks the user to carry a code to another tab. Windows already offers a single action for that step.

**Effort:** S · **Impact:** M

### USE-3. Explain when Windows cannot copy a code or open GitHub

**Location:** `src/AgentBridge.App/SettingsView.xaml.cs:48`, `src/AgentBridge.App/SettingsView.xaml.cs:53`

**Proof:**

`src/AgentBridge.App/SettingsView.xaml.cs:48`
```
            var data = new DataPackage();
            data.SetText(code);
            Clipboard.SetContent(data);
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) { await Windows.System.Launcher.LaunchUriAsync(u); }
        }
```

`src/AgentBridge.App/SettingsView.xaml.cs:53`
```
        // async void: an escape would end the app. The code and the page are on screen anyway.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
    }
```

**Verified:**

```console
$ rg --sort path -n -- 'LaunchUriAsync|clipboard.*failed|copy.*failed|could not open' src/AgentBridge.App
src/AgentBridge.App/SettingsView.xaml.cs:51:            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) { await Windows.System.Launcher.LaunchUriAsync(u); }
$ rg --sort path -n -F -- 'data.SetText(code);' src/AgentBridge.App/SettingsView.xaml.cs
49:            data.SetText(code);
$ rg --sort path -n -F -- '// async void: an escape would end the app. The code and the page are on screen anyway.' src/AgentBridge.App/SettingsView.xaml.cs
53:        // async void: an escape would end the app. The code and the page are on screen anyway.
```

**Do:** Show a short actionable status (new) if the clipboard write throws or the browser launch returns false; keep the manually selectable code and a trusted URL available.

**Why:** Windows catches clipboard failures without feedback and ignores the browser launch result. Pressing the primary control can therefore appear to do nothing.

**Effort:** S · **Impact:** M

### USE-4. Show the local profile in Mac connection recovery

**Location:** `apple/Sources/AgentBridgeAppleCore/LocalBridge.swift:13`, `apple/Apps/ConnectionView.swift:79`

**Proof:**

`apple/Sources/AgentBridgeAppleCore/LocalBridge.swift:13`
```
                switch self {
                case .disconnected: "The local host disconnected."
                case .timeout: "The local host did not respond in time."
                case .duplicateID: "A request identifier was reused."
                case .invalidMessage: "The local host sent an invalid message."
```

`apple/Apps/ConnectionView.swift:79`
```
                    TextField("This device’s name", text: $deviceName)
                    #if os(macOS)
                        TextField("Local host profile", text: $profile).autocorrectionDisabled()
                    #endif
                    Button("Apply and Reconnect", action: connect)
```

**Verified:**

```console
$ rg --sort path -n -- 'timed out.*profile|profile.*timed out|did not respond.*profile|check.*[Hh]ost profile' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
# (no matches)
$ rg --sort path -n -F -- 'switch self {' apple/Sources/AgentBridgeAppleCore/LocalBridge.swift
13:                switch self {
$ rg --sort path -n -F -- '#if os(macOS)' apple/Apps/ConnectionView.swift
2:#if os(macOS)
80:                    #if os(macOS)
96:                #if os(macOS)
130:        #if os(macOS)
152:        #if os(macOS)
```

**Do:** When the Mac local connection times out, show the selected profile with an action (new) to review it and retry. Keep the explanation separate from GitHub sign-in, since local profile selection and relay authentication are independent.

**Why:** The timeout message names neither the profile nor the next check. The profile is editable elsewhere in settings, which makes a mismatch easy to miss.

**Effort:** S · **Impact:** M

### USE-5. Give web users an action when search has no matches

**Location:** `pwa/js/ui/sessions-page.js:590`, `pwa/js/ui/sessions-page.js:595`

**Proof:**

`pwa/js/ui/sessions-page.js:590`
```
    if (query && n > 0) {
      // Search finds nothing among sessions that are there.
      empty.hidden = this.view.shownCount !== 0;
      empty.textContent = 'No sessions match your search.';
      this.onRendered(n);
```

`pwa/js/ui/sessions-page.js:595`
```
      return;
    }
```

**Verified:**

```console
$ rg --sort path -n -- 'Clear search|clear-search|resetSearch|No sessions match' pwa/app.css pwa/index.html pwa/js pwa/sw.js
pwa/js/ui/sessions-page.js:593:      empty.textContent = 'No sessions match your search.';
$ rg --sort path -n -F -- 'this.onRendered(n);' pwa/js/ui/sessions-page.js
594:      this.onRendered(n);
601:    this.onRendered(n);
```

**Do:** Add a clear-search action (new) beside the zero-results message and return focus to the search input. Preserve the separate offline and zero-live-session explanations.

**Why:** The result of a failed query is a sentence; the user must locate the search field to reset it. This is an inline recovery affordance in addition to the native search input.

**Effort:** S · **Impact:** S

## Attractiveness / Sexiness

### ATTR-1. Separate the conversation visually from the inspector

**Location:** `apple/Apps/SessionDetail.swift:64`, `apple/Apps/SessionDetail.swift:83`

**Proof:**

`apple/Apps/SessionDetail.swift:64`
```
                if let message = session.activity?["userMessage"]?.string, !message.isEmpty {
                    Section("You") { Text(message) }
                }
                if session.response?.isEmpty == false || session.responseTruncated {
                    Section("\(session.agent.capitalized) response") {
```

`apple/Apps/SessionDetail.swift:83`
```
                Section("Details") {
                    ForEach(SessionPresentation.details(session: session, fullSession: fullSession, machineName: machineName)) { detail in
                        LabeledContent(detail.label) {
                            Text(detail.value)
                                .fontDesign(detail.monospaced ? .monospaced : .default)
```

**Verified:**

```console
$ rg --sort path -n -- 'chatBubble|conversationSurface|messageSurface|transcriptStyle' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
# (no matches)
$ rg --sort path -n -F -- 'Section("You") { Text(message) }' apple/Apps/SessionDetail.swift
65:                    Section("You") { Text(message) }
$ rg --sort path -n -F -- 'Text(detail.value)' apple/Apps/SessionDetail.swift
86:                            Text(detail.value)
```

**Do:** Give user prompts and responses distinct spacing and readable text widths (new conversation treatment). Retain compact key/value rows for machine details, and verify the composer stays anchored.

**Why:** Both the prompt and response render as form sections in the same idiom as inspector metadata. The session is difficult to scan as an exchange.

**Effort:** M · **Impact:** M

### ATTR-2. Give long session titles a second line on narrow web and Windows lists

**Location:** `pwa/app.css:607`, `src/AgentBridge.App/SessionRow.xaml:93`

**Proof:**

`pwa/app.css:607`
```
.row-title { min-width: 0; font-size: var(--type-row); line-height: var(--type-row-line); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
```

`src/AgentBridge.App/SessionRow.xaml:93`
```
            TextTrimming="CharacterEllipsis"
            TextWrapping="NoWrap"
            AutomationProperties.AutomationId="RowTitle"
            FontWeight="{x:Bind local:SessionRow.Weight(Card.Unread), Mode=OneWay}"
            Text="{x:Bind Card.Title, Mode=OneWay}" />
```

**Verified:**

```console
$ rg --sort path -n -- 'line-clamp|MaxLines="2"|TextWrapping="Wrap"' pwa/app.css pwa/index.html pwa/js pwa/sw.js src/AgentBridge.App
src/AgentBridge.App/DelegateInspector.xaml:50:                    TextWrapping="Wrap"
src/AgentBridge.App/DelegateInspector.xaml:89:            TextWrapping="Wrap"
src/AgentBridge.App/DelegateInspector.xaml:97:            <TextBlock Style="{StaticResource AbTypeRow}" TextWrapping="Wrap" IsTextSelectionEnabled="True" AutomationProperties.AutomationId="DelegateTask" Text="{x:Bind Card.Delegates.Detail.Task, Mode=OneWay}" />
src/AgentBridge.App/DelegateInspector.xaml:105:                TextWrapping="Wrap"
src/AgentBridge.App/DelegateInspector.xaml:117:            <TextBlock Style="{StaticResource AbTypeRow}" TextWrapping="Wrap" IsTextSelectionEnabled="True" Text="{x:Bind Card.Delegates.Detail.Reasoning, Mode=OneWay}" />
src/AgentBridge.App/DelegateInspector.xaml:137:                            <TextBlock Grid.Column="1" Style="{StaticResource InlineMonoMeta}" TextWrapping="Wrap" ToolTipService.ToolTip="{x:Bind Summary}">
src/AgentBridge.App/LaunchView.xaml:25:                    <TextBlock Style="{StaticResource BodyStrongTextBlockStyle}" TextWrapping="Wrap" Text="{x:Bind Title, Mode=OneWay}" />
src/AgentBridge.App/LaunchView.xaml:29:                        Style="{StaticResource InlineMonoRow}" TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/LaunchView.xaml:32:                        <TextBlock Style="{StaticResource AbSecondaryText}" TextWrapping="Wrap" Text="{x:Bind RootNote, Mode=OneWay}" />
src/AgentBridge.App/LaunchView.xaml:35:                            Style="{StaticResource InlineMonoRow}" TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/LaunchView.xaml:40:                        TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/LaunchView.xaml:52:                        TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/LaunchView.xaml:62:                        TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/LaunchView.xaml:86:                    TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/LaunchView.xaml:103:                    TextWrapping="Wrap"
src/AgentBridge.App/LaunchView.xaml:151:                        AcceptsReturn="True" TextWrapping="Wrap" MinHeight="110" MaxHeight="320"
src/AgentBridge.App/LaunchView.xaml:169:                        TextWrapping="Wrap"
src/AgentBridge.App/LaunchView.xaml:180:                    TextWrapping="Wrap" IsTextSelectionEnabled="True"
src/AgentBridge.App/MainWindow.xaml:224:                                                        TextWrapping="Wrap"
src/AgentBridge.App/MainWindow.xaml:229:                                                        <TextBlock Style="{StaticResource AbTypeMeta}" Foreground="{ThemeResource AbText2}" TextWrapping="Wrap" Text="{x:Bind Update.StatusText, Mode=OneWay}" />
src/AgentBridge.App/MainWindow.xaml:233:                                                            TextWrapping="Wrap"
src/AgentBridge.App/MainWindow.xaml:239:                                                            TextWrapping="Wrap"
src/AgentBridge.App/SessionDetails.xaml:59:                TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:33:                <TextBlock Grid.Row="2" Grid.Column="1" Grid.ColumnSpan="2" Style="{StaticResource AbTypeMeta}" Foreground="{ThemeResource AbText2}" TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis" Text="{x:Bind Line, Mode=OneWay}" Visibility="{x:Bind local:Ui.Show(HasLine), Mode=OneWay}" />
src/AgentBridge.App/SessionView.xaml:269:                            <TextBox IsReadOnly="True" TextWrapping="Wrap" Text="{x:Bind Card.OrphanText, Mode=OneWay}" />
src/AgentBridge.App/SessionView.xaml:276:                        TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:298:                            <TextBlock Style="{StaticResource AbTypeReading}" TextWrapping="Wrap" IsTextSelectionEnabled="True" Text="{x:Bind Card.UserMessage, Mode=OneWay}" />
src/AgentBridge.App/SessionView.xaml:310:                            TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:316:                            TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:332:                        <TextBlock Style="{StaticResource AbTypeRow}" TextWrapping="Wrap" IsTextSelectionEnabled="True" Text="{x:Bind Card.Reasoning, Mode=OneWay}" />
src/AgentBridge.App/SessionView.xaml:381:                        <TextBlock Grid.Column="1" Style="{StaticResource InlineMonoMeta}" Foreground="{ThemeResource AbText2}" TextWrapping="Wrap" AutomationProperties.AutomationId="CurrentTool">
src/AgentBridge.App/SessionView.xaml:392:                    <TextBlock Style="{StaticResource AbTypeRow}" TextWrapping="Wrap" AutomationProperties.AutomationId="DelegatesText" Text="{x:Bind Card.DelegatesText, Mode=OneWay}" />
src/AgentBridge.App/SessionView.xaml:458:                    TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:481:                            TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:558:                    TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:563:                    TextWrapping="Wrap"
src/AgentBridge.App/SessionView.xaml:569:                    TextWrapping="Wrap"
src/AgentBridge.App/SessionsPage.xaml:136:                    TextWrapping="Wrap"
src/AgentBridge.App/SessionsPage.xaml:205:                                                        <TextBlock FontWeight="SemiBold" TextWrapping="Wrap" Text="{x:Bind Title, Mode=OneWay}" />
src/AgentBridge.App/SessionsPage.xaml:208:                                                        TextWrapping="Wrap"
src/AgentBridge.App/SessionsPage.xaml:255:                                                        TextWrapping="Wrap"
src/AgentBridge.App/SettingsView.xaml:47:                <TextBlock TextWrapping="Wrap" AutomationProperties.AutomationId="HostState" Text="{x:Bind ViewModel.HostState, Mode=OneWay}" />
src/AgentBridge.App/SettingsView.xaml:50:                    TextWrapping="Wrap"
src/AgentBridge.App/SettingsView.xaml:66:                <TextBlock TextWrapping="Wrap" AutomationProperties.AutomationId="Account" Text="{x:Bind ViewModel.AccountText, Mode=OneWay}" />
src/AgentBridge.App/SettingsView.xaml:83:                        <TextBlock TextWrapping="Wrap" Text="Open the page below, sign in to GitHub and enter this code:" />
src/AgentBridge.App/SettingsView.xaml:98:                    TextWrapping="Wrap"
src/AgentBridge.App/SettingsView.xaml:155:                    <TextBlock TextWrapping="Wrap" AutomationProperties.AutomationId="DevBoxState" Text="{x:Bind ViewModel.DevBoxText, Mode=OneWay}" />
src/AgentBridge.App/SettingsView.xaml:157:                        TextWrapping="Wrap"
src/AgentBridge.App/SettingsView.xaml:184:                <TextBlock TextWrapping="Wrap" AutomationProperties.AutomationId="UpdateStatus" Text="{x:Bind ViewModel.UpdateStatus, Mode=OneWay}" />
src/AgentBridge.App/ToolList.xaml:33:                        <TextBlock Grid.Column="1" Style="{StaticResource InlineMonoMeta}" TextWrapping="Wrap" ToolTipService.ToolTip="{x:Bind Summary}">
src/AgentBridge.App/UsageView.xaml:16:                <TextBlock Style="{StaticResource AbSecondaryText}" TextWrapping="Wrap" AutomationProperties.AutomationId="UsageSummary" Text="{x:Bind ViewModel.Summary, Mode=OneWay}" />
src/AgentBridge.App/UsageView.xaml:35:                                        <TextBlock FontSize="16" FontWeight="SemiBold" TextWrapping="Wrap" Text="{x:Bind Label}" />
src/AgentBridge.App/UsageView.xaml:36:                                        <TextBlock TextWrapping="Wrap" AutomationProperties.AutomationId="UsageAccount" Text="{x:Bind Detail}" />
src/AgentBridge.App/UsageView.xaml:46:                                    <TextBlock TextWrapping="Wrap" Foreground="{ThemeResource AbDangerText}" Text="{x:Bind Error}" Visibility="{x:Bind local:Ui.Some(Error)}" />
src/AgentBridge.App/UsageView.xaml:68:                                                        TextWrapping="Wrap"
$ rg --sort path -n -F -- '.row-title { min-width: 0; font-size: var(--type-row); line-height: var(--type-row-line); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }' pwa/app.css
607:.row-title { min-width: 0; font-size: var(--type-row); line-height: var(--type-row-line); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
$ rg --sort path -n -F -- 'TextWrapping="NoWrap"' src/AgentBridge.App/SessionRow.xaml
94:            TextWrapping="NoWrap"
124:            TextWrapping="NoWrap"
```

**Do:** Use an adaptive two-line title treatment (new) in narrow lists while keeping status and age aligned. Compare two sessions with identical title prefixes in compact Windows and browser layouts.

**Why:** Both list implementations force a single truncated title. Distinguishing long, similar session names becomes harder exactly where the panes are narrow.

**Effort:** M · **Impact:** M

### ATTR-3. Give distinct connection states distinct visual cues

**Location:** `apple/Apps/SessionWorkbench.swift:265`

**Proof:**

`apple/Apps/SessionWorkbench.swift:265`
```
    private var emptyState: some View {
        ContentUnavailableView {
            Label(store.isConnecting ? "Connecting" : store.isLive ? "No active sessions" : "Connect to Agent Bridge", systemImage: "terminal")
        } description: {
            if store.account == nil, !store.isSample, let status = store.signInStatus {
```

**Verified:**

```console
$ rg --sort path -n -- 'systemImage:.*(antenna|bolt|network)|connectingIcon' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
apple/Apps/SessionActions.swift:88:                        Label(host.status, systemImage: "network.slash")
apple/Apps/SessionWorkbench.swift:101:                            Button("Connection Settings", systemImage: "network") { showingConnection = true }
apple/Apps/SessionWorkbench.swift:300:            Label(store.connectionStatus, systemImage: store.isLive ? "checkmark.circle" : "network")
$ rg --sort path -n -F -- '} description: {' apple/Apps/SessionWorkbench.swift
268:        } description: {
```

**Do:** Use distinct symbols and a restrained activity indicator (new) for connecting, connected-but-empty, and signed-out states. Pair them with the existing text and respect Reduce Motion.

**Why:** All three states use the same terminal glyph; a transient connection attempt looks much like a stable empty workspace.

**Effort:** S · **Impact:** M

### ATTR-4. Make the Windows host log easier to scan without horizontal panning

**Location:** `src/AgentBridge.App/SettingsView.xaml:193`, `src/AgentBridge.App/SettingsView.xaml:198`

**Proof:**

`src/AgentBridge.App/SettingsView.xaml:193`
```
                    AcceptsReturn="True"
                    TextWrapping="NoWrap"
                    MaxHeight="360"
                    FontFamily="{StaticResource MonoFontFamily}"
                    FontSize="12"
```

`src/AgentBridge.App/SettingsView.xaml:198`
```
                    AutomationProperties.AutomationId="HostLog"
                    ScrollViewer.HorizontalScrollBarVisibility="Auto"
                    ScrollViewer.VerticalScrollBarVisibility="Auto"
```

**Verified:**

```console
$ rg --sort path -n -- 'Wrap.*log|wrapLog|LogWrap|WordWrap' src/AgentBridge.App
# (no matches)
$ rg --sort path -n -F -- 'FontSize="12"' src/AgentBridge.App/SettingsView.xaml
197:                    FontSize="12"
$ rg --sort path -n -F -- 'AutomationProperties.AutomationId="HostLog"' src/AgentBridge.App/SettingsView.xaml
198:                    AutomationProperties.AutomationId="HostLog"
```

**Do:** Provide a wrap toggle (new) for the host-log surface. Preserve monospace and the unwrapped mode for structured data; check long error lines at narrow widths and increased text scale.

**Why:** The current log combines small monospace text, no wrapping and a horizontal scrollbar. Long errors extend past the visible pane.

**Effort:** S · **Impact:** M

### ATTR-5. Keep ended rows readable while marking them as history

**Location:** `apple/Apps/SessionWorkbench.swift:67`

**Proof:**

`apple/Apps/SessionWorkbench.swift:67`
```
                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
                    Section("Recently ended") {
                        ForEach(store.endedEntries.filter { store.includesSelectedMachine($0.host) }) { entry in row(entry).opacity(0.6) }
                    }
                }
```

**Verified:**

```console
$ rg --sort path -n -- 'opacity\(0\.6\)|ended.*foregroundStyle' apple/Apps apple/Sources/AgentBridgeAppleCore apple/State
apple/Apps/SessionWorkbench.swift:69:                        ForEach(store.endedEntries.filter { store.includesSelectedMachine($0.host) }) { entry in row(entry).opacity(0.6) }
$ rg --sort path -n -F -- 'Section("Recently ended") {' apple/Apps/SessionWorkbench.swift
68:                    Section("Recently ended") {
```

**Do:** Tone down the ended status and symbol rather than the entire row. Check title legibility in light, dark and increased-contrast settings.

**Why:** Opacity is applied to the entire row, including secondary metadata; every part of a selectable historical item loses prominence together.

**Effort:** S · **Impact:** M

## Dropped during verification

- “The iPad has no reply field or right pane” — MobileSessionWorkspace.swift:19–34 renders both; docs/apple-parity.md:100–105 records a signed physical iPad displaying them. The missing evidence is sending and receiving, retained in REL-1.
- “The browser does not search ended sessions” — pwa/js/ui/views.js:99–100 filters ended items and :178–179 counts them. Web search term matching, not history coverage, survives.
- “Windows offers no way to cancel GitHub authorization” — src/AgentBridge.App/SettingsView.xaml:90–94 has an explicit Cancel control. FUNC-1 is web-specific.
- “Web lacks a keyboard shortcut for search” — pwa/js/ui/shell.js:45–55 already wires keyboard focus, advertises the platform-specific shortcut and sets accessible metadata.
- “Add reduced-motion support to web indicators” — pwa/app.css contains reduced-motion branches for usage, status glyphs, disclosure and answer progress (lines 432, 752, 825, 1044).
- “Offline shell excludes JavaScript modules” — comparing pwa/js/**/*.js against the install list in pwa/sw.js found no omitted module on this head. Runtime cache-write lifetime is a separate issue.
- “All Windows JSON stores lack atomic saves” — src/AgentBridge.App.Core/UsageCache.cs:133–135 already uses a temporary file and replace-on-move. REL-3 only concerns the two directly overwritten paths.
- “Relay forwards pushes to arbitrary origins” — src/AgentBridge.Relay/PushGateway.cs validates the scheme and allowed hosts; RelayApp.cs:68–73 also disables redirects and push request URL logging.
- “Windows local peers bypass user or elevation checks” — src/AgentBridge.Local/PeerPolicy.cs:26–35 refuses unverifiable identity, other users and mismatched elevation.
- “Windows Arm64 packaging is missing” — the historical deferred bullet in docs/app-windows.md:464 conflicts with that same document’s current artifact set and architecture-specific install commands. Not a grounded feature gap.
- “Mac usage polling should read the Keychain automatically” — CopilotCredentials.cs:65–68 documents disruptive prompts; only explicit, recoverable opt-in survives.
- “Unbounded waits in managed tmux” — src/AgentBridge.Host.Delivery/Mac/TmuxCommand.cs:29–36 already enforces a deadline. SEC-5 concerns the outcome of cleanup, not its duration.

## First move

**SEC-1. Pin the GitHub device-authorization destination across clients.** A person pressing the sign-in control is explicitly trying to authorize on GitHub. Today the browser follows the relay-provided URL, Windows will open any absolute HTTPS URL, and Apple permits unrelated paths on github.com. Rejecting noncanonical destinations before a click or clipboard write fixes this at the point where intent is clear. Start with one table of valid and invalid URLs so each platform makes the same decision. Do not wait for live credentials to test it.

## Burn-down — 20261102

[PR #397](https://github.com/danswett/agent-bridge/pull/397) targets the Apple bridge branch in [PR #358](https://github.com/danswett/agent-bridge/pull/358). Integration started at `f0bf103` on `codex/burndown/20261102`. This records local verification and submission for review. No merge, store submission, backend rollout or production deployment is part of this record.

Four worktrees kept concurrent changes separate: `bd-run-1102` (integration and inline changes), `bd-security-1102` (sign-in/relay), `bd-apple-1102` (Apple UI/state), and `bd-web-1102` (Windows/web). The integration owner verified the cited locations after sequential integration. The security agent reviewed non-authentication work, and the Apple agent reviewed the authentication lane. Requested lane models were gpt-6-astra, gpt-6.1-terra and gpt-6.1-sol respectively. The runtime did not independently attest the executing model IDs. Review corrections covered malformed/redirected OAuth data, replay and ownership in tmux cleanup, launch authorization, credential recovery, and network feed paths and redirects.

| Item | Status | Commits | Verified source and post-change evidence |
| --- | --- | --- | --- |
| PERF-1 | Submitted | 0afdd91 | [src/AgentBridge.App.Core/MainViewModel.Search.cs:17](src/AgentBridge.App.Core/MainViewModel.Search.cs#L17) · `foreach (var card in byId.Values)`. Same 2,002-card fleet, 100 queries. 119.35 → 100.36 ms and 114,244,840 → 103,968,040 allocated bytes on the measured run. |
| PERF-2 | Submitted | 6cf26bb | [pwa/js/ui/views.js:78](pwa/js/ui/views.js#L78) · `!this.#active \|\| document.hidden`. Both browser engines exercised pause and wake; PWA unit suite. |
| PERF-3 | Submitted | e4bcf79 | [apple/State/BridgeStore.swift:747](apple/State/BridgeStore.swift#L747) · `let orderMembership = Set(manualOrder)`. Swift tests, formatting and native builds. |
| PERF-4 | Submitted | 4d7f2f5 | [pwa/js/ui/views.js:236](pwa/js/ui/views.js#L236) · `if (el.open) { this.#renderEnded(); }`. Eight ended-history browser checks; closed history and unchanged node identity. |
| PERF-5 | Submitted | f395ed5 | [apple/State/BridgeStore.swift:104](apple/State/BridgeStore.swift#L104) · `func sessionEntry(id:`. Synthetic 300 live/300 ended, 100,000 selections: 290.7 → 20.5 ms. Selection regression test and Apple build. The synthetic run measured 300,000 malloc calls requesting 1,987,200,000 bytes before and zero observed malloc calls after. |
| FUNC-1 | Submitted | ee2c866 | [pwa/js/main.js:385](pwa/js/main.js#L385) · `$('cancel-signin').onclick`. Chromium and WebKit covered cancellation, restart and stale completion. |
| FUNC-2 | Submitted | 8a990b8, 1dec2d7 | [apple/Apps/SessionWorkbench.swift:288](apple/Apps/SessionWorkbench.swift#L288) · `if store.isLive, store.canOpenLauncher`. Host eligibility tested for live, offline, pending and read-only. Native builds. |
| FUNC-3 | Submitted | e2404c1 | [pwa/js/ui/views.js:122](pwa/js/ui/views.js#L122) · `this.terms.every`. Two cross-engine cases for terms split across fields and recently ended sessions. |
| FUNC-4 | Design verified | 01af0bf | [apple/scripts/build-ios-push-development.sh:12](apple/scripts/build-ios-push-development.sh#L12) · `codesign --verify --strict`. Design packet. Real signed sandbox build; both entitlements verified. APNs provider and background delivery remain a future implementation. |
| FUNC-5 | Submitted | bccde14 | [src/AgentBridge.Host.Core/Usage/CopilotCredentials.cs:118](src/AgentBridge.Host.Core/Usage/CopilotCredentials.cs#L118) · `keychainResults[account] = null;`. 43 usage tests, including denial and rotation with a fake CLI. Keychain remains explicitly opt-in. |
| STAB-1 | Submitted | 1be73ce | [pwa/js/signin.js:26](pwa/js/signin.js#L26) · `!seconds(s.interval, 60)`. 14 .NET sign-in cases and JS adversarial grants. Bounds also apply to updated intervals. |
| STAB-2 | Submitted | 4b00f28 | [pwa/js/signin.js:16](pwa/js/signin.js#L16) · `const validAccount`. Web account parsing and incomplete/invalid 200 responses covered in unit suite. |
| STAB-3 | Submitted | 7e3c30a, 7b5106b | [src/AgentBridge.Relay/RelayApp.cs:173](src/AgentBridge.Relay/RelayApp.cs#L173) · `code.Length > 200`. Tests for malformed data, invalid device URL, oversized code, CRLF credential and unknown upstream error. |
| STAB-4 | Submitted | a567026 | [apple/State/BridgeStore.swift:758](apple/State/BridgeStore.swift#L758) · `let kept = Set(manualOrder)`. 300-new-session rollover regression proves pins survive while read/order history stays bounded. |
| STAB-5 | Submitted | c51022c | [src/AgentBridge.App.Core/MainViewModel.Search.cs:12](src/AgentBridge.App.Core/MainViewModel.Search.cs#L12) · `.Split((char[]?)null`. Windows search tests for tabs, newlines, blanks and normalized prefix. |
| REL-1 | Physical acceptance | 3308be5, 7103f7b | [docs/apple-native-interop-acceptance.md:100](docs/apple-native-interop-acceptance.md#L100) · `## Physical reply and receipt recovery`. Physical iPad to Mac host e153a80: exact ACK user turn once and Delivered on the iPad. Discarding a second real receipt at the client and checking again after reconnect also yielded Delivered and one Mac user turn. |
| REL-2 | Submitted | 42c7b1a | [pwa/sw.js:49](pwa/sw.js#L49) · `e.waitUntil(response.then`. Chromium worker termination and offline reload; quota/write rejection unit tests. |
| REL-3 | Submitted | 94d8ed4 | [src/AgentBridge.App.Core/Stores.cs:143](src/AgentBridge.App.Core/Stores.cs#L143) · `File.Move(temporary, path, overwrite: true)`. Injected mid-write failure preserves the old bytes and removes the partial temporary file. |
| REL-4 | Submitted | 9774372 | [src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs:25](src/AgentBridge.HomeAssistant/HaConnector.Upkeep.cs#L25) · `count = ++failedUpkeepReads`. 57 disposable upkeep tests, including rate-limited failures and restored session state. |
| REL-5 | Submitted | 4b56d98 | [src/AgentBridge.Hooks/HookRunner.cs:228](src/AgentBridge.Hooks/HookRunner.cs#L228) · `event input read failed`. 26 hook runner tests, including partial read followed by IOException and fail-open reply. |
| SEC-1 | Submitted | 74def11 | [src/AgentBridge.Transport/SignIn.cs:12](src/AgentBridge.Transport/SignIn.cs#L12) · `public static bool IsDevicePage`. Shared 17-case fixture exercises the raw destination across three client families before clipboard or navigation. |
| SEC-2 | Submitted | 781c91b, 7b5106b | [src/AgentBridge.Relay/RelayApp.cs:231](src/AgentBridge.Relay/RelayApp.cs#L231) · `finally { await RevokeAsync`. Fake upstream lookup failure still revokes; 307 redirect sink receives no credential. |
| SEC-3 | Design verified | 5b84cec, 5f7bf4e | [docs/protocol/browser-session-renewal.md:16](docs/protocol/browser-session-renewal.md#L16) · `Coordinate refresh independently`. Design packet. Chromium and WebKit reload the non-extractable device identity and reconnect to a separate origin with cookies cleared. Proposed rotation endpoints are unshipped. |
| SEC-4 | Submitted | a1921d5, ce059c3 | [src/AgentBridge.App.Core/UpdateFeedPolicy.cs:9](src/AgentBridge.App.Core/UpdateFeedPolicy.cs#L9) · `public static Uri? WebUri`. Custom update feeds, loopback-only development HTTP, remote file/redirect restrictions and tests. |
| SEC-5 | Submitted | 630c69e, 5fa216f | [src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:331](src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs#L331) · `const string mismatch`. 35 managed-tmux tests and a private disposable tmux server verified the server PID condition and buffer absence. |
| USE-1 | Submitted | 6559266, ab244cc | [apple/State/BridgeStore.swift:246](apple/State/BridgeStore.swift#L246) · `connectionStage = .credentialStorage`. Credential-read failures recover without issuing another device grant; focused state tests, Apple builds. |
| USE-2 | Submitted | a0581f6 | [pwa/js/main.js:372](pwa/js/main.js#L372) · `await navigator.clipboard.writeText(code.textContent)`. Chromium/WebKit covered successful copy, clipboard refusal, fallback selection and popup target. |
| USE-3 | Submitted | 2762439 | [src/AgentBridge.App.Core/SettingsViewModel.cs:106](src/AgentBridge.App.Core/SettingsViewModel.cs#L106) · `Clipboard and launcher can fail independently`. All four clipboard/open combinations tested. WinUI runtime is covered separately by Windows CI. |
| USE-4 | Submitted | 240ac49 | [apple/Apps/ConnectionView.swift:40](apple/Apps/ConnectionView.swift#L40) · `Review Local Profile`. Typed timeout opens profile review and retry. Swift checks and native builds; interactive timeout recovery not independently exercised. |
| USE-5 | Submitted | b3d9146 | [pwa/js/ui/sessions-page.js:597](pwa/js/ui/sessions-page.js#L597) · `const clear = h('button'`. Four browser checks including inline reset and restored focus. |
| ATTR-1 | Submitted | f8f676f | [apple/Apps/SessionDetail.swift:70](apple/Apps/SessionDetail.swift#L70) · `.frame(maxWidth: 680`. Fixture capture on iPad simulator showed the anchored composer and independent conversation/inspector panes. |
| ATTR-2 | Submitted | efc9ca9, b8f0e94, 8c8a835 | [src/AgentBridge.App/SessionRow.xaml.cs:50](src/AgentBridge.App/SessionRow.xaml.cs#L50) · `SessionTitle.MaxLines = compact ? 2 : 1`. Compact Chromium/WebKit layout comparisons and updated first-line Windows self-test. |
| ATTR-3 | Submitted | 468a2e6 | [apple/Apps/SessionWorkbench.swift:269](apple/Apps/SessionWorkbench.swift#L269) · `if store.isConnecting, !reduceMotion`. Apple tests and both native builds. Connecting, empty and signed-out states now have different cues. |
| ATTR-4 | Submitted | 3c19f1c, c31cd10 | [src/AgentBridge.App/SettingsView.xaml.cs:46](src/AgentBridge.App/SettingsView.xaml.cs#L46) · `log.TextWrapping = wrap ?`. XAML parsed and 33 Settings tests passed in worker. Final Windows UI result depends on CI. |
| ATTR-5 | Submitted | cb243fa | [apple/Apps/SessionRow.swift:11](apple/Apps/SessionRow.swift#L11) · `.foregroundStyle(ended ?`. Apple builds and simulator appearance inspection.  |

### Verification and limits

- Apple wave: SwiftFormat, core/crypto/client tests (241 total) and native macOS/iOS builds. Physical iPad testing uses the signed development app and the Mac host rather than sample data.
- Web wave: 371 combined unit tests passed. Both Chromium and WebKit ran. The isolated final web wave had 751 passed and 29 skipped, with two WebKit failures also observed at baseline: radio focus after answering and a phone sort target measuring 21 px. Corrected first-line glyph alignment passed the focused checks.
- Host and Windows wave: full .NET run on macOS recorded 2,825 passes, 39 skips and two migration timeouts. A focused migration rerun passed all three cases. Windows desktop execution remains a hosted CI check. Focused host checks exercised interrupted hook reads, report redaction, tmux identity/cleanup, Keychain opt-in and Windows feed rejection.
- SEC-3 is a design and browser feasibility result. FUNC-4 is a design with verified sandbox signing. Neither enables the proposed production service.
