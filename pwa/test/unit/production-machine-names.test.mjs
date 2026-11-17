import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const files = [
  'src/AgentBridge.Host.Core/Parsing/CopilotParser.cs',
  'src/AgentBridge.Host/Program.cs',
  'src/AgentBridge.App.Core/StatusRules.cs',
  'src/AgentBridge.App.Core/SignInBanners.cs',
  'src/AgentBridge.App.Core/MainViewModel.SignIn.cs',
  'src/AgentBridge.App.Core/DelegateLabel.cs',
  'src/AgentBridge.App/SelfTest.Code.cs',
  'pwa/js/status.js',
  'pwa/js/attribution.js',
  'pwa/js/ui/sign-in-view.js',
];
const machineName = /DSWETT[-_]|dswett-arm|\bARM8\b|\bVM[1-9]\d*\b/i;

for (const file of files) {
  test(`${file} uses neutral examples rather than fleet machine names`, () => {
    const hits = readFileSync(new URL(file, root), 'utf8').split(/\r?\n/)
      .flatMap((line, i) => machineName.test(line) ? [`${file}:${i + 1}: ${line.trim()}`] : []);
    assert.deepEqual(hits, [], 'Machine names must come from the relay roster, not source or comments');
  });
}
