// Presentation only: MachineLoad in, bars out. Freshness uses the record's age and limit,
// never a client timer; a future load subscription can supply the same input.
const STATES = {
  fresh: ['Fresh', '\u25cf'], partial: ['Partial', '\u25d0'], stale: ['Stale', '\u25cc'],
  unavailable: ['Unavailable', '\u25cb'], offline: ['Offline', '\u2298'],
};
const nonnegative = (n) => Number.isFinite(n) && n >= 0;
const percent = (n) => nonnegative(n) && n <= 100 ? n : null;
const bar = (label, value, text = value) => ({ label, percent: value, text: value === null ? 'no figure' : `${text}%` });

export function loadBars(load, { online = true } = {}) {
  let state = !online ? 'offline' : load && Object.hasOwn(STATES, load.state) && load.state !== 'offline' ? load.state : 'unavailable';
  const ageMs = nonnegative(load?.ageMs) ? load.ageMs : null;
  if (state === 'fresh' || state === 'partial') {
    if (ageMs !== null && nonnegative(load.staleAfterMs) && ageMs > load.staleAfterMs) { state = 'stale'; }
  }
  const live = state === 'fresh' || state === 'partial';
  const cpu = live ? percent(load?.cpu?.percent) : null;
  const m = load?.memory;
  const memory = live && nonnegative(m?.totalBytes) && m.totalBytes > 0
    && nonnegative(m.availableBytes) && m.availableBytes <= m.totalBytes
    ? (m.totalBytes - m.availableBytes) / m.totalBytes * 100 : null;
  if (state === 'fresh' && (cpu === null || memory === null)) { state = 'partial'; }
  const [word, icon] = STATES[state];
  return { state, word, icon, ageMs, cpu: bar('CPU (whole machine)', cpu), memory: bar('Memory used', memory, Math.round(memory * 10) / 10) };
}
