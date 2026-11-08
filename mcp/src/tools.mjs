// The tools, with agent-ha-bridge's names, over one host's pipe. `host` is { request(op, data) }.
import { createHash, randomUUID } from 'node:crypto';
import { PipeError } from './pipe.mjs';

export const LAUNCH_SETTINGS = ['agent', 'workspace', 'profile', 'resume', 'model', 'effort', 'context', 'permissions'];
const PERMISSIONS = { 'Ask permission': 'ask', 'Allow all': 'allowAll' };
const REMEMBERED = 200;

/** What a reply is answered against: changes whenever the session's visible turn changes. */
export function turnMarker(session) {
  const response = session?.activity?.response ?? '';
  // Not statusSince: it moves with every status change (idle to waiting, a host re-stamp) without a new turn.
  return createHash('sha256').update(`${session?.activity?.turnCount ?? ''}|${response}`, 'utf8').digest('hex').slice(0, 16);
}

function typed(session) {
  return {
    id: session.id,
    agent: session.agent,
    title: session.title,
    cwd: session.cwd,
    repository: session.repository,
    branch: session.branch,
    model: session.model,
    status: session.status,
    statusSince: session.statusSince,
    startedAt: session.startedAt,
    updatedAt: session.updatedAt,
  };
}

export async function listSessions(host, { include } = {}) {
  const sessions = [];
  let cursor;
  let discoveryHeld = false;
  do {
    const page = await host.request('sessions.list', cursor ? { cursor } : {});
    for (const s of page.sessions ?? []) {
      sessions.push(include === 'content' ? { ...typed(s), activity: s.activity, question: s.question } : typed(s));
    }
    discoveryHeld ||= Boolean(page.discoveryHeld);
    cursor = page.next;
  } while (cursor && sessions.length < 500);
  return { sessions, ...(discoveryHeld ? { discoveryHeld } : {}) };
}

/**
 * Reads one session. With `since` (what reply_to_agent_session returned) the answer is `done`
 * only once the turn marker has moved and the session is no longer working, so a stale
 * answer is never handed back as the reply.
 */
export async function readSession(host, id, { since = '', waitSeconds = 0, sleep = defaultSleep } = {}) {
  const deadline = Date.now() + Math.max(0, waitSeconds) * 1000;
  for (;;) {
    const session = await host.request('session.get', { id });
    if (!session || session.missing) return { id, found: false, done: false };
    const moved = !since || turnMarker(session) !== since;
    const done = moved && session.status !== 'working';
    if (done || Date.now() >= deadline) {
      return {
        id,
        found: true,
        done,
        status: session.status,
        statusSince: session.statusSince,
        response: moved ? (session.activity?.response ?? '') : '',
        ...(session.activity?.responseTruncated ? { responseTruncated: true } : {}),
        ...(session.question ? { question: session.question } : {}),
        since: turnMarker(session),
      };
    }
    await sleep(Math.min(1000, Math.max(50, deadline - Date.now())));
  }
}

const defaultSleep = (ms) => new Promise((r) => setTimeout(r, ms));

export async function replyToSession(host, id, text) {
  const before = await host.request('session.get', { id });
  if (!before || before.missing) return { id, outcome: 'refused', reason: 'No such session.' };
  const sent = Buffer.byteLength(text, 'utf8');
  const since = turnMarker(before);
  let result;
  try {
    result = await host.request('session.reply', { id, request: randomUUID(), text });
  } catch (error) {
    if (!(error instanceof PipeError)) throw error;
    // A timeout leaves the reply possibly delivered: say so, with since, rather than throwing.
    if (error.kind === 'unavailable') return { id, outcome: 'unknown', reason: error.message, sent, since };
    return { id, outcome: 'refused', reason: error.message, sent };
  }
  const accepted = result.outcome !== 'refused';
  return {
    id,
    outcome: result.outcome,
    ...(result.reason ? { reason: result.reason } : {}),
    sent,
    ...(accepted ? { since } : {}),
  };
}

export class LaunchLedger {
  constructor() {
    this.results = new Map();
  }

  remember(request, result) {
    this.results.set(request, result);
    if (this.results.size > REMEMBERED) this.results.delete(this.results.keys().next().value);
  }

  read(request) {
    return this.results.get(request) ?? { status: 'unknown', reason: 'No launch with that request id was made through this server.' };
  }
}

export async function launchSession(host, ledger, options = {}) {
  const data = { request: typeof options.request === 'string' && options.request ? options.request : randomUUID() };
  for (const key of LAUNCH_SETTINGS) {
    let value = options[key];
    if (value === undefined || value === null) continue;
    if (key === 'permissions') value = PERMISSIONS[value] ?? value;
    data[key] = value;
  }
  if (options.prompt) data.prompt = options.prompt;
  let result;
  try {
    result = await host.request('session.launch', data);
  } catch (error) {
    if (!(error instanceof PipeError) || error.kind !== 'refused') throw error;
    result = { outcome: 'refused', reason: error.message };
  }
  const answer = { request: data.request, outcome: result.outcome, ...(result.reason ? { reason: result.reason } : {}), ...(result.session ? { session: result.session } : {}) };
  ledger.remember(data.request, answer);
  return answer;
}

export const getLaunchOptions = (host) => host.request('launch.options', {});
