// Temporary #2055 reuse of #2045's return diagnostics; removed before landing.
import { readFileSync, writeFileSync, mkdirSync, copyFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { Worker } from 'node:worker_threads';
import { availableParallelism } from 'node:os';

mkdirSync('diag-output', { recursive: true });
function patch(file, before, after) {
  const text = readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
  if (text.split(before).length !== 2) { throw new Error(`Non-unique diagnostic patch: ${file}: ${before}`); }
  writeFileSync(file, text.replace(before, after));
}
const log = (event, fields) => `globalThis.__diag?.('${event}', { ${fields} });`;
patch('js/return-grace.js', '  start() {', `  start() { ${log('grace.start', 'since: this.#since, age: this.age, visible: document.visibilityState')}`);
patch('js/return-grace.js', '  end() {', `  end() { ${log('grace.end', 'since: this.#since, age: this.age')}`);
patch('js/return-grace.js', '  #emit() {', `  #emit() { ${log('grace.emit', 'since: this.#since, age: this.age, drawn: this.drawn')}`);
patch('js/ui/sessions-page.js', '    const grace = hosts.grace;', `    const grace = hosts.grace; ${log('page.notes', 'active: grace?.active, age: grace?.age, ready: this.hosts.connection?.ready')}`);
patch('test/return.spec.mjs', "import { test, expect } from '@playwright/test';", "import { test, expect } from './diag-fixture.mjs';");
patch('test/return.spec.mjs', "test.skip('a machine that goes away", "test('a machine that goes away");
patch('test/return.spec.mjs', "test.skip('focus on the sort control", "test('focus on the sort control");
copyFileSync('../quality/pwa-flake-fixture.mjs', 'test/diag-fixture.mjs');
if (process.argv.includes('--prepare-only')) { process.exit(0); }
const workers = [];
if (process.env.DIAG_LOAD === 'loaded') {
  for (let i = 0; i < Math.max(1, availableParallelism() - 1); i++) {
    workers.push(new Worker('while (true) { Math.sqrt(Math.random()); }', { eval: true }));
  }
}
try {
  const args = ['node_modules/@playwright/test/cli.js', 'test', 'return.spec.mjs', '--project', process.env.DIAG_BROWSER,
    '--grep', 'a machine that goes away|focus on the sort control|a lane entry held',
    '--repeat-each=20', '--workers=2', '--fully-parallel', '--retries=0', '--reporter=list,json'];
  const result = spawnSync(process.execPath, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, PLAYWRIGHT_JSON_OUTPUT_NAME: 'diag-output/2055-report.json' } });
  writeFileSync('diag-output/2055-stdout.log', result.stdout ?? '');
  writeFileSync('diag-output/2055-stderr.log', result.stderr ?? '');
  console.log(JSON.stringify({ args, exit: result.status, error: result.error?.message }));
  if (result.error || result.status === null) { throw result.error ?? new Error('Runner terminated'); }
  if (existsSync('diag-output/page-errors.log')) { throw new Error('Page error invalidates diagnostics: see page-errors.log'); }
  const report = JSON.parse(readFileSync('diag-output/2055-report.json', 'utf8'));
  console.log(JSON.stringify(report.stats));
  if (result.status !== 0 || report.stats.expected !== 60 || report.stats.skipped || report.stats.unexpected || report.stats.flaky) {
    throw new Error('All 60 assertions must pass without retries or skips');
  }
} finally {
  await Promise.all(workers.map((worker) => worker.terminate()));
}
