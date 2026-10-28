// Demo mode (?demo=1): the app with no relay and no machine. A host is simulated in the
// page, speaking the real protocol to the real HostClient, so everything from event
// dispatch to the cards is the code a live host drives. Its sessions come from the
// protocol's worked example (fixtures/sessions-list.json, a copy of
// docs/protocol/examples/sessions-list.json).
//
// window.agentBridgeDemo (demo mode only) lets tests and curious people drive it:
//   upsert(session), remove(id), frame(rawFrame), calls (ops received), pause(),
//   script(...outcomes) for the next answers
//
// It answers questions as a host would: the same validation (answers.js mirrors the C#
// Answers.Check), stale when the session is no longer asking, a request seen before gets
// its first outcome again, and an answer that is typed takes the question away with an
// upsert. Outcomes are delivered unless a test or curious person scripts others.
import { checkAnswer } from './answers.js';
import { readQuestion } from './protocol.js';

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

  // Each session.answer request: its outcome, by request UUID, as a host keeps them.
  answers = new Map();
  // Every session.answer request as received, resends included.
  answerLog = [];
  // What was typed into a session, once per answer: what a resend must not repeat.
  typed = [];
  #script = [];
  onTyped = null;

  // answering: false plays a host that can't answer (agent-ha-bridge still running, say).
  constructor(fixture, { machineName = 'Demo machine', answerDelayMs = 700, answering = true } = {}) {
    this.machine = { id: 'demo', name: machineName, os: 'windows', hostVersion: 'demo' };
    this.answerDelayMs = answerDelayMs;
    this.capabilities = answering ? ['sessions.read', 'sessions.answer'] : ['sessions.read'];
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
        return reply({ protocol: 1, machine: this.machine, capabilities: this.capabilities });
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
      case 'session.answer':
        return this.#answer(data, reply);
      default:
        return t.deliver({ re: id, ok: false, error: `unknown op ${op}` });
    }
  }

  scriptAnswers(outcomes) { this.#script.push(...outcomes); }

  #answer(data, reply) {
    this.answerLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    // A repeat, even of one still being delivered, gets the first one's outcome.
    const known = this.answers.get(request);
    if (known) { known.result.then(reply); return; }
    const step = this.#script.shift() ?? 'delivered';
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = new Promise((resolve) => setTimeout(() => resolve(this.#decide(data, scripted)), this.answerDelayMs));
    this.answers.set(request, { result });
    // drop: the host did the work, but its response never reached the phone.
    result.then((r) => { if (!scripted.drop) { reply(r); } });
  }

  #decide(data, scripted) {
    const s = this.sessions.get(data?.id);
    const q = s?.question ? readQuestion(s.question) : null;
    if (!q || q.id !== data.question) { return { outcome: 'stale', reason: 'the session is no longer asking that question' }; }
    const problem = checkAnswer(q, data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    const { outcome } = scripted;
    if (outcome === 'stale') { return { outcome, reason: scripted.reason ?? 'answered at the machine' }; }
    if (outcome === 'busy') { return { outcome, reason: scripted.reason ?? 'another answer to this session is being delivered' }; }
    if (outcome === 'refused') { return { outcome, reason: scripted.reason ?? 'the session is running elevated' }; }
    // Typed: the question goes, and the session works on the answer.
    const said = data.text ?? Object.entries(data.fields ?? {}).map(([k, v]) => `${k}: ${JSON.stringify(v)}`).join('; ');
    this.typed.push({ id: s.id, question: q.id, request: data.request, said });
    this.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), question: null, activity: { ...s.activity, userMessage: said } });
    this.onTyped?.(s.id);
    if (outcome === 'delivered') { return { outcome }; }
    return { outcome, reason: scripted.reason ?? (outcome === 'unconfirmed' ? 'the session stopped answering' : 'from a newer host') };
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
// a new session appears and goes, the delegating one's background agents come and go, and
// the idle one gets a follow-up.
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
      // One of the delegating session's background agents finishes, then another starts.
      const s = host.get('8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35');
      if (!s) { return; }
      const agents = s.activity?.background?.agents === 2 ? 1 : 2;
      host.upsert({ ...s, updatedAt: now(), activity: { ...s.activity, background: { agents, shells: 1 } } });
    },
    () => {
      const s = host.get('019a1b2c-codex-example');
      if (!s) { return; }
      host.upsert({ ...s, status: 'idle', statusSince: now(), updatedAt: now(), activity: { ...s.activity, response: 'Done. 20 of 20 runs passed.' } });
    },
  ];
  // An answered question gets a follow-up a little later, so the demo always has one to
  // answer: each kind of question in turn.
  let asked = 0;
  host.onTyped = (id) => setTimeout(() => {
    const s = host.get(id);
    if (!s || paused) { return; }
    const question = { ...DEMO_QUESTIONS[asked++ % DEMO_QUESTIONS.length], id: `toolu_demo_${Date.now()}`, askedAt: now() };
    host.upsert({ ...s, status: 'waiting', statusSince: now(), updatedAt: now(), question });
  }, tickMs * 1.5);
  let k = 0, timer = null, paused = false;
  const tick = () => {
    if (!paused) { steps[k++ % steps.length](); }
    timer = setTimeout(tick, tickMs);
  };
  timer = setTimeout(tick, tickMs);
  return { pause() { paused = true; }, resume() { paused = false; }, stop() { clearTimeout(timer); } };
}

// What the demo asks after an answer: an approval, a single choice answered on a tap, and
// a question that wants only text.
const DEMO_QUESTIONS = [
  {
    kind: 'approval',
    message: 'Run `git push --force-with-lease origin p1/answer-pwa`?',
    fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'always', label: 'Yes, and don\'t ask again for git push' }, { value: 'no', label: 'No, and tell the agent what to do instead' }] }],
  },
  {
    kind: 'form',
    message: 'The relay tests pass. **Open the pull request now?**',
    fields: [{ name: 'answer', type: 'choice', required: true, allowOther: true, options: [{ value: 'later', label: 'Wait for the E2E run' }, { value: 'open', label: 'Open it now (Recommended)' }] }],
  },
  {
    kind: 'form',
    message: 'What should the release notes lead with?',
    fields: [],
  },
];
