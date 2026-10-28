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

export function localTime(iso) {
  const t = Date.parse(iso ?? '');
  return Number.isFinite(t) ? new Date(t).toLocaleString() : '';
}
