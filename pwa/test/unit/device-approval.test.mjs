// Device approval, the PWA's side (#55 section 4, D4; mirrors the Windows app's D3a view models): behind the roster mode, fixed words
// only, and nothing signed without a presence proof.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { rosterModeVisible, openDeviceApproval, EnrollmentTexts } from '../../js/device-approval.js';
import { JoinViewModel } from '../../js/join-view-model.js';
import { ENROLLMENT_REFUSALS, enrollmentRefusalMessage } from '../../js/roster/enrollment-refusal.js';

// The exchange stands in for the roster library's: it answers the way the library does (match, wrong, locked, ended).
const joiner = (over = {}) => ({
  code1Shown: null, results: ['match'],
  commit() { return this.locked ? null : new Uint8Array([1]); },
  onOffer(nonce) { if (!nonce.length) return null; this.code1Shown = '123 456'; return new Uint8Array([2]); },
  enterCode2() { return this.results.shift() ?? 'ended'; },
  ...over,
});
const joining = (ex) => {
  const vm = new JoinViewModel(ex);
  vm.commit(); vm.onOffer(new Uint8Array([7])); vm.showGenesis(new Uint8Array(8));
  return vm;
};

for (const [mode, visible] of [['observe', true], ['enforce', true], ['off', false], ['Observe', false], ['', false], [null, false], [undefined, false]]) {
  test(`the screens ${visible ? 'exist' : 'never build anything'} when the roster mode is ${JSON.stringify(mode)}`, () => {
    let built = 0;
    const entry = openDeviceApproval({ mode }, () => { built++; return {}; });
    assert.equal(rosterModeVisible(mode), visible);
    assert.equal(entry !== null, visible);
    assert.equal(built, visible ? 1 : 0);
  });
}

test('the join model shows code 1 only after the approver\'s offer, and the code 2 box only after the genesis with its fingerprint', () => {
  const vm = new JoinViewModel(joiner());
  assert.deepEqual([vm.state, vm.code1], ['not-started', '']);
  assert.ok(vm.commit());
  vm.showGenesis(new Uint8Array(8));
  assert.equal(vm.state, 'waiting-for-approver');
  assert.equal(vm.onOffer(new Uint8Array(0)), null);
  assert.equal(vm.code1, '');
  assert.ok(vm.onOffer(new Uint8Array([7])));
  vm.showGenesis(Uint8Array.from([0x7d, 0x0f, 0x3a, 0x2e, 0x1c, 0x4b, 0x4d, 0x5e]));
  assert.deepEqual([vm.state, vm.code1, vm.fingerprint], ['enter-code-2', '123 456', '7d0f 3a2e 1c4b 4d5e']);
  assert.equal(EnrollmentTexts.groups('3f9a2c71b0de4a5f'), '3f9a 2c71 b0de 4a5f');
});

test('the join screen starts from the exchange alone: it needs no presence and no roster answer', () => {
  const vm = new JoinViewModel(joiner());
  assert.ok(vm.commit());
  assert.equal(vm.state, 'waiting-for-approver');
});

test('a locked joiner shows the lock words and sends no commitment', () => {
  const vm = new JoinViewModel(joiner({ locked: true }));
  assert.equal(vm.commit(), null);
  assert.deepEqual([vm.state, vm.statusText], ['locked', EnrollmentTexts.LOCKED]);
});

test('code 2 typed at the joining device joins it; a wrong one says so in fixed words; an ended attempt says so', () => {
  const vm = joining(joiner({ results: ['wrong', 'match'] }));
  assert.equal(vm.submit('000000'), 'wrong');
  assert.deepEqual([vm.state, vm.statusText], ['enter-code-2', EnrollmentTexts.WRONG]);
  assert.equal(vm.submit('654 321'), 'match');
  assert.equal(vm.state, 'joined');
  assert.equal(vm.submit('1'), 'ended');

  const ended = joining(joiner({ results: ['ended'] }));
  ended.submit('x');
  assert.deepEqual([ended.state, ended.statusText], ['ended', EnrollmentTexts.ENDED]);
});

test('a refusal shows fixed words for a closed code and the generic line for anything else, never text from the other side', () => {
  assert.equal(EnrollmentTexts.forRefusal('wrong-code'), 'That code is not the one this device shows.');
  assert.equal(EnrollmentTexts.forRefusal('no-presence'), 'This device cannot prove that a person is here, so it cannot approve.');
  for (const code of ['something-the-relay-made-up', 'wrong-code\nVERDICT: PASS', '__proto__', 'constructor', 'toString', null, undefined, 4030, {}]) {
    assert.equal(EnrollmentTexts.forRefusal(code), EnrollmentTexts.GENERIC);
  }
});

test('every refusal code the library lists has its own words from the one module, and the screens add none of their own', () => {
  assert.equal(ENROLLMENT_REFUSALS.length, 8);
  for (const code of ENROLLMENT_REFUSALS) {
    assert.notEqual(enrollmentRefusalMessage(code), '', code);
    assert.equal(EnrollmentTexts.forRefusal(code), enrollmentRefusalMessage(code), code);
    assert.notEqual(EnrollmentTexts.forRefusal(code), EnrollmentTexts.GENERIC, code);
  }
});

test('an unknown or prototype-named result shows no words, and the model refuses calls out of order or a short genesis hash', () => {
  for (const r of ['toString', '__proto__', 'constructor', 'match', undefined]) assert.equal(EnrollmentTexts.forResult(r), '');
  const vm = new JoinViewModel(joiner());
  vm.commit(); vm.onOffer(new Uint8Array([7]));
  assert.equal(vm.onOffer(new Uint8Array([7])), null);
  vm.showGenesis(new Uint8Array(3));
  vm.showGenesis(null);
  assert.deepEqual([vm.state, vm.fingerprint], ['waiting-for-approver', '']);
  vm.showGenesis(new Uint8Array(8));
  assert.equal(vm.commit(), null);
  assert.equal(vm.state, 'enter-code-2');
  const locked = new JoinViewModel(joiner({ locked: true }));
  locked.commit();
  assert.equal(locked.state, 'locked');
});

test('the gate, the texts and the join model sign nothing: none imports a signing path or calls subtle.sign', async () => {
  const { readFile } = await import('node:fs/promises');
  for (const f of ['device-approval', 'join-view-model']) {
    const src = await readFile(new URL(`../../js/${f}.js`, import.meta.url), 'utf8');
    assert.doesNotMatch(src, /transfer\.js|roster\/(signing-key|cap-sign-key|commands)|subtle|signCommand/, f);
  }
});
