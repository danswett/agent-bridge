// I-JSON (RFC 7493) as the roster requires it (docs/protocol/devices.md, "Strict parsing"): valid
// UTF-8 without a byte-order mark, valid JSON, no duplicate member names, no lone surrogates, at
// most 16 levels deep. JSON.parse keeps the last of two members with one name and accepts lone
// surrogates, so this has its own reader. The checks run in the order the C# library's
// (src/AgentBridge.Roster/StrictJson.cs) do: any syntax error first, then each member in document
// order (its name, whether it repeats one, then its value), so one defect gives one refusal.
import { RosterFormatError, REFUSAL } from './refusal.js';

export const MAX_DEPTH = 16;

const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
const NUMBER = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;

// Nodes: { t: 'object', members: [[name, node]], get(name) }, { t: 'array', items },
// { t: 'string', v }, { t: 'number', raw }, { t: 'true' | 'false' | 'null' }.
export function parseStrict(bytes, what) {
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) throw new RosterFormatError(REFUSAL.json, what);
  let text;
  try { text = decoder.decode(bytes); } catch { throw new RosterFormatError(REFUSAL.json, what); }
  let root;
  try { root = new Reader(text).document(); } catch (e) {
    if (e instanceof SyntaxError) throw new RosterFormatError(REFUSAL.json, what);
    throw e;
  }
  walk(root, what);
  return root;
}

function walk(node, what) {
  if (node.t === 'object') {
    const seen = new Set();
    for (const [name, value] of node.members) {
      if (!isWellFormed(name)) throw new RosterFormatError(REFUSAL.json, what);
      if (seen.has(name)) throw new RosterFormatError(REFUSAL.duplicate, `${what}: ${name}`);
      seen.add(name);
      walk(value, what);
    }
  } else if (node.t === 'array') {
    for (const item of node.items) walk(item, what);
  } else if (node.t === 'string' && !isWellFormed(node.v)) {
    throw new RosterFormatError(REFUSAL.json, what);
  }
}

function isWellFormed(s) {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) {
      const d = i + 1 < s.length ? s.charCodeAt(i + 1) : 0;
      if (d >= 0xdc00 && d <= 0xdfff) { i++; continue; }
      return false;
    }
    if (c >= 0xdc00 && c <= 0xdfff) return false;
  }
  return true;
}

function objectNode(members) {
  const map = new Map();
  for (const [name, value] of members) if (!map.has(name)) map.set(name, value);
  return { t: 'object', members, get: (name) => map.get(name), has: (name) => map.has(name) };
}

class Reader {
  constructor(text) { this.s = text; this.i = 0; }

  document() {
    this.space();
    const value = this.value(0);
    this.space();
    if (this.i !== this.s.length) throw new SyntaxError('trailing content');
    return value;
  }

  space() {
    while (this.i < this.s.length) {
      const c = this.s[this.i];
      if (c === ' ' || c === '\t' || c === '\n' || c === '\r') this.i++;
      else break;
    }
  }

  value(depth) {
    const c = this.s[this.i];
    if (c === '{' || c === '[') {
      if (depth + 1 > MAX_DEPTH) throw new SyntaxError('too deep');
      return c === '{' ? this.object(depth + 1) : this.array(depth + 1);
    }
    if (c === '"') return { t: 'string', v: this.string() };
    if (c === '-' || (c >= '0' && c <= '9')) return { t: 'number', raw: this.number() };
    for (const word of ['true', 'false', 'null']) {
      if (this.s.startsWith(word, this.i)) { this.i += word.length; return { t: word }; }
    }
    throw new SyntaxError('unexpected character');
  }

  object(depth) {
    this.i++;
    const members = [];
    this.space();
    if (this.s[this.i] === '}') { this.i++; return objectNode(members); }
    for (;;) {
      this.space();
      if (this.s[this.i] !== '"') throw new SyntaxError('expected a name');
      const name = this.string();
      this.space();
      if (this.s[this.i++] !== ':') throw new SyntaxError('expected a colon');
      this.space();
      members.push([name, this.value(depth)]);
      this.space();
      const c = this.s[this.i++];
      if (c === '}') return objectNode(members);
      if (c !== ',') throw new SyntaxError('expected a comma');
    }
  }

  array(depth) {
    this.i++;
    const items = [];
    this.space();
    if (this.s[this.i] === ']') { this.i++; return { t: 'array', items }; }
    for (;;) {
      this.space();
      items.push(this.value(depth));
      this.space();
      const c = this.s[this.i++];
      if (c === ']') return { t: 'array', items };
      if (c !== ',') throw new SyntaxError('expected a comma');
    }
  }

  // The decoded string, lone surrogates and all: walk() refuses those after the syntax is known
  // good, as the C# reader does.
  string() {
    this.i++;
    let out = '';
    for (;;) {
      if (this.i >= this.s.length) throw new SyntaxError('unterminated string');
      const c = this.s[this.i++];
      if (c === '"') return out;
      if (c.charCodeAt(0) < 0x20) throw new SyntaxError('control character in a string');
      if (c !== '\\') { out += c; continue; }
      const e = this.s[this.i++];
      const simple = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' }[e];
      if (simple !== undefined) { out += simple; continue; }
      if (e !== 'u') throw new SyntaxError('bad escape');
      const hex = this.s.slice(this.i, this.i + 4);
      if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw new SyntaxError('bad unicode escape');
      out += String.fromCharCode(parseInt(hex, 16));
      this.i += 4;
    }
  }

  number() {
    NUMBER.lastIndex = this.i;
    const m = NUMBER.exec(this.s);
    if (!m) throw new SyntaxError('bad number');
    this.i += m[0].length;
    return m[0];
  }
}

// Members not in `allowed` are refused, the first in document order; then the first of
// `required`, in that order, that is absent.
export function members(node, allowed, required, what) {
  for (const [name] of node.members) {
    if (!allowed.includes(name)) throw new RosterFormatError(REFUSAL.unknown, `${what}.${name}`);
  }
  for (const name of required) {
    if (!node.has(name)) throw new RosterFormatError(REFUSAL.missing, `${what}.${name}`);
  }
}

export function object(node, what) {
  if (node.t !== 'object') throw new RosterFormatError(REFUSAL.type, what);
  return node;
}

export function string(node, what) {
  if (node.t !== 'string') throw new RosterFormatError(REFUSAL.type, what);
  return node.v;
}

export function bool(node, what) {
  if (node.t === 'true') return true;
  if (node.t === 'false') return false;
  throw new RosterFormatError(REFUSAL.type, what);
}

// A non-negative integer below 2³¹ in plain decimal digits; null for any other number.
export function integer(node, what) {
  if (node.t !== 'number') throw new RosterFormatError(REFUSAL.type, what);
  const raw = node.raw;
  if (!/^(?:0|[1-9][0-9]{0,9})$/.test(raw)) return null;
  const value = Number(raw);
  return value < 2 ** 31 ? value : null;
}

export const scalars = (text) => [...text].length;

export const utf8Bytes = (text) => new TextEncoder().encode(text).length;

// A C0 or C1 control, or DEL.
export function hasControl(text) {
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c < 0x20 || (c >= 0x7f && c <= 0x9f)) return true;
  }
  return false;
}
