import { h } from './dom.js';
import { sentAttachments } from '../sent-attachments.js';
import { sentImages } from '../sent-images.js';
import { describeSize } from '../attachments.js';
import { openSentImage } from './sent-image-viewer.js';

const CONTENT = new WeakMap();
const ACTIVE = new Map();
let observer;
function release(bubble) {
  const state = ACTIVE.get(bubble);
  if (!state) { return; }
  state.close?.();
  for (const url of state.urls) { URL.revokeObjectURL(url); }
  ACTIVE.delete(bubble);
}
sentImages.addEventListener('change', () => {
  for (const list of globalThis.document?.querySelectorAll('.sent-attachments') ?? []) {
    const bubble = list.parentElement, state = CONTENT.get(bubble);
    if (state) { setSentMessage(bubble, state.raw, true); }
  }
});

export function sentMessage(message, cls) {
  const plain = h('p', { class: 'plain' });
  const files = h('ul', { class: 'sent-attachments', 'aria-label': 'Attachments' });
  const bubble = h('div', { class: cls }, files, plain);
  CONTENT.set(bubble, { plain, files, raw: null, text: '' });
  setSentMessage(bubble, message);
  return bubble;
}

export function setSentMessage(bubble, message, refresh = false) {
  const state = CONTENT.get(bubble);
  if (state.raw !== message || refresh) {
    release(bubble);
    const { text, files } = sentAttachments(message, true);
    const generation = state.generation = {};
    state.raw = message;
    state.text = text;
    state.files.hidden = !files.length;
    state.files.classList.remove('sent-image-grid');
    state.files.replaceChildren(...files.map(({ name }) => {
      const cut = name.length > 32 ? name.length - 16 : name.length;
      return h('li', { class: 'sent-attachment', title: name },
        h('span', { class: 'sent-file-start' }, name.slice(0, cut)),
        cut < name.length ? h('span', { class: 'sent-file-end' }, name.slice(cut)) : null);
    }));
    if (files.length) {
      void Promise.all(files.map(({ ref }) => sentImages.get(ref))).then((entries) => {
        if (state.generation !== generation || !bubble.isConnected || !entries.some(Boolean)) { return; }
        const resources = { urls: [], close: null }, images = [];
        ACTIVE.set(bubble, resources);
        observer ??= new MutationObserver(() => { for (const node of ACTIVE.keys()) { if (!node.isConnected) { release(node); } } });
        observer.observe(document.body, { childList: true, subtree: true });
        entries.forEach((entry, i) => {
          if (!entry) { return; }
          const name = files[i].name, chip = state.files.children[i];
          const url = URL.createObjectURL(new Blob([entry.bytes], { type: entry.type }));
          resources.urls.push(url);
          const image = { url, name, size: entry.bytes.length };
          images.push(image);
          const picture = h('img', { src: url, alt: name, width: entry.width, height: entry.height });
          const button = h('button', { type: 'button', class: 'sent-image', 'aria-label': `Image, ${name}, ${describeSize(image.size)}. Opens larger.` }, picture);
          button.onclick = () => { resources.close?.(); resources.close = openSentImage(images, images.indexOf(image), button); };
          picture.addEventListener('error', () => { images.splice(images.indexOf(image), 1); button.replaceWith(document.createTextNode(name)); }, { once: true });
          if (images.length > 4) { chip.hidden = true; }
          if (images.length === 4 && entries.filter(Boolean).length > 4) { button.replaceChildren(`+${entries.filter(Boolean).length - 3}`); }
          chip.classList.add('sent-image-tile'); chip.replaceChildren(button);
        });
        state.files.classList.toggle('sent-image-grid', images.length > 1);
      });
    }
  }
  state.plain.textContent = state.text;
  state.plain.hidden = !state.text;
}
