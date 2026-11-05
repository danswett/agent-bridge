// What a session says about itself: docs/design/ui.md §2.2, the one rule set for the card
// today and for the rows and header the layout slices build (#179). The glyph, the words, the
// colour role the words take, and the name a screen reader hears (§9). No DOM here, so every
// rule is tested on its own (test/unit/status.test.mjs, named after the rules).
//
// Rules 1-6 are first match wins. Rule 8 refines rule 4. Rule 7 is not a row of its own but an
// overlay: a driven session keeps the glyph and words rules 1-6 picked, gains the driven badge,
// and its active colour and its words turn to the agent role.
import { aloud, delegateTitle } from './attribution.js';
import { ERROR_WORDS, clockTime, confirming, needsSignIn } from './auth-state.js';

const AGENT_WORDS = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex' };

// A sub-agent still running: working, or waiting on a question of its own. An idle one waits
// to be woken and is not counted (docs/design/subagents.md §2).
const RUNNING = new Set(['working', 'waiting']);

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// { rule, glyph, view, facts, text, tone, driven, spoken }
//   rule: 1-6 or 8 from §2.2, 10-12 (#800: sign-in, an error, not responding), or 'ending' while
//   the host ends the session.
//   facts: the status's facts ("Waiting on you", "2 delegates still running"), kept apart until
//   they are drawn: a row shows the first, the header all of them. A fact can carry a delegate's
//   name, so none is ever found by cutting the joined words at a separator (#286, #316).
//   text: the facts joined with " · ", as the header and Details read.
//   glyph: the §2.1 glyph; view: the card's state (data-view), for what keys off it.
//   tone: the colour role the words take: needs, agent (rule 7), muted or plain. Words are
//   never in active or neutral (§3).
//   spoken: the words as they read mid-sentence, for the glyph's accessible name. Each fact has
//   no comma of its own there (aloud): statusLabel joins with ", ", and a delegate named "x,
//   idle, DEV-VM2" would read as facts of its own to a screen reader (#303's security review).
// The rules for an agent that needs sign-in, failed or stopped answering (#800).
export const ATTENTION_RULES = new Set([10, 11, 12]);

export function sessionStatus(s) {
  const { facts, ...picked } = pick(s);
  const kept = facts.filter(Boolean);
  const driven = s.driver === 'agent';
  // Rules 10-12 (#800) say what is wrong, which the driven badge doesn't: their words and tone stay.
  const attention = ATTENTION_RULES.has(picked.rule);
  const said = kept.map(aloud).join(SEPARATOR);
  return {
    ...picked,
    facts: kept,
    text: kept.join(SEPARATOR),
    driven,
    tone: driven && !attention ? 'agent' : picked.tone,
    spoken: picked.rule === 2 || /^\d/.test(said) ? said : said.charAt(0).toLowerCase() + said.slice(1),
  };
}

const SEPARATOR = ' \u00b7 ';

function pick(s) {
  const q = s.question;
  const atMachine = q?.answerAtMachine ? 'answer at the machine' : null;
  // Not one of §2.2's rows: the host is ending the session (session.stop), so nothing it was
  // doing is news any more. Its dotted ring is the ended glyph's.
  if (s.status === 'ending') { return { rule: 'ending', glyph: 'ended', view: 'ending', facts: ['Ending session…'], tone: 'muted' }; }
  // The session's own question asks (rule 1), or a delegate's (rule 2). The session's own wins at the
  // host, which then sends no waitingOn.
  const asker = s.status === 'waiting' ? s.waitingOn ?? q?.agent ?? null : null;
  if (s.status === 'waiting' && !asker) {
    const n = runningDelegates(s);
    return { rule: 1, glyph: 'needs', view: 'waiting', facts: ['Waiting on you', n ? `${plural(n, 'delegate', 'delegates')} still running` : null, atMachine], tone: 'needs' };
  }
  // #800: what the host says is wrong with the session, shown over its status, which stays truthful
  // (Session.attention, auth-state.js). Never idle, working or a stale age for any of them.
  const at = s.attention;
  const agent = AGENT_WORDS[s.agent] ?? null;
  // Rule 10, confirming: signed in, and the host hasn't yet seen the model answer (recovery
  // pending). Not waiting on you, and not recovered either: one fact, so the row says all of it.
  // After the session's own question and before a delegate's (ui.md §6.13): the main agent can't
  // run, which a delegate's question would otherwise hide.
  if (confirming(at)) { return { rule: 10, glyph: 'idle', view: 'confirming', facts: ['Signed in \u00b7 waiting for the session to confirm'], tone: 'plain' }; }
  // Rule 10: the agent can't run until a person signs it in. It waits on you as a question does:
  // "Needs sign-in · Copilot · @octocat".
  if (needsSignIn(at)) { return { rule: 10, glyph: 'needs', view: 'signin', facts: ['Needs sign-in', agent, at.account ? `@${at.account}` : null], tone: 'needs' }; }
  if (asker) {
    // Rule 2: a delegate's question, named for the delegate: "Delegate <name> asks", the word
    // Delegate before any name, so never "Main agent asks".
    const agent = s.subagents?.find((a) => a.id === asker);
    const name = agent ? delegateTitle(agent) : 'A delegate';
    return { rule: 2, glyph: 'needs', view: 'waiting', facts: [`${name} asks`, atMachine], tone: 'needs' };
  }
  // Rule 12, refining rule 3: the host has heard nothing from a working session past its
  // threshold. The failed glyph, and no button until there is something to do.
  if (at?.kind === 'notResponding') {
    const since = clockTime(at.lastEventAt ?? at.since);
    return { rule: 12, glyph: 'failed', view: 'stalled', facts: ['Not responding', since ? `since ${since}` : null], tone: 'needs' };
  }
  if (s.status === 'working') {
    const task = foregroundTask(s);
    return { rule: 3, glyph: 'working', view: 'working', facts: ['Working', task ? `task: ${delegateTitle(task)}` : null], tone: 'plain' };
  }
  const b = s.activity?.background;
  // Rule 11, before rules 4 and 5: the latest turn ended on an agent error. Background work that
  // survived the turn never hides it behind "Delegating" (ui.md §6.13). The row says its class in the
  // client's own words; the host's message is in Details only, as plain text.
  if (at?.kind === 'error') {
    // Every delegate still running, task ones too, which can survive the failed turn (v1-sessions.md).
    const n = runningDelegates(s);
    return { rule: 11, glyph: 'failed', view: 'error', facts: [ERROR_WORDS[at.errorClass] ?? ERROR_WORDS.other, n ? `${plural(n, 'delegate', 'delegates')} still running` : null], tone: 'needs' };
  }
  if (b?.agents) {
    // The host's count is at any depth and never trimmed, so a nested delegate counts once,
    // and the count holds when the list is cut short.
    const text = `Delegating to ${plural(b.agents, 'agent', 'agents')}`;
    // Rule 8: delegation the host can count but not list (Claude and Codex until #84's later
    // slices, or a host older than the list). Nothing is attributed that wasn't reported.
    if (!s.subagents?.length && !s.subagentsMore) { return { rule: 8, glyph: 'delegating', view: 'delegating', facts: [text, 'details not reported'], tone: 'plain' }; }
    return { rule: 4, glyph: 'delegating', view: 'delegating', facts: [text], tone: 'plain' };
  }
  if (b?.shells) { return { rule: 5, glyph: 'delegating', view: 'delegating', facts: [plural(b.shells, 'background command', 'background commands')], tone: 'plain' }; }
  return { rule: 6, glyph: 'idle', view: 'idle', facts: ['Idle'], tone: 'plain' };
}

// Delegates still running beside the session's own question. The host's background count is
// at any depth and never trimmed, but counts background agents only; task agents come from the
// list (which a budget may trim). The listed background ones stand in for the count when a host
// sends none, and are never counted twice.
function runningDelegates(s) {
  const listed = (s.subagents ?? []).filter((a) => RUNNING.has(a.status));
  const background = listed.filter((a) => a.kind === 'background').length;
  return Math.max(s.activity?.background?.agents ?? 0, background) + (listed.length - background);
}

// The task agent running as part of the main agent's own turn: a running task entry whose
// starters are all task agents too. One a background delegate started is that delegate's work,
// not the main agent's. The list is newest first, so this is the latest to start.
function foregroundTask(s) {
  const list = s.subagents ?? [];
  const byId = new Map(list.map((a) => [a.id, a]));
  const foreground = (a) => {
    for (let seen = 0, p = a.parent; p; p = byId.get(p)?.parent, seen++) {
      const parent = byId.get(p);
      if (!parent || parent.kind !== 'task' || seen > list.length) { return false; }
    }
    return true;
  };
  return list.find((a) => a.kind === 'task' && RUNNING.has(a.status) && a.name && foreground(a)) ?? null;
}

// Since when a Not responding session (rule 12) has been silent: when the host last heard from the
// agent (attention.lastEventAt), else since it said so (attention.since); null for any other row
// (#878, ui.md §6.13).
export function silentSince(s) {
  const at = s.attention;
  return sessionStatus(s).rule === 12 ? (at.lastEventAt ?? at.since ?? null) : null;
}

// When the row's trailing age counts from: for Not responding, since it went silent, so the age
// says since when; otherwise the session's last update.
export function rowAt(s) {
  return silentSince(s) ?? s.updatedAt;
}

// The glyph's accessible name (ui.md §9): "Plan Migration to Windows App, delegating to 3
// agents, DEV-VM1", with the driver said in words, since the badge is only a shape.
export function statusLabel(status, { title, machine = null } = {}) {
  return [title || 'Untitled session', status.spoken, status.driven ? 'driven by an agent' : null, machine].filter(Boolean).join(', ');
}
