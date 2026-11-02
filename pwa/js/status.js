// What a session says about itself: docs/design/ui.md §2.2, the one rule set for the card
// today and for the rows and header the layout slices build (#179). The glyph, the words, the
// colour role the words take, and the name a screen reader hears (§9). No DOM here, so every
// rule is tested on its own (test/unit/status.test.mjs, named after the rules).
//
// Rules 1-6 are first match wins. Rule 8 refines rule 4. Rule 7 is not a row of its own but an
// overlay: a driven session keeps the glyph and words rules 1-6 picked, gains the driven badge,
// and its active colour and its words turn to the agent role.

// A sub-agent still running: working, or waiting on a question of its own. An idle one waits
// to be woken and is not counted (docs/design/subagents.md §2).
const RUNNING = new Set(['working', 'waiting']);

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// { rule, glyph, view, text, tone, driven, spoken }
//   rule: 1-6 or 8 from §2.2, or 'ending' while the host ends the session.
//   glyph: the §2.1 glyph; view: the card's state (data-view), for what keys off it.
//   tone: the colour role the words take: needs, agent (rule 7), muted or plain. Words are
//   never in active or neutral (§3).
//   spoken: the words as they read mid-sentence, for the glyph's accessible name.
export function sessionStatus(s) {
  const picked = pick(s);
  const driven = s.driver === 'agent';
  return {
    ...picked,
    driven,
    tone: driven ? 'agent' : picked.tone,
    spoken: picked.rule === 2 || /^\d/.test(picked.text) ? picked.text : picked.text.charAt(0).toLowerCase() + picked.text.slice(1),
  };
}

function pick(s) {
  const q = s.question;
  const atMachine = q?.answerAtMachine ? ' · answer at the machine' : '';
  // Not one of §2.2's rows: the host is ending the session (session.stop), so nothing it was
  // doing is news any more. Its dotted ring is the ended glyph's.
  if (s.status === 'ending') { return { rule: 'ending', glyph: 'ended', view: 'ending', text: 'Ending session…', tone: 'muted' }; }
  if (s.status === 'waiting') {
    // Rule 2: a delegate's question, named for the delegate. The session's own question wins
    // over it at the host, which then sends no waitingOn.
    const asker = s.waitingOn ?? q?.agent ?? null;
    if (asker) {
      const name = s.subagents?.find((a) => a.id === asker)?.name ?? 'A delegate';
      return { rule: 2, glyph: 'needs', view: 'waiting', text: `${name} asks${atMachine}`, tone: 'needs' };
    }
    const n = runningDelegates(s);
    return { rule: 1, glyph: 'needs', view: 'waiting', text: `Waiting on you${n ? ` · ${plural(n, 'delegate', 'delegates')} still running` : ''}${atMachine}`, tone: 'needs' };
  }
  if (s.status === 'working') {
    const task = foregroundTask(s);
    return { rule: 3, glyph: 'working', view: 'working', text: task ? `Working · task: ${task.name}` : 'Working', tone: 'plain' };
  }
  const b = s.activity?.background;
  if (b?.agents) {
    // The host's count is at any depth and never trimmed, so a nested delegate counts once,
    // and the count holds when the list is cut short.
    const text = `Delegating to ${plural(b.agents, 'agent', 'agents')}`;
    // Rule 8: delegation the host can count but not list (Claude and Codex until #84's later
    // slices, or a host older than the list). Nothing is attributed that wasn't reported.
    if (!s.subagents?.length && !s.subagentsMore) { return { rule: 8, glyph: 'delegating', view: 'delegating', text: `${text} · details not reported`, tone: 'plain' }; }
    return { rule: 4, glyph: 'delegating', view: 'delegating', text, tone: 'plain' };
  }
  if (b?.shells) { return { rule: 5, glyph: 'delegating', view: 'delegating', text: plural(b.shells, 'background command', 'background commands'), tone: 'plain' }; }
  return { rule: 6, glyph: 'idle', view: 'idle', text: 'Idle', tone: 'plain' };
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

// The glyph's accessible name (ui.md §9): "Plan Migration to Windows App, delegating to 3
// agents, DEV-VM1", with the driver said in words, since the badge is only a shape.
export function statusLabel(status, { title, machine = null } = {}) {
  return [title || 'Untitled session', status.spoken, status.driven ? 'driven by an agent' : null, machine].filter(Boolean).join(', ');
}
