// A session's Details rows (docs/design/ui.md §6.2, #1087): the words, in the order the one
// shared table sets (docs/design/session-details.json, which the Windows app's tests read as
// well). Pure, so a test can check them against that table without a page; the card draws what
// this returns. A row with no value is left out, never drawn empty.
import { sessionStatus, silentSince } from './status.js';
import { clockTime } from './auth-state.js';
import { ago, duration } from './ui/time.js';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
// What is code, drawn in the mono face (§4.1, #215).
const MONO = new Set(['branch', 'folder', 'pid', 'id']);

// ui.md §2.2 (#269), as facts: "Offline · last seen 2m ago" while its machine is away,
// "Reconnecting…" while it has no open channel and is still tried; null otherwise, including
// while its snapshot is still coming (its rows say what that snapshot says, and still take
// nothing) and for a machine that no longer offers sessions (its host line says so).
// stale: { reason, since, grace } as the fleet holds it. On a return the row is as it was last
// seen: the away states wait for the grace to end (#950).
export function awayWords(stale, now = Date.now()) {
  if (!stale || stale.grace || stale.reason === 'confirming' || stale.reason === 'unsupported') { return null; }
  if (stale.reason === 'reconnecting') { return ['Reconnecting…']; }
  const seen = ago(stale.since, now);
  return seen ? ['Offline', `last seen ${seen}`] : ['Offline'];
}

// The start as a clock time, with the date once it was another day (or year) than now's.
export function startedText(iso, now = Date.now()) {
  const t = Date.parse(iso ?? '');
  if (!Number.isFinite(t)) { return ''; }
  const at = new Date(t);
  const today = new Date(now);
  const time = { hour: 'numeric', minute: '2-digit' };
  if (at.toDateString() === today.toDateString()) { return at.toLocaleTimeString([], time); }
  const date = at.getFullYear() === today.getFullYear() ? { month: 'short', day: 'numeric' } : { month: 'short', day: 'numeric', year: 'numeric' };
  return at.toLocaleString([], { ...date, ...time });
}

// status: the words an offline machine's session shows in their place; null otherwise. Only the
// status words, never Not responding's time, which Not responding since carries (§6.2). machine: its
// name, or null while unknown; away: its machine is offline, so nothing says an agent still drives
// it and no silence is known. Each row: { term, text, kind, mono } with kind for how it is drawn.
export function detailRows(s, { status = null, machine = null, away = false, now = Date.now() } = {}) {
  const state = sessionStatus(s);
  const rows = [];
  const add = (term, text, kind = null, extra = null) => {
    if (text === null || text === undefined || text === '') { return; }
    rows.push({ term, text: String(text), kind, mono: MONO.has(kind), ...extra });
  };
  const b = s.activity.background;
  const turns = s.activity.turnCount;
  add('Status', status ?? (state.rule === 12 ? state.facts[0] : state.text), 'status');
  // How long in the session's own status: nothing when idle, and nothing while the machine is away,
  // where it would count up for a session nobody can see (§6.2). The offline words' "last seen" is
  // the only time in the state group then.
  add('In this state', away || state.view === 'idle' ? null : `for ${duration(s.statusSince, now)}`, 'since');
  add('Message', s.attention?.message, 'attention');
  add('Not responding since', away ? null : clockTime(silentSince(s)), 'silent');
  add('Driven by', state.driven && !away ? (s.driverPrincipal || 'an agent') : null, 'driver');
  add('Agent', { copilot: 'Copilot', claude: 'Claude', codex: 'Codex' }[s.agent] ?? 'Agent', 'agent');
  add('Model', s.model, 'model');
  add('Machine', machine, 'machine');
  add('Repository', s.repository, 'repo');
  add('Branch', s.branch, 'branch');
  add('Folder', s.cwd, 'folder');
  add('Started', startedText(s.startedAt, now), 'started', { at: s.startedAt });
  add('Updated', ago(s.updatedAt, now), 'updated');
  add('Background', [b?.agents ? plural(b.agents, 'background agent', 'background agents') : null, b?.shells ? plural(b.shells, 'background command', 'background commands') : null].filter(Boolean).join(' \u00b7 '), 'background');
  add('Turns', turns === null || turns === undefined ? null : plural(turns, 'turn', 'turns'), 'turns');
  add('Compactions', s.activity.compactions ? plural(s.activity.compactions, 'compaction', 'compactions') : null, 'compactions');
  add('Process', s.processId === null || s.processId === undefined ? null : `pid ${s.processId}`, 'pid');
  add('ID', s.id, 'id');
  return rows;
}
