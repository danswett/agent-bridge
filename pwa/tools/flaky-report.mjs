// Lists the tests a Playwright run passed only on a retry, from its JSON report, so a
// pass-on-retry is on the run's summary and not hidden by the green job (quality/README.md §5).
// Usage: node tools/flaky-report.mjs <report.json> [--summary <file>]
// With --summary the list is appended to that file (CI gives $GITHUB_STEP_SUMMARY); each
// test is also written as a `::warning` line, which GitHub shows on the run.
import { appendFileSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

// Every test the report calls flaky: its last result passed after an earlier one failed.
export function flakyTests(report) {
  const found = [];
  const walk = (suite, titles) => {
    const here = suite.title ? [...titles, suite.title] : titles;
    for (const spec of suite.specs ?? []) {
      for (const test of spec.tests ?? []) {
        if (test.status !== 'flaky') { continue; }
        const failed = (test.results ?? []).filter((r) => r.status !== 'passed' && r.status !== 'skipped').length;
        found.push({ file: spec.file, line: spec.line, project: test.projectName, title: [...here.slice(1), spec.title].join(' › '), failed });
      }
    }
    for (const child of suite.suites ?? []) { walk(child, here); }
  };
  for (const suite of report.suites ?? []) { walk(suite, []); }
  return found;
}

export function render(flaky) {
  if (flaky.length === 0) { return '### Playwright flaky tests\n\nNone: no test passed only on a retry.\n'; }
  const rows = flaky.map((f) => `- \`${f.file}:${f.line}\` [${f.project}] ${f.title} (failed ${f.failed}, then passed on retry)`);
  return `### Playwright flaky tests\n\n${flaky.length} test(s) passed only on a retry. A test that flakes more than twice in a week is quarantined with an issue (quality/README.md §5).\n\n${rows.join('\n')}\n`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [file, flag, summary] = process.argv.slice(2);
  if (!file) { console.error('usage: flaky-report.mjs <report.json> [--summary <file>]'); process.exit(2); }
  let flaky = [];
  try { flaky = flakyTests(JSON.parse(readFileSync(file, 'utf8'))); } catch (e) {
    // No report means the run died before writing one; its own failure is the signal.
    console.log(`flaky-report: no report read (${e.code ?? e.message})`);
    process.exit(0);
  }
  const text = render(flaky);
  console.log(text);
  for (const f of flaky) { console.log(`::warning file=pwa/${f.file},line=${f.line},title=flaky test::[${f.project}] ${f.title} passed only on a retry`); }
  if (flag === '--summary' && summary) { appendFileSync(summary, text); }
}
