// A session on its way to another machine (#1715, ui.md 6.6.2): the five steps of the move
// contract (docs/protocol/v1-machine-load.md, MoveTelemetry) as what the person is shown. Pure:
// a move's steps and outcome in, each step's state and the words for it out. Never subtracts
// `at` values across hosts, holds no session content, and names no machine of its own.
export const STEPS = [
  ['handoffRequested', 'Hand-off requested'],
  ['handoffConfirmed', 'Hand-off confirmed'],
  ['artifactsPushed', 'Files sent'],
  ['newLaunched', 'New session launched'],
  ['newOnline', 'New session online'],
];
export const STATE_WORDS = { done: 'Done', current: 'In progress', failed: 'Failed', notYet: 'Not yet', notReached: 'Not reached' };
const ENDED_BADLY = new Set(['refused', 'failed', 'timeout']);

export function moveView(move) {
  const reached = STEPS.filter(([name]) => move.steps?.some((s) => s.name === name)).length;
  const bad = ENDED_BADLY.has(move.outcome) && reached < STEPS.length;
  const moved = move.outcome === 'online' || reached === STEPS.length;
  const steps = STEPS.map(([name, label], i) => {
    const state = i < reached ? 'done' : moved ? 'done' : i === reached ? (bad ? 'failed' : 'current') : bad ? 'notReached' : 'notYet';
    return { name, label, state, word: STATE_WORDS[state] };
  });
  const at = steps[Math.min(reached, STEPS.length - 1)];
  const summary = moved ? `Moved to ${move.to}`
    : bad ? `Failed at step ${reached + 1} of 5: ${at.label}` : `Step ${reached + 1} of 5: ${at.label}`;
  const note = bad ? `${move.reason ? `${move.reason} ` : ''}The original session is untouched and you can still answer it.` : '';
  return { steps, summary, note, moved, failed: bad };
}

// Moves by session id, so a card shows its own without the page wiring each one through.
const byId = new Map();
const listeners = new Set();
export const moves = {
  get: (id) => byId.get(id),
  set(id, move) {
    if (move) { byId.set(id, move); } else { byId.delete(id); }
    for (const fn of [...listeners]) { fn(id, move ?? undefined); }
  },
  subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
};

// The demo's stand-in for the host's step events: a step every 2 s, then online, or a failure at
// step 3. The machines are made up here, as the demo's are.
export function fakeMove(store, session, fail = false, every = 2000) {
  let n = 0;
  const send = () => {
    const steps = STEPS.slice(0, n).map(([name], i) => ({ name, at: new Date().toISOString(), ms: i ? every : 0 }));
    const outcome = fail && n === 2 ? 'failed' : n === STEPS.length ? 'online' : undefined;
    store.set(session, { moveId: 'demo', from: 'Laptop', to: 'Studio PC', session, steps, outcome, ...(outcome === 'failed' ? { reason: 'The files were too large to send.' } : {}) });
    if (!outcome) { n += 1; setTimeout(send, every); }
  };
  send();
}
