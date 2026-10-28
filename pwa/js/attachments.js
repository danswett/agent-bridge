// Files and images sent with a reply (docs/protocol/v1-sessions.md, "Attachments"): what a
// picked or pasted file becomes before it goes, and sending it to the machine in chunks.
//
// Each file goes ahead of its reply, one chunk at a time, each from where the machine says it
// is, so a file interrupted by a reconnect carries on rather than starting over, and one the
// machine already has costs one small request. The limits mirror AttachmentLimits in
// src/AgentBridge.Protocol/Attachments.cs.
import { utf8Bytes } from './answers.js';

export const AttachmentLimits = Object.freeze({
  // One file, as it is sent.
  bytes: 256 * 1024,
  // Bytes in one attachment.chunk: its request stays under 16 KiB, so it never holds an
  // answer or a reply back for long.
  chunk: 11 * 1024,
  perReply: 10,
  name: 255,
  type: 127,
});

// Longest side a shrunk image is drawn at. A phone's screenshot is about 2.5k tall; this keeps
// it legible to the agent while leaving JPEG room to get under the limit.
const SHRINK_EDGE = 2048;
const QUALITIES = [0.85, 0.72, 0.6];
// What Copilot and agent-ha-bridge take as images, by MIME type: anything else is sent as a
// plain file.
const IMAGE_TYPES = { 'image/png': 'png', 'image/jpeg': 'jpg', 'image/gif': 'gif', 'image/webp': 'webp', 'image/heic': 'heic', 'image/heif': 'heif' };

export const describeSize = (n) => (n < 1024 ? `${n} bytes` : n < 1024 * 1024 ? `${Math.round(n / 1024)} KiB` : `${(n / (1024 * 1024)).toFixed(1)} MiB`);

export const isImage = (type) => typeof type === 'string' && type.startsWith('image/');

// The name a file goes by. A pasted image has none, or a generic one, so it is given one the
// host can tell the format from; a name over the limit keeps its extension.
export function attachmentName(name, type) {
  let n = typeof name === 'string' ? name.trim() : '';
  if (!n) { n = IMAGE_TYPES[type] ? `image.${IMAGE_TYPES[type]}` : 'attachment'; }
  if (utf8Bytes(n) <= AttachmentLimits.name) { return n; }
  const dot = n.lastIndexOf('.');
  const ext = dot > 0 && n.length - dot <= 13 ? n.slice(dot) : '';
  let stem = [...n.slice(0, n.length - ext.length)];
  while (stem.length && utf8Bytes(stem.join('') + ext) > AttachmentLimits.name) { stem.pop(); }
  return stem.join('') + ext;
}

const tooBig = (name, size) => new Error(`${name} is ${describeSize(size)}, over the ${describeSize(AttachmentLimits.bytes)} limit for an attachment`);

// A File (or Blob with a name) ready to send: { id, name, type, size, bytes, sha256, image,
// resized }. An image over the limit is redrawn smaller as a JPEG, as a screenshot from a
// phone usually is; anything else over it, or empty, is refused with a reason to show.
export async function prepareAttachment(file, { uuid = () => globalThis.crypto.randomUUID(), shrink = shrinkImage } = {}) {
  const type = typeof file?.type === 'string' ? file.type : '';
  let name = attachmentName(file?.name, type);
  let blob = file;
  let resized = false;
  if (typeof file?.size === 'number' && file.size > AttachmentLimits.bytes) {
    const smaller = isImage(type) ? await shrink(file, AttachmentLimits.bytes) : null;
    if (!smaller) { throw tooBig(name, file.size); }
    blob = smaller;
    resized = true;
    name = attachmentName(`${name.replace(/\.[^.]*$/, '') || 'image'}.jpg`, 'image/jpeg');
  }
  if (typeof blob?.arrayBuffer !== 'function') { throw new Error(`this browser can't read ${name}`); }
  const bytes = new Uint8Array(await blob.arrayBuffer());
  // Read, then checked again: a file can change between being picked and being read.
  if (bytes.length > AttachmentLimits.bytes) { throw tooBig(name, bytes.length); }
  if (!bytes.length) { throw new Error(`${name} is empty`); }
  const sentType = resized ? 'image/jpeg' : type;
  return {
    id: uuid(),
    name,
    type: utf8Bytes(sentType) <= AttachmentLimits.type ? sentType : '',
    size: bytes.length,
    bytes,
    sha256: base64url(new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', bytes))),
    image: isImage(sentType),
    resized,
  };
}

// Redraws an image as a JPEG that fits maxBytes: at most SHRINK_EDGE on its longest side,
// then smaller and lower quality until it fits. Null when this browser can't decode it (HEIC
// outside Safari, say) or it won't fit.
export async function shrinkImage(blob, maxBytes) {
  if (typeof createImageBitmap !== 'function') { return null; }
  let bitmap;
  try { bitmap = await createImageBitmap(blob); } catch { return null; }
  try {
    let scale = Math.min(1, SHRINK_EDGE / Math.max(bitmap.width, bitmap.height, 1));
    for (let round = 0; round < 6; round++) {
      const w = Math.max(1, Math.round(bitmap.width * scale));
      const h = Math.max(1, Math.round(bitmap.height * scale));
      const canvas = typeof OffscreenCanvas === 'function' ? new OffscreenCanvas(w, h) : Object.assign(document.createElement('canvas'), { width: w, height: h });
      const ctx = canvas.getContext('2d');
      if (!ctx) { return null; }
      // JPEG has no transparency; white is what a screenshot's clear parts look like.
      ctx.fillStyle = '#fff';
      ctx.fillRect(0, 0, w, h);
      ctx.drawImage(bitmap, 0, 0, w, h);
      for (const quality of QUALITIES) {
        const out = await jpeg(canvas, quality);
        if (out && out.size > 0 && out.size <= maxBytes) { return out; }
      }
      scale *= 0.7;
    }
    return null;
  } finally {
    bitmap.close?.();
  }
}

function jpeg(canvas, quality) {
  if (typeof canvas.convertToBlob === 'function') { return canvas.convertToBlob({ type: 'image/jpeg', quality }).catch(() => null); }
  return new Promise((resolve) => canvas.toBlob((b) => resolve(b), 'image/jpeg', quality));
}

// Sends one prepared file to the machine, from wherever the machine has got to with it.
// onProgress(bytes the machine has). Rejects with the client's HostError: fromHost when the
// machine turned the file down, otherwise when the channel failed under it.
export async function uploadAttachment(client, file, onProgress = () => {}) {
  let state = await client.attachmentStart({ attachment: file.id, name: file.name, ...(file.type ? { type: file.type } : {}), size: file.size, sha256: file.sha256 });
  onProgress(Math.min(state.received, file.size));
  while (!state.complete) {
    const at = state.received;
    if (at >= file.size) { throw new Error(`the machine has all of ${file.name} but did not accept it`); }
    const end = Math.min(file.size, at + AttachmentLimits.chunk);
    state = await client.attachmentChunk({ attachment: file.id, offset: at, data: base64(file.bytes.subarray(at, end)) });
    if (!state.complete && state.received <= at) { throw new Error(`the machine did not take the next part of ${file.name}`); }
    onProgress(Math.min(state.received, file.size));
  }
}

export function base64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) { s += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); }
  return btoa(s);
}

export const base64url = (bytes) => base64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
