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

function hasPinnedMachineName(text) {
  const normalized = text.normalize('NFKD').toLowerCase().replace(/[^a-z0-9]/g, '');
  return normalized.includes('dswett') || normalized.includes('arm8') || /vm[0-9]/.test(normalized);
}

const vm = ['DEV', 'VM', '1'];
const prefix = 'dswett';
const probes = [
  ['space before the VM number', `${vm[0]}-${vm[1]} ${vm[2]}`],
  ['space-separated VM name', vm.join(' ')],
  ['underscored VM name', vm.join('_')],
  ['mixed-case VM name', vm.join('_').toLowerCase().replace('vm', 'vM')],
  ['spaced architecture name', ['ARM', '8'].join(' ')],
  ['underscored architecture name', ['arm', '8'].join('_')],
  ['underscored owner-prefixed host', [prefix, 'machine-a'].join('_')],
  ['spaced owner-prefixed host', [prefix, 'machine-a'].join(' ')],
  ['mixed-case owner-prefixed host', [prefix.toUpperCase(), 'machine-a'].join('.')],
  ['owner-prefixed domain name', [prefix, 'machine-a.example.test'].join('.')],
  ['joined owner-prefixed host', [prefix, 'machine-a'].join('')],
];

for (const [description, probe] of probes) {
  test(`the machine-name scan rejects a ${description}`, () => {
    assert.equal(hasPinnedMachineName(probe), true);
  });
}

test('the machine-name scan permits neutral fixtures and repository references', () => {
  for (const text of ['machine-a', 'machine-b.example.test', 'Laptop', 'danswett/agent-bridge']) {
    assert.equal(hasPinnedMachineName(text), false);
  }
});

test('S6 exporters, self-tests, demos and comments never pin fleet machine names', () => {
  const pinned = files.filter((file) => hasPinnedMachineName(readFileSync(new URL(file, root), 'utf8')));
  assert.deepEqual(pinned, []);
});
