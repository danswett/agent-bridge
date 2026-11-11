import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const pwa = fileURLToPath(new URL('../', import.meta.url));

export function changedSpecs({ cwd, base, head = 'HEAD' }) {
  if (!base || !head) throw new Error('Both PR base and head are required');
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8' });
  const mergeBase = git('merge-base', base, head).trim();
  // Disable rename detection so the destination is added and the old path is deleted.
  return git('diff', '--name-only', '--no-renames', '--diff-filter=AM', '-z', mergeBase, head, '--', 'pwa/test/')
    .split('\0').filter((name) => /^pwa\/test\/.*\.spec\.mjs$/.test(name) && !name.split('/').includes('e2e'))
    .map((name) => name.slice(4));
}

export function repeatSpecs(specs, { project, shard, run = execFileSync } = {}) {
  if (!specs.length) return;
  // Playwright's positional filters are regexes, not literal file names.
  const filters = specs.map((spec) => '(?:^|[/\\\\])' +
    spec.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replaceAll('/', '[/\\\\]') + '$');
  console.log('Repeating changed PWA specs:', JSON.stringify(specs));
  run(process.execPath, [fileURLToPath(new URL('../node_modules/@playwright/test/cli.js', import.meta.url)),
    'test', `--project=${project}`, `--shard=${shard}`, '--fully-parallel', '--repeat-each=5', '--retries=0',
    '--reporter=list,github', '--output=test-results/repeats', ...filters],
  { cwd: pwa, stdio: 'inherit' });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.includes('--select')) {
    const specs = changedSpecs({ cwd: path.resolve(pwa, '..'), base: process.env.PR_BASE, head: process.env.PR_HEAD });
    appendFileSync(process.env.GITHUB_OUTPUT, `specs=${JSON.stringify(specs)}\n`);
  } else {
    repeatSpecs(JSON.parse(process.env.CHANGED_SPECS), { project: process.env.PROJECT, shard: process.env.SHARD });
  }
}
