export const utf8 = new TextEncoder();
export const fromUtf8 = new TextDecoder();

export const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
};

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
