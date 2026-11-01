// sRGB colours as design/tokens.json writes them (#RRGGBB or rgba(r,g,b,a)), and WCAG 2.x
// contrast between them. A translucent colour is measured as it is seen: composited over the
// surface it sits on.

export function parseColor(value) {
  const s = String(value).trim();
  let m = /^#([0-9a-f]{6})$/i.exec(s);
  if (m) {
    const n = parseInt(m[1], 16);
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255, a: 1 };
  }
  m = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)$/i.exec(s);
  if (m) {
    const [r, g, b] = [m[1], m[2], m[3]].map(Number);
    const a = m[4] === undefined ? 1 : Number(m[4]);
    if ([r, g, b].some((c) => c > 255) || !(a >= 0 && a <= 1)) { throw new Error(`colour out of range: ${value}`); }
    return { r, g, b, a };
  }
  throw new Error(`not a colour tokens.json may use (#RRGGBB or rgba()): ${value}`);
}

// fg drawn over an opaque bg.
export function over(fg, bg) {
  const f = typeof fg === 'string' ? parseColor(fg) : fg;
  const b = typeof bg === 'string' ? parseColor(bg) : bg;
  if (b.a !== 1) { throw new Error('the surface behind a colour must be opaque'); }
  const mix = (x, y) => Math.round(x * f.a + y * (1 - f.a));
  return { r: mix(f.r, b.r), g: mix(f.g, b.g), b: mix(f.b, b.b), a: 1 };
}

function channel(c) {
  const s = c / 255;
  return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
}

export function luminance(color) {
  const c = typeof color === 'string' ? parseColor(color) : color;
  return 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b);
}

// The contrast of fg seen on bg: fg composited first when it is translucent.
export function contrast(fg, bg) {
  const b = typeof bg === 'string' ? parseColor(bg) : bg;
  const f = over(fg, b);
  const [l1, l2] = [luminance(f), luminance(b)].sort((x, y) => y - x);
  return (l1 + 0.05) / (l2 + 0.05);
}

const hex2 = (n) => n.toString(16).padStart(2, '0').toUpperCase();

// XAML's #AARRGGBB, or #RRGGBB when opaque.
export function toXamlColor(value) {
  const c = parseColor(value);
  const rgb = `${hex2(c.r)}${hex2(c.g)}${hex2(c.b)}`;
  return c.a === 1 ? `#${rgb}` : `#${hex2(Math.round(c.a * 255))}${rgb}`;
}

// Upper-case hex, or rgba() as tokens.json spells it, so a diff of the CSS reads like one of
// tokens.json.
export function toCssColor(value) {
  const c = parseColor(value);
  return c.a === 1 ? `#${hex2(c.r)}${hex2(c.g)}${hex2(c.b)}` : `rgba(${c.r}, ${c.g}, ${c.b}, ${c.a})`;
}
