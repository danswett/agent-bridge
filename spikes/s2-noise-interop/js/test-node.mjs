// Runs the S2 self-test in Node (WebCrypto via globalThis.crypto).
import { readFile } from 'node:fs/promises';
import { vectorChecks, negativeChecks, randomChecks } from '../web/selftest.js';

const v = JSON.parse(await readFile(new URL('../web/vectors.json', import.meta.url), 'utf8'));
const results = [...await vectorChecks(v), ...await negativeChecks(v), ...await randomChecks()];
for (const r of results) { console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.detail ? ` (${r.detail})` : ''}`); }
const failed = results.filter((r) => !r.ok).length;
console.log(`node ${process.version}: ${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
