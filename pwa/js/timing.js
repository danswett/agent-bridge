// Timing marks for replies and answers (#600): the tap, each send, each progress frame and the
// outcome, as the browser's performance marks and measures, with the host's own stage timings
// (the result's `timings`, ms since the host received the request) beside them. Before these,
// how long a reply took, and where, could only be guessed from logs with 1 s resolution (#597).
//
// Kept on this device only, and never sent anywhere. Read them in the browser's Performance
// panel (measures named "agent-bridge reply 1a2b3c4d typed", from the tap), with
// agentBridge.timings() in the console, or in the console's verbose level, which has a line
// for each outcome.
//
// One delivery is named by its request, which a resend keeps, so every attempt of one reply is
// one record.

const KEEP = 50;
const PREFIX = 'agent-bridge';

// Deliveries still on their way, by request, and the last KEEP that have an outcome.
const open = new Map();
const kept = [];

const perf = () => (typeof globalThis.performance?.mark === 'function' ? globalThis.performance : null);
const name = (run, what) => `${PREFIX} ${run.kind} ${run.request.slice(0, 8)} ${what}`;

function markAt(run, what) {
  const at = perf()?.now() ?? Date.now();
  // A resend is sent again, and a repeat replays its stages: each keeps a mark of its own.
  const again = run.marks.filter(([w]) => w === what || w.startsWith(`${what} `)).length;
  const label = again ? `${what} ${again + 1}` : what;
  run.marks.push([label, Math.round(at - run.start)]);
  try { perf()?.mark(name(run, label)); } catch { /* a timeline that is full or missing loses a mark, nothing else */ }
}

// The person pressed Send or an answer: kind is 'reply' or 'answer'.
export function tapped(kind, request) {
  const run = { kind, request, start: perf()?.now() ?? Date.now(), marks: [] };
  open.set(request, run);
  // A delivery that never ends (signed out mid-send) doesn't stay forever.
  if (open.size > KEEP) { open.delete(open.keys().next().value); }
  markAt(run, 'tap');
}

// Something happened to a delivery on its way: 'sent' (each attempt), or a progress stage.
export function marked(request, what) {
  const run = open.get(request);
  if (run) { markAt(run, what); }
}

// The delivery's outcome, with the host's timings if it sent any: measures each mark from the
// tap, keeps the record, and says it on the console's verbose level.
export function finished(request, outcome, host = null) {
  const run = open.get(request);
  if (!run) { return; }
  open.delete(request);
  markAt(run, 'result');
  const p = perf();
  for (const [what] of run.marks.slice(1)) {
    try { p?.measure(name(run, what), { start: name(run, 'tap'), end: name(run, what) }); } catch { /* as above */ }
  }
  for (const [what] of run.marks) { try { p?.clearMarks(name(run, what)); } catch { /* as above */ } }
  const record = { kind: run.kind, request: run.request, outcome, at: new Date().toISOString(), client: run.marks.slice(1), host: host ? { ...host } : null };
  kept.push(record);
  while (kept.length > KEEP) {
    const old = kept.shift();
    for (const [what] of old.client) { try { p?.clearMeasures(name(old, what)); } catch { /* as above */ } }
  }
  const client = record.client.map(([what, ms]) => `${what} ${ms} ms`).join(', ');
  const fromHost = host ? `; host: ${Object.entries(host).map(([what, ms]) => `${what} ${ms} ms`).join(', ')}` : '';
  console.debug(`${PREFIX} ${run.kind} ${run.request.slice(0, 8)} ${outcome}: ${client}${fromHost}`);
}

// The last deliveries' records, oldest first: { kind, request, outcome, at, client: [[mark, ms
// since the tap]], host: { stage: ms since the host received it } | null }.
export function recentTimings() { return kept.map((r) => ({ ...r, client: r.client.map((m) => [...m]), host: r.host ? { ...r.host } : null })); }
