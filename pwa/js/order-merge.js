// The merge for Your order and for read markers (docs/design/session-order-roaming.md §5, #1583).
// Pure: no UI, no storage, no network, no clock. Relay time is never used.
//
// A register is { value, counter, deviceTag } or a tombstone { tombstone: true, counter, deviceTag }.
// The higher counter wins; on equal counters the higher deviceTag wins, compared as bytes. A
// deviceTag is a Uint8Array or a hex string; it is kept as lowercase hex, whose string order is
// its byte order. If two registers still tie (the same device wrote twice at one counter, which a
// correct device never does), a tombstone beats a value and then the value's JSON decides, so the
// merge gives one answer in either order.
//
// An order record is { rows, membership, groups }, each a map of registers:
//   rows[rowKey]         value { key, pinned }: the fractional key places the row, so two inserts
//                        between the same neighbours order by (key, deviceTag) and never collide.
//   membership[rowKey]   value is a group id, or null for Sessions.
//   groups[groupId]      value is the group's name; a tombstone deletes the group.
// A marker record is a map of session key to the highest turn read, which never moves backward.
// Merges return new objects and never change their inputs. Records arrive from other devices, so
// maps are prototype-free and anything malformed is dropped rather than trusted.

const bare = () => Object.create(null);
const own = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
const cmp = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

function tagOf(t) {
  if (t instanceof Uint8Array) { return Array.from(t, (b) => b.toString(16).padStart(2, '0')).join(''); }
  return typeof t === 'string' && /^(?:[0-9a-fA-F]{2})*$/.test(t) ? t.toLowerCase() : null;
}

function cleanRegister(r) {
  if (r === null || typeof r !== 'object') { return null; }
  const deviceTag = tagOf(r.deviceTag);
  if (deviceTag === null || !Number.isSafeInteger(r.counter) || r.counter < 0) { return null; }
  if (r.tombstone === true) { return { tombstone: true, counter: r.counter, deviceTag }; }
  return { value: r.value, counter: r.counter, deviceTag };
}

// Positive when a wins, negative when b wins, zero only for identical registers.
export function compareRegisters(a, b) {
  return cmp(a.counter, b.counter)
    || cmp(a.deviceTag, b.deviceTag)
    || cmp(a.tombstone === true ? 1 : 0, b.tombstone === true ? 1 : 0)
    || cmp(JSON.stringify(a.value ?? null), JSON.stringify(b.value ?? null));
}

export function mergeRegisters(a, b) {
  const x = cleanRegister(a);
  const y = cleanRegister(b);
  if (x === null || y === null) { return x ?? y; }
  return compareRegisters(x, y) >= 0 ? x : y;
}

function mergeMaps(a, b) {
  const out = bare();
  for (const side of [a, b]) {
    if (side === null || typeof side !== 'object') { continue; }
    for (const k of Object.keys(side)) {
      const r = mergeRegisters(own(out, k) ? out[k] : null, side[k]);
      if (r !== null) { out[k] = r; }
    }
  }
  return out;
}

export function emptyOrder() {
  return { rows: bare(), membership: bare(), groups: bare() };
}

export function mergeOrder(a, b) {
  return {
    rows: mergeMaps(a?.rows, b?.rows),
    membership: mergeMaps(a?.membership, b?.membership),
    groups: mergeMaps(a?.groups, b?.groups),
  };
}

// A device writes with a counter above every counter it has seen.
export function nextCounter(order) {
  let max = 0;
  for (const map of [order?.rows, order?.membership, order?.groups]) {
    for (const k of Object.keys(map ?? {})) { max = Math.max(max, map[k]?.counter ?? 0); }
  }
  return max + 1;
}

// The group a row shows in: a deleted or unknown group falls back to Sessions (null).
export function groupOf(order, rowKey) {
  const id = own(order.membership, rowKey) ? order.membership[rowKey].value : null;
  if (typeof id !== 'string' || !own(order.groups, id)) { return null; }
  return order.groups[id].tombstone === true ? null : id;
}

// Live rows in order: by (key, deviceTag), then row key so two equal rows still sort one way.
export function orderedRows(order) {
  return Object.keys(order.rows)
    .filter((k) => order.rows[k].tombstone !== true)
    .map((k) => ({
      row: k,
      key: String(order.rows[k].value?.key ?? ''),
      pinned: order.rows[k].value?.pinned === true,
      deviceTag: order.rows[k].deviceTag,
      group: groupOf(order, k),
    }))
    .sort((x, y) => cmp(x.key, y.key) || cmp(x.deviceTag, y.deviceTag) || cmp(x.row, y.row));
}

// Read markers: max(turn) per session. A marker for a session with no row is kept, and a turn
// that is not a whole number of at least 0 is ignored.
export function mergeMarkers(a, b) {
  const out = bare();
  for (const side of [a, b]) {
    if (side === null || typeof side !== 'object') { continue; }
    for (const k of Object.keys(side)) {
      const t = side[k];
      if (Number.isSafeInteger(t) && t >= 0 && (!own(out, k) || t > out[k])) { out[k] = t; }
    }
  }
  return out;
}
