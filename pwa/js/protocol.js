// Bridge Protocol v1, sessions slice: docs/protocol/v1-sessions.md. Mirrors
// src/AgentBridge.Protocol/Sessions.cs, including its tolerant reading of enums.
//
// Everything a host sends is read through here before anything shows it, so the views
// can rely on types: a newer host may add fields and enum values, and a broken or hostile
// one may send a number where a string belongs. Unknown fields are dropped; unknown enum
// values read as the C# reader's [UnknownAs] value.

export const PROTOCOL_VERSION = 1;

export const Ops = Object.freeze({
  hello: 'hello',
  sessionsList: 'sessions.list',
  sessionGet: 'session.get',
  sessionsSubscribe: 'sessions.subscribe',
  sessionAnswer: 'session.answer',
  sessionReply: 'session.reply',
  sessionStop: 'session.stop',
  pushSubscribe: 'push.subscribe',
  pushUnsubscribe: 'push.unsubscribe',
  pushList: 'push.list',
  attachmentStart: 'attachment.start',
  attachmentChunk: 'attachment.chunk',
  attachmentDiscard: 'attachment.discard',
});

export const Events = Object.freeze({
  sessionUpsert: 'session.upsert',
  sessionPatch: 'session.patch',
  sessionRemove: 'session.remove',
  sessionsSynced: 'sessions.synced',
  discoveryChanged: 'discovery.changed',
});

export const Capabilities = Object.freeze({ sessionsRead: 'sessions.read', sessionsPatch: 'sessions.patch', sessionsAnswer: 'sessions.answer', sessionsReply: 'sessions.reply', sessionsAttach: 'sessions.attach', sessionsAttachBlob: 'sessions.attach.blob', sessionsStop: 'sessions.stop', sessionsApprove: 'sessions.approve', push: 'push' });

export const Outcomes = Object.freeze(['delivered', 'unconfirmed', 'stale', 'busy', 'refused']);

const tolerant = (known, fallback) => (v) => (known.includes(v) ? v : fallback);
export const readAgent = tolerant(['copilot', 'claude', 'codex'], 'unknown');
// ending: the host is ending the session (session.stop).
export const readStatus = tolerant(['working', 'waiting', 'idle', 'ending'], 'idle');
export const readToolStatus = tolerant(['running', 'done', 'failed'], 'done');
export const readQuestionKind = tolerant(['form', 'approval'], 'form');
export const readFieldType = tolerant(['choice', 'multi', 'boolean', 'text', 'number'], 'text');
// Who drives the session's turn. Absent, and anything unknown, is the person: a wrong purple
// edge is worse than none.
export const readDriver = tolerant(['person', 'agent'], 'person');
// Who sent activity.userMessage (#489). Absent (or null) is the person, as from a host before the
// field; any other text this client doesn't know is still a sender the host says isn't the
// person, so it is never drawn as theirs. A value that isn't text at all is refused with the whole
// activity (readActivity): strict under v1-sessions.md, as the host's reader has it.
export const readUserMessageFrom = (v) => (v === undefined || v === null ? 'person' : tolerant(['person', 'schedule', 'agent', 'other'], 'other')(v));
// An outcome from a newer host is unconfirmed: never delivered, and never a reason to
// answer again.
export const readOutcome = tolerant(Outcomes, 'unconfirmed');
// A sub-agent (Subagent in the spec): its kind, status and how its latest run went.
export const readSubagentKind = tolerant(['background', 'task'], 'task');
export const readSubagentStatus = tolerant(['working', 'idle', 'waiting', 'ended'], 'idle');
export const readSubagentOutcome = tolerant(['done', 'failed'], 'done');

export const StopOutcomes = Object.freeze(['ended', 'confirm', 'stale', 'busy', 'refused', 'failed']);
// A stop outcome from a newer host is failed: never ended, and never confirm, which would
// invite a press that confirms nothing.
export const readStopOutcome = tolerant(StopOutcomes, 'failed');

// Absent and null mean the same thing, so anything of the wrong type is treated as absent.
const str = (v) => (typeof v === 'string' ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);

export function readTool(raw) {
  const t = obj(raw);
  if (!t) { return null; }
  return {
    id: str(t.id) ?? '',
    name: str(t.name) ?? 'tool',
    summary: str(t.summary),
    status: readToolStatus(t.status),
    startedAt: str(t.startedAt),
    endedAt: str(t.endedAt),
  };
}

function readOption(raw) {
  const o = obj(raw);
  if (!o) { return null; }
  // Values are strings in every agent seen so far; a number is shown as written.
  const value = typeof o.value === 'number' ? String(o.value) : str(o.value);
  if (value === null) { return null; }
  return { value, label: str(o.label) ?? value };
}

function readField(raw) {
  const f = obj(raw);
  if (!f || str(f.name) === null) { return null; }
  return {
    name: f.name,
    title: str(f.title),
    description: str(f.description),
    type: readFieldType(f.type),
    options: Array.isArray(f.options) ? f.options.map(readOption).filter(Boolean) : [],
    allowOther: f.allowOther === true,
    // Any JSON value. Views format it as text and never interpret it as markup.
    default: f.default === undefined ? null : f.default,
    required: f.required === true,
  };
}

export function readQuestion(raw) {
  const q = obj(raw);
  if (!q) { return null; }
  return {
    id: str(q.id) ?? '',
    kind: readQuestionKind(q.kind),
    message: str(q.message) ?? '',
    fields: Array.isArray(q.fields) ? q.fields.map(readField).filter(Boolean) : [],
    askedAt: str(q.askedAt),
    truncated: q.truncated === true,
    answerAtMachine: q.answerAtMachine === true,
    // The sub-agent that asked, when it is not the session's own question.
    agent: str(q.agent),
  };
}

// One sub-agent the session started: who it is, where it sits, whether it runs, and its latest
// line. response, reasoning and history come only from session.get { id, agent }; a list's
// entry reads them as absent.
export function readSubagent(raw) {
  const a = obj(raw);
  if (!a || !str(a.id)) { return null; }
  const status = readSubagentStatus(a.status);
  return {
    id: a.id,
    parent: str(a.parent),
    kind: readSubagentKind(a.kind),
    name: str(a.name),
    task: str(a.task),
    model: str(a.model),
    status,
    outcome: status === 'idle' || status === 'ended' ? (a.outcome === undefined || a.outcome === null ? null : readSubagentOutcome(a.outcome)) : null,
    startedAt: str(a.startedAt),
    endedAt: status === 'idle' || status === 'ended' ? str(a.endedAt) : null,
    line: str(a.line),
    currentTool: status === 'working' || status === 'waiting' ? readTool(a.currentTool) : null,
    shells: Number.isSafeInteger(a.shells) && a.shells > 0 ? a.shells : 0,
    response: str(a.response),
    responseTruncated: a.responseTruncated === true,
    reasoning: str(a.reasoning),
    history: Array.isArray(a.history) ? a.history.map(readTool).filter(Boolean) : null,
  };
}

// activity.source: which sub-agent wrote the top-level response and reasoning, each on its
// own, by id. A field left out is the main agent's; null when both are.
export function readSource(raw) {
  const s = obj(raw);
  if (!s) { return null; }
  const response = str(s.response) || null;
  const reasoning = str(s.reasoning) || null;
  return response || reasoning ? { response, reasoning } : null;
}

// activity.own: the main agent's own text for a field whose top-level text is not it. null
// when it says nothing.
export function readOwn(raw) {
  const o = obj(raw);
  if (!o) { return null; }
  const own = { response: str(o.response), responseTruncated: o.responseTruncated === true, reasoning: str(o.reasoning), at: str(o.at) };
  return own.response !== null || own.responseTruncated || own.reasoning !== null ? own : null;
}

// Work still running after the session's own turn: { agents, shells }, or null when there
// is none. A count that is not a whole number above zero reads as none, and both at none
// is the same as the field being absent, so a client never shows "waiting for 0".
export function readBackground(raw) {
  const b = obj(raw);
  if (!b) { return null; }
  const count = (v) => (Number.isSafeInteger(v) && v > 0 ? v : 0);
  const agents = count(b.agents);
  const shells = count(b.shells);
  return agents || shells ? { agents, shells } : null;
}

// A Session's activity as the views use it, or null when the host's reader would refuse it: a
// userMessageFrom that isn't text (v1-sessions.md, strict fields). Read as absent, a prompt a
// schedule or an agent sent would be drawn as the person's own words; the session keeps an empty
// activity instead (rule 4), so no prompt is shown at all.
export function readActivity(raw) {
  const a = obj(raw) ?? {};
  if (a.userMessageFrom !== undefined && a.userMessageFrom !== null && typeof a.userMessageFrom !== 'string') { return null; }
  return {
    response: str(a.response),
    responseTruncated: a.responseTruncated === true,
    reasoning: str(a.reasoning),
    currentTool: readTool(a.currentTool),
    history: Array.isArray(a.history) ? a.history.map(readTool).filter(Boolean) : null,
    userMessage: str(a.userMessage),
    // Who sent it (person, schedule, agent or other), and a short label of the host's, such as
    // a schedule's number: an agent's text, shown only through prompt-origin.js (#489).
    userMessageFrom: readUserMessageFrom(a.userMessageFrom),
    userMessageLabel: str(a.userMessageLabel),
    turnCount: num(a.turnCount),
    compactions: num(a.compactions),
    background: readBackground(a.background),
    // Whose the top-level response and reasoning are, and the main agent's own beside them
    // (attribution.js says which to show). A host before sub-agents sends neither.
    source: readSource(a.source),
    own: readOwn(a.own),
  };
}

// A Session as the views use it, or null if it has no id to file it under.
export function readSession(raw) {
  const s = obj(raw);
  if (!s || !str(s.id)) { return null; }
  return {
    id: s.id,
    agent: readAgent(s.agent),
    title: str(s.title),
    cwd: str(s.cwd),
    repository: str(s.repository),
    branch: str(s.branch),
    model: str(s.model),
    status: readStatus(s.status),
    statusSince: str(s.statusSince),
    startedAt: str(s.startedAt),
    updatedAt: str(s.updatedAt) ?? '',
    processId: num(s.processId),
    driver: readDriver(s.driver),
    // Which agent, when the host knows; only ever with an agent driving.
    driverPrincipal: readDriver(s.driver) === 'agent' ? str(s.driverPrincipal) : null,
    activity: readActivity(s.activity) ?? readActivity({}),
    question: readQuestion(s.question),
    // Absent (null) from a host that lists none, or can't: Claude and Codex so far.
    subagents: Array.isArray(s.subagents) ? s.subagents.map(readSubagent).filter(Boolean) : null,
    subagentsMore: Number.isSafeInteger(s.subagentsMore) && s.subagentsMore > 0 ? s.subagentsMore : 0,
    waitingOn: str(s.waitingOn),
  };
}

// What holds the host's discovery ({ reason, agents }), from sessions.synced or
// discovery.changed; null when nothing does, or when what came is unreadable.
export function readDiscoveryHeld(raw) {
  const h = obj(raw);
  const reason = str(h?.reason);
  if (!reason) { return null; }
  return { reason, agents: Array.isArray(h.agents) ? h.agents.filter((a) => typeof a === 'string').map(readAgent) : [] };
}

export function readHello(raw) {
  const h = obj(raw) ?? {};
  const m = obj(h.machine) ?? {};
  return {
    protocol: num(h.protocol),
    // readOnly: why the machine takes nothing from the phone now (agent-ha-bridge runs there).
    // instance: this run of the host, which a reconnect resumes the list of (sessions.subscribe's since).
    machine: { id: str(m.id), name: str(m.name), os: str(m.os), hostVersion: str(m.hostVersion), dev: m.dev === true, sessions: Number.isSafeInteger(m.sessions) && m.sessions >= 0 ? m.sessions : null, readOnly: str(m.readOnly) || null, instance: str(m.instance) },
    capabilities: Array.isArray(h.capabilities) ? h.capabilities.filter((c) => typeof c === 'string') : [],
  };
}

// session.answer's and session.reply's result: { outcome, reason }. Anything unreadable is
// unconfirmed, since the host may have typed it.
export function readAnswerResult(raw) {
  const r = obj(raw) ?? {};
  return { outcome: readOutcome(r.outcome), reason: str(r.reason) };
}

// attachment.start's and attachment.chunk's result: { received, complete }. A count that is
// not a whole number reads as nothing received, so a client starts again from the top.
export function readAttachmentState(raw) {
  const r = obj(raw) ?? {};
  return { received: Number.isSafeInteger(r.received) && r.received >= 0 ? r.received : 0, complete: r.complete === true };
}

// session.stop's result: { outcome, reason, confirm, confirmSeconds, forced }. confirm is the
// token a second press sends, and comes only with the confirm outcome; a confirm without a
// usable one reads as failed, since no press could confirm it.
export function readStopResult(raw) {
  const r = obj(raw) ?? {};
  let outcome = readStopOutcome(r.outcome);
  const confirm = outcome === 'confirm' && typeof r.confirm === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(r.confirm) ? r.confirm : null;
  if (outcome === 'confirm' && !confirm) { outcome = 'failed'; }
  const seconds = num(r.confirmSeconds);
  return {
    outcome,
    reason: str(r.reason),
    confirm,
    confirmSeconds: confirm ? (seconds !== null && seconds > 0 && seconds <= 600 ? seconds : 10) : null,
    forced: r.forced === true,
  };
}
