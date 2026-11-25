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

test('redisplay does not mistake hidden response measurements for arriving content', (t) => {
  const pane = new Pane();
  const response = { getBoundingClientRect: () => ({ height: 20 }) };
  pane.children = [response];
  pane.ownerDocument.defaultView.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
  let visible = true;
  const follow = new ScrollFollow(pane, { visible: () => visible });
  t.after(() => follow.dispose());
  const resize = (height) => follow.changed([{ target: response, contentRect: { height } }]);
  resize(20);
  pane.scrollHeight = 1400;
  follow.after(false);
  visible = false;
  resize(0);
  visible = true;
  resize(20);
  assert.equal(pane.scrollTop, 900);
});

test('opening resets a cached reader hold but a later scroll up holds new content again', (t) => {
  const pane = new Pane();
  const follow = new ScrollFollow(pane);
  t.after(() => follow.dispose());
  pane.scrollTop = 500;
  pane.dispatchEvent(new Event('scroll'));
  follow.appScroll(() => {})();
  follow.open();
  assert.equal(pane.scrollTop, 900);
  pane.scrollTop = 820;
  pane.dispatchEvent(new Event('scroll'));
  pane.scrollHeight += 300;
  follow.after();
  assert.equal(pane.scrollTop, 820);
});

test('opening applies deferred turns received while the cached session was not following', (t) => {
  const pane = new Pane();
  let shown;
  const follow = new ScrollFollow(pane, { onFollow: () => { shown = follow.shown.activity.turn; } });
  t.after(() => follow.dispose());
  follow.display({ activity: { turn: 'first' } });
  pane.scrollTop = 500;
  pane.dispatchEvent(new Event('scroll'));
  follow.display({ activity: { turn: 'second' } });
  follow.open();
  assert.equal(shown, 'second');
  assert.equal(follow.deferred, false);
  assert.equal(pane.scrollTop, 900);
});

for (const growth of ['later response', 'same-frame response', 'padding', 'newly drawn history', 'initially hidden content', 'replacement reply']) {
  test(`a disclosure holds its initial measurement, but ${growth} growth is followed`, (t) => {
    const pane = new Pane();
    let height = 30;
    let padding = 10;
    let visible = growth !== 'initially hidden content';
    let response = { getBoundingClientRect: () => ({ height: visible ? height : 0 }) };
    pane.children = growth === 'newly drawn history' ? [] : [response];
    pane.ownerDocument.defaultView.ResizeObserver = class { observe(el, { box } = {}) { el.observedBox = box; } unobserve() {} disconnect() {} };
    const follow = new ScrollFollow(pane, { visible: () => visible });
    t.after(() => follow.dispose());
    pane.scrollHeight += 300;
    visible = true;
    pane.children = [response];
    follow.after(false);
    const grow = () => {
      height += 40;
      pane.scrollHeight += 40;
      if (growth === 'padding') { padding += 40; }
      if (growth === 'replacement reply') { response = { getBoundingClientRect: () => ({ height }) }; pane.children = [response]; }
      follow.after(false);
    };
    const resize = () => { if (growth !== 'padding' || response.observedBox === 'border-box') { follow.changed([{ target: response, contentRect: { height: height - padding }, borderBoxSize: [{ blockSize: height }] }]); } };
    if (growth === 'same-frame response') { grow(); }
    resize();
    assert.equal(pane.scrollTop, growth === 'same-frame response' ? 1240 : 900);
    assert.equal(follow.following, true);
    if (growth !== 'same-frame response') { grow(); resize(); }
    assert.equal(pane.scrollTop, 1240);
  });
}

// "Jump to latest" (ui.md §6.4 H3, #1720): where the view is, what arrived below while it was away, and the jump.
class JumpPane extends Pane {
  scrolled = [];
  scrollTo({ top, behavior }) {
    this.scrolled.push({ top, behavior });
    if (behavior !== 'smooth') { this.scrollTop = top; }
  }
}
const turn = (id) => ({ activity: { turn: id } });
const at = (pane, top) => { pane.scrollTop = top; pane.dispatchEvent(new Event('scroll')); };

test('away is more than a screen above the end, said once for each change, from what it already remembers', (t) => {
  const pane = new JumpPane();
  const seen = [];
  const follow = new ScrollFollow(pane, { onPosition: (state) => seen.push(state) });
  t.after(() => follow.dispose());
  assert.equal(follow.away, false, 'it opens at the end');
  pane.scrollTop = 0;
  follow.remember();
  assert.equal(follow.away, false, 'a layout that has not reached the end yet is not the reader going back');
  pane.scrollTop = 900;
  follow.remember();
  for (const [top, away] of [[850, false], [800, false], [799, true], [300, true], [100, true], [900, false]]) {
    at(pane, top);
    assert.equal(follow.away, away, `at ${top}`);
  }
  assert.deepEqual(seen, [{ away: true, count: 0 }, { away: false, count: 0 }], 'one report for each change, not each scroll');
  at(pane, 300);
  Object.assign(pane, { scrollHeight: 0, clientHeight: 0 });
  follow.remember();
  assert.equal(seen.length, 3, 'a view with no height (hidden) says nothing: it cannot hide the pill of a reader still back');
});

test('turns arriving while away are counted; the live turn, and turns at the end, are not; reading to the end clears it', (t) => {
  const pane = new JumpPane();
  const seen = [];
  const follow = new ScrollFollow(pane, { onPosition: (state) => seen.push(state) });
  t.after(() => follow.dispose());
  follow.display(turn('t1'));
  at(pane, 300);
  for (const id of ['t2', 't3', 't3']) { follow.display(turn(id)); }
  assert.equal(follow.newCount, 2, 'the same turn twice is one');
  follow.display({ activity: { turn: 't1' }, extra: 'the live words of the turn on screen' });
  assert.equal(follow.newCount, 2, 'the turn on screen growing is not new');
  at(pane, 900);
  assert.equal(follow.newCount, 0);
  assert.deepEqual(seen.at(-1), { away: false, count: 0 });
});

test('jump goes to the end and draws what waits there; a smooth one scrolls once, and a wheel takes it over', (t) => {
  const pane = new JumpPane();
  const input = new EventTarget();
  let drawn = 0;
  const follow = new ScrollFollow(pane, { input, onFollow: () => { drawn += 1; pane.scrollHeight += 200; } });
  t.after(() => follow.dispose());
  follow.display(turn('t1'));
  at(pane, 300);
  follow.display(turn('t2'));
  follow.jump('auto');
  assert.equal(drawn, 1, 'the waiting turn is drawn');
  assert.equal(pane.scrollTop, pane.scrollHeight - pane.clientHeight, 'at the end, past the turn it drew');
  assert.deepEqual([follow.following, follow.away, follow.newCount], [true, false, 0]);
  at(pane, 300);
  follow.jump('smooth');
  assert.deepEqual([pane.scrolled.at(-1), pane.scrollTop], [{ top: 1100, behavior: 'smooth' }, 300], 'the browser animates it; nothing is set at once');
  input.dispatchEvent(new Event('wheel'));
  assert.equal(follow.following, false, 'a touch that stops it, with no scroll after it, is the person reading');
  at(pane, 500);
  assert.deepEqual([follow.following, follow.away], [false, true], 'the person is reading again');
});

test('a smooth jump ends following the end: landing with or without scrollend, and when the end moved', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  for (const [name, scrollend, grew] of [['landing', false, false], ['landing with scrollend', true, false], ['the end moved', true, true], ['the end moved, no scrollend', false, true]]) {
    const pane = new JumpPane();
    const follow = new ScrollFollow(pane);
    t.after(() => follow.dispose());
    at(pane, 300);
    follow.jump('smooth');
    at(pane, 600);
    if (grew) { pane.scrollHeight += 300; }
    at(pane, 900);
    if (scrollend) { pane.dispatchEvent(new Event('scrollend')); } else if (grew) { t.mock.timers.tick(1500); }
    assert.equal(pane.scrollTop, pane.scrollHeight - pane.clientHeight, `${name}: at the end there is now`);
    pane.scrollHeight += 120;
    follow.after();
    assert.deepEqual([follow.away, pane.scrollTop], [false, pane.scrollHeight - pane.clientHeight], `${name}: and following it`);
  }
});
