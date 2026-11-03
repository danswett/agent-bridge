import { readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';

const profile = JSON.parse(readFileSync(new URL('../../docs/protocol/connector-read-v1.json', import.meta.url), 'utf8'));
const fixture = JSON.parse(readFileSync(new URL(profile.conversationVectors, new URL('../../docs/protocol/', import.meta.url)), 'utf8'));
class MockInputError extends Error {}
const same = (a, b) => JSON.stringify(sort(a)) === JSON.stringify(sort(b));
function sort(value) {
  if (Array.isArray(value)) return value.map(sort);
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, sort(value[key])]));
  }
  return value;
}

export function createMockHost() {
  let sequence = 0;
  const ids = new Set();
  return request => {
    if (!request || !Number.isSafeInteger(request.id) || request.id < 1 || typeof request.op !== 'string') {
      throw new MockInputError('mock request needs a positive safe-integer id and an op');
    }
    if (ids.has(request.id)) throw new MockInputError('mock request id was already used on this channel');
    if (ids.size >= 10000) throw new MockInputError('mock request limit reached; start a new mock channel');
    ids.add(request.id);
    if (!Object.hasOwn(profile.operations, request.op)) {
      return [{ re: request.id, ok: false, error: 'operation is outside the read-only mock profile' }];
    }
    const entry = fixture.cases.find(item => item.request.op === request.op && same(item.request.data, request.data ?? {}));
    if (!entry) return [{ re: request.id, ok: false, error: 'request has no recorded mock fixture' }];
    return [
      { re: request.id, ...structuredClone(entry.response) },
      ...(entry.events ?? []).map(event => ({ ...structuredClone(event), seq: ++sequence })),
    ];
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const reply = createMockHost();
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  try {
    for await (const line of lines) {
      if (Buffer.byteLength(line, 'utf8') > profile.maxMessageBytes) throw new MockInputError('mock request exceeds the message limit');
      for (const message of reply(JSON.parse(line))) process.stdout.write(`${JSON.stringify(message)}\n`);
    }
  } catch (error) {
    if (!(error instanceof SyntaxError || error instanceof MockInputError)) throw error;
    const reason = error instanceof SyntaxError ? 'input is not valid JSON' : error.message;
    process.stderr.write(`Invalid mock input: ${reason}; no request content is logged.\n`);
    process.exitCode = 1;
    lines.close();
    process.stdin.destroy();
  }
}
