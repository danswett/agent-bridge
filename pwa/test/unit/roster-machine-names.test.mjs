import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const files = [
  'tools/ha-contract/Export-HaContract.ps1',
  'tools/ha-contract/Test-HaContractDrift.ps1',
  'src/AgentBridge.HomeAssistant/HaContract.cs',
  'src/AgentBridge.App/SelfTest.MachineTabs.cs',
  'quality/merge-rule.yml',
  '.github/workflows/merge-rule.yml',
  'pwa/js/demo.js',
];

test('S6 exporters, self-tests, demos and comments never pin fleet machine names', () => {
  const pinned = files.filter((file) => /VM[1-9]|DSWETT-|dswett-arm|ARM8/i.test(
    readFileSync(new URL(file, root), 'utf8'),
  ));
  assert.deepEqual(pinned, []);
});
