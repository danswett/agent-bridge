// The phone header's fold (docs/design/ui.md §6.2): the thresholds, the hysteresis, what counts
// as the person's scrolling, and what must stay visible. Pure logic; the DOM is in the specs.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BAR, DOWN, UP, TITLE_IN, clamp, Fold, parts, landscapeFold, focusHidden } from '../../js/ui/collapse-header.js';

const ROOM = 209; // the at-rest height less the 48 bar, at 390 x 844
const MAX = 5000;

// Scrolls the fold to `top`, as one scroll event.
const to = (f, top, extra = {}) => f.scroll({ top, max: MAX, room: ROOM, ...extra });
const walk = (f, from, to_, step = 8, extra = {}) => {
  const seen = [];
  for (let y = from; from < to_ ? y < to_ : y > to_; y += from < to_ ? step : -step) {
    const next = from < to_ ? Math.min(y + step, to_) : Math.max(y - step, to_);
    seen.push(to(f, next, extra).action);
  }
  return seen.filter(Boolean);
};
const at = (top) => { const f = new Fold(); f.sync(top, MAX); return f; };

test('the numbers are the spec\u2019s: a 48 bar, 32 down, 64 up, title after 40', () => {
  assert.deepEqual([BAR, DOWN, UP, TITLE_IN], [48, 32, 64, 40]);
});

test('overscroll is clamped: above the top is 0, below the end is the end', () => {
  assert.equal(clamp(-30, MAX), 0);
  assert.equal(clamp(MAX + 40, MAX), MAX);
  assert.equal(clamp(120, MAX), 120);
  assert.equal(clamp(10, -5), 0);
});

test('a rubber-band past the top or the end is not a scroll', () => {
  const f = at(0);
  assert.equal(to(f, -40).action, null);
  assert.equal(f.last, 0);
  const g = at(MAX);
  g.returned = true;
  assert.equal(to(g, MAX + 60).action, null);
  assert.equal(g.returned, true);
});

test('near the top the header tracks the scroll 1:1, to the bar and no further', () => {
  const f = new Fold();
  for (const y of [0, 1, 37, 100, 208, 209, 400]) {
    to(f, y);
    assert.equal(f.hidden({ room: ROOM }), Math.min(y, ROOM), `at ${y}`);
  }
});

test('its parts: nothing at rest, fill and title once 40 are hidden, compact at the room', () => {
  assert.deepEqual(parts(0, ROOM), { solid: false, titled: false, state: 'rest' });
  assert.deepEqual(parts(39, ROOM), { solid: false, titled: false, state: 'riding' });
  assert.deepEqual(parts(TITLE_IN, ROOM), { solid: true, titled: true, state: 'riding' });
  assert.deepEqual(parts(ROOM, ROOM), { solid: true, titled: true, state: 'compact' });
});

test('past the top range, 64 up brings the header back and 32 down hides it', () => {
  const f = at(600);
  assert.equal(to(f, 600 - 63).action, null);
  assert.equal(to(f, 600 - 64).action, 'show');
  assert.equal(f.returned, true);
  assert.equal(f.hidden({ room: ROOM }), 0);
  assert.equal(to(f, 600 - 64 + 31).action, null);
  assert.equal(to(f, 600 - 64 + 32).action, 'hide');
  assert.equal(f.returned, false);
});

test('the count restarts whenever the direction changes', () => {
  const f = at(600);
  to(f, 540);
  to(f, 550);
  assert.equal(to(f, 490).action, null, '60 up, 10 down, 60 up is not 64');
  assert.equal(to(f, 485).action, 'show', 'but 65 up in a row is');
  const g = at(900);
  g.returned = true;
  to(g, 920);
  to(g, 910);
  assert.equal(to(g, 940).action, null, '20 down, 10 up, 30 down is not 32');
  assert.equal(to(g, 942).action, 'hide');
});

test('it comes back only once the reader is below the top range', () => {
  const f = at(250);
  assert.deepEqual(walk(f, 250, 150), [], '64 up ends inside the top range: it rides there instead');
  assert.equal(f.hidden({ room: ROOM }), 150);
});

test('it never comes back over the focused control', () => {
  const f = at(600);
  assert.equal(to(f, 520, { coversFocus: true }).action, null);
  assert.equal(to(f, 500, { coversFocus: false }).action, 'show');
});

test('focus in the header keeps it, and keeps it from hiding', () => {
  const f = at(600);
  to(f, 590, { focusInside: true });
  assert.equal(f.returned, true);
  assert.equal(f.hidden({ room: ROOM, focusInside: true }), 0);
  assert.equal(to(f, 700, { focusInside: true }).action, null);
  assert.equal(f.hidden({ room: ROOM, focusInside: true }), 0);
});

test('at the very top the header is the full one again, whatever it was', () => {
  const f = at(600);
  f.returned = true;
  f.held = 100;
  to(f, 0);
  assert.equal(f.returned, false);
  assert.equal(f.held, null);
  assert.equal(f.hidden({ room: ROOM }), 0);
});

test('no movement is not a scroll, so the count is kept', () => {
  const f = at(600);
  to(f, 560);
  assert.equal(to(f, 560).action, null);
  assert.equal(to(f, 536).action, 'show');
});

test('an app-made scroll leaves the header as it was, and the reader carries on from there', () => {
  const f = at(0);
  const wasFull = f.hold(0);
  assert.equal(wasFull, true);
  f.sync(900, MAX);
  f.release(wasFull);
  assert.equal(f.returned, true, 'it was full, and stays full below the top');
  assert.equal(f.hidden({ room: ROOM }), 0);

  const g = at(120);
  const full = g.hold(g.hidden({ room: ROOM }));
  assert.equal(full, false);
  assert.equal(g.held, 120);
  g.sync(3000, MAX);
  g.release(full);
  assert.equal(g.hidden({ room: ROOM }), 120, 'part-way slid stays part-way');
  to(g, 3030);
  assert.equal(g.hidden({ room: ROOM }), 150, 'the reader carries on from where the app left it');
  to(g, 3100);
  assert.equal(g.held, null);
  assert.equal(g.hidden({ room: ROOM }), ROOM);
});

test('an app-made scroll to the top of a full header leaves it full and at rest', () => {
  const f = at(400);
  const wasFull = f.hold(0);
  f.sync(0, MAX);
  f.release(wasFull);
  assert.equal(f.returned, false);
  assert.equal(parts(f.hidden({ room: ROOM }), ROOM).state, 'rest');
});

test('content growing below changes nothing: a resync is not a scroll', () => {
  const f = at(600);
  to(f, 560);
  f.sync(560, MAX + 900);
  assert.equal(f.run, 40);
  assert.equal(f.hidden({ room: ROOM }), ROOM);
});

test('a header fading out is gone at once when the reader comes back into the top range', () => {
  const f = at(400);
  f.returned = true;
  const r = to(f, 100, { fading: true });
  assert.equal(r.cancelFade, true);
  assert.equal(f.returned, false);
});

test('a screen shorter than twice the header folds it for an app-made scroll', () => {
  assert.equal(landscapeFold({ stageH: 390, fullH: 257, hidden: 0, room: ROOM }), true);
  assert.equal(landscapeFold({ stageH: 844, fullH: 257, hidden: 0, room: ROOM }), false);
  assert.equal(landscapeFold({ stageH: 390, fullH: 257, hidden: ROOM, room: ROOM }), false, 'already folded');
});

test('focused content is hidden when the header covers it or it is off the stage', () => {
  const stage = { top: 0, bottom: 800 };
  assert.equal(focusHidden({ top: 300, bottom: 340 }, { stage, covered: 257 }), false);
  assert.equal(focusHidden({ top: 200, bottom: 240 }, { stage, covered: 257 }), true);
  assert.equal(focusHidden({ top: 770, bottom: 830 }, { stage, covered: 257 }), true);
});

test('after a resize or rotation the same check finds focus the header now covers', () => {
  const stage = { top: 0, bottom: 800 };
  const control = { top: 280, bottom: 320 };
  assert.equal(focusHidden(control, { stage, covered: 257 }), false);
  assert.equal(focusHidden(control, { stage, covered: 340 }), true, 'the header grew over it');
  assert.equal(focusHidden(control, { stage: { top: 0, bottom: 300 }, covered: 257 }), true, 'the stage shrank under it');
});
