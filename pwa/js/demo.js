// Demo mode (?demo=1): the app with no relay and no machine. A host is simulated in the
// page, speaking the real protocol to the real HostClient, so everything from event
// dispatch to the cards is the code a live host drives. Its sessions come from the
// protocol's worked example (fixtures/sessions-list.json, a copy of
// docs/protocol/examples/sessions-list.json).
//
// window.agentBridgeDemo (demo mode only) lets tests and curious people drive it:
//   upsert(session), remove(id), frame(rawFrame), calls (ops received), pause()

const clone = (v) => JSON.parse(JSON.stringify(v));
const now = () => new Date().toISOString();

// The transport HostClient expects, connected straight to a DemoHost.
class DemoTransport {
  onmessage = null;
  onclose = null;
  constructor(host) { this.host = host; }
  send(message) {
    // Asynchronous, like a real channel, so ordering bugs show up here too.
    setTimeout(() => this.host.receive(this, message), 0);
    return Promise.resolve();
  }
  deliver(message) { setTimeout(() => this.onmessage?.(clone(message)), 0); }
  close() { this.host.transports.delete(this); }
}

export class DemoHost {
  transports = new Set();
  calls = [];
  seq = 0;
  revision = 0;

  constructor(fixture, { machineName = 'Demo machine' } = {}) {
    this.machine = { id: 'demo', name: machineName, os: 'windows', hostVersion: 'demo' };
    this.sessions = new Map();
    for (const s of fixture.sessions ?? []) { this.sessions.set(s.id, s); }
    this.revision = fixture.revision ?? 1;
  }

  connect() {
    const t = new DemoTransport(this);
    this.transports.add(t);
    return t;
  }

  receive(t, { id, op, data }) {
    this.calls.push(op);
    const reply = (result) => t.deliver({ re: id, ok: true, result });
    switch (op) {
      case 'hello':
        return reply({ protocol: 1, machine: this.machine, capabilities: ['sessions.read'] });
      case 'sessions.list':
        return reply({ sessions: [...this.sessions.values()].map(withoutHistory), revision: this.revision });
      case 'session.get': {
        const s = this.sessions.get(data?.id);
        return reply(s ? withHistory(s) : { missing: true });
      }
      case 'sessions.subscribe': {
        t.subscribed = true;
        reply({ revision: this.revision, count: this.sessions.size });
        for (const s of this.sessions.values()) { this.#event(t, 'session.upsert', withoutHistory(s)); }
        this.#event(t, 'sessions.synced', { revision: this.revision });
        return undefined;
      }
      default:
        return t.deliver({ re: id, ok: false, error: `unknown op ${op}` });
    }
  }

  #event(t, ev, data) { t.deliver({ ev, seq: ++this.seq, data }); }

  #broadcast(ev, data) {
    for (const t of this.transports) { if (t.subscribed) { this.#event(t, ev, data); } }
  }

  upsert(session) {
    const s = { ...session, updatedAt: session.updatedAt ?? now() };
    this.sessions.set(s.id, s);
    this.revision++;
    this.#broadcast('session.upsert', withoutHistory(s));
  }

  remove(id) {
    this.sessions.delete(id);
    this.revision++;
    this.#broadcast('session.remove', { id });
  }

  // A frame exactly as given, for testing what the client does with odd ones. A frame
  // with a numeric seq moves this host's counter on to it.
  frame(f) {
    if (Number.isInteger(f.seq)) { this.seq = f.seq; }
    for (const t of this.transports) { t.deliver(f); }
  }

  get(id) { return this.sessions.get(id); }
}

function withoutHistory(s) {
  const c = clone(s);
  if (c.activity) { delete c.activity.history; }
  return c;
}

// Tool history for session.get, made up from the session so each card has some.
function withHistory(s) {
  const c = clone(s);
  c.activity ??= {};
  if (!Array.isArray(c.activity.history)) {
    const base = Date.parse(s.updatedAt) || Date.now();
    const at = (k) => new Date(base - k * 47000).toISOString();
    const tools = [['view', 'docs/protocol/v1-sessions.md'], ['powershell', 'Run the unit tests'], ['edit', 'pwa/js/host-client.js'], ['grep', 'session.upsert'], ['powershell', 'npx playwright test']];
    c.activity.history = tools.map(([name, summary], k) => ({
      id: `${s.id}-t${k}`, name, summary, status: k === 1 ? 'failed' : 'done', startedAt: at(tools.length - k), endedAt: at(tools.length - k - 0.5),
    }));
    if (c.activity.currentTool) { c.activity.history.push(c.activity.currentTool); }
  }
  return c;
}

// A few changes on a loop, so the demo looks alive: the working session's tool moves on,
// a new session appears and goes, and the idle one gets a follow-up.
export function runScript(host, { tickMs = 4000 } = {}) {
  const steps = [
    () => {
      const s = host.get('6f1c2b7e-claude-example');
      if (!s) { return; }
      host.upsert({ ...s, updatedAt: now(), activity: { ...s.activity, currentTool: { id: `toolu_${Date.now()}`, name: 'Bash', summary: 'dotnet test --filter Relay', status: 'running', startedAt: now() } } });
    },
    () => host.upsert({
      id: 'demo-new-session', agent: 'copilot', title: 'Draft the release notes', repository: 'danswett/agent-bridge', branch: 'docs/release-notes',
      model: 'gpt-5.6-sol', status: 'working', statusSince: now(), startedAt: now(), updatedAt: now(),
      activity: { response: 'Reading the merged PRs since **v0.1.0**…', currentTool: { id: 'toolu_demo', name: 'gh', summary: 'gh pr list --state merged', status: 'running', startedAt: now() }, turnCount: 1 },
    }),
    () => {
      const s = host.get('019a1b2c-codex-example');
      if (!s) { return; }
      host.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), activity: { ...s.activity, response: 'Re-running the suite 20 times to be sure the fix holds.' } });
    },
    () => host.remove('demo-new-session'),
    () => {
      const s = host.get('019a1b2c-codex-example');
      if (!s) { return; }
      host.upsert({ ...s, status: 'idle', statusSince: now(), updatedAt: now(), activity: { ...s.activity, response: 'Done. 20 of 20 runs passed.' } });
    },
  ];
  let k = 0, timer = null, paused = false;
  const tick = () => {
    if (!paused) { steps[k++ % steps.length](); }
    timer = setTimeout(tick, tickMs);
  };
  timer = setTimeout(tick, tickMs);
  return { pause() { paused = true; }, resume() { paused = false; }, stop() { clearTimeout(timer); } };
}
