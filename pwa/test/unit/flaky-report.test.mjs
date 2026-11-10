import test from 'node:test';
import assert from 'node:assert/strict';
import { flakyTests, render } from '../../tools/flaky-report.mjs';

const report = {
  suites: [{
    title: 'answer.spec.mjs', specs: [], suites: [{
      title: 'group', specs: [
        { title: 'steady', file: 'answer.spec.mjs', line: 10, tests: [{ projectName: 'webkit', status: 'expected', results: [{ status: 'passed' }] }] },
        { title: 'wobbly', file: 'answer.spec.mjs', line: 20, tests: [{ projectName: 'webkit', status: 'flaky', results: [{ status: 'timedOut' }, { status: 'passed' }] }] },
        { title: 'broken', file: 'answer.spec.mjs', line: 30, tests: [{ projectName: 'webkit', status: 'unexpected', results: [{ status: 'failed' }, { status: 'failed' }] }] },
      ],
    }],
  }],
};

test('lists only the tests that passed on a retry', () => {
  const flaky = flakyTests(report);
  assert.equal(flaky.length, 1);
  assert.deepEqual(flaky[0], { file: 'answer.spec.mjs', line: 20, project: 'webkit', title: 'group › wobbly', failed: 1 });
  assert.match(render(flaky), /answer\.spec\.mjs:20` \[webkit\] group › wobbly/);
});

test('says so when nothing flaked', () => {
  assert.match(render(flakyTests({ suites: [] })), /None/);
});
