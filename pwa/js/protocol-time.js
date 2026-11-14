const TIME = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(\.\d{1,7})?(Z|[+-](\d{2}):(\d{2}))$/;

// Check each component as .NET does: Date.parse rolls February 30 into March.
export function isProtocolTime(v) {
  const m = typeof v === 'string' ? TIME.exec(v) : null;
  if (!m) { return false; }
  const [year, month, day, hour, minute, second] = m.slice(1, 7).map(Number);
  const days = [31, year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > days[month - 1] || hour > 23 || minute > 59 || second > 59) { return false; }
  if (m[8] !== 'Z' && (Number(m[10]) > 59 || Number(m[9]) * 60 + Number(m[10]) > 14 * 60)) { return false; }
  return !Number.isNaN(Date.parse(v));
}
