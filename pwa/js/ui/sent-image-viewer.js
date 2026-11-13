import { h } from './dom.js';
import { describeSize } from '../attachments.js';

export function openSentImage(images, index, returnTo) {
  const picture = h('img', { draggable: 'false' });
  const title = h('span');
  const counter = h('span');
  const closeButton = h('button', { type: 'button', class: 'button', 'aria-label': 'Close image' }, 'Close');
  const previous = h('button', { type: 'button', class: 'button', 'aria-label': 'Previous image' }, 'Previous');
  const next = h('button', { type: 'button', class: 'button', 'aria-label': 'Next image' }, 'Next');
  const dialog = h('dialog', { class: 'sent-image-viewer', 'aria-label': 'Image viewer' },
    h('header', null, closeButton, title, counter), picture, h('footer', null, previous, next));
  let zoom = 1, start = null, distance = 0;
  const points = new Map();
  const scale = (value) => { zoom = Math.max(1, Math.min(4, value)); picture.style.transform = `scale(${zoom})`; };
  const show = () => {
    const image = images[index];
    picture.src = image.url; picture.alt = image.name;
    title.textContent = `${image.name}, ${describeSize(image.size)}`;
    counter.textContent = `${index + 1} of ${images.length}`;
    previous.disabled = index === 0; next.disabled = index === images.length - 1; scale(1);
  };
  const move = (step) => { index = Math.max(0, Math.min(images.length - 1, index + step)); show(); };
  const close = () => { if (dialog.open) { dialog.close(); } dialog.remove(); if (returnTo.isConnected) { returnTo.focus({ preventScroll: true }); } };
  closeButton.onclick = close; previous.onclick = () => move(-1); next.onclick = () => move(1);
  dialog.addEventListener('cancel', (event) => { event.preventDefault(); close(); });
  dialog.addEventListener('click', (event) => { if (event.target === dialog) { close(); } });
  dialog.addEventListener('keydown', (event) => { if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); move(event.key === 'ArrowLeft' ? -1 : 1); } });
  picture.addEventListener('dblclick', () => scale(zoom === 1 ? 2 : 1));
  const gap = () => { const [a, b] = [...points.values()]; return b ? Math.hypot(a.x - b.x, a.y - b.y) : 0; };
  picture.addEventListener('pointerdown', (event) => {
    picture.setPointerCapture(event.pointerId);
    points.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (points.size === 1) { start = { x: event.clientX, y: event.clientY }; } else { start = null; distance = gap(); }
  });
  picture.addEventListener('pointermove', (event) => {
    if (!points.has(event.pointerId)) { return; }
    points.set(event.pointerId, { x: event.clientX, y: event.clientY });
    const nextGap = gap();
    if (distance && nextGap) { scale(zoom * nextGap / distance); } distance = nextGap;
  });
  const end = (event) => {
    if (start && zoom === 1) {
      const dx = event.clientX - start.x, dy = event.clientY - start.y;
      if (dy > 80 && Math.abs(dy) > Math.abs(dx)) { close(); } else if (Math.abs(dx) > 60) { move(dx < 0 ? 1 : -1); }
    }
    points.delete(event.pointerId); start = null; distance = gap();
  };
  picture.addEventListener('pointerup', end);
  picture.addEventListener('pointercancel', () => { points.clear(); start = null; distance = 0; });
  show(); document.body.append(dialog); dialog.showModal(); closeButton.focus();
  return close;
}
