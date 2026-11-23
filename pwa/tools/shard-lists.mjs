// The spec files of one Playwright shard, packed by measured time (#2454): longest first into the lightest
// shard, so no shard is the long pole of the pwa-ui job (the file-order split of `--shard` put axe and answer,
// 860 s of WebKit, in the first shard). Fully parallel inside a shard still splits a file by test.
//
//   node tools/shard-lists.mjs --project webkit --shard 2/4          # one anchored filter per spec, for Playwright
//   node tools/shard-lists.mjs --project webkit --shard 2/4 --files  # the spec paths
//   node tools/shard-lists.mjs --update test/shard-timings.json report-1.json report-2.json ...
//
// The times are test/shard-timings.json (seconds per spec per engine). A spec with no time is weighed as a
// typical one of its engine; a time for a spec that is gone is ignored. Every spec is in exactly one shard,
// proved on each call, and anything that could leave a shard empty fails: Playwright reads no filters as
// every test.
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const pwa = fileURLToPath(new URL('../', import.meta.url));

// Every *.spec.mjs under testDir, but not the e2e ones (their own job), as 'test/<path>', in name order.
export function specFiles(testDir = path.join(pwa, 'test')) {
  const found = [];
  const walk = (dir, shown) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (entry.name !== 'e2e' && entry.name !== 'node_modules') walk(path.join(dir, entry.name), `${shown}/${entry.name}`);
      } else if (entry.name.endsWith('.spec.mjs')) found.push(`${shown}/${entry.name}`);
    }
  };
  walk(testDir, path.basename(testDir));
  return found.sort();
}

const median = (values) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
};

export function assignShards({ files, timings, project, count }) {
  if (!project) throw new Error('a project (chromium or webkit) is required');
  if (!Number.isInteger(count) || count < 1) throw new Error(`${count} shards is not a number of shards`);
  const specs = [...new Set(files)].sort();
  if (specs.length === 0) throw new Error('there are no spec files');
  if (count > specs.length) throw new Error(`${count} shards for ${specs.length} specs would leave one empty, which Playwright reads as every test`);
  const known = timings?.[project] ?? {};
  const timed = specs.map((file) => known[file]).filter((seconds) => Number.isFinite(seconds) && seconds >= 0);
  const typical = timed.length ? median(timed) : 1;
  // A floor, so specs measured at nothing do not all pile into one shard.
  const weight = (file) => Math.max(Number.isFinite(known[file]) && known[file] >= 0 ? known[file] : typical, 0.1);
  const bins = Array.from({ length: count }, () => ({ files: [], load: 0 }));
  for (const file of [...specs].sort((a, b) => weight(b) - weight(a) || (a < b ? -1 : 1))) {
    const lightest = bins.reduce((best, bin) => (bin.load < best.load ? bin : best), bins[0]);
    lightest.files.push(file);
    lightest.load += weight(file);
  }
  const shards = bins.map((bin) => bin.files.sort());
  if (shards.some((shard) => shard.length === 0)) throw new Error('a shard came out empty');
  const placed = shards.flat().sort();
  if (placed.length !== specs.length || placed.some((file, i) => file !== specs[i])) throw new Error('the shards do not hold every spec exactly once');
  return shards;
}

export function shardSpecs({ files, timings, project, shard }) {
  const match = /^(\d+)\/(\d+)$/.exec(String(shard));
  if (!match) throw new Error(`shard "${shard}" is not i/n`);
  const [index, count] = [Number(match[1]), Number(match[2])];
  if (index < 1 || index > count) throw new Error(`shard ${index}/${count} is out of range`);
  return assignShards({ files, timings, project, count })[index - 1];
}

// Playwright's positional filters are regexes: anchored at a path separator so a.spec.mjs never takes data.spec.mjs.
export function shardFilter(file) {
  return '(?:^|[/\\\\])' + file.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replaceAll('/', '[/\\\\]') + '$';
}

// Seconds of test time per spec per engine, summed over Playwright JSON reports (one per shard, so a spec
// is in one report per engine), retries included.
export function updateTimings(reports) {
  const totals = {};
  const visit = (suite, inherited) => {
    const file = suite.file ?? inherited;
    for (const spec of suite.specs ?? []) {
      const specFile = spec.file ?? file;
      if (!specFile) continue;
      for (const test of spec.tests ?? []) {
        const seconds = (test.results ?? []).reduce((sum, result) => sum + (result.duration ?? 0), 0) / 1000;
        const engine = (totals[test.projectName] ??= {});
        engine[`test/${specFile}`] = (engine[`test/${specFile}`] ?? 0) + seconds;
      }
    }
    for (const child of suite.suites ?? []) visit(child, file);
  };
  for (const report of reports) for (const suite of report.suites ?? []) visit(suite, undefined);
  return totals;
}

function main(args) {
  const value = (name) => { const at = args.indexOf(name); return at < 0 ? undefined : args[at + 1]; };
  if (args.includes('--update')) {
    const at = args.indexOf('--update');
    const out = args[at + 1];
    const reports = args.slice(at + 2).map((file) => JSON.parse(readFileSync(file, 'utf8')));
    if (!out || reports.length === 0) throw new Error('--update needs the timings file to write and at least one report');
    let previous = { projects: {} };
    try { previous = JSON.parse(readFileSync(out, 'utf8')); } catch { /* a first file */ }
    const projects = { ...previous.projects };
    for (const [engine, specs] of Object.entries(updateTimings(reports))) {
      projects[engine] = Object.fromEntries(Object.entries(specs).sort(([a], [b]) => (a < b ? -1 : 1)).map(([file, seconds]) => [file, Math.round(seconds * 10) / 10]));
    }
    writeFileSync(out, `${JSON.stringify({ source: `Playwright JSON reports, ${reports.length} merged: seconds of test time per spec file, retries included; refresh with: node tools/shard-lists.mjs --update test/shard-timings.json <report.json>...`, projects }, null, 2)}\n`);
    return;
  }
  const timings = JSON.parse(readFileSync(value('--timings') ?? path.join(pwa, 'test/shard-timings.json'), 'utf8')).projects;
  const list = shardSpecs({ files: specFiles(), timings, project: value('--project'), shard: value('--shard') });
  console.log((args.includes('--files') ? list : list.map(shardFilter)).join('\n'));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(process.argv.slice(2)); } catch (error) { console.error(`shard-lists: ${error.message}`); process.exit(1); }
}