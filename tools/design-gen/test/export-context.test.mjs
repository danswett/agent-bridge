import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { asCommitted, buildPacket, gitFacts, planPacket, KIND } from '../export-context.mjs';
import { repoRoot } from '../generate.mjs';

const REV = 'f0808725a2b3c4d5e6f708192a3b4c5d6e7f8091';
const scratch = mkdtempSync(path.join(os.tmpdir(), 'ab-design-context-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

const real = gitFacts(repoRoot);
const sources = planPacket(repoRoot, { tracked: real.tracked });

// A repository holding only what the packet reads, all of it "tracked".
function fakeRepo(name) {
  const root = path.join(scratch, name, 'repo');
  for (const rel of sources) {
    mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
    cpSync(path.join(repoRoot, rel), path.join(root, rel));
  }
  return { root, tracked: [...sources], out: path.join(scratch, name, 'packet') };
}

const tree = (dir) => readdirSync(dir, { recursive: true, withFileTypes: true })
  .filter((e) => e.isFile())
  .map((e) => path.relative(dir, path.join(e.parentPath, e.name)).split(path.sep).join('/'))
  .sort()
  .map((rel) => [rel, readFileSync(path.join(dir, rel)).toString('base64')]);

test('two exports of the same revision are byte for byte the same', () => {
  const { root, tracked, out } = fakeRepo('twice');
  buildPacket({ root, out, revision: REV, tracked, stale: [] });
  const first = tree(out);
  buildPacket({ root, out: `${out}-2`, revision: REV, tracked, stale: [] });
  assert.deepEqual(tree(`${out}-2`), first);
});

test('the manifest names the revision and every file with its hash, and says it is neither approval nor sync', () => {
  const { root, tracked, out } = fakeRepo('manifest');
  const manifest = buildPacket({ root, out, revision: REV, tracked, stale: [] });
  assert.equal(manifest.kind, KIND);
  assert.equal(manifest.revision, REV);
  assert.match(manifest.status, /proposal until the owner approves/);
  assert.match(manifest.sync, /^None\. A one-way export/);
  assert.equal(manifest.files.length, sources.length);
  assert.ok(manifest.files.every((f) => f.path.startsWith('sources/') && /^[0-9a-f]{64}$/.test(f.sha256)));
  const readme = readFileSync(path.join(out, 'README.md'), 'utf8');
  assert.ok(readme.includes(`approved design system at commit ${REV}`), 'the brief carries the revision');
  assert.ok(!readme.includes('<sha>'));
});

test('the handoff template is the proposal template, ready to fill in', () => {
  const { root, tracked, out } = fakeRepo('template');
  buildPacket({ root, out, revision: REV, tracked, stale: [] });
  const template = readFileSync(path.join(out, 'handoff-template.md'), 'utf8');
  assert.match(template, /^# Proposal #<issue>/);
  assert.match(template, /Status: Proposed, not approved/);
  assert.ok(!template.includes('```markdown'));
});

test('a missing source is refused, naming it, and nothing is written', () => {
  const { root, tracked, out } = fakeRepo('missing');
  rmSync(path.join(root, 'design/tokens.json'));
  assert.throws(() => buildPacket({ root, out, revision: REV, tracked, stale: [] }), /design\/tokens\.json is missing/);
  assert.ok(!existsSync(out));
});

test('a file git does not track is never exported, even beside tracked ones', () => {
  const { root, tracked, out } = fakeRepo('untracked');
  writeFileSync(path.join(root, 'design/glyphs/private-sketch.svg'), '<svg/>');
  const manifest = buildPacket({ root, out, revision: REV, tracked, stale: [] });
  assert.ok(!manifest.files.some((f) => f.path.includes('private-sketch')));
  assert.ok(!existsSync(path.join(out, 'sources/design/glyphs/private-sketch.svg')));
  assert.throws(() => buildPacket({ root, out, revision: REV, tracked: tracked.filter((f) => f !== 'docs/design/ui.md'), stale: [] }),
    /docs\/design\/ui\.md is not tracked by git/);
});

test('a source with uncommitted changes is refused', () => {
  const { root, tracked, out } = fakeRepo('dirty');
  assert.throws(() => buildPacket({ root, out, revision: REV, tracked, modified: ['design/tokens.json'], stale: [] }),
    /uncommitted changes in design\/tokens\.json/);
});

test('stale generated files are refused', () => {
  const { root, tracked, out } = fakeRepo('stale');
  assert.throws(() => buildPacket({ root, out, revision: REV, tracked, stale: ['pwa/design/tokens.css'] }),
    /generated files are stale \(pwa\/design\/tokens\.css\)/);
});

test('a packet is never written inside the repository', () => {
  const { root, tracked } = fakeRepo('inside');
  assert.throws(() => buildPacket({ root, out: path.join(root, 'design', 'packet'), revision: REV, tracked, stale: [] }),
    /inside the repository/);
  assert.throws(() => buildPacket({ root, out: root, revision: REV, tracked, stale: [] }), /inside the repository/);
});

test('a folder holding something else is never overwritten, but an earlier packet is replaced', () => {
  const { root, tracked, out } = fakeRepo('overwrite');
  mkdirSync(out, { recursive: true });
  writeFileSync(path.join(out, 'notes.txt'), 'mine');
  assert.throws(() => buildPacket({ root, out, revision: REV, tracked, stale: [] }), /not empty and holds no earlier packet/);
  assert.equal(readFileSync(path.join(out, 'notes.txt'), 'utf8'), 'mine');

  const again = `${out}-again`;
  buildPacket({ root, out: again, revision: REV, tracked, stale: [] });
  writeFileSync(path.join(again, 'left-over.txt'), 'old');
  buildPacket({ root, out: again, revision: REV, tracked, stale: [] });
  assert.ok(!existsSync(path.join(again, 'left-over.txt')));
});

test('a revision that is not a full commit sha is refused', () => {
  const { root, tracked, out } = fakeRepo('revision');
  assert.throws(() => buildPacket({ root, out, revision: 'main', tracked, stale: [] }), /full commit sha/);
});

test('the repository itself exports, so every listed source still exists and is tracked', () => {
  const manifest = buildPacket({ root: repoRoot, out: path.join(scratch, 'real'), revision: real.revision, tracked: real.tracked, modified: [] });
  assert.ok(manifest.files.some((f) => f.path === 'sources/docs/design/ui.md'));
  assert.ok(manifest.files.some((f) => f.path === 'sources/pwa/design/tokens.css'));
  assert.ok(!manifest.files.some((f) => f.path.includes('/fonts/') || f.path.includes('/canvas/')));

  // Whatever the checkout's line endings, each source is exactly git's blob at that commit.
  const changed = new Set(real.modified);
  for (const f of manifest.files) {
    const rel = f.path.slice('sources/'.length);
    if (changed.has(rel)) { continue; }
    const blob = execFileSync('git', ['-C', repoRoot, 'cat-file', 'blob', `HEAD:${rel}`]);
    assert.equal(f.sha256, createHash('sha256').update(blob).digest('hex'), `${rel} differs from the committed blob`);
  }
});

test('text goes out with LF and binary files go out untouched', () => {
  assert.equal(asCommitted(Buffer.from('a\r\nb\r\n')).toString(), 'a\nb\n');
  const binary = Buffer.from([0x89, 0x50, 0x00, 0x0d, 0x0a]);
  assert.deepEqual(asCommitted(binary), binary);
});
