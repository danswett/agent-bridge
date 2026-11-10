// The time on a conversation message (#1619, ui.md §6.4 "Time on messages"). One formatter for
// every kind of message, so a message never shows two time styles. Nothing here guesses: a time
// that is missing, unreadable or history's "no time" stand-in gives no text at all.
import { NO_TIME } from '../history.js';

const FORMATTERS = new Map();
function formatter(locale, options) {
  const key = JSON.stringify([locale ?? null, options]);
  let f = FORMATTERS.get(key);
  if (!f) {
    f = new Intl.DateTimeFormat(locale, options);
    FORMATTERS.set(key, f);
  }
  return f;
}

// Intl puts a narrow no-break space before AM or PM; a plain one reads, and compares, better.
const plain = (s) => s.replace(/[\s\u202f]/gu, ' ');

// The instant (ms) of a message's time, or null when it has none to show.
export function messageInstant(iso) {
  if (typeof iso !== 'string' || iso === NO_TIME) { return null; }
  const t = Date.parse(iso);
  return Number.isFinite(t) ? t : null;
}

const day = (t, locale, timeZone) => formatter(locale, { year: 'numeric', month: 'numeric', day: 'numeric', timeZone }).format(t);
const year = (t, locale, timeZone) => formatter(locale, { year: 'numeric', timeZone }).format(t);

// The visible text. Under 10 s "just now", then "42s ago", then "7 min ago" (rounded down) up to
// an hour; after that the clock time ("7:41 PM"), with the date for another day and the year for
// another year. The relative window wins over the calendar day. A time in the future is "just
// now". locale and timeZone are the viewer's unless a test names them.
export function messageTime(iso, now = Date.now(), { locale, timeZone } = {}) {
  const t = messageInstant(iso);
  if (t === null) { return ''; }
  const s = Math.floor((now - t) / 1000);
  if (s < 10) { return 'just now'; }
  if (s < 60) { return `${s}s ago`; }
  if (s < 3600) { return `${Math.floor(s / 60)} min ago`; }
  const clock = { hour: 'numeric', minute: '2-digit', timeZone };
  if (day(t, locale, timeZone) === day(now, locale, timeZone)) { return plain(formatter(locale, clock).format(t)); }
  const date = { month: 'short', day: 'numeric', ...(year(t, locale, timeZone) === year(now, locale, timeZone) ? {} : { year: 'numeric' }), ...clock };
  return plain(formatter(locale, date).format(t));
}

// The exact time, with seconds and zone: what the reveal shows and the accessible name says. It
// does not change as the minutes pass.
export function exactTime(iso, { locale, timeZone } = {}) {
  const t = messageInstant(iso);
  if (t === null) { return ''; }
  return plain(formatter(locale, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'short', timeZone }).format(t));
}

// How long until the text could change: a second while under a minute old, else the time to the
// next whole minute of its age. null when it has no time or is already a clock time.
export function nextChange(iso, now = Date.now()) {
  const t = messageInstant(iso);
  if (t === null) { return null; }
  const age = now - t;
  if (age >= 3600000) { return null; }
  if (age < 60000) { return 1000; }
  return Math.max(1000, 60000 - (age % 60000));
}
