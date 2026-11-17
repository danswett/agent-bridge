// Opt-in only: execution requires the approvals described in lost-final-audit.md.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir, platform, arch, release } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite } from './build-site.mjs';
import { startStaticServer } from '../test/serve.mjs';

const started = performance.now();
const root = fileURLToPath(new URL('../', import.meta.url));
const git = (...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8' }).trim();
const cpuMask = process.platform === 'win32'
  ? execFileSync('powershell.exe', ['-NoProfile', '-Command',
    `(Get-Process -Id ${process.pid}).ProcessorAffinity.ToInt64().ToString('X')`], { encoding: 'utf8' }).trim()
  : (await readFile('/proc/self/status', 'utf8')).match(/^Cpus_allowed_list:\s*(.+)$/m)?.[1];
assert.equal(git('status', '--porcelain'), '', 'Commit the audit inputs before measuring');
const revision = git('rev-parse', 'HEAD'), builtAt = git('show', '-s', '--format=%cI', 'HEAD');
const fixture = await readFile(new URL('../test/lost-final-loopback.mjs', import.meta.url));
const script = await readFile(fileURLToPath(import.meta.url));
const { chromium } = await import('@playwright/test');
const dir = await mkdtemp(path.join(tmpdir(), 'ab-lost-final-'));
let server, browser;
try {
  const site = buildSite({ out: path.join(dir, 'site'), version: revision, builtAt });
  server = await startStaticServer({ dir: site });
  const origin = `http://127.0.0.1:${server.address().port}`;
  browser = await chromium.launch();
  const samples = [];
  for (const phase of ['baseline', 'faults']) {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' });
    try {
      const forbidden = [];
      await context.route('**/*', async (route) => {
        const url = new URL(route.request().url());
        if (url.origin !== origin) { forbidden.push(url.origin); await route.abort(); }
        else if (url.pathname === '/audit-loopback.mjs') {
          await route.fulfill({ contentType: 'text/javascript', body: fixture });
        } else { await route.continue(); }
      });
      const page = await context.newPage();
      await page.goto(`${origin}/?demo=1&static=1`);
      await page.waitForSelector('html[data-demo-ready]');
      const result = await page.evaluate(async (phase) => {
        const { sampleLoopback } = await import('/audit-loopback.mjs');
        return sampleLoopback(phase);
      }, phase);
      assert.deepEqual(forbidden, [], 'Audit attempted non-loopback traffic');
      assert.equal(result.outcome, 'delivered');
      assert.equal(result.hostCopies, 1);
      assert.equal(new Set(result.requests).size, 1);
      assert.equal(result.requests.length, phase === 'faults' ? 2 : 1);
      assert.equal(result.dropped, phase === 'faults' ? 1 : 0);
      samples.push(result);
    } finally { await context.close(); }
  }
  process.stdout.write(`${JSON.stringify({
    label: 'reconstruction, not comparable to 2026-11-13 runs',
    runtimeMs: performance.now() - started,
    atPT: new Date().toLocaleString('sv-SE', { timeZone: 'America/Los_Angeles' }),
    runtime: { node: process.version, platform: platform(), arch: arch(), os: release(), cpuMask },
    browser: { name: 'chromium', version: browser.version(), headless: true },
    build: { revision, builtAt }, scriptRevision: revision,
    scriptSha256: createHash('sha256').update(script).digest('hex'),
    fixtureSha256: createHash('sha256').update(fixture).digest('hex'),
    viewport: { width: 390, height: 844 }, persistent: false,
    receiveDelayMs: 200, deliverDelayMs: 200, replyTimeoutMs: 90000, retryDelayMs: 3000,
    sampling: 'one service-submit-to-settled sample per phase, no warmup or clock override', samples,
  }, null, 2)}\n`);
} finally {
  try { await browser?.close(); }
  finally {
    try { if (server) { await new Promise((resolve, reject) => server.close((e) => e ? reject(e) : resolve())); } }
    finally { await rm(dir, { recursive: true }); }
  }
}
