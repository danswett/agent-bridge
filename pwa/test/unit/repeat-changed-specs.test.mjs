import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { changedSpecs, repeatSpecs } from '../../tools/repeat-changed-specs.mjs';

function repository(t) {
  const parent = fileURLToPath(new URL('../../.site/', import.meta.url));
  mkdirSync(parent, { recursive: true });
  const cwd = mkdtempSync(path.join(parent, 'repeat-specs-'));
  t.after(() => rmSync(cwd, { recursive: true }));
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
  const write = (name, text = name) => {
    const full = path.join(cwd, name);
    mkdirSync(path.dirname(full), { recursive: true });
    writeFileSync(full, text);
  };
  const commit = () => {
    git('add', '.');
    git('-c', 'user.name=quality', '-c', 'user.email=quality@example.invalid',
      '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture');
  };
  git('init', '-q', '-b', 'main');
  write('pwa/test/motion.spec.mjs');
  write('pwa/test/deleted.spec.mjs');
  write('pwa/test/old.spec.mjs');
  commit();
  git('switch', '-qc', 'topic');
  return { cwd, git, write, commit };
}

test('only added or modified pwa-ui specs survive deletes, renames and unrelated changes', (t) => {
  const r = repository(t);
  for (const name of ['motion', 'history-perf', 'recent-order', 'launch', 'literal [space]']) {
    r.write(`pwa/test/${name}.spec.mjs`, 'changed');
  }
  r.write('pwa/test/e2e/e2e.spec.mjs');
  r.write('pwa/test/unit/other.test.mjs');
  r.write('other/test/outside.spec.mjs');
  rmSync(path.join(r.cwd, 'pwa/test/deleted.spec.mjs'));
  renameSync(path.join(r.cwd, 'pwa/test/old.spec.mjs'), path.join(r.cwd, 'pwa/test/renamed.spec.mjs'));
  r.commit();
  const head = r.git('rev-parse', 'HEAD');
  r.git('switch', '-q', 'main');
  r.write('pwa/test/base-only.spec.mjs');
  r.commit();
  // The PR head, not a moving base tip or the checked-out merge commit.
  assert.deepEqual(changedSpecs({ cwd: r.cwd, base: 'main', head }), [
    'test/history-perf.spec.mjs', 'test/launch.spec.mjs', 'test/literal [space].spec.mjs',
    'test/motion.spec.mjs', 'test/recent-order.spec.mjs', 'test/renamed.spec.mjs',
  ]);
});

test('no change and non-spec-only changes select nothing, but a missing base fails loudly', (t) => {
  const r = repository(t);
  assert.deepEqual(changedSpecs({ cwd: r.cwd, base: 'main' }), []);
  r.write('pwa/js/main.js');
  r.commit();
  assert.deepEqual(changedSpecs({ cwd: r.cwd, base: 'main' }), []);
  assert.throws(() => changedSpecs({ cwd: r.cwd, base: 'missing-base' }), /merge-base|Not a valid/);
});

test('repeats use each browser and its shard, literal paths, five runs and zero retries', () => {
  const spec = 'test/literal [space].spec.mjs';
  for (const project of ['chromium', 'webkit']) {
    const calls = [];
    repeatSpecs([spec], { project, shard: '2/3', run: (...args) => calls.push(args) });
    assert.equal(calls.length, 1);
    const [command, args] = calls[0];
    assert.equal(command, process.execPath);
    for (const flag of [`--project=${project}`, '--shard=2/3', '--fully-parallel', '--repeat-each=5', '--retries=0']) {
      assert.ok(args.includes(flag), flag);
    }
    const filter = new RegExp(args.at(-1));
    assert.ok(filter.test(`/checkout/pwa/${spec}`));
    assert.ok(!filter.test('/checkout/pwa/test/literal spaceXspecXmjs'));
    assert.ok(!filter.test(`/checkout/pwa/${spec}.other`));
  }
});

test('an empty selection never starts Playwright and a failing repeat fails the caller', () => {
  const run = () => { throw new Error('repeat failed'); };
  repeatSpecs([], { run });
  assert.throws(() => repeatSpecs(['test/motion.spec.mjs'], { project: 'webkit', shard: '1/3', run }), /repeat failed/);
});
