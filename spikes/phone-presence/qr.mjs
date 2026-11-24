// Fixed QR version 5-L, byte mode, mask 0: one 108-data/26-parity RS block.
export function qrMatrix(text) {
  const bytes = new TextEncoder().encode(text);
  if (bytes.length > 106) throw new Error('Refused: QR capacity is 106 UTF-8 bytes.');
  const bits = [], push = (value, count) => { for (let i = count - 1; i >= 0; i--) bits.push((value >>> i) & 1); };
  push(4, 4); push(bytes.length, 8);
  for (const b of bytes) push(b, 8);
  push(0, Math.min(4, 864 - bits.length));
  while (bits.length % 8) bits.push(0);
  const data = [];
  for (let i = 0; i < bits.length; i += 8) data.push(bits.slice(i, i + 8).reduce((v, b) => v * 2 + b, 0));
  while (data.length < 108) data.push(data.length % 2 === Math.ceil(bits.length / 8) % 2 ? 0xec : 0x11);
  const multiply = (a, b) => {
    let result = 0;
    for (let i = 0; i < 8; i++) { if (b & 1) result ^= a; b >>>= 1; a <<= 1; if (a & 256) a ^= 0x11d; }
    return result;
  };
  let generator = [1], root = 1;
  for (let i = 0; i < 26; i++) {
    const next = Array(generator.length + 1).fill(0);
    generator.forEach((v, j) => { next[j] ^= v; next[j + 1] ^= multiply(v, root); });
    generator = next; root = multiply(root, 2);
  }
  const parity = Array(26).fill(0);
  for (const b of data) {
    const factor = b ^ parity.shift(); parity.push(0);
    parity.forEach((v, j) => { parity[j] = v ^ multiply(generator[j + 1], factor); });
  }
  const payload = [];
  for (const b of [...data, ...parity]) for (let i = 7; i >= 0; i--) payload.push((b >>> i) & 1);
  const size = 37, matrix = Array.from({ length: size }, () => Array(size).fill(0)), reserved = matrix.map(row => row.slice());
  const put = (x, y, bit) => { if (x >= 0 && y >= 0 && x < size && y < size) { matrix[y][x] = Number(bit); reserved[y][x] = 1; } };
  for (const [cx, cy] of [[3, 3], [33, 3], [3, 33]]) {
    for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
      const distance = Math.max(Math.abs(dx), Math.abs(dy)); put(cx + dx, cy + dy, distance !== 2 && distance !== 4);
    }
  }
  for (let i = 8; i < 29; i++) { put(i, 6, i % 2 === 0); put(6, i, i % 2 === 0); }
  for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) put(30 + dx, 30 + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
  let remainder = 8 << 10;
  for (let i = 14; i >= 10; i--) if (remainder & (1 << i)) remainder ^= 0x537 << (i - 10);
  const format = ((8 << 10) | remainder) ^ 0x5412;
  for (let i = 0; i < 15; i++) {
    const bit = (format >>> i) & 1;
    put(8, i < 6 ? i : i < 8 ? i + 1 : size - 15 + i, bit);
    put(i < 8 ? size - i - 1 : i < 9 ? 15 - i : 14 - i, 8, bit);
  }
  put(8, size - 8, 1);
  let offset = 0, upwards = true;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right--;
    for (let step = 0; step < size; step++) {
      const y = upwards ? size - step - 1 : step;
      for (let x = right; x >= right - 1; x--) if (!reserved[y][x]) matrix[y][x] = (payload[offset++] ?? 0) ^ Number((x + y) % 2 === 0);
    }
    upwards = !upwards;
  }
  return matrix;
}
export function qrSvg(text) {
  const matrix = qrMatrix(text);
  const path = matrix.flatMap((row, y) => row.flatMap((b, x) => b ? [`M${x + 4},${y + 4}h1v1h-1z`] : [])).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 45 45" role="img" aria-label="Scan this QR with your separate phone"><rect width="45" height="45" fill="white"/><path d="${path}" fill="black"/></svg>`;
}
