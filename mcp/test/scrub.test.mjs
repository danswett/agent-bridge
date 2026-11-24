import test from 'node:test';
import assert from 'node:assert/strict';
import { scrubHostText, MAX_HOST_TEXT } from '../src/pipe.mjs';

// #4471: the host's words reach an agent in an error message, so they are bounded and scrubbed (the .NET client's McpHost.Scrub does the same).
test('a long host refusal is bounded and flattened', () => {
  const said = scrubHostText('refused: ' + 'x'.repeat(5000));
  assert.ok(said.length <= MAX_HOST_TEXT + 1);
  assert.ok(said.startsWith('refused: xxx') && said.endsWith('…'));
  assert.equal(scrubHostText('a\r\nb\u0000c\t d‮e​f'), 'a b c d e f');
  assert.equal(scrubHostText('red \u001B[31mtext\u001B[0m ok'), 'red text ok');
});

test('a token-shaped string and the exact token sent are redacted, ordinary words and UUIDs stay', () => {
  assert.equal(scrubHostText('bad credential abat1_AbCdEf0123456789xyz here'), 'bad credential [redacted] here');
  assert.ok(!scrubHostText('bad 0123456789abcdef0123456789abcdef01234567 here').includes('0123456789abcdef'));
  assert.equal(scrubHostText('nobody holds hunter2 here', 'hunter2'), 'nobody holds [redacted] here');
  assert.equal(scrubHostText('no such session 5f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f'), 'no such session 5f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f');
  assert.equal(scrubHostText('not here', 't'), 'not here'); // a short value would redact ordinary letters
});

test('the cut never leaves half of a surrogate pair and a token at the cut is redacted whole', () => {
  const emoji = scrubHostText('a'.repeat(MAX_HOST_TEXT - 1) + '\u{1F600} and more');
  assert.ok(!/[\ud800-\udfff]/.test(emoji));
  const token = scrubHostText('a'.repeat(MAX_HOST_TEXT - 8) + ' abat1_SECRETSECRETSECRET tail');
  assert.ok(!token.includes('abat1_') && !token.includes('SECRET'));
});
