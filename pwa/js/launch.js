// Launching and resuming sessions: docs/protocol/v1-launch.md. Mirrors
// src/AgentBridge.Protocol/Launch.cs: the op names, the tolerant reading of launch.options and
// of a launch's result, and checkLaunch, which is LaunchProtocol.Check case for case, with its
// wording, so a launch the machine would refuse is never sent.
import { readAgent } from './protocol.js';

export const LAUNCH = Object.freeze({
  capability: 'sessions.launch',
  options: 'launch.options',
  launch: 'session.launch',
});

export const LAUNCHERS = Object.freeze(['agency', 'copilot', 'claude', 'codex']);
export const ASK = 'ask';
export const ALLOW_ALL = 'allowAll';
export const MAX_PROMPT = 16 * 1024;
const MAX_NAME = 256;
const MAX_ATTACHMENTS = 10;
const TUNING = /^[A-Za-z0-9][A-Za-z0-9._:[\]-]{0,127}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const LaunchOutcomes = Object.freeze(['started', 'unconfirmed', 'busy', 'refused', 'failed']);
// Something may have started: an outcome from a newer host is never read as refused.
export const readLaunchOutcome = (v) => (LaunchOutcomes.includes(v) ? v : 'unconfirmed');

const utf8 = (s) => new TextEncoder().encode(s).length;
const cut = (s) => { let out = ''; for (const ch of s) { if (utf8(out + ch) > 64) { break; } out += ch; } return out; };
const isControl = (c) => /[\u0000-\u001f\u007f-\u009f]/.test(c);

// Null when the launch can be sent; otherwise why not, in the host's words.
export function checkLaunch(l) {
  if (l.agent != null && !LAUNCHERS.includes(l.agent)) { return `unknown agent '${cut(l.agent)}'; this protocol launches ${LAUNCHERS.join(', ')}`; }
  for (const name of ['workspace', 'resume', 'profile', 'model', 'effort', 'context']) {
    const v = l[name];
    if (v == null) { continue; }
    if (utf8(v) > MAX_NAME) { return `the ${name} is longer than ${MAX_NAME} bytes`; }
    if ([...v].some(isControl)) { return `the ${name} holds control characters`; }
  }
  for (const name of ['model', 'effort', 'context']) {
    const v = l[name];
    if (v && !TUNING.test(v)) { return `'${cut(v)}' is not a ${name} any agent offers`; }
  }
  if (l.resume != null && l.workspace != null) { return 'a resumed session reopens in its own folder; name a workspace or a session to resume, not both'; }
  if (l.resume === '') { return 'the session to resume is empty'; }
  if (l.transfer != null) {
    if (l.resume != null) { return 'a transferred session is brought from another machine; name a session to resume or one to transfer, not both'; }
    if (l.workspace == null) { return 'a session from another machine has no folder here; name the workspace it opens in'; }
  }
  if (l.workspace === '') { return 'the workspace is empty'; }
  if (l.permissions != null && l.permissions !== ASK && l.permissions !== ALLOW_ALL) { return `unknown permissions '${cut(l.permissions)}'; use '${ASK}' or '${ALLOW_ALL}'`; }
  const attachments = l.attachments ?? [];
  if (attachments.length > MAX_ATTACHMENTS) { return `a launch carries at most ${MAX_ATTACHMENTS} attachments`; }
  if (attachments.some((a) => !a || typeof a.id !== 'string' || !UUID.test(a.id))) { return 'each attachment must be the UUID of one sent with attachment.start'; }
  if (new Set(attachments.map((a) => a.id.toLowerCase())).size !== attachments.length) { return 'the same attachment is in the launch twice'; }
  const prompt = l.prompt ?? '';
  if (utf8(prompt) > MAX_PROMPT) { return `the prompt is longer than ${MAX_PROMPT / 1024} KiB; send the rest as an attachment or a reply once it has started`; }
  const first = [...prompt].find((c) => !/\s/.test(c) && !isControl(c));
  if (first === '/') { return 'a prompt cannot start with /, which the agent would run as a command; type commands at the machine'; }
  if (first === '!') { return 'a prompt cannot start with !, which the agent would run as a shell command; type commands at the machine'; }
  return null;
}

// The prompt as the agent receives it: one line.
export function flattenPrompt(prompt) {
  return [...(prompt ?? '')].map((c) => (isControl(c) || c === '\u2028' || c === '\u2029' ? ' ' : c)).join('').trim();
}

const str = (v) => (typeof v === 'string' ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);
const strings = (v, max = 64) => (Array.isArray(v) ? [...new Set(v.filter((x) => typeof x === 'string' && x.length > 0))].slice(0, max) : []);

// launch.options, as the form uses it. Anything unreadable is left out, never guessed at: a
// launcher this client does not know cannot be launched from it.
export function readLaunchOptions(raw) {
  const o = obj(raw) ?? {};
  const agents = (Array.isArray(o.agents) ? o.agents : []).map((a) => {
    const x = obj(a);
    if (!x || !LAUNCHERS.includes(x.id)) { return null; }
    const d = obj(x.defaults) ?? {};
    return {
      id: x.id,
      label: str(x.label) ?? x.id,
      kind: readAgent(x.kind),
      models: strings(x.models),
      efforts: strings(x.efforts),
      contexts: strings(x.contexts),
      profiles: Array.isArray(x.profiles) ? strings(x.profiles) : null,
      defaults: { model: str(d.model), effort: str(d.effort), context: str(d.context), profile: str(d.profile) },
      attachments: x.attachments === true,
      needsPrompt: x.needsPrompt === true,
    };
  }).filter(Boolean);
  const workspaces = (Array.isArray(o.workspaces) ? o.workspaces : []).map((w) => {
    const x = obj(w);
    return x && str(x.label) ? { label: x.label, path: str(x.path) ?? '', isolate: x.isolate === true } : null;
  }).filter(Boolean);
  const resumable = (Array.isArray(o.resumable) ? o.resumable : []).map((r) => {
    const x = obj(r);
    if (!x || !str(x.id) || !LAUNCHERS.includes(x.launcher)) { return null; }
    return { id: x.id, agent: readAgent(x.agent), launcher: x.launcher, title: str(x.title) ?? x.id, cwd: str(x.cwd), workspace: str(x.workspace), updatedAt: str(x.updatedAt) };
  }).filter(Boolean);
  const d = obj(o.defaults) ?? {};
  return {
    agents,
    workspaces,
    resumable,
    defaults: {
      agent: agents.some((a) => a.id === d.agent) ? d.agent : agents[0]?.id ?? null,
      workspace: workspaces.some((w) => w.label === d.workspace) ? d.workspace : workspaces[0]?.label ?? null,
      permissions: d.permissions === ALLOW_ALL ? ALLOW_ALL : ASK,
    },
    busy: str(o.busy),
    trust: (Array.isArray(o.trust) ? o.trust : []).map(readTrustQuestion).filter(Boolean),
  };
}

// session.launch's result: { outcome, reason, session, launched, trust }.
export function readLaunchResult(raw) {
  const r = obj(raw) ?? {};
  const l = obj(r.launched);
  return {
    outcome: readLaunchOutcome(r.outcome),
    reason: str(r.reason),
    session: str(r.session),
    launched: l ? {
      agent: str(l.agent), workspace: str(l.workspace), resume: str(l.resume), cwd: str(l.cwd), worktree: l.worktree === true, permissions: str(l.permissions),
      transfer: obj(l.transfer) && str(l.transfer.from) ? { from: l.transfer.from, session: str(l.transfer.session), bytes: typeof l.transfer.bytes === 'number' ? l.transfer.bytes : null } : null,
    } : null,
    trust: readTrustQuestion(r.trust),
  };
}

// ---- folder trust: docs/protocol/v1-launch.md, "Folder trust" ----
// Mirrors src/AgentBridge.Protocol/LaunchTrust.cs. A launched agent asking whether to trust its
// folder, which a person may answer from here (Trust or Quit) where the machine allows it.

export const TRUST = Object.freeze({
  capability: 'launch.trust',
  answer: 'launch.trust.answer',
  trust: 'trust',
  quit: 'quit',
});

// The signed command's action: one choice's command cannot be sent as the other.
export const trustAction = (choice) => `launch.trust:${choice}`;

export const TrustOutcomes = Object.freeze(['started', 'declined', 'unconfirmed', 'refused']);
// Something may have been typed: an outcome from a newer host is never read as refused.
export const readTrustOutcome = (v) => (TrustOutcomes.includes(v) ? v : 'unconfirmed');

// A question as the machine describes it; null when it has no id or folder to show.
export function readTrustQuestion(raw) {
  const q = obj(raw);
  if (!q || !str(q.id) || !str(q.folder)) { return null; }
  return {
    id: q.id,
    agent: str(q.agent),
    kind: readAgent(q.kind),
    label: str(q.label) ?? str(q.agent) ?? 'The agent',
    workspace: str(q.workspace),
    folder: q.folder,
    root: str(q.root),
    rootNote: str(q.rootNote),
    warning: str(q.warning),
    askedAt: str(q.askedAt),
    answerable: q.answerable === true,
    atMachine: str(q.atMachine),
  };
}

// launch.trust.answer's result: { outcome, reason, session }.
export function readTrustResult(raw) {
  const r = obj(raw) ?? {};
  return { outcome: readTrustOutcome(r.outcome), reason: str(r.reason), session: str(r.session) };
}
