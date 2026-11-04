import test from 'node:test';
import assert from 'node:assert/strict';
import { watchMoves } from '../../js/ui/session-card.js';

// A conversation that scrolls (scroller) holding a question (holder) with one answer (button), on
// a page whose frames, resize reads and scroll events the test hands out itself. It doesn't lay
// anything out: where things are is what the test says.
function page() {
  const frames = [];
  const on = {};
  let resized = null;
  globalThis.ResizeObserver = class {
    constructor(cb) { resized = cb; }
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  const win = {
    innerWidth: 1440,
    innerHeight: 900,
    requestAnimationFrame: (fn) => frames.push(fn),
    addEventListener: (type, fn) => { on[type] = fn; },
    removeEventListener() {},
    getComputedStyle: () => ({ position: 'static', overflowAnchor: 'auto' }),
  };
  // No layout to ask whether the browser anchors: as WebKit, it doesn't.
  const doc = { defaultView: win, createElement() { throw new Error('no layout'); } };
  const at = { top: 400, left: 100 };
  const button = { getClientRects: () => [{}], getBoundingClientRect: () => ({ ...at }) };
  const holder = { ownerDocument: doc, children: [button], getBoundingClientRect: () => ({ top: at.top - 20 }) };
  holder.contains = (el) => el === holder || el === button;
  const scroller = { scrollTop: 1923, scrollHeight: 2233, clientHeight: 310, children: [holder], parentElement: null, getBoundingClientRect: () => ({ top: 0 }) };
  scroller.contains = (el) => el === scroller || holder.contains(el);
  holder.parentElement = scroller;
  let moves = 0;
  const watcher = watchMoves({ holder, controls: () => [button], moved: () => { moves += 1; } });
  return {
    at,
    win,
    holder,
    scroller,
    watcher,
    moves: () => moves,
    read: (targets = [scroller]) => resized(targets.map((target) => ({ target }))),
    scroll: () => on.scroll({ target: scroller }),
    frames() { while (frames.length) { frames.shift()(); } },
  };
}

test('a scroll pulled back to its end after the read for the new size is a move, not a scroll (#491)', () => {
  const p = page();
  p.read();
  // The window gets taller: the new size is read before the browser moves the scroll, which is
  // left past its end for that read.
  p.scroller.clientHeight = 430;
  p.read();
  assert.equal(p.moves(), 0);
  // Then the scroll is pulled back to the end, and the answer slides down under the pointer:
  // found then, before another frame, when a click may already be on its way.
  p.scroller.scrollTop = 1803;
  p.at.top += 120;
  p.scroll();
  assert.equal(p.moves(), 1);
  p.frames();
  assert.equal(p.moves(), 1);
});

test('a scroll that isn\'t pulled back moves nothing under a pointer', () => {
  const p = page();
  p.read();
  p.scroller.scrollTop -= 120;
  p.at.top += 120;
  p.scroll();
  p.frames();
  p.read();
  assert.equal(p.moves(), 0);
});

test('an elastic bounce past the end and back (iOS) is the person\'s own scroll, not a pull', () => {
  const p = page();
  p.read();
  // Flicked to the end (scrollTop 1923 is it) and past it, then back, a step at a time.
  for (const [top, by] of [[1958, -35], [1940, 18], [1923, 17]]) {
    p.scroller.scrollTop = top;
    p.at.top += by;
    p.scroll();
  }
  p.frames();
  p.read();
  assert.equal(p.moves(), 0);
});

test('an answer moved sideways with nothing changing height is a move (#491)', () => {
  const p = page();
  p.read();
  // A window narrowed within one layout: the centred conversation, and its answers, move across.
  p.at.left -= 50;
  p.read();
  assert.equal(p.moves(), 1);
});

test('the person\'s own question growing as they type in it is theirs, not a move', () => {
  const p = page();
  p.read();
  p.watcher.own();
  // An error under their field goes: the question, and the conversation holding it, resize.
  p.at.top -= 20;
  p.read([p.holder, p.scroller]);
  assert.equal(p.moves(), 0);
});

test('a window resized as the person types, resizing only what holds their question, is a move (#491)', () => {
  const p = page();
  p.read();
  p.watcher.own();
  p.win.innerHeight = 1020;
  p.scroller.clientHeight = 430;
  p.at.top += 30;
  p.read([p.scroller]);
  assert.equal(p.moves(), 1);
});

test('a window widened as the person types, re-wrapping their question too, is a move (#491)', () => {
  const p = page();
  p.read();
  p.watcher.own();
  p.win.innerWidth = 1500;
  p.at.top -= 18;
  p.at.left += 30;
  p.read([p.holder, p.scroller]);
  assert.equal(p.moves(), 1);
});

test('something holding the question resizing alone as the person types, the window as it was, is a move (#491)', () => {
  const p = page();
  p.read();
  p.watcher.own();
  // The conversation's padding above the question grows: their question didn't resize.
  p.scroller.scrollHeight += 30;
  p.at.top += 30;
  p.read([p.scroller]);
  assert.equal(p.moves(), 1);
});

test('what the conversation shows changing height as the person types, the window as it was, is a move (#491)', () => {
  const p = page();
  p.read();
  p.watcher.own();
  // A bar above the conversation takes some of its height as their question grows: the question
  // resized, and so did what the conversation shows.
  p.scroller.clientHeight = 280;
  p.at.top -= 30;
  p.read([p.holder, p.scroller]);
  assert.equal(p.moves(), 1);
});

test('a scroll pulled back to 0 as what it holds stops overflowing, after the read for the new size, is a move (#491)', () => {
  const p = page();
  p.scroller.scrollTop = 50;
  p.scroller.scrollHeight = 400;
  p.scroller.clientHeight = 350;
  p.read();
  // Taller now, as tall as what it holds: the new size is read before the scroll is pulled back.
  p.scroller.clientHeight = 400;
  p.read();
  assert.equal(p.moves(), 0);
  p.scroller.scrollTop = 0;
  p.at.top += 50;
  p.scroll();
  assert.equal(p.moves(), 1);
});

test('a window resized as the person types, its clamp coming after the read for the new size, is a move at once (#491)', () => {
  const p = page();
  p.read();
  p.watcher.own();
  p.win.innerHeight = 1020;
  p.scroller.clientHeight = 430;
  p.read([p.scroller]);
  assert.equal(p.moves(), 0);
  p.scroller.scrollTop = 1803;
  p.at.top += 120;
  p.scroll();
  assert.equal(p.moves(), 1);
});

test('a move laid out since the last read is found when a click asks, before any frame (#491)', () => {
  const p = page();
  p.read();
  p.watcher.own();
  p.at.top += 40;
  assert.equal(p.watcher.check(), true);
  assert.equal(p.moves(), 1);
  assert.equal(p.watcher.check(), false);
});