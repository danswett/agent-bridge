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
  const decoded = text.replace(/\\(?:u\{([0-9a-f]+)\}|u([0-9a-f]{4})|x([0-9a-f]{2}))/gi,
    (escape, point, unit, byte) => {
      const value = Number.parseInt(point ?? unit ?? byte, 16);
      return value <= 0x10ffff ? String.fromCodePoint(value) : escape;
    });
  const normalized = decoded.replace(/\p{Cf}/gu, '').normalize('NFKD').toLowerCase().replace(/\p{M}/gu, '');
  const tokens = normalized.match(/[a-z0-9]+/g) ?? [];
  return tokens.some((_, start) => {
    let candidate = '';
    // Six tokens cover the longest family prefix even with a separator between every letter.
    for (const token of tokens.slice(start, start + 6)) {
      candidate += token;
      if (candidate.startsWith('dswett') || /^(?:arm8|vm[0-9]+|ultra[0-9]*)$/.test(candidate)) { return true; }
    }
    return false;
  });
}

function pinnedFiles(readSource = (file) => readFileSync(new URL(file, root), 'utf8')) {
  return files.filter((file) => hasPinnedMachineName(readSource(file)));
}

const vm = ['DEV', 'VM', '1'];
const prefix = 'dswett';
const ultra = ['UL', 'TRA'].join('');
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
  ['Unicode-escaped VM digit', vm[1] + String.raw`\u0031`],
  ['braced Unicode-escaped VM digit', vm[1] + String.raw`\u{31}`],
  ['zero-padded braced Unicode-escaped VM digit', vm[1] + String.raw`\u{00000031}`],
  ['hex-escaped VM digit', vm[1] + String.raw`\x31`],
  ['escaped zero-width VM separator', vm.slice(1).join(String.raw`\u200b`)],
  ['escaped owner-prefix format character', prefix.slice(0, 3) + String.raw`\u200b` + prefix.slice(3) + '-machine-a'],
  ['braced escaped owner-prefix format character', prefix.slice(0, 3) + String.raw`\u{200B}` + prefix.slice(3) + '-machine-a'],
  ['literal zero-width VM separator', vm.slice(1).join('\u200b')],
  ['literal format-character VM separator', vm.slice(1).join('\u2060')],
  ['hex-escaped owner-prefix letter', prefix.replace('s', String.raw`\x73`) + '-machine-a'],
  ['underscored Ultra-family host', [ultra, 'machine-a'].join('_')],
  ['numbered Ultra-family host', ultra + '2'],
];

for (const [description, probe] of probes) {
  test(`the machine-name scan rejects a ${description}`, () => {
    assert.equal(hasPinnedMachineName(probe), true);
  });
}

for (const [description, value, rejected] of [
  ['standalone Ultra host', ultra, true],
  ['lowercase Ultra host', ultra.toLowerCase(), true],
  ['compiler version', 'LLVM 19', false],
  ['ordinary numbered word', 'alarm 8', false],
]) {
  test(`the scoped source scan classifies a ${description} correctly`, () => {
    const file = 'pwa/js/demo.js';
    const source = `const machineName = ${JSON.stringify(value)};`;
    assert.deepEqual(pinnedFiles((path) => path === file ? source : ''), rejected ? [file] : []);
  });
}

test('the machine-name scan permits neutral fixtures and repository references', () => {
  for (const text of ['machine-a', 'machine-b.example.test', 'Laptop', 'danswett/agent-bridge', String.raw`machine-a\u{110000}`]) {
    assert.equal(hasPinnedMachineName(text), false);
  }
});

test('S6 exporters, self-tests, demos and comments never pin fleet machine names', () => {
  assert.deepEqual(pinnedFiles(), []);
});
