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
  pushSubscribe: 'push.subscribe',
  pushUnsubscribe: 'push.unsubscribe',
  pushList: 'push.list',
  attachmentStart: 'attachment.start',
  attachmentChunk: 'attachment.chunk',
  attachmentDiscard: 'attachment.discard',
});

export const Events = Object.freeze({
  sessionUpsert: 'session.upsert',
  sessionRemove: 'session.remove',
  sessionsSynced: 'sessions.synced',
});

export const Capabilities = Object.freeze({ sessionsRead: 'sessions.read', sessionsAnswer: 'sessions.answer', sessionsReply: 'sessions.reply', sessionsAttach: 'sessions.attach', push: 'push' });

export const Outcomes = Object.freeze(['delivered', 'unconfirmed', 'stale', 'busy', 'refused']);

const tolerant = (known, fallback) => (v) => (known.includes(v) ? v : fallback);
export const readAgent = tolerant(['copilot', 'claude', 'codex'], 'unknown');
export const readStatus = tolerant(['working', 'waiting', 'idle'], 'idle');
export const readToolStatus = tolerant(['running', 'done', 'failed'], 'done');
export const readQuestionKind = tolerant(['form', 'approval'], 'form');
export const readFieldType = tolerant(['choice', 'multi', 'boolean', 'text', 'number'], 'text');
// An outcome from a newer host is unconfirmed: never delivered, and never a reason to
// answer again.
export const readOutcome = tolerant(Outcomes, 'unconfirmed');

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
  };
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

// A Session as the views use it, or null if it has no id to file it under.
export function readSession(raw) {
  const s = obj(raw);
  if (!s || !str(s.id)) { return null; }
  const a = obj(s.activity) ?? {};
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
    activity: {
      response: str(a.response),
      responseTruncated: a.responseTruncated === true,
      reasoning: str(a.reasoning),
      currentTool: readTool(a.currentTool),
      history: Array.isArray(a.history) ? a.history.map(readTool).filter(Boolean) : null,
      userMessage: str(a.userMessage),
      turnCount: num(a.turnCount),
      compactions: num(a.compactions),
      background: readBackground(a.background),
    },
    question: readQuestion(s.question),
  };
}

export function readHello(raw) {
  const h = obj(raw) ?? {};
  const m = obj(h.machine) ?? {};
  return {
    protocol: num(h.protocol),
    machine: { id: str(m.id), name: str(m.name), os: str(m.os), hostVersion: str(m.hostVersion) },
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
