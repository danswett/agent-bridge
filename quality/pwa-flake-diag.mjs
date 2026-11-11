// Throwaway diagnostic branch only: preserve assertions and collect internal ordering.
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
patch('js/ui/session-card.js', '  #wantEarlier() {', `  #wantEarlier() { ${log('history.want', 'id: this.#s.id, held: this.historyStore.get(this.hostId, this.#s.id).turns.length, pending: this.turns.pending, asked: this.#earlierAsked')}`);
patch('js/ui/session-card.js', '  #nearTop() {', `  #nearTop() { ${log('history.nearTop', 'id: this.#s.id, top: this.els.scroll.scrollTop, pending: this.turns.pending')}`);
patch('js/ui/session-card.js', '  #bringQuestion(atEnd) {', `  #bringQuestion(atEnd) { ${log('question.bring', 'id: this.#s.id, atEnd, touches: this.#touches, pressing: this.#pressing, hovering: this.#hovering, selected: this.#selected, top: this.els.scroll.scrollTop')}`);
patch('js/ui/session-card.js', '  #reading() {', `  #reading() { ${log('question.reading', 'id: this.#s?.id, reading: this.#follow.reading, top: this.els.scroll.scrollTop')}`);
patch('js/ui/history-view.js', '  #drawSlice() {', `  #drawSlice() { ${log('history.slice', 'backlog: this.#backlog.length, drawn: this.#drawn.size')}`);
patch('js/history-store.js', '  #emit(e, kind, before = e.turns) {',
  `  #emit(e, kind, before = e.turns) { ${log('store.emit', 'id: e.sessionId, kind, held: e.turns.length, loading: e.loading, before: e.before, phase: e.phase, error: e.error')}`);
patch('js/ui/scroll-follow.js', '  read() {', `  read() { ${log('follow.read', 'following: this.following, app: this.#app, top: this.element.scrollTop, rememberedTop: this.#top, max: this.element.scrollHeight - this.element.clientHeight, rememberedMax: this.#max')}`);
patch('js/ui/scroll-follow.js', '  remember() {', `  remember() { ${log('follow.remember', 'following: this.following, app: this.#app, top: this.element.scrollTop, max: this.element.scrollHeight - this.element.clientHeight')}`);
patch('js/ui/scroll-follow.js', '  appScroll(change, protect = true) {', `  appScroll(change, protect = true) { ${log('follow.appScroll', 'protect, top: this.element.scrollTop')}`);
patch('js/ui/scroll-follow.js', '      hold.active = false;', `      ${log('follow.release', 'top: this.element.scrollTop')} hold.active = false;`);
for (const name of ['return', 'history-perf', 'motion']) {
  patch(`test/${name}.spec.mjs`, "import { test, expect } from '@playwright/test';", "import { test, expect } from './diag-fixture.mjs';");
}
// The two quarantine declarations are enabled only in this disposable run, never on main.
patch('test/return.spec.mjs', "test.skip('a machine that goes away", "test('a machine that goes away");
patch('test/return.spec.mjs', "test.skip('focus on the sort control", "test('focus on the sort control");
copyFileSync('../quality/pwa-flake-fixture.mjs', 'test/diag-fixture.mjs');
patch('test/history-perf.spec.mjs', "  const before = await pane.locator('.history-turn').count();",
  "  const before = await pane.locator('.history-turn').count(); await page.evaluate((before) => window.__diag('history.assertionBaseline', { before, expectedGreaterThan: before + 20 }), before);");
if (process.env.DIAG_CONTROLS === 'true') {
  // Reach the quarantined clock assertion using the same control's current identity.
  patch('test/return.spec.mjs', "page.locator('.sessions-head select').first().focus()", "page.locator('#session-sort').focus()");
  // Model the runner resuming the test only after background fill has reached its cap.
  patch('test/history-perf.spec.mjs', "  await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(12);",
    "  await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(12);\n  await page.waitForFunction(() => document.querySelectorAll('.session-view[data-id=\"long-1\"] .history-turn').length === 104);");
  const title = "test('where the reader is is read when a question comes, though the scroll that took them there has yet to send its event', async ({ page }) => {\n  await openDemo(page, { height: 600 });";
  patch('test/motion.spec.mjs', title, `${title}
  await page.evaluate(() => {
    const client = window.agentBridgeDemo.client;
    const history = client.history.bind(client);
    let release;
    const gate = new Promise((resolve) => { release = resolve; });
    window.__releaseDiagHistory = () => {
      window.__diag('diag.history.release');
      release();
      window.__releaseDiagHistory = null;
    };
    client.history = (id, opts) => {
      const result = history(id, opts);
      return id === '019a1b2c-codex-example' ? gate.then(() => result) : result;
    };
  });`);
  // Land prepared history during the first question reveal, before native scrolling settles.
  patch('js/ui/session-card.js', "      this.#scrollTo(view, scrollBehavior());\n    }\n  }",
    "      this.#scrollTo(view, scrollBehavior());\n    }\n    if (this.#s.question?.id === 'q-race-end') { globalThis.__releaseDiagHistory?.(); }\n  }");
}
if (process.argv.includes('--prepare-only')) { process.exit(0); }
const workers = [];
if (process.env.DIAG_LOAD === 'loaded') {
  for (let i = 0; i < Math.max(1, availableParallelism() - 1); i++) {
    workers.push(new Worker('while (true) { Math.sqrt(Math.random()); }', { eval: true }));
  }
}
const cases = [
  ['smoke', ['return.spec.mjs', 'history-perf.spec.mjs', 'motion.spec.mjs'], 'a lane entry held|a person who scrolled up|where the reader is is read', 1],
  ['1493', ['return.spec.mjs'], 'a machine that goes away|focus on the sort control|a lane entry held', 20],
  ['1936', ['history-perf.spec.mjs'], 'a person who scrolled up', 10],
  ['1938', ['motion.spec.mjs'], 'where the reader is is read', 20],
];
let broken = false;
try {
  for (const [issue, files, grep, repeats] of cases) {
    const report = `diag-output/${issue}-report.json`;
    const args = ['node_modules/@playwright/test/cli.js', 'test', ...files, '--project', process.env.DIAG_BROWSER,
      '--grep', grep, '--repeat-each', String(repeats), '--workers=2', '--fully-parallel', '--retries=0', '--reporter=list,json'];
    const result = spawnSync(process.execPath, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
      env: { ...process.env, PLAYWRIGHT_JSON_OUTPUT_NAME: report } });
    writeFileSync(`diag-output/${issue}-stdout.log`, result.stdout ?? '');
    writeFileSync(`diag-output/${issue}-stderr.log`, result.stderr ?? '');
    console.log(JSON.stringify({ issue, args, exit: result.status, error: result.error?.message }));
    if (result.error || result.status === null) { throw result.error ?? new Error('Runner terminated'); }
    if (existsSync('diag-output/page-errors.log')) { throw new Error('Page error invalidates diagnostics: see page-errors.log'); }
    if (result.status > 1) { broken = true; }
    if (!readFileSync(report, 'utf8').includes('"suites"')) { throw new Error('Missing test report'); }
  }
} finally {
  await Promise.all(workers.map((worker) => worker.terminate()));
}
if (broken) { process.exitCode = 1; }
