// Device approval, the PWA's side (#55 section 4, D4; mirrors the Windows app's D3a view models): the approving side: nothing signed without a presence proof.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { NoPresence, EnrollmentTexts } from '../../js/device-approval.js';
import { ApproveViewModel, ApproveTexts } from '../../js/approve-view-model.js';

// The exchange stands in for the roster library's: it answers the way the library does (match, wrong, locked, ended).
const approver = (over = {}) => ({
  code2Shown: null, transcriptToSign: new Uint8Array([9]), results: ['match'],
  enterCode1() { const r = this.results.shift() ?? 'ended'; if (r === 'match') this.code2Shown = '654 321'; return r; },
  ...over,
});
const NOW = Date.parse('2026-11-23T12:00:00Z');
const pending = (over = {}) => ({ name: 'Kitchen laptop', kind: 'app', platform: 'windows', id: '3f9a2c71b0de4a5f0123456789abcdef', signedInAt: NOW - 120_000, ...over });
const spyProof = (kind) => ({ kind, signs: 0, trySign() { this.signs++; return new Uint8Array([1]); } });
const approve = (presence = 'key', over = {}) => {
  const signed = [];
  const proof = spyProof(presence);
  const ex = approver(over.exchange);
  const vm = new ApproveViewModel(ex, pending(), proof, (r) => { signed.push(r); return over.signResult ?? true; }, () => NOW);
  return { vm, ex, proof, signed };
};

test('the approver\'s code 2 stays hidden until code 1 is typed right, and anything else is not a match', async () => {
  const { vm } = approve('key', { exchange: { results: ['wrong', 'match'] } });
  assert.equal(vm.code2, null);
  assert.equal(vm.enterCode1('12345'), 'wrong');
  assert.equal(vm.code2, null);
  assert.deepEqual([vm.state, vm.statusText], ['waiting-for-code-1', EnrollmentTexts.WRONG]);
  assert.equal(vm.enterCode1('123 456'), 'match');
  assert.equal(vm.code2, '654 321');
  assert.equal(vm.state, 'code-2-shown');
});

test('an ended or locked exchange ends the approver\'s attempt in fixed words', async () => {
  const a = approve('key', { exchange: { results: ['ended'] } });
  a.vm.enterCode1('1');
  assert.deepEqual([a.vm.state, a.vm.statusText], ['ended', EnrollmentTexts.ENDED]);
  assert.equal(a.vm.enterCode1('1'), 'ended');
  const b = approve('key', { exchange: { results: ['locked'] } });
  b.vm.enterCode1('1');
  assert.deepEqual([b.vm.state, b.vm.statusText], ['locked', EnrollmentTexts.LOCKED]);
});

test('a name is one to sixty-four characters with no control characters, and a refusal keeps the old one', async () => {
  const { vm } = approve();
  assert.equal(vm.setName('  Office PC  '), true);
  assert.equal(vm.name, 'Office PC');
  for (const bad of ['   ', 'x'.repeat(65), 'bad\u0007name', 'bad\u009fname', 'a\u007fb']) assert.equal(vm.setName(bad), false);
  assert.equal(vm.name, 'Office PC');
  assert.equal(vm.setName('x'.repeat(64)), true);
  assert.equal(vm.setName('😀'.repeat(64)), true);
  assert.equal(vm.setName('😀'.repeat(65)), false);
});

test('the banner names the device, its kind, its id and how long ago it signed in, in fixed phrases', async () => {
  assert.equal(approve().vm.banner, 'A new device wants to join: Kitchen laptop, says it is a Windows app, id 3f9a 2c71 b0de 4a5f, signed in 2 min ago');
  assert.equal(ApproveTexts.kindText('<script>', 'x'), 'a device of an unknown kind');
  assert.equal(ApproveTexts.kindText('pwa', 'x'), 'a browser app');
  assert.equal(ApproveTexts.ago(0), 'just now');
  assert.equal(ApproveTexts.ago(2 * 3_600_000), '2 h ago');
});

test('with no presence proof approving ends in cannot-approve and never signs or asks the proof', async () => {
  for (const kind of ['none', 'something-else']) {
    const { vm, proof, signed } = approve(kind);
    vm.enterCode1('123 456');
    vm.onJoinerOk();
    assert.equal(vm.canApprove, false);
    assert.equal(await vm.approve(), 'cannot-approve');
    assert.deepEqual([vm.state, vm.statusText], ['cannot-approve', ApproveTexts.CANNOT_APPROVE]);
    assert.equal(signed.length, 0);
    assert.equal(proof.signs, 0);
  }
  const none = new NoPresence();
  assert.equal(none.kind, 'none');
  assert.equal(none.trySign(new Uint8Array([1])), null);
});

test('with a presence proof approving waits for the joiner\'s ok, then hands this exchange\'s transcript and the edited name to the caller', async () => {
  const { vm, ex, proof, signed } = approve('key');
  assert.equal(await vm.approve(), 'not-ready');
  vm.enterCode1('123 456');
  vm.setName('Office PC');
  assert.equal(await vm.approve(), 'not-ready');
  assert.equal(signed.length, 0);
  vm.onJoinerOk();
  assert.equal(await vm.approve(), 'approved');
  assert.equal(signed.length, 1);
  assert.deepEqual([signed[0].transcript, signed[0].name, signed[0].proof], [ex.transcriptToSign, 'Office PC', proof]);
  assert.equal(vm.state, 'approved');
  assert.equal(proof.signs, 0, 'the model never signs itself');
});

test('a joiner ok before code 1 matched is ignored, and a failed signing can be tried again', async () => {
  const { vm } = approve('webauthn', { signResult: false });
  vm.onJoinerOk();
  assert.equal(vm.state, 'waiting-for-code-1');
  vm.enterCode1('123 456');
  vm.onJoinerOk();
  assert.equal(await vm.approve(), 'failed');
  assert.deepEqual([vm.state, vm.statusText], ['ready-to-approve', EnrollmentTexts.GENERIC]);
  vm.setName('Office PC');
  assert.equal(vm.name, 'Office PC');
});

test('an exchange with no transcript ends the attempt instead of signing', async () => {
  const { vm, signed } = approve('key', { exchange: { transcriptToSign: null } });
  vm.enterCode1('123 456');
  vm.onJoinerOk();
  assert.equal(await vm.approve(), 'failed');
  assert.deepEqual([vm.state, vm.statusText, signed.length], ['ended', EnrollmentTexts.ENDED, 0]);
});

test('the approve model signs nothing itself: it imports no signing path and calls no subtle.sign', async () => {
  const { readFile } = await import('node:fs/promises');
  for (const f of ['approve-view-model']) {
    const src = await readFile(new URL(`../../js/${f}.js`, import.meta.url), 'utf8');
    assert.doesNotMatch(src, /transfer\.js|roster\/(signing-key|cap-sign-key|commands)|subtle|signCommand/, f);
  }
});

test('a device that lies about its id or sign-in time shows a placeholder or "just now", never markup or NaN', async () => {
  const { vm } = approve();
  const mk = (over) => new ApproveViewModel(approver(), pending(over), spyProof('key'), () => true, () => NOW);
  assert.equal(mk({ id: '<img src=x onerror=1>' }).idShown, 'unknown');
  assert.equal(mk({ id: 'ab\ncdefghij' }).idShown, 'unknown');
  assert.equal(mk({ id: undefined }).idShown, 'unknown');
  assert.match(mk({ signedInAt: undefined }).banner, /signed in just now$/);
  assert.equal(ApproveTexts.ago(59 * 60_000), '59 min ago');
  assert.equal(ApproveTexts.ago(60 * 60_000), '1 h ago');
  assert.equal(vm.idShown, '3f9a 2c71 b0de 4a5f');
});

test('an asynchronous sign counts only when it answers true; a refusal or a throw fails and the same model can approve again', async () => {
  const run = async (sign) => {
    const vm = new ApproveViewModel(approver(), pending(), spyProof('key'), sign, () => NOW);
    vm.enterCode1('123 456');
    vm.onJoinerOk();
    return vm;
  };
  for (const sign of [async () => false, async () => { throw new Error('denied'); }, () => { throw new Error('denied'); }, async () => 'yes', () => undefined]) {
    const vm = await run(sign);
    assert.equal(await vm.approve(), 'failed');
    assert.deepEqual([vm.state, vm.statusText], ['ready-to-approve', EnrollmentTexts.GENERIC]);
  }
  let answer = false;
  const vm = await run(async () => answer);
  assert.equal(await vm.approve(), 'failed');
  answer = true;
  assert.equal(await vm.approve(), 'approved');
  assert.equal(await vm.approve(), 'not-ready');
});

test('a second approve while the first is still signing does not sign twice', async () => {
  let release; let calls = 0;
  const vm = new ApproveViewModel(approver(), pending(), spyProof('key'), () => { calls++; return new Promise((r) => { release = r; }); }, () => NOW);
  vm.enterCode1('123 456');
  vm.onJoinerOk();
  const first = vm.approve();
  assert.equal(await vm.approve(), 'not-ready');
  release(true);
  assert.equal(await first, 'approved');
  assert.equal(calls, 1);
});

test('an id that is a number shows the placeholder instead of throwing', () => {
  const vm = new ApproveViewModel(approver(), pending({ id: 12345678901234567890 }), spyProof('key'), () => true, () => NOW);
  assert.equal(vm.idShown, 'unknown');
});

test('nothing outside the model can set its state, status or name, or change the device it describes', async () => {
  const p = pending();
  const vm = new ApproveViewModel(approver(), p, spyProof('key'), () => true, () => NOW);
  for (const key of ['state', 'statusText', 'name', 'pending']) assert.throws(() => { vm[key] = 'x'; }, TypeError, key);
  assert.throws(() => { vm.pending.name = 'x'; }, TypeError);
  assert.equal(await vm.approve(), 'not-ready');
  const banner = vm.banner;
  p.name = 'Changed later';
  p.id = 'ffffffffffffffff';
  assert.equal(vm.banner, banner);
  assert.equal(vm.name, 'Kitchen laptop');
});

test('the device\'s own name is cleaned before it is shown or signed: no controls, no direction or hidden characters, at most 64', () => {
  const name = (value) => new ApproveViewModel(approver(), pending({ name: value }), spyProof('key'), () => true, () => NOW);
  const dirty = name('Evil‮exe.txt\u0007​⁦ PC\n<b>');
  assert.equal(dirty.name, 'Evilexe.txt PC<b>');
  assert.match(dirty.banner, /^A new device wants to join: Evilexe\.txt PC<b>, says/);
  assert.equal(name('x'.repeat(100)).name, 'x'.repeat(64));
  assert.equal(name('‮\u0007  ').name, 'unnamed device');
  for (const bad of [undefined, null, {}]) assert.equal(name(bad).name, 'unnamed device');
  const vm = name('ok');
  assert.equal(vm.setName('a‮b'), false);
  assert.equal(vm.setName('a​b'), false);
  assert.equal(vm.name, 'ok');
});
