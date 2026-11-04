// session.patch (docs/protocol/v1-sessions.md, Patches): a live change as only what changed, for a
// client that asked for it on sessions.subscribe. Mirrors SessionPatches.Apply in
// src/AgentBridge.Protocol/SessionPatches.cs, reasons word for word.
//
// A patch is applied to the session as last received (the JSON), and the result is read as an
// upsert is, so it gives exactly the session an upsert of that moment would. Nothing of a patch
// that cannot be applied is applied: the client drops it and subscribes again.

export const PatchReasons = Object.freeze({
  notOnHeld: 'the patch is not on the session as held',
  notAMerge: 'the patch is not a merge of the session',
  notByIds: "the patch's delegates are not entries by id",
});

export class PatchError extends Error {}

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const has = (o, k) => Object.hasOwn(o, k);
// Defined, never assigned: a key "__proto__" from the wire is then a field like any other, and
// never the object's prototype.
const put = (o, k, v) => Object.defineProperty(o, k, { value: v, writable: true, enumerable: true, configurable: true });
const strings = (v) => Array.isArray(v) && v.every((s) => typeof s === 'string');
// A lone surrogate, which the C# reader can't decode (Received refuses the whole patch for one):
// applied here, the PWA would show a session the Windows app never had. And nesting no session has,
// deeper than the C# reader reads a whole message (System.Text.Json's 64 levels): walked without
// recursion, so a deep patch is refused rather than overflowing the stack and closing the channel.
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;
const MAX_DEPTH = 64;
const decodable = (root) => {
  const stack = [[root, 0]];
  while (stack.length > 0) {
    const [v, depth] = stack.pop();
    if (typeof v === 'string') {
      if (LONE_SURROGATE.test(v)) { return false; }
    } else if (v !== null && typeof v === 'object') {
      if (depth >= MAX_DEPTH) { return false; }
      for (const [k, x] of Object.entries(v)) {
        if (!Array.isArray(v) && LONE_SURROGATE.test(k)) { return false; }
        stack.push([x, depth + 1]);
      }
    }
  }
  return true;
};

// The patch as the PWA reads it, or null when it cannot be read at all: every part is strict
// (Malformed records), since a patch read past a part would leave the session unlike the host's.
// set and append are taken as they are; what they hold is applyPatch's to judge.
export function readPatch(raw) {
  if (!isObject(raw) || typeof raw.id !== 'string') { return null; }
  if (![raw.id, raw.set, raw.append, raw.subagents?.set, raw.subagents?.remove, raw.subagents?.order].every(decodable)) { return null; }
  const base = raw.base ?? null;
  if (base !== null && !Number.isSafeInteger(base)) { return null; }
  const subagents = raw.subagents ?? null;
  if (subagents !== null) {
    if (!isObject(subagents)) { return null; }
    const set = subagents.set ?? null;
    if (set !== null && !Array.isArray(set)) { return null; }
    for (const k of ['remove', 'order']) {
      if ((subagents[k] ?? null) !== null && !strings(subagents[k])) { return null; }
    }
  }
  const patch = { id: raw.id };
  if (base !== null) { patch.base = base; }
  if (typeof raw.revision === 'number' && Number.isSafeInteger(raw.revision)) { patch.revision = raw.revision; }
  if ((raw.set ?? null) !== null) { patch.set = raw.set; }
  if (subagents !== null) {
    patch.subagents = {};
    for (const k of ['set', 'remove', 'order']) { if ((subagents[k] ?? null) !== null) { patch.subagents[k] = subagents[k]; } }
  }
  if ((raw.append ?? null) !== null) { patch.append = raw.append; }
  return patch;
}

// The session a client holds once it has applied the patch to `held`, the JSON it last received
// of it, from the event numbered heldSeq (held null: it holds none). Held is not changed. Throws
// PatchError, with the reason, for a patch that cannot be applied.
export function applyPatch(held, heldSeq, patch) {
  let target;
  if (patch.base !== undefined) {
    if (!isObject(held) || heldSeq !== patch.base || held.id !== patch.id) { throw new PatchError(PatchReasons.notOnHeld); }
    target = structuredClone(held);
  } else {
    target = {};
  }
  if (patch.set !== undefined) { merge(target, mergeOf(patch.set)); }
  if (patch.subagents !== undefined) { applySubagents(target, patch.subagents); }
  if (patch.append !== undefined) { append(target, mergeOf(patch.append)); }
  // A whole session names itself; one that names another, or none, is not this patch's.
  if (target.id !== patch.id) { throw new PatchError(PatchReasons.notOnHeld); }
  return target;
}

function mergeOf(v) {
  if (!isObject(v)) { throw new PatchError(PatchReasons.notAMerge); }
  return v;
}

// RFC 7396.
function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) { delete target[key]; continue; }
    if (isObject(value)) {
      if (!has(target, key) || !isObject(target[key])) { put(target, key, {}); }
      merge(target[key], value);
      continue;
    }
    put(target, key, structuredClone(value));
  }
}

function append(target, more) {
  for (const [key, value] of Object.entries(more)) {
    const was = has(target, key) ? target[key] : undefined;
    if (isObject(value) && isObject(was)) { append(was, value); continue; }
    if (typeof value === 'string' && typeof was === 'string') { put(target, key, was + value); continue; }
    throw new PatchError(PatchReasons.notAMerge);
  }
}

// The entries' ids in order, when every entry is an object with a text id of its own; else null.
function ids(list) {
  if (!Array.isArray(list)) { return null; }
  const seen = new Set();
  for (const e of list) {
    if (!isObject(e) || typeof e.id !== 'string' || seen.has(e.id)) { return null; }
    seen.add(e.id);
  }
  return [...seen];
}

function applySubagents(target, patch) {
  const current = has(target, 'subagents') ? target.subagents : null;
  if (current !== null && ids(current) === null) { throw new PatchError(PatchReasons.notByIds); }
  let entries = (current ?? []).map((e) => structuredClone(e));
  if (patch.remove) {
    const gone = new Set(patch.remove);
    entries = entries.filter((e) => !gone.has(e.id));
  }
  for (const e of patch.set ?? []) {
    if (!isObject(e) || typeof e.id !== 'string') { throw new PatchError(PatchReasons.notByIds); }
    let entry = entries.find((x) => x.id === e.id);
    if (!entry) {
      entry = {};
      entries.push(entry);
    }
    merge(entry, e);
    if (entry.id !== e.id) { throw new PatchError(PatchReasons.notByIds); }
  }
  if (patch.order) {
    const byId = new Map(entries.map((e) => [e.id, e]));
    if (patch.order.length !== byId.size || new Set(patch.order).size !== patch.order.length || !patch.order.every((id) => byId.has(id))) {
      throw new PatchError(PatchReasons.notByIds);
    }
    entries = patch.order.map((id) => byId.get(id));
  }
  if (entries.length === 0) { delete target.subagents; } else { put(target, 'subagents', entries); }
}
