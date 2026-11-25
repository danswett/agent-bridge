// Owner request 09:33 PT Nov 24: session search finds a session by its id as well as its name. A query names an id when it is hex digits and dashes
// only (no spaces), has 4 or more hex digits once the dashes are dropped, and is the start of the id, in any case and with or without its
// dashes: the full GUID, or a prefix such as 0287fdf0 or 0287-fdf0. Anything shorter matches names only, so a few letters never flood the list.
export const MIN_ID_DIGITS = 4;

export function matchesSessionId(query, id) {
  if (typeof query !== 'string' || typeof id !== 'string') { return false; }
  const q = query.trim().toLowerCase();
  if (!/^[0-9a-f-]+$/.test(q)) { return false; }
  const digits = q.replaceAll('-', '');
  return digits.length >= MIN_ID_DIGITS && id.toLowerCase().replaceAll('-', '').startsWith(digits);
}