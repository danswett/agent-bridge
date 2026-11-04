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
  watchMoves({ holder, controls: () => [button], moved: () => { moves += 1; } });
  return {
    at,
    scroller,
    moves: () => moves,
    read: () => resized([{ target: scroller }]),
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
  // Then the scroll is pulled back to the end, and the answer slides down under the pointer.
  p.scroller.scrollTop = 1803;
  p.at.top += 120;
  p.scroll();
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
