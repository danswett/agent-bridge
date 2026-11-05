// An agent that needs sign-in (#800), as Connection's producer contract has it: what a host's
// attention, roll-up and signin.* fields read as, and what is refused.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readHello, readSession } from '../../js/protocol.js';
import { ALLOWED_VERIFICATION_URIS, RESOLVED_SHOWN_MS, agentText, plainText, allowedVerificationUri, flowExpired, readAttention, readCode, readDone, readStartResult, shownUserCode, spelled, tokenShaped } from '../../js/auth-state.js';
import { needsYou } from '../../js/needs-you.js';
import { bannerVerb, bannerWords, signInBanners } from '../../js/ui/sign-in-view.js';

const GITHUB = 'https://github.com/login/device';
// Credentials' shapes, made up and put together here, so no scanner takes the file for a leak.
const fake = (prefix, body) => `${prefix}_${body}`;
const later = (ms) => new Date(Date.now() + ms).toISOString();
const codeEvent = (fields = {}) => ({ flow: 'f1', agent: 'copilot', userCode: 'WDJB-MJHT', verificationUri: GITHUB, expiresAt: later(600000), ...fields });
const signInAttention = (fields = {}) => ({ kind: 'signInNeeded', since: '2026-11-04T23:00:00.000Z', agent: 'copilot', provider: 'github', account: 'octocat', message: 'Copilot needs you to sign in again (401).', signIn: 'phone', ...fields });

test('only github.com/login/device, exactly as written, is a verification URL the clients draw or open', () => {
  assert.deepEqual(ALLOWED_VERIFICATION_URIS, [GITHUB]);
  assert.equal(allowedVerificationUri(GITHUB), GITHUB);
  for (const spoof of [
    'http://github.com/login/device', // not https
    'https://evil.example/login/device', // another host
    'https://github.com.evil.example/login/device', // a lookalike suffix
    'https://github.co/login/device', // a lookalike domain
    'https://githuh.com/login/device',
    'https://github.com@evil.example/login/device', // userinfo
    'https://user:pass@github.com/login/device',
    'https://xn--github-9ua.com/login/device', // punycode
    'https://g\u0456thub.com/login/device', // IDN: a Cyrillic і
    'https://github.com./login/device', // a trailing dot
    'https://github.com:8443/login/device', // a port
    'https://GITHUB.COM/login/device', // case is not folded
    'https://github.com/login/device/', // a trailing slash
    'https://github.com/login/device?next=https://evil.example', // a query
    'https://github.com/login/device#x',
    'https://github.com/login/device\u200B', // a hidden character
    ' https://github.com/login/device',
    'javascript:alert(1)',
    '',
  ]) {
    assert.equal(allowedVerificationUri(spoof), null, spoof);
  }
  assert.equal(allowedVerificationUri(42), null);
});

test('only a code of GitHub\'s shape is shown, and one with a hidden character is not', () => {
  assert.equal(shownUserCode('WDJB-MJHT'), 'WDJB-MJHT');
  assert.equal(shownUserCode('AB12-CD34'), 'AB12-CD34');
  for (const bad of ['wdjb-mjht', 'WDJBMJHT', 'WDJB-MJH', 'WDJB-MJHTX', 'WDJB MJHT', 'WDJB-MJHT\u200B', '\u202EWDJB-MJHT', 'WDJ\u0412-MJHT', '<b>WDJB</b>', fake('ghp', 'a'.repeat(36)), '', null, 1234]) {
    assert.equal(shownUserCode(bad), null, String(bad));
  }
});

test('a signin.code reads only its flow, code, URL and expiry, so nothing else the host sends reaches a view', () => {
  const c = readCode({ ...codeEvent(), accessToken: fake('gho', 'a'.repeat(36)), deviceCode: '3584d83530557fdd1f46af8289938c8ef79f9dc5' });
  assert.deepEqual(Object.keys(c).sort(), ['agent', 'code', 'expiresAt', 'flow', 'uri']);
  assert.doesNotMatch(JSON.stringify(c), /gho_|3584d835/);
});

test('a signin.code\'s expiry is an RFC 3339 time, kept as this client writes it, and nothing else is', () => {
  const at = (expiresAt) => readCode({ flow: 'f1', userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt });
  assert.equal(at('2099-01-01T00:00:00.1234567+00:00').expiresAt, '2099-01-01T00:00:00.123Z');
  assert.equal(at('2099-01-01T01:00:00+01:00').expiresAt, '2099-01-01T00:00:00.000Z');
  const token = ['gho', '16C7e42F292c6912E7710c838347Ae178B4a'].join('_');
  for (const bad of [`Fri, 01 Jan 2099 00:00:00 GMT (${token})`, 'Fri, 01 Jan 2099 00:00:00 GMT', '2099-01-01', '2099-01-01T00:00:00', '2099-01-01T00:00:00Z ', 'soon', token, '', 4102444800000]) {
    assert.deepEqual(at(bad), { flow: 'f1', refused: true }, String(bad));
  }
});

test('a signin.code with a spoofed URL or an odd code is refused whole, and none of it is kept', () => {
  for (const c of [codeEvent({ verificationUri: 'https://github.com.evil.example/login/device' }), codeEvent({ userCode: 'wdjb-mjht' }), codeEvent({ userCode: 7 }), codeEvent({ expiresAt: 'soon' })]) {
    assert.deepEqual(readCode(c), { flow: 'f1', refused: true });
  }
  // One that names no flow is no flow's: not shown at all.
  assert.equal(readCode({ ...codeEvent(), flow: null }), null);
});

test('a start\'s outcome this client doesn\'t know, or one with no flow, is refused: never started, never "at the machine"', () => {
  assert.deepEqual(readStartResult({ outcome: 'started', flow: 'f1' }), { outcome: 'started', flow: 'f1', reason: null });
  assert.equal(readStartResult({ outcome: 'started' }).outcome, 'refused');
  assert.equal(readStartResult({ outcome: 'teleported' }).outcome, 'refused');
  // The merged protocol (#834, #841) has started, busy and refused only: "Sign in at the machine"
  // comes from the host's signIn: machine, never from a start's result.
  assert.deepEqual(readStartResult({ outcome: 'machine', reason: 'no device flow' }), { outcome: 'refused', flow: null, reason: null });
  assert.equal(readStartResult({ outcome: 'refused', reason: 'Signed out' }).reason, 'Signed out');
  assert.equal(readStartResult({ outcome: 'recovered', flow: 'f1' }).outcome, 'refused');
  assert.equal(readStartResult({ outcome: 'busy', flow: 'f1' }).outcome, 'busy');
  assert.equal(readStartResult(null).outcome, 'refused');
});

test('a signin.done reads its outcome, an unknown one as failed, and the sessions it resumed', () => {
  assert.deepEqual(readDone({ flow: 'f1', outcome: 'signedIn', resumed: ['a', 3, 'b'], continue: ['a'] }), { flow: 'f1', outcome: 'signedIn', reason: null, resumed: ['a', 'b'], continued: ['a'] });
  assert.equal(readDone({ flow: 'f1', outcome: 'later' }).outcome, 'failed');
  // It carries no reason in the protocol: a host's is never read.
  assert.equal(readDone({ flow: 'f1', outcome: 'failed', reason: 'quota for octocat exceeded' }).reason, null);
});

test('text shaped like a token is never shown, whatever else it says', () => {
  for (const t of [`token ${fake('gho', '16C7e42F292c6912E7710c838347Ae178B4a')}`, fake('github_pat', '11ABCDEFG0123456789_abcdefghij'), ['sk', 'ant', 'api03', 'abcdefghijklmnopqrstuv'].join('-'), `Bearer ${['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiIxMjM0NTY3ODkwIn0', 'x'].join('.')}`, 'key 0123456789abcdef0123456789abcdef']) {
    assert.ok(tokenShaped(t), t);
    assert.equal(agentText(t, 300), null);
  }
  assert.equal(tokenShaped('Authorization error. Your credentials may be expired or invalid.'), false);
});

// Judged by growth, not milliseconds (#681): four times the input may take at most eight times as
// long, past a floor below which nothing is measured. A run of "eyJ" with no dot was read again from
// each "eyJ" in it, growing as a square (16 times).
test('a long run of JWT-like prefixes is judged in time proportional to its length', () => {
  const time = (text) => {
    let best = Infinity;
    for (let k = 0; k < 5; k++) { const start = performance.now(); tokenShaped(text); best = Math.min(best, performance.now() - start); }
    return best;
  };
  for (const unit of ['eyJ', 'eyJ' + 'a'.repeat(5)]) {
    const small = unit.repeat(4000);
    const large = unit.repeat(16000);
    time(small);
    let [a, b] = [time(small), time(large)];
    if (b >= 20 && b > 8 * a) { [a, b] = [time(small), time(large)]; }
    assert.ok(b < 20 || b <= 8 * a, `${unit}: ${small.length} characters in ${a.toFixed(2)} ms, ${large.length} in ${b.toFixed(2)} ms`);
  }
  // Still a token where it starts a run.
  assert.ok(tokenShaped(`token ${['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiIxIn0'].join('.')}`));
});

test('the host\'s message is shown as it is, one line, its hidden characters drawn as escapes, and nothing else done to it', () => {
  assert.equal(plainText('Copilot hit a rate limit (429).', 300), 'Copilot hit a rate limit (429).');
  assert.equal(plainText('Copilot\nstopped  \u202Ewith an error.', 300), 'Copilot stopped \\u202Ewith an error.');
  assert.equal(readAttention({ kind: 'error', errorClass: 'rate', message: 'Copilot hit a rate limit (429).' }).message, 'Copilot hit a rate limit (429).');
});

test('a machine\'s reason is one line, its hidden characters drawn as escapes, cut short', () => {
  assert.equal(agentText('Rate\nlimited  \u202Enow', 300), 'Rate limited \\u202Enow');
  assert.equal(agentText('x'.repeat(10) + ' ' + 'y'.repeat(20), 12), 'xxxxxxxxxx …');
});

test('a session\'s attention is read over its truthful status; an unknown kind is an error, an unknown class other', () => {
  const s = readSession({ id: 's1', agent: 'copilot', status: 'idle', attention: signInAttention() });
  assert.equal(s.status, 'idle');
  assert.deepEqual([s.attention.kind, s.attention.account, s.attention.signIn, s.attention.message], ['signInNeeded', 'octocat', 'phone', 'Copilot needs you to sign in again (401).']);
  assert.deepEqual([readAttention({ kind: 'meteor' }).kind, readAttention({ kind: 'error', errorClass: 'solar' }).errorClass], ['error', 'other']);
  // An account that could pass for another is not shown.
  assert.equal(readAttention(signInAttention({ account: 'octo\u202Ecat' })).account, null);
  // A way to sign in this client doesn't know makes no claim, as none said does (6648000166). An
  // unknown recovery is failed, as the protocol reads it.
  assert.equal(readAttention(signInAttention({ signIn: 'carrier-pigeon' })).signIn, null);
  assert.equal(readAttention(signInAttention({ signIn: undefined })).signIn, null);
  assert.equal(readAttention(signInAttention({ recovery: 'later' })).recovery, 'failed');
  assert.equal(readSession({ id: 's2', status: 'idle' }).attention, null);
});

test('hello reads its machine\'s sign-ins needed, leaving out an entry with no agent', () => {
  const h = readHello({ protocol: 1, machine: { id: 'm', name: 'DEV-VM1', signIn: [{ agent: 'copilot', account: 'octocat', sessions: ['a', 'b', 'a'], signIn: 'phone' }, { sessions: ['c'] }] }, capabilities: [] });
  assert.deepEqual(h.machine.signIn, [{ agent: 'copilot', account: 'octocat', since: null, sessions: ['a', 'b'], signIn: 'phone', recovery: null }]);
});

test('a code that has run out by this device\'s clock is expired, whatever the host still says', () => {
  assert.ok(!flowExpired({ expiresAt: later(60000) }));
  assert.ok(flowExpired({ expiresAt: later(-1000) }));
  assert.ok(flowExpired({ expiresAt: null }));
});

test('Needs you lists a session that needs sign-in as "Needs sign-in", with nothing to answer there; errors are not counted', () => {
  const session = readSession({ id: 's1', agent: 'copilot', title: 'Coordinate the Shiproom', status: 'idle', attention: signInAttention() });
  const [e] = needsYou([{ hostId: 'm', session }], { nameOf: () => 'DEV-VM1' });
  assert.equal(e.mode, 'signIn');
  assert.equal(e.line, 'Needs sign-in');
  assert.equal(e.decisions, null);
  assert.deepEqual(needsYou([{ hostId: 'm', session }], { current: () => false }), []);
  const failed = readSession({ id: 's2', status: 'idle', attention: { kind: 'error', errorClass: 'quota' } });
  const stalled = readSession({ id: 's3', status: 'working', attention: { kind: 'notResponding' } });
  assert.deepEqual(needsYou([{ hostId: 'm', session: failed }, { hostId: 'm', session: stalled }]), []);
});

const sessions = (n, attention = signInAttention()) => Array.from({ length: n }, (_, i) => readSession({ id: `s${i + 1}`, agent: 'copilot', title: `Session ${i + 1}`, status: 'idle', attention }));

test('one banner per machine, agent and account rolls up every session it holds up, from the sessions alone if need be', () => {
  const [b] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(4), canSignIn: true }]);
  assert.equal(b.sessions.length, 4);
  assert.deepEqual([bannerWords(b).head, bannerWords(b).line], ['Copilot on DEV-VM1 needs sign-in \u00b7 @octocat', '4 sessions affected']);
  assert.equal(bannerVerb(b), 'Sign in');
  const two = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: [...sessions(2), readSession({ id: 'c1', agent: 'claude', status: 'idle', attention: signInAttention({ agent: 'claude', account: null }) })] }]);
  assert.deepEqual(two.map((x) => [x.agent, x.sessions.length]).sort(), [['claude', 1], ['copilot', 2]]);
});

test('only a provider the host says has no device flow is "at the machine"; without the capability there is no button, and no such claim', () => {
  const [machine] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(1, signInAttention({ signIn: 'machine' })), canSignIn: true }]);
  assert.equal(bannerWords(machine).line, 'Sign in at the machine');
  assert.equal(bannerVerb(machine), null);
  // None said, or a word this client doesn't know: "Needs sign-in" with no action and no claim.
  const [unsaid] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(1, signInAttention({ signIn: undefined })), canSignIn: true }]);
  assert.deepEqual([bannerVerb(unsaid), bannerWords(unsaid).line], [null, '1 session affected']);
  // A word this client doesn't know: no Sign in the host may not relay, and no claim about the machine.
  const [unknown] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(1, signInAttention({ signIn: 'carrier-pigeon' })), canSignIn: true }]);
  assert.deepEqual([bannerVerb(unknown), bannerWords(unknown).line], [null, '1 session affected']);
  const [older] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(1), canSignIn: false }]);
  assert.deepEqual([bannerVerb(older), bannerWords(older).line], [null, '1 session affected']);
});

test('a flow under way offers no verb; one running elsewhere offers Show code; a failed or expired one, Try again', () => {
  const flows = (f) => new Map([['m\ncopilot\noctocat', f]]);
  const one = (f, attention) => signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(1, attention), canSignIn: true }], { flows: f ? flows(f) : new Map() })[0];
  assert.equal(bannerVerb(one({ state: 'pending', flow: 'f1', code: readCode(codeEvent()) })), null);
  assert.equal(bannerVerb(one(null, signInAttention({ recovery: 'signingIn' }))), 'Show code');
  assert.equal(bannerVerb(one({ state: 'done', outcome: 'failed', reason: 'The code was declined.' })), 'Try again');
  assert.equal(bannerWords(one({ state: 'done', outcome: 'failed', reason: 'The code was declined.' })).line, 'Sign-in failed \u00b7 The code was declined.');
  assert.match(bannerWords(one({ state: 'pending', flow: 'f1', code: readCode(codeEvent()) })).line, /^Waiting for sign-in \u00b7 code expires \d{1,2}:\d{2}/);
  const expired = one({ state: 'pending', flow: 'f1', code: readCode(codeEvent({ expiresAt: later(-1000) })) });
  assert.deepEqual([expired.flow.code, bannerWords(expired).line, bannerVerb(expired)], [null, 'Code expired', 'Try again']);
});

test('"signed in" and "recovered by itself" are said for a minute, then go', () => {
  const at = Date.now();
  const resolved = new Map([['m\ncopilot\noctocat', { kind: 'signedIn', count: 4, at, agent: 'copilot', account: 'octocat', hostId: 'm', name: 'DEV-VM1' }]]);
  const [b] = signInBanners([], { resolved, now: at + 1000 });
  assert.deepEqual([bannerWords(b).head, bannerWords(b).line], ['Copilot on DEV-VM1 · @octocat', 'Signed in · 4 sessions recovered']);
  assert.deepEqual(signInBanners([], { resolved, now: at + RESOLVED_SHOWN_MS }), []);
  resolved.set('m\ncopilot\noctocat', { ...resolved.get('m\ncopilot\noctocat'), kind: 'recovered' });
  assert.equal(bannerWords(signInBanners([], { resolved, now: at })[0]).line, 'Recovered by itself');
});

test('the code is read a character at a time, in its two halves', () => {
  assert.equal(spelled('WDJB-MJHT'), 'Code: W D J B, M J H T');
});

test('the sessions are the live word: a stale roll-up neither overrides their recovery nor keeps a session in the account it left', () => {
  const s = readSession({ id: 's1', agent: 'copilot', title: 'One', status: 'idle', attention: signInAttention({ account: 'hubot', recovery: 'signingIn' }) });
  const rollUp = [{ agent: 'copilot', account: 'octocat', sessions: ['s1'], signIn: 'phone', recovery: 'checking' }, { agent: 'copilot', account: 'hubot', sessions: ['s1'], signIn: 'phone', recovery: 'checking' }];
  const banners = signInBanners([{ hostId: 'm', name: 'DEV-VM1', rollUp, sessions: [s], canSignIn: true }]);
  assert.deepEqual(banners.map((b) => [b.account, b.sessions.length, b.recovery]), [['hubot', 1, 'signingIn']]);
});

test('the newest session needing sign-in decides how, and its no claim wins over an older session\'s phone or machine and the roll-up', () => {
  const at = (id, since, signIn) => readSession({ id, agent: 'copilot', title: id, status: 'idle', attention: signInAttention({ since, signIn }) });
  const rollUp = [{ agent: 'copilot', account: 'octocat', sessions: ['new', 'old'], signIn: 'phone' }];
  for (const [newest, older] of [['future-flow', 'phone'], [undefined, 'phone'], ['future-flow', 'machine'], [undefined, 'machine']]) {
    const [b] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', rollUp, sessions: [at('old', '2026-11-04T22:00:00.000Z', older), at('new', '2026-11-04T23:00:00.000Z', newest)], canSignIn: true }]);
    assert.deepEqual([b.canSignIn, b.atMachine, bannerVerb(b), bannerWords(b).line], [false, false, null, '2 sessions affected'], `${newest} over ${older}`);
  }
  // The newest saying phone is Sign in, whatever an older one said.
  const [p] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: [at('old', '2026-11-04T22:00:00.000Z', 'machine'), at('new', '2026-11-04T23:00:00.000Z', 'phone')], canSignIn: true }]);
  assert.equal(bannerVerb(p), 'Sign in');
});

test('only a flow still under way keeps a banner whose sessions have gone; one that ended keeps nothing', () => {
  const key = 'm\ncopilot\noctocat';
  const one = (f) => signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: [], canSignIn: true }], { flows: new Map([[key, f]]) });
  assert.equal(one({ state: 'pending', flow: 'f1' }).length, 1);
  assert.deepEqual(one({ state: 'done', outcome: 'failed' }), []);
});

test('signed in and waiting for the sessions to confirm is never recovered, and a new failure makes it needed again', () => {
  const links = (recoveries) => [{ hostId: 'm', name: 'DEV-VM1', sessions: recoveries.map((r, i) => readSession({ id: `s${i}`, agent: 'copilot', title: `S${i}`, status: 'idle', attention: signInAttention({ recovery: r }) })), canSignIn: true }];
  const [b] = signInBanners(links(['pending', 'pending']));
  assert.deepEqual([b.state, bannerWords(b).line, bannerVerb(b)], ['confirming', 'Signed in · waiting for 2 sessions to confirm', null]);
  // One fails sign-in again: needed, counting it alone.
  const [again] = signInBanners(links(['pending', 'failed']));
  assert.deepEqual([again.state, bannerWords(again).line, bannerVerb(again)], ['needed', '1 session affected', 'Sign in']);
  // Neither counts in Needs you while confirming.
  assert.deepEqual(needsYou(links(['pending'])[0].sessions.map((session) => ({ hostId: 'm', session }))), []);
});

test('a machine-only start result with no reason still says "Sign in at the machine" and offers no Sign in', () => {
  const [b] = signInBanners([{ hostId: 'm', name: 'DEV-VM1', sessions: sessions(1), canSignIn: true }], { flows: new Map([['m\ncopilot\noctocat', { state: 'done', atMachine: '' }]]) });
  assert.deepEqual([b.atMachine, bannerVerb(b), bannerWords(b).line], [true, null, 'Sign in at the machine']);
});

