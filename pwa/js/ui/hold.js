// Why a session's machine can't be acted on now, as the views under a card take it (#269, #950).
// Either the words that say why (text), drawn as a note with the controls disabled; or, while the
// app is catching up after a return (return-grace.js), a quiet hold: updating, with drawn once the
// hold has lasted long enough to show. A quiet hold says nothing and disables nothing: its controls
// are aria-disabled at once, drawn disabled when drawn, and a press says "Updating…" under them.
// offline: the machine is away, and its line takes the place of the usual actions (ui.md rule 8).
export const UPDATING = 'Updating\u2026';

export function holdOf(held) {
  if (!held) { return { text: null, updating: false, drawn: false, offline: false }; }
  if (typeof held === 'string') { return { text: held, updating: false, drawn: false, offline: false }; }
  return { text: held.text ?? null, updating: held.updating === true, drawn: held.drawn === true, offline: held.offline === true };
}

// One value for what is held, to tell whether it changed.
export function holdKey(held) {
  const h = holdOf(held);
  return h.updating ? `updating:${h.drawn}` : `${h.offline ? 'offline:' : ''}${h.text ?? ''}`;
}
