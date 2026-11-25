import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assignShards, shardFilter, shardSpecs, specFiles, updateTimings } from '../../tools/shard-lists.mjs';

const pwa = fileURLToPath(new URL('../../', import.meta.url));
const tool = path.join(pwa, 'tools/shard-lists.mjs');
const timings = JSON.parse(readFileSync(path.join(pwa, 'test/shard-timings.json'), 'utf8')).projects;
const files = specFiles(path.join(pwa, 'test'));
const seconds = (list, project) => list.reduce((sum, file) => sum + (timings[project][file] ?? 0), 0);

test('the specs are every *.spec.mjs under test, in name order, never the e2e ones', () => {
  assert.ok(files.length > 60, `found ${files.length}`);
  assert.deepEqual(files, [...files].sort());
  for (const file of files) {
    assert.match(file, /^test\/.+\.spec\.mjs$/);
    assert.ok(!file.split('/').includes('e2e'), file);
  }
});

test('every spec is in exactly one shard, for both engines and for 1 to 6 shards', () => {
  for (const project of ['chromium', 'webkit']) {
    for (let count = 1; count <= 6; count++) {
      const shards = assignShards({ files, timings, project, count });
      assert.equal(shards.length, count);
      assert.ok(shards.every((shard) => shard.length > 0), `${project} ${count}: an empty shard would run every test`);
      assert.deepEqual(shards.flat().sort(), files, `${project} ${count}`);
    }
  }
});

test('the lists are the same every time, and each is in name order', () => {
  const first = assignShards({ files, timings, project: 'webkit', count: 4 });
  assert.deepEqual(assignShards({ files: [...files].reverse(), timings, project: 'webkit', count: 4 }), first);
  for (const shard of first) assert.deepEqual(shard, [...shard].sort());
});

test('the shards are balanced by measured time: none is more than 10 % above the mean', () => {
  for (const [project, count] of [['chromium', 3], ['webkit', 3], ['webkit', 4]]) {
    const times = assignShards({ files, timings, project, count }).map((shard) => seconds(shard, project));
    const mean = times.reduce((a, b) => a + b, 0) / count;
    assert.ok(Math.max(...times) <= mean * 1.1, `${project} ${count}: ${times.map(Math.round)} against a mean of ${Math.round(mean)}`);
  }
});

test('a spec with no timing is still assigned, once, weighed as a typical one', () => {
  const extra = [...files, 'test/brand-new.spec.mjs'];
  const shards = assignShards({ files: extra, timings, project: 'chromium', count: 3 });
  assert.equal(shards.flat().filter((file) => file === 'test/brand-new.spec.mjs').length, 1);
  assert.deepEqual(shards.flat().sort(), extra.sort());
});

test('a timing for a spec that is gone is ignored', () => {
  const withGone = { chromium: { ...timings.chromium, 'test/gone.spec.mjs': 9999 } };
  const shards = assignShards({ files, timings: withGone, project: 'chromium', count: 3 });
  assert.ok(!shards.flat().includes('test/gone.spec.mjs'));
  assert.deepEqual(shards.flat().sort(), files);
});

test('bad input fails closed: no shards, more shards than specs, a shard out of range, no project', () => {
  const ok = { files, timings, project: 'webkit', count: 3 };
  assert.throws(() => assignShards({ ...ok, count: 0 }));
  assert.throws(() => assignShards({ ...ok, count: files.length + 1 }));
  assert.throws(() => assignShards({ ...ok, project: undefined }));
  assert.throws(() => shardSpecs({ ...ok, shard: '0/3' }));
  assert.throws(() => shardSpecs({ ...ok, shard: '4/3' }));
  assert.throws(() => shardSpecs({ ...ok, shard: 'x' }));
  assert.throws(() => assignShards({ files: [], timings, project: 'webkit', count: 1 }));
});

test('a shard is read as i/n and is the i-th list', () => {
  const shards = assignShards({ files, timings, project: 'webkit', count: 4 });
  for (let i = 1; i <= 4; i++) assert.deepEqual(shardSpecs({ files, timings, project: 'webkit', shard: `${i}/4` }), shards[i - 1]);
});

test("a filter matches its own spec and no other (Playwright's filters are regexes)", () => {
  for (const file of files) {
    const matches = files.filter((other) => new RegExp(shardFilter(file)).test(other));
    assert.deepEqual(matches, [file]);
  }
  assert.ok(new RegExp(shardFilter('test/a.spec.mjs')).test('C:\\work\\pwa\\test\\a.spec.mjs'));
  assert.ok(!new RegExp(shardFilter('test/a.spec.mjs')).test('test/data.spec.mjs'));
});

test('the timings are rebuilt from Playwright JSON reports, retries included', () => {
  const report = {
    suites: [{
      title: 'a.spec.mjs', file: 'a.spec.mjs', specs: [], suites: [{
        title: 'group', specs: [
          { title: 'one', file: 'a.spec.mjs', tests: [{ projectName: 'webkit', results: [{ duration: 1500 }, { duration: 500 }] }] },
          { title: 'two', file: 'a.spec.mjs', tests: [{ projectName: 'chromium', results: [{ duration: 250 }] }] },
        ],
      }],
    }, { title: 'b.spec.mjs', file: 'b.spec.mjs', specs: [{ title: 'three', file: 'b.spec.mjs', tests: [{ projectName: 'webkit', results: [{ duration: 4000 }] }] }], suites: [] }],
  };
  assert.deepEqual(updateTimings([report, report]), {
    chromium: { 'test/a.spec.mjs': 0.5 },
    webkit: { 'test/a.spec.mjs': 4, 'test/b.spec.mjs': 8 },
  });
});

test('the command line prints one filter per spec of the shard, and says nothing and fails on bad input', () => {
  const run = (...args) => spawnSync(process.execPath, [tool, ...args], { cwd: pwa, encoding: 'utf8' });
  const good = run('--project', 'webkit', '--shard', '2/4');
  assert.equal(good.status, 0, good.stderr);
  const lines = good.stdout.split('\n').filter(Boolean);
  assert.equal(lines.length, shardSpecs({ files, timings, project: 'webkit', shard: '2/4' }).length);
  assert.ok(lines.every((line) => files.some((file) => new RegExp(line).test(file))));
  const bad = run('--project', 'webkit', '--shard', '9/4');
  assert.notEqual(bad.status, 0);
  assert.equal(bad.stdout, '');
});