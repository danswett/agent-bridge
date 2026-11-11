import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ScrollFollow } from '../../js/ui/scroll-follow.js';

class Pane extends EventTarget {
  #top = 900;
  scrollHeight = 1000;
  clientHeight = 100;
  style = {};
  children = [];
  ownerDocument = { defaultView: {} };
  get scrollTop() { return this.#top; }
  set scrollTop(value) { this.#top = Math.max(0, Math.min(value, this.scrollHeight - this.clientHeight)); }
  getClientRects() { return [{}]; }
}

test('a reveal completion without scrollend releases only its own hold', (t) => {
  const pane = new Pane();
  const follow = new ScrollFollow(pane);
  t.after(() => follow.dispose());
  const first = follow.appScroll(() => { pane.scrollTop = 500; });
  const second = follow.appScroll(() => { pane.scrollTop = 600; });
  first();
  pane.scrollTop = 560;
  pane.dispatchEvent(new Event('scroll'));
  assert.equal(follow.following, true, 'an old completion must not release the newer reveal');
  second();
  pane.scrollTop = 520;
  pane.dispatchEvent(new Event('scroll'));
  assert.equal(follow.following, false);
});

for (const type of ['wheel', 'touchmove']) {
  test(`header ${type} takes over a smooth reveal without scrollend and question closure keeps the reader`, (t) => {
    const pane = new Pane();
    const input = new EventTarget();
    let question = true;
    const follow = new ScrollFollow(pane, { input, canFollow: () => !question });
    t.after(() => follow.dispose());
    follow.appScroll(() => { pane.scrollTop = 500; });
    input.dispatchEvent(new Event(type));
    pane.scrollTop = 460;
    pane.dispatchEvent(new Event('scroll'));
    question = false;
    follow.resume();
    follow.after();
    assert.equal(pane.scrollTop, 460, 'closing the question must not jump to the bottom');
    assert.equal(follow.following, false);

    question = true;
    follow.appScroll(() => { pane.scrollTop = 500; });
    input.dispatchEvent(new Event(type));
    pane.scrollTop = 900;
    pane.dispatchEvent(new Event('scroll'));
    assert.equal(follow.following, true, 'header input back to the bottom must re-engage');
  });
}

test('a viewport resize without arriving content does not pin the conversation', (t) => {
  const pane = new Pane();
  const follow = new ScrollFollow(pane);
  t.after(() => follow.dispose());
  pane.clientHeight = 50;
  follow.changed([{ target: pane }]);
  assert.equal(pane.scrollTop, 900);
});

test('same-turn records stay current while reading, before any conversion is deferred', (t) => {
  const pane = new Pane();
  const follow = new ScrollFollow(pane);
  t.after(() => follow.dispose());
  follow.display({ activity: { turn: 'a' } });
  pane.scrollTop = 500;
  follow.read();
  follow.snapshot({ turns: [{ id: 'a', parts: [] }] });
  const recorded = { id: 'a', parts: [{ kind: 'question', id: 'recorded-question' }] };
  assert.equal(follow.snapshot({ turns: [recorded] }).turns[0], recorded);
});

test('initial question positioning is allowed but a completed deliberate reveal stays protected', (t) => {
  const pane = new Pane();
  const follow = new ScrollFollow(pane, { canFollow: (revealed) => !revealed });
  t.after(() => follow.dispose());
  pane.scrollTop = 100;
  follow.remember();
  follow.appScroll(() => {}, false)();
  follow.after();
  assert.equal(pane.scrollTop, 900);
  const done = follow.appScroll(() => { pane.scrollTop = 500; });
  done();
  follow.after();
  assert.equal(pane.scrollTop, 500);
});
