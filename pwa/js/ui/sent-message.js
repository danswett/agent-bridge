import { h } from './dom.js';
import { sentAttachments } from '../sent-attachments.js';

const CONTENT = new WeakMap();

export function sentMessage(message, cls) {
  const plain = h('p', { class: 'plain' });
  const files = h('ul', { class: 'sent-attachments', 'aria-label': 'Attachments' });
  const bubble = h('div', { class: cls }, files, plain);
  CONTENT.set(bubble, { plain, files, raw: null, text: '' });
  setSentMessage(bubble, message);
  return bubble;
}

export function setSentMessage(bubble, message) {
  const state = CONTENT.get(bubble);
  if (state.raw !== message) {
    const { text, files } = sentAttachments(message);
    state.raw = message;
    state.text = text;
    state.files.hidden = !files.length;
    state.files.replaceChildren(...files.map((name) => {
      const cut = name.length > 32 ? name.length - 16 : name.length;
      return h('li', { class: 'sent-attachment', title: name },
        h('span', { class: 'sent-file-start' }, name.slice(0, cut)),
        cut < name.length ? h('span', { class: 'sent-file-end' }, name.slice(cut)) : null);
    }));
  }
  state.plain.textContent = state.text;
  state.plain.hidden = !state.text;
}
