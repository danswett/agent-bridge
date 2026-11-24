import { open, link, unlink } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
export function resultText(report) {
  if (!['PASS', 'FAIL'].includes(report.outcome) || !Number.isSafeInteger(report.invalidBlobs) || report.invalidBlobs < 0) throw new Error('Refused: invalid result.');
  const lines = ['# RESULTS-PHONE', '', `Outcome: ${report.outcome} (credential-possession SPIKE only)`, 'Genesis: UNSOLVED. Root acceptance: NOT CLAIMED.', 'Enrollment trust gap: none/self attestation permits a software key on the PC.', 'F1 provenance and F2 PC-independent request display remain UNSOLVED (Engineering).', `owner console confirmation: ${report.outcome === 'PASS' ? 'matched' : 'not accepted'}`, 'Limit: same-PC code able to write to the console could fake confirmation.', `Invalid blobs refused: ${report.invalidBlobs}`];
  if (report.outcome === 'PASS') {
    if (!report.facts?.UP || !report.facts?.UV || !report.phone) throw new Error('Refused: unverified result facts.');
    for (const field of ['aaguid', 'BE', 'BS', 'UP', 'UV', 'fmt', 'signCount', 'credentialIdLength', 'transports']) lines.push(`${field}: ${JSON.stringify(report.facts[field])}`);
    for (const field of ['OS', 'browser', 'path', 'authenticatorAttachment']) lines.push(`${field} (self-reported, not provenance): ${JSON.stringify(report.phone[field])}`);
  } else lines.push('Refused: no current assertion with matching owner console confirmation.');
  return lines.join('\n') + '\n';
}
export async function writeResult(path, report) {
  const text = resultText(report), temp = path + '.tmp-' + randomBytes(16).toString('hex'), handle = await open(temp, 'wx');
  try { await handle.writeFile(text); await handle.close(); await link(temp, path); }
  finally { await handle.close(); await unlink(temp); }
}
