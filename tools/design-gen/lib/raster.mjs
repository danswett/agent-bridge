// Draws the app icon's rasters (design/README.md, "App icon") without a browser or a native
// library: the icon is only rounded squares and circles, filled or stroked, so each is a
// signed distance and every pixel is 4×4 samples of them. Only +, -, ×, ÷ and sqrt, which IEEE
// rounds exactly, so the same pixels come out on every machine and CI can compare them.
//
// Also PNG and ICO, written and read: a staleness check compares pixels, never compressed
// bytes, since zlib's output may differ between Node builds while the image does not.

import { deflateSync, inflateSync } from 'node:zlib';

const SAMPLES = 4;

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

function roundedRectDistance(px, py, s) {
  const qx = Math.abs(px - s.cx) - (s.hw - s.r);
  const qy = Math.abs(py - s.cy) - (s.hh - s.r);
  const ox = qx > 0 ? qx : 0;
  const oy = qy > 0 ? qy : 0;
  return Math.sqrt(ox * ox + oy * oy) + Math.min(Math.max(qx, qy), 0) - s.r;
}

function circleDistance(px, py, s) {
  const dx = px - s.cx;
  const dy = py - s.cy;
  return Math.sqrt(dx * dx + dy * dy) - s.r;
}

// A shape, in the scene's grid units: { kind: 'rect', x, y, width, height, rx } or
// { kind: 'circle', cx, cy, r }, painted { fill } or { stroke, strokeWidth } with an
// { r, g, b } colour.
function prepare(shape, k) {
  const s = shape.kind === 'rect'
    ? { at: roundedRectDistance, cx: (shape.x + shape.width / 2) * k, cy: (shape.y + shape.height / 2) * k, hw: (shape.width / 2) * k, hh: (shape.height / 2) * k, r: Math.min(shape.rx ?? 0, shape.width / 2, shape.height / 2) * k }
    : { at: circleDistance, cx: shape.cx * k, cy: shape.cy * k, r: shape.r * k };
  s.half = shape.stroke ? ((shape.strokeWidth ?? 1) * k) / 2 : null;
  s.color = shape.stroke ?? shape.fill;
  return s;
}

// The scene at size × size pixels: RGBA, 8 bits a channel, not premultiplied.
export function render(scene, size) {
  const k = size / scene.grid;
  const shapes = scene.shapes.map((s) => prepare(s, k));
  const out = Buffer.alloc(size * size * 4);
  const n = SAMPLES * SAMPLES;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let R = 0, G = 0, B = 0, A = 0;
      for (let sy = 0; sy < SAMPLES; sy++) {
        for (let sx = 0; sx < SAMPLES; sx++) {
          const px = x + (sx + 0.5) / SAMPLES;
          const py = y + (sy + 0.5) / SAMPLES;
          // Premultiplied, source over, shape by shape.
          let r = 0, g = 0, b = 0, a = 0;
          for (const s of shapes) {
            let d = s.at(px, py, s);
            if (s.half !== null) { d = Math.abs(d) - s.half; }
            // Coverage of one sample, as the distance to the edge in samples.
            const c = clamp01(0.5 - d * SAMPLES);
            if (c === 0) { continue; }
            r = s.color.r * c + r * (1 - c);
            g = s.color.g * c + g * (1 - c);
            b = s.color.b * c + b * (1 - c);
            a = c + a * (1 - c);
          }
          R += r; G += g; B += b; A += a;
        }
      }
      const i = (y * size + x) * 4;
      if (A === 0) { continue; }
      out[i] = Math.round(R / A);
      out[i + 1] = Math.round(G / A);
      out[i + 2] = Math.round(B / A);
      out[i + 3] = Math.round((A / n) * 255);
    }
  }
  return { width: size, height: size, pixels: out };
}

// --- PNG ---

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) { c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; }
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (const byte of buf) { c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8); }
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
  return Buffer.concat([head, data, crc]);
}

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

function predict(type, a, b, c) {
  switch (type) {
    case 1: return a;
    case 2: return b;
    case 3: return (a + b) >> 1;
    case 4: return paeth(a, b, c);
    default: return 0;
  }
}

function filterRow(type, row, prev, bpp) {
  const out = Buffer.alloc(row.length + 1);
  out[0] = type;
  for (let i = 0; i < row.length; i++) {
    const a = i >= bpp ? row[i - bpp] : 0;
    const b = prev ? prev[i] : 0;
    const c = prev && i >= bpp ? prev[i - bpp] : 0;
    const pred = predict(type, a, b, c);
    out[i + 1] = (row[i] - pred) & 0xff;
  }
  return out;
}

export function encodePng({ width, height, pixels }, { level = 9 } = {}) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bits per channel
  ihdr[9] = 6; // RGBA
  const stride = width * 4;
  const rows = [];
  for (let y = 0; y < height; y++) {
    const row = pixels.subarray(y * stride, (y + 1) * stride);
    const prev = y ? pixels.subarray((y - 1) * stride, y * stride) : null;
    // The filter whose bytes sum smallest as signed values, as libpng chooses.
    let best = null, bestSum = Infinity;
    for (let t = 0; t < 5; t++) {
      const f = filterRow(t, row, prev, 4);
      let sum = 0;
      for (let i = 1; i < f.length; i++) { sum += f[i] < 128 ? f[i] : 256 - f[i]; }
      if (sum < bestSum) { best = f; bestSum = sum; }
    }
    rows.push(best);
  }
  return Buffer.concat([SIGNATURE, chunk('IHDR', ihdr), chunk('IDAT', deflateSync(Buffer.concat(rows), { level })), chunk('IEND', Buffer.alloc(0))]);
}

// Only what encodePng writes, RGBA at 8 bits and not interlaced; anything else is null, which
// the staleness check reports as stale.
export function decodePng(buf) {
  if (buf.length < 8 || !buf.subarray(0, 8).equals(SIGNATURE)) { return null; }
  let at = 8, width = 0, height = 0;
  const idat = [];
  while (at + 8 <= buf.length) {
    const len = buf.readUInt32BE(at);
    const type = buf.toString('ascii', at + 4, at + 8);
    const data = buf.subarray(at + 8, at + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      if (data[8] !== 8 || data[9] !== 6 || data[12] !== 0) { return null; }
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
    at += 12 + len;
  }
  if (!width || !height) { return null; }
  let raw;
  try { raw = inflateSync(Buffer.concat(idat)); } catch { return null; }
  const stride = width * 4;
  if (raw.length !== (stride + 1) * height) { return null; }
  const pixels = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const type = raw[y * (stride + 1)];
    if (type > 4) { return null; }
    const src = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let i = 0; i < stride; i++) {
      const a = i >= 4 ? pixels[y * stride + i - 4] : 0;
      const b = y ? pixels[(y - 1) * stride + i] : 0;
      const c = y && i >= 4 ? pixels[(y - 1) * stride + i - 4] : 0;
      const pred = predict(type, a, b, c);
      pixels[y * stride + i] = (src[i] + pred) & 0xff;
    }
  }
  return { width, height, pixels };
}

// The size of a PNG, from its header alone.
export function pngSize(buf) {
  if (buf.length < 24 || !buf.subarray(0, 8).equals(SIGNATURE) || buf.toString('ascii', 12, 16) !== 'IHDR') { return null; }
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

// --- ICO: a PNG per size, as Windows has read since Vista ---

export function encodeIco(images) {
  const pngs = images.map((img) => encodePng(img));
  const head = Buffer.alloc(6 + 16 * images.length);
  head.writeUInt16LE(0, 0);
  head.writeUInt16LE(1, 2);
  head.writeUInt16LE(images.length, 4);
  let offset = head.length;
  images.forEach((img, i) => {
    const e = 6 + 16 * i;
    // 0 means 256: the field is a byte.
    head[e] = img.width >= 256 ? 0 : img.width;
    head[e + 1] = img.height >= 256 ? 0 : img.height;
    head.writeUInt16LE(1, e + 4);
    head.writeUInt16LE(32, e + 6);
    head.writeUInt32LE(pngs[i].length, e + 8);
    head.writeUInt32LE(offset, e + 12);
    offset += pngs[i].length;
  });
  return Buffer.concat([head, ...pngs]);
}

// Each entry's declared size and its PNG, or null when the file isn't an icon of PNGs.
export function readIco(buf) {
  if (buf.length < 6 || buf.readUInt16LE(0) !== 0 || buf.readUInt16LE(2) !== 1) { return null; }
  const count = buf.readUInt16LE(4);
  if (buf.length < 6 + 16 * count) { return null; }
  const entries = [];
  for (let i = 0; i < count; i++) {
    const e = 6 + 16 * i;
    const size = buf.readUInt32LE(e + 8), offset = buf.readUInt32LE(e + 12);
    if (offset + size > buf.length) { return null; }
    entries.push({ width: buf[e] || 256, height: buf[e + 1] || 256, png: buf.subarray(offset, offset + size) });
  }
  return entries;
}

export function decodeIco(buf) {
  const entries = readIco(buf);
  if (!entries) { return null; }
  const images = entries.map((e) => decodePng(e.png));
  return images.every(Boolean) ? images : null;
}

export const samePixels = (a, b) => Boolean(a && b) && a.width === b.width && a.height === b.height && a.pixels.equals(b.pixels);
