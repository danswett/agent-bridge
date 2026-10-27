// Runs the S2 page, including the live checks against the .NET WebSocket server, in
// Chromium and WebKit (Safari's engine) through Playwright.
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium, webkit } from 'playwright';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const dll = path.join(root, 'dotnet', 'bin', 'Release', 'net10.0', 's2-noise.dll');
const port = 8765;

const server = spawn('dotnet', [dll, 'serve', '--port', String(port), '--web', path.join(root, 'web')], { stdio: ['ignore', 'pipe', 'inherit'] });
const serverLog = [];
await new Promise((ok, fail) => {
  const t = setTimeout(() => fail(new Error('server did not start')), 30000);
  server.stdout.on('data', (d) => {
    const s = d.toString(); serverLog.push(s);
    if (s.includes('serving')) { clearTimeout(t); ok(); }
  });
});

let failed = 0;
try {
  for (const [name, type] of [['chromium', chromium], ['webkit', webkit]]) {
    const browser = await type.launch();
    const page = await browser.newPage();
    await page.goto(`http://127.0.0.1:${port}/index.html?live=1`);
    await page.waitForFunction(() => window.__S2_RESULT, null, { timeout: 60000 });
    const r = await page.evaluate(() => window.__S2_RESULT);
    console.log(`\n== ${name} ${browser.version()} ==`);
    for (const x of r.results) { console.log(`${x.ok ? 'PASS' : 'FAIL'}  ${x.name}${x.detail ? ` (${x.detail})` : ''}`); }
    console.log(`${name}: ${r.passed}/${r.passed + r.failed} passed`);
    failed += r.failed;
    await browser.close();
  }
} finally {
  server.kill();
}
console.log(`\nserver log:\n${serverLog.join('').trim()}`);
process.exit(failed ? 1 : 0);
