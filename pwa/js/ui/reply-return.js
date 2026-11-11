import { isSendKey, platform } from '../platform.js';
import { PHONE } from './collapse-header.js';

const KEY = 'agent-bridge.app.replyReturn';
const group = globalThis.document?.getElementById('reply-return');
const phone = globalThis.matchMedia?.(PHONE);
const phoneTouch = () => !!phone?.matches && globalThis.navigator?.maxTouchPoints > 0;
const apple = ['macos', 'ios', 'ipados'].includes(platform().platform);
const key = apple ? 'Return' : 'Enter';
const modifier = apple ? 'Cmd' : 'Ctrl';
let sends = false;
function storageError() {
  const note = group?.querySelector('[role="status"]');
  if (note) { note.hidden = false; note.textContent = 'Device storage is unavailable. This choice applies until this page closes.'; }
}
try { sends = globalThis.localStorage?.getItem(KEY) === 'send'; } catch { storageError(); }

export function replyReturnSends(event) {
  return isSendKey(event) || (sends && !phoneTouch() && event.key === 'Enter' && !event.shiftKey && !event.altKey);
}
export function replyReturnHint() {
  return `Send (${sends && !phoneTouch() ? key : `${modifier}+${key}`})`;
}
function draw() {
  if (!group) { return; }
  for (const button of group.querySelectorAll('button')) {
    button.setAttribute('aria-pressed', String((button.dataset.replyReturn === 'send') === sends));
  }
  group.querySelector('#reply-return-newline-help').textContent = `Send with ${modifier}+${key}.`;
  group.querySelector('#reply-return-send-help').textContent = `Shift+${key} makes a new line.`;
  group.querySelector('#reply-return-phone').hidden = !phoneTouch();
}
group?.addEventListener('click', (event) => {
  const button = event.target.closest('button[data-reply-return]');
  if (!button) { return; }
  sends = button.dataset.replyReturn === 'send';
  try {
    if (!globalThis.localStorage) { storageError(); }
    else { globalThis.localStorage.setItem(KEY, sends ? 'send' : 'newline'); }
  } catch { storageError(); }
  draw();
  globalThis.dispatchEvent(new Event('reply-return-change'));
});
phone?.addEventListener('change', () => { draw(); globalThis.dispatchEvent(new Event('reply-return-change')); });
draw();
