// Builds a design-context packet: the approved design system at one commit, as files to carry
// into a private design studio by hand (docs/design/handoff.md).
//
//   node tools/design-gen/export-context.mjs --out <folder outside the repository>
//
// The packet is a one-way, unauthenticated export. It isn't sync, it isn't a connector, and it
// grants no access to any studio. Everything drawn from it is a proposal until the owner
// approves it. It holds only files git tracks, from a clean tree whose generated files are
// current, so what leaves is exactly what is public at that commit and nothing local or private
// rides along.

import { existsSync, mkdirSync, readdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { staleFiles, repoRoot } from './generate.mjs';

export const KIND = 'agent-bridge-design-context';

// What a studio needs, and no more. A pattern is a tracked file, or `dir/*.ext` for the tracked
// files directly in dir. Fonts and canvas sources stay out: the first are large and licensed
// separately, the second are a sketchpad the spec overrides.
export const SOURCES = [
  'docs/design/ui.md',
  'docs/design/subagents.md',
  'docs/design/ui-plan.md',
  'docs/design/handoff.md',
  'docs/design/proposals/README.md',
  'design/README.md',
  'design/tokens.json',
  'design/preview.png',
  'design/glyphs/*.svg',
  'design/icons/*.svg',
  'pwa/design/tokens.css',
  'docs/design/mockups/README.md',
  'docs/design/mockups/*.png',
];

const STATUS = 'Reference: the approved design system at this revision. Anything made from it is a proposal until the owner approves it (docs/design/handoff.md).';
const SYNC = 'None. A one-way export: nothing returns automatically, and it grants no access to any design studio.';

function inside(child, parent) {
  const rel = path.relative(parent, child);
  return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

// realpath of a path that may not exist yet: its nearest existing ancestor's, plus the rest.
function realpathLoose(p) {
  const abs = path.resolve(p);
  let head = abs;
  const tail = [];
  while (!existsSync(head)) {
    const parent = path.dirname(head);
    if (parent === head) { break; }
    tail.unshift(path.basename(head));
    head = parent;
  }
  return path.join(realpathSync.native(head), ...tail);
}

// The files the packet takes, in a fixed order, each checked to be tracked, unmodified and
// inside the repository (a symlink out of it is refused, not followed).
export function planPacket(root, { tracked, modified = [] }) {
  const realRoot = realpathSync.native(root);
  const trackedSet = new Set(tracked);
  const files = [];
  for (const pattern of SOURCES) {
    const star = pattern.indexOf('*');
    let matches;
    if (star < 0) {
      if (!existsSync(path.join(root, pattern))) { throw new Error(`${pattern} is missing`); }
      if (!trackedSet.has(pattern)) { throw new Error(`${pattern} is not tracked by git, so it is not exported`); }
      matches = [pattern];
    } else {
      const dir = pattern.slice(0, pattern.lastIndexOf('/') + 1);
      const ext = pattern.slice(star + 1);
      matches = tracked.filter((f) => f.startsWith(dir) && !f.slice(dir.length).includes('/') && f.endsWith(ext)).sort();
      if (!matches.length) { throw new Error(`${pattern} matches no tracked file`); }
    }
    for (const rel of matches) {
      const abs = path.resolve(root, rel);
      if (!inside(abs, path.resolve(root))) { throw new Error(`${rel} is outside the repository`); }
      if (!existsSync(abs)) { throw new Error(`${rel} is missing`); }
      if (!inside(realpathSync.native(abs), realRoot)) { throw new Error(`${rel} resolves outside the repository`); }
      files.push(rel);
    }
  }
  const dirty = files.filter((f) => modified.includes(f));
  if (dirty.length) { throw new Error(`uncommitted changes in ${dirty.join(', ')}: commit them, or export from a clean checkout, so the packet matches its revision`); }
  return [...new Set(files)];
}

// The fenced block after a heading in a Markdown file, so the brief and the template each have
// one source: handoff.md and proposals/README.md.
export function fencedBlockAfter(text, heading) {
  // A Windows checkout may hold CRLF; the packet is always LF so it is the same everywhere.
  const markdown = text.replace(/\r\n/g, '\n');
  const at = markdown.indexOf(`\n${heading}\n`);
  if (at < 0) { throw new Error(`no "${heading}" section`); }
  const open = markdown.slice(at).match(/\n(```+)[^\n]*\n/);
  if (!open) { throw new Error(`no code block under "${heading}"`); }
  const start = at + open.index + open[0].length;
  const end = markdown.indexOf(`\n${open[1]}\n`, start);
  if (end < 0) { throw new Error(`the code block under "${heading}" is not closed`); }
  return `${markdown.slice(start, end)}\n`;
}

function packetReadme(brief, manifest) {
  return [
    '# Agent Bridge design-context packet',
    '',
    `**Revision:** \`${manifest.revision}\`${manifest.revisionDate ? ` (${manifest.revisionDate})` : ''}`,
    '',
    `**Status:** ${manifest.status}`,
    '',
    `**Sync:** ${manifest.sync}`,
    '',
    '- `sources/` keeps the repository\'s layout, so the spec\'s relative links still work. Start with `sources/docs/design/ui.md`.',
    '- `handoff-template.md` is the proposal template to fill in when work comes back.',
    '- `manifest.json` lists every file with its SHA-256.',
    '',
    '## Brief',
    '',
    'Paste this into the studio and attach the packet:',
    '',
    '```',
    brief.replaceAll('<sha>', manifest.revision).trimEnd(),
    '```',
    '',
  ].join('\n');
}

// A Windows checkout with core.autocrlf holds text with CRLF, but git stores it with LF. Text
// (no NUL byte, the test git itself uses) goes out with LF so a packet's bytes and hashes are
// the committed ones on every machine; binary files go out as they are.
export function asCommitted(bytes) {
  if (bytes.includes(0)) { return bytes; }
  return Buffer.from(bytes.toString('latin1').replace(/\r\n/g, '\n'), 'latin1');
}

// Writes the packet to out. Throws, writing nothing, if a source is missing, untracked or
// modified, a generated file is stale, out is inside the repository, or out holds something
// that isn't an earlier packet.
export function buildPacket({ root = repoRoot, out, revision, revisionDate, tracked, modified = [], stale = staleFiles(root) }) {
  if (!out) { throw new Error('--out is required'); }
  if (!/^[0-9a-f]{40}$/.test(revision ?? '')) { throw new Error(`revision must be a full commit sha, not "${revision}"`); }
  const realOut = realpathLoose(out);
  if (inside(realOut, realpathSync.native(root))) { throw new Error(`${out} is inside the repository; write the packet outside it so it is never committed`); }
  if (stale.length) { throw new Error(`generated files are stale (${stale.join(', ')}): run node tools/design-gen/generate.mjs first`); }

  const files = planPacket(root, { tracked, modified });
  const brief = fencedBlockAfter(readFileSync(path.join(root, 'docs/design/handoff.md'), 'utf8'), '## Paste-ready brief for the studio');
  const template = fencedBlockAfter(readFileSync(path.join(root, 'docs/design/proposals/README.md'), 'utf8'), '## Template');

  if (existsSync(realOut) && readdirSync(realOut).length) {
    let kind;
    try { kind = JSON.parse(readFileSync(path.join(realOut, 'manifest.json'), 'utf8')).kind; } catch { kind = undefined; }
    if (kind !== KIND) { throw new Error(`${out} is not empty and holds no earlier packet; choose an empty folder`); }
    rmSync(realOut, { recursive: true });
  }
  mkdirSync(realOut, { recursive: true });

  const entries = [];
  for (const rel of files) {
    const dest = `sources/${rel}`;
    mkdirSync(path.dirname(path.join(realOut, dest)), { recursive: true });
    const bytes = asCommitted(readFileSync(path.join(root, rel)));
    writeFileSync(path.join(realOut, dest), bytes);
    entries.push({ path: dest, sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length });
  }
  const manifest = {
    kind: KIND,
    format: 1,
    status: STATUS,
    sync: SYNC,
    repository: 'danswett/agent-bridge',
    revision,
    ...(revisionDate ? { revisionDate } : {}),
    files: entries,
  };
  writeFileSync(path.join(realOut, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  writeFileSync(path.join(realOut, 'README.md'), packetReadme(brief, manifest));
  writeFileSync(path.join(realOut, 'handoff-template.md'), template);
  return manifest;
}

const git = (root, ...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8' });

// What the repository says about itself: the files it tracks, which of them are modified, and
// the commit checked out.
export function gitFacts(root = repoRoot) {
  const tracked = git(root, 'ls-files', '-z').split('\0').filter(Boolean);
  const modified = git(root, 'status', '--porcelain=v1', '-z', '--untracked-files=no').split('\0').filter(Boolean)
    .map((line) => line.slice(3));
  const revision = git(root, 'rev-parse', 'HEAD').trim();
  const revisionDate = git(root, 'show', '-s', '--format=%cI', 'HEAD').trim();
  return { tracked, modified, revision, revisionDate };
}

function behindOriginMain(root) {
  try { return Number(git(root, 'rev-list', '--count', 'HEAD..origin/main').trim()); } catch { return undefined; }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const args = process.argv.slice(2);
  const i = args.indexOf('--out');
  const out = i >= 0 ? args[i + 1] : undefined;
  try {
    const manifest = buildPacket({ out, ...gitFacts(repoRoot) });
    console.log(`Wrote ${manifest.files.length} files to ${path.resolve(out)} at ${manifest.revision}.`);
    console.log('One-way export: not sync, and not approval. See docs/design/handoff.md.');
    const behind = behindOriginMain(repoRoot);
    if (behind) { console.warn(`Warning: this checkout is ${behind} commits behind origin/main. Fetch and export from a newer commit for the current spec.`); }
  } catch (e) {
    console.error(`export-context: ${e.message}`);
    process.exit(1);
  }
}
