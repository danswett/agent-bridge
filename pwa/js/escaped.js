// An approval the host escaped anything in (#803, docs/protocol/v1-sessions.md, "Escaped
// approvals"): Deny goes as for any approval; Approve goes only as a two-step confirmation from a
// device the machine approved. "Approve once…" opens a sheet showing the escaped command in full,
// and only the sheet's own button, at the end of the command, signs and sends (ui.md §6.4,
// "Escaped approvals"). The app's twin is src/AgentBridge.App.Core/EscapedApprovalView.cs.
import { hasHidden, hasRightToLeft } from './hidden-text.js';
import { utf8 } from './bytes.js';

export const ESCAPED_CAPABILITY = 'sessions.approveEscaped';

// How Approve is given for a question on this card: null for one with nothing escaped (Approve
// once, as before), 'confirm' (the sheet), 'device' (this device is not one the machine approved),
// 'many' (too many escapes for the question to mark, so Approve is the machine's) or 'machine' (the
// machine takes Approve for it only there).
export function escapedApprove(question, capabilities) {
  const e = question?.kind === 'approval' ? question.escaped : null;
  if (!e) { return null; }
  if (e.approve === 'tooMany') { return 'many'; }
  if (e.approve !== 'confirm' || !e.shown) { return 'machine'; }
  return (capabilities ?? []).includes(ESCAPED_CAPABILITY) ? 'confirm' : 'device';
}

// The message in pieces, each { text, mark }: mark is true for a run the host marked as its own
// escapes. Marks outside the message, overlapping or out of order are not drawn as marks: the text
// is still drawn whole.
// A mark [start, length] inside text, from at on: whole numbers only, measured against what
// remains. A NaN start passed every comparison and set at to NaN, dropping the rest of the text;
// a negative length moved at backwards and drew text twice (#836 review).
function within(text, m, at) {
  if (!Array.isArray(m) || m.length !== 2) { return false; }
  const [start, length] = m;
  return Number.isSafeInteger(start) && Number.isSafeInteger(length) && start >= at && start <= text.length
    && length > 0 && length <= text.length - start;
}

export function segments(message, marks) {
  const text = String(message ?? '');
  const out = [];
  let at = 0;
  for (const m of marks ?? []) {
    if (!within(text, m, at)) { continue; }
    const [start, length] = m;
    if (start > at) { out.push({ text: text.slice(at, start), mark: false }); }
    out.push({ text: text.slice(start, start + length), mark: true });
    at = start + length;
  }
  if (at < text.length) { out.push({ text: text.slice(at), mark: false }); }
  return out;
}

// One run of marks as the escapes it is made of: \u202E, \u{E0041}.
export const tokens = (run) => String(run).match(/\\u\{[0-9A-Fa-f]{1,6}\}|\\u[0-9A-Fa-f]{4}/g) ?? [];

// One escape exactly as the host writes it (ShownText, EscapedApproval.IsEscape): \u and four
// upper-case hex digits, or \u{...} of five or six past the first plane.
const ESCAPE = /^\\u(?:[0-9A-F]{4}|\{[0-9A-F]{5,6}\})$/;

// Whether every mark lies in the message, in order, apart, and covers exactly one escape and
// nothing beside it. One that covered "\u202E; rm -rf work" was drawn as the token alone, while the
// confirmation hashed all of it (#836 review): a message whose marks don't check is held at the
// machine for Approve, as malformed escaped text is.
export function marksExact(message, marks) {
  const text = String(message ?? '');
  let at = 0;
  for (const m of marks ?? []) {
    if (!within(text, m, at)) { return false; }
    const [start, length] = m;
    if (!ESCAPE.test(text.slice(start, start + length))) { return false; }
    at = start + length;
  }
  return true;
}

// A marked run in pieces, every character of it kept: { text, token } for each escape in it and
// for whatever lies between, which is drawn as the plain text it is, never dropped.
export function pieces(run) {
  const text = String(run);
  const out = [];
  let at = 0;
  for (const m of text.matchAll(/\\u\{[0-9A-Fa-f]{1,6}\}|\\u[0-9A-Fa-f]{4}/g)) {
    if (m.index > at) { out.push({ text: text.slice(at, m.index), token: false }); }
    out.push({ text: m[0], token: true });
    at = m.index + m[0].length;
  }
  if (at < text.length) { out.push({ text: text.slice(at), token: false }); }
  return out;
}

// The names of the characters people meet most in these, for a token's accessible name.
const NAMES = {
  0x0008: 'backspace', 0x000B: 'line tabulation', 0x000C: 'form feed', 0x000D: 'carriage return', 0x001B: 'escape',
  0x00A0: 'no-break space', 0x00AD: 'soft hyphen', 0x061C: 'Arabic letter mark', 0x180E: 'Mongolian vowel separator',
  0x200B: 'zero width space', 0x200C: 'zero width non-joiner', 0x200D: 'zero width joiner', 0x200E: 'left-to-right mark',
  0x200F: 'right-to-left mark', 0x2028: 'line separator', 0x2029: 'paragraph separator', 0x202A: 'left-to-right embedding',
  0x202B: 'right-to-left embedding', 0x202C: 'pop directional formatting', 0x202D: 'left-to-right override',
  0x202E: 'right-to-left override', 0x2060: 'word joiner', 0x2066: 'left-to-right isolate', 0x2067: 'right-to-left isolate',
  0x2068: 'first strong isolate', 0x2069: 'pop directional isolate', 0xFEFF: 'zero width no-break space',
};

// A token's accessible name (ui.md §6.4): "hidden character, right-to-left override".
export function describeToken(token) {
  const m = /^\\u\{?([0-9A-Fa-f]+)\}?$/.exec(token);
  if (!m) { return token; }
  const value = parseInt(m[1], 16);
  const code = `U+${m[1].toUpperCase().padStart(4, '0')}`;
  const c = value <= 0x10FFFF ? String.fromCodePoint(value) : '';
  const kind = !hasHidden(c) && hasRightToLeft(c) ? 'right-to-left letter' : 'hidden character';
  return `${kind}, ${NAMES[value] ?? code}`;
}

// The warning's sentences, each only where it applies (ui.md §6.4): what the escapes stand for.
export function warnings(message) {
  const { hidden, rightToLeft } = kinds(message);
  return [
    hidden && 'Boxed codes like \\u000D are characters that run but aren\'t drawn.',
    rightToLeft && 'Boxed codes like \\u202E and right-to-left letters can reorder what you see.',
    'Approving runs everything below, boxed characters included.',
  ].filter(Boolean);
}

// Which kinds of character the host's marks stand for. Bidi controls are hidden characters that
// also reorder, so they count for both sentences.
function kinds(message) {
  let hidden = false;
  let rightToLeft = false;
  for (const { text, mark } of segments(message.message, message.marks)) {
    if (!mark) { continue; }
    for (const t of tokens(text)) {
      const value = parseInt(t.replace(/[\\u{}]/g, ''), 16);
      const c = value <= 0x10FFFF ? String.fromCodePoint(value) : '';
      if (hasHidden(c)) { hidden = true; }
      if (hasRightToLeft(c) || (value >= 0x202A && value <= 0x202E) || (value >= 0x2066 && value <= 0x2069) || value === 0x200F || value === 0x061C) { rightToLeft = true; }
    }
  }
  return { hidden, rightToLeft };
}

// EscapedText.shown: SHA-256, lower-case hex, of the message's UTF-8 as this device has it.
export async function shownDigest(message) {
  const bytes = await globalThis.crypto.subtle.digest('SHA-256', utf8.encode(String(message ?? '')));
  return [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
