// "for 3m", "5m ago": short, for a phone-width status line.

export function ago(iso, now = Date.now()) {
  const t = Date.parse(iso ?? '');
  if (!Number.isFinite(t)) { return ''; }
  const s = Math.floor((now - t) / 1000);
  if (s < 10) { return 'just now'; }
  return `${span(s)} ago`;
}

export function duration(iso, now = Date.now()) {
  const t = Date.parse(iso ?? '');
  if (!Number.isFinite(t)) { return ''; }
  return span(Math.max(0, Math.floor((now - t) / 1000)));
}

function span(s) {
  if (s < 60) { return `${s}s`; }
  const m = Math.floor(s / 60);
  if (m < 60) { return `${m}m`; }
  const h = Math.floor(m / 60);
  if (h < 48) { return `${h}h ${m % 60}m`; }
  return `${Math.floor(h / 24)}d`;
}

// How long a call took, as a clock: "0:07", "12:30", "1:02:05" (tabular figures line them up).
// to: when it ended, or null while it runs (now).
export function clock(from, to = null, now = Date.now()) {
  const a = Date.parse(from ?? '');
  if (!Number.isFinite(a)) { return ''; }
  const b = to ? Date.parse(to) : now;
  const s = Math.max(0, Math.floor(((Number.isFinite(b) ? b : now) - a) / 1000));
  const two = (n) => String(n).padStart(2, '0');
  return s < 3600 ? `${Math.floor(s / 60)}:${two(s % 60)}` : `${Math.floor(s / 3600)}:${two(Math.floor(s / 60) % 60)}:${two(s % 60)}`;
}

// A clock time in the viewer's own zone and locale, "12:41 PM", with no zone after it, for an
// event's tooltip and accessible name (ui.md §6.4). Made each time, so it follows the zone the
// device is in now. The space before AM or PM is a plain one, whatever space Intl puts there.
export function timeOfDay(iso) {
  const t = Date.parse(iso ?? '');
  return Number.isFinite(t) ? new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' }).format(t).replace(/\s/gu, ' ') : '';
}

export function localTime(iso) {
  const t = Date.parse(iso ?? '');
  return Number.isFinite(t) ? new Date(t).toLocaleString() : '';
}
