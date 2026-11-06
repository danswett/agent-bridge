// An agent that needs sign-in, whose turn ended on an error, or that has stopped answering (#800):
// every protocol name for it, read in this one file, as Connection's producer contract has them
// (issue #800, comment 6646501884, with the push amendment 6646515428). A rename is a change here
// and nowhere else. The app's twin is src/AgentBridge.App.Core/AuthState.cs.
//
//   Session.attention          { kind, since, agent, provider, account, message, errorClass,
//                                retryAt, signIn, recovery, lastEventAt }
//   hello.machine.signIn       [{ agent, provider, account, since, sessions, signIn, recovery }]
//   capability sessions.signin, ops signin.start and signin.cancel, events signin.code and
//   signin.done, which come only to the channels in the flow: a device code is never in a
//   session, a snapshot or hello.
//
// What a host sends here is untrusted (a spoofed or broken host), and what it can lead a person
// to do is sign an account in. So:
//   * only a verification URL on ALLOWED_VERIFICATION_URIS is ever drawn as a link or opened, and
//     then only as the allowlist's own string, never the host's;
//   * only a user code of USER_CODE's shape is shown, after the same hidden-character check the
//     Needs you lane applies;
//   * nothing else of a signin.code is read at all, so no token can reach the page through it;
//   * an attention's message is a sentence the host composes from kind, errorClass, statusCode
//     and provider, never the agent's text (6649775484): shown as it is, as plain text, revealed;
//   * a refused start's reason is shown only as this client's words for one of the host's known
//     reasons (START_REASONS), never as the machine's text; signin.done's is never read.
// No code here completes a flow, enters a code, or opens anything: a person presses for each.
import { hasHidden, hasRightToLeft, reveal } from './hidden-text.js';

export const SIGN_IN = Object.freeze({
  capability: 'sessions.signin',
  start: 'signin.start',
  cancel: 'signin.cancel',
  code: 'signin.code',
  done: 'signin.done',
});

export const ATTENTION_KINDS = Object.freeze(['signInNeeded', 'error', 'notResponding']);
export const ERROR_CLASSES = Object.freeze(['auth', 'rate', 'quota', 'model', 'network', 'other']);

// Exactly these strings, compared whole: no prefix, no case folding, no normalising. A lookalike
// (an IDN, a userinfo, another port or path, a trailing dot or slash) is simply not on the list.
export const ALLOWED_VERIFICATION_URIS = Object.freeze(['https://github.com/login/device']);

// GitHub's device codes: ABCD-1234. Anything else is not shown.
export const USER_CODE = /^[A-Z0-9]{4}-[A-Z0-9]{4}$/;

// How long "signed in" or "recovered by itself" stays.
export const RESOLVED_SHOWN_MS = 60_000;

export const AGENT_NAME = Object.freeze({ copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'The agent' });

// What a row says of an error, by its class (ui.md §6.13): a class without words of its own here
// reads as "Agent error". The agent's own message is in Details only.
export const ERROR_WORDS = Object.freeze({
  auth: 'Not authorised',
  rate: 'Rate limited',
  other: 'Agent error',
});

const str = (v) => (typeof v === 'string' ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);
// The protocol's timestamps (RFC 3339) only: Date.parse takes much else, a comment in parentheses
// included. Kept as this client writes it, so nothing the host sent beside the time reaches a view.
const ISO_TIME = /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,9})?)?(Z|[+-][0-9]{2}:[0-9]{2})$/;
const time = (v) => {
  if (typeof v !== 'string' || !ISO_TIME.test(v)) { return null; }
  const ms = Date.parse(v);
  return Number.isFinite(ms) ? new Date(ms).toISOString() : null;
};
const agentOf = (v) => (['copilot', 'claude', 'codex'].includes(v) ? v : 'unknown');

// Shapes a credential has: GitHub's tokens (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_), the
// sk- keys of OpenAI and Anthropic, a JWT, and any long unbroken run of key characters. Text
// that holds one is not shown at all: cutting it out would still say where it was.
// The long run first: any run of a JWT's characters 40 or more long is one, found at its start. The
// JWT itself only from the start of a run, so a run of "eyJ" with no dot is read once, not again
// from each "eyJ" in it (quadratic, on the UI thread).
const TOKEN_SHAPES = [
  /[A-Za-z0-9_+=-]{40,}/,
  /gh[pousr]_[A-Za-z0-9]{16,}/,
  /github_pat_[A-Za-z0-9_]{16,}/,
  /sk-[A-Za-z0-9_-]{16,}/,
  /(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/,
  /\b[0-9a-fA-F]{32,}\b/,
];
export const tokenShaped = (text) => TOKEN_SHAPES.some((r) => r.test(String(text ?? '')));

// A host's sentence for a line of plain text: revealed (a hidden or right-to-left character is
// drawn as its escape, so the line reads in the order it runs), one line, at most max characters.
// Nothing else: an attention's message is composed by the host, not the agent (6649775484).
export function plainText(v, max) {
  const t = str(v);
  if (t === null) { return null; }
  const one = reveal(t, { rightToLeft: true }).replace(/\s+/g, ' ').trim();
  if (!one) { return null; }
  return one.length > max ? `${one.slice(0, max - 1)}…` : one;
}

// A machine's own words (a sign-in's reason) as plain text, the same way, and none of it when it
// holds anything shaped like a token: those can come from the agent's sign-in.
export function agentText(v, max) {
  const t = str(v);
  return t !== null && tokenShaped(t) ? null : plainText(t, max);
}

// An account's name: shown only as it is, or not at all. A login with a hidden or right-to-left
// character could pass for another.
export function accountName(v) {
  const t = str(v)?.trim();
  if (!t || t.length > 64 || hasHidden(t) || hasRightToLeft(t) || /\s/.test(t) || tokenShaped(t)) { return null; }
  return t;
}

// The allowlist's own string for a URL on it, or null.
export function allowedVerificationUri(v) {
  return typeof v === 'string' ? ALLOWED_VERIFICATION_URIS.find((u) => u === v) ?? null : null;
}

// The code as shown, or null when it isn't one.
export function shownUserCode(v) {
  if (typeof v !== 'string' || hasHidden(v) || hasRightToLeft(v)) { return null; }
  return USER_CODE.test(v) ? v : null;
}

// Where an agent can be signed in (attention.signIn): phone or machine, only as the host says. A
// value this client doesn't know, or none, makes no claim (Engineering's ruling on #800,
// 6648000166): no Sign in, which the host may not relay, and no "Sign in at the machine", which
// only a host saying so may claim (the no-RDP rule, #696).
const signInOf = (v) => (v === 'phone' || v === 'machine' ? v : null);
// attention.recovery: one this client doesn't know reads as failed, as the protocol has it, so a
// sign-in is offered where it can be rather than waited on. pending: signed in, the host not yet
// sure the session answers (its clear gate). There is no "resumed": the host clears the attention.
const RECOVERIES = ['checking', 'signingIn', 'failed', 'pending'];
const recoveryOf = (v) => (RECOVERIES.includes(v) ? v : typeof v === 'string' ? 'failed' : null);

// Whether a session's attention still needs a person to sign in: not while it is signed in and
// waiting for the session to confirm (recovery pending).
export const needsSignIn = (at) => at?.kind === 'signInNeeded' && at.recovery !== 'pending';
// Signed in, waiting for the session to confirm (ui.md §6.13): a person's signin.done alone never
// gets further than this; only the host clearing signInNeeded does.
export const confirming = (at) => at?.kind === 'signInNeeded' && at.recovery === 'pending';

// Session.attention, as the views use it, or null when there is none. An unknown kind is an error;
// an unknown error class is other.
export function readAttention(raw) {
  const a = obj(raw);
  if (!a) { return null; }
  const kind = ATTENTION_KINDS.includes(a.kind) ? a.kind : 'error';
  return {
    kind,
    since: time(a.since),
    agent: agentOf(a.agent),
    account: kind === 'signInNeeded' ? accountName(a.account) : null,
    message: plainText(a.message, 300),
    errorClass: kind === 'error' ? (ERROR_CLASSES.includes(a.errorClass) ? a.errorClass : 'other') : null,
    retryAt: kind === 'error' ? time(a.retryAt) : null,
    signIn: kind === 'signInNeeded' ? signInOf(a.signIn) : null,
    recovery: kind === 'signInNeeded' ? recoveryOf(a.recovery) : null,
    lastEventAt: kind === 'notResponding' ? time(a.lastEventAt) : null,
  };
}

// hello.machine.signIn: one entry per agent and account with sessions that need sign-in. An entry
// with no agent is left out.
export function readMachineSignIn(raw) {
  if (!Array.isArray(raw)) { return []; }
  return raw.map((e) => {
    const n = obj(e);
    if (!n || typeof n.agent !== 'string') { return null; }
    return {
      agent: agentOf(n.agent),
      account: accountName(n.account),
      since: time(n.since),
      sessions: Array.isArray(n.sessions) ? [...new Set(n.sessions.filter((id) => typeof id === 'string' && id))] : [],
      signIn: signInOf(n.signIn),
      recovery: recoveryOf(n.recovery),
    };
  }).filter(Boolean);
}

// A refused start's reason in plain words, by the host's own reason (AgentSignInService), compared
// whole (ui.md §6.13: a reason is the host's, from a known set, never the machine's words). Any
// other reason, a request this client got wrong included, reads as none: "Sign-in refused". The
// raw text stays in the host's own log, for diagnostics (#802). AuthState.StartReasons is its twin.
export const START_REASONS = Object.freeze({
  'too many recent sign-in requests': 'too many requests, try again later',
  'this credential store is busy': 'another sign-in is under way',
  'too many sign-ins are pending': 'too many sign-ins under way',
  'sign-in is unavailable or not authorized': 'not available from this device',
});

// signin.start's result: { outcome, flow, reason }. An outcome this client doesn't know reads as
// refused, with no reason: never as started, which would wait for a code, nor as machine, which
// would say the provider has no device flow when nothing proved it. Only a refused says why.
export function readStartResult(raw) {
  const r = obj(raw) ?? {};
  // The merged protocol's (#834, #841): any other reads as refused.
  const outcome = ['started', 'busy', 'refused'].includes(r.outcome) ? r.outcome : 'refused';
  const flow = str(r.flow) || null;
  const reason = r.outcome === 'refused' && typeof r.reason === 'string' && Object.hasOwn(START_REASONS, r.reason) ? START_REASONS[r.reason] : null;
  return { outcome: (outcome === 'started' || outcome === 'busy') && !flow ? 'refused' : outcome, flow, reason };
}

// signin.code's data, as it may be shown: { flow, agent, code, uri, expiresAt }, or { flow,
// refused: true } when it isn't GitHub's shape. Its three fields are strict: one of the wrong type
// refuses it whole, so no half-read code is shown. Nothing else of it is read.
export function readCode(raw) {
  const d = obj(raw);
  const flow = str(d?.flow);
  if (!flow) { return null; }
  if (typeof d.userCode !== 'string' || typeof d.verificationUri !== 'string' || time(d.expiresAt) === null) { return { flow, refused: true }; }
  const code = shownUserCode(d.userCode);
  const uri = allowedVerificationUri(d.verificationUri);
  return code && uri ? { flow, agent: agentOf(d.agent), code, uri, expiresAt: time(d.expiresAt) } : { flow, refused: true };
}

// signin.done's data: { flow, outcome, reason, resumed, continued }. An unknown outcome is failed.
export function readDone(raw) {
  const d = obj(raw);
  const flow = str(d?.flow);
  if (!flow) { return null; }
  const ids = (v) => (Array.isArray(v) ? v.filter((id) => typeof id === 'string' && id) : []);
  return {
    flow,
    outcome: ['signedIn', 'expired', 'cancelled', 'failed', 'refused'].includes(d.outcome) ? d.outcome : 'failed',
    // signin.done carries no reason in the protocol: none is read, whatever a host sends.
    reason: null,
    resumed: ids(d.resumed),
    continued: ids(d.continue),
  };
}

// A code as a screen reader says it, a character at a time in its two halves: "Code: W D J B, M J H T".
export const spelled = (code) => `Code: ${code.split('-').map((half) => half.split('').join(' ')).join(', ')}`;

// Whether a device flow's code has run out, by this device's clock: the code goes then, whatever
// the host still says.
export function flowExpired(code, now = Date.now()) {
  const t = Date.parse(code?.expiresAt ?? '');
  return !Number.isFinite(t) || now >= t;
}

// A clock time for a line ("4:27 PM"), in this device's zone.
export function clockTime(iso) {
  const t = Date.parse(iso ?? '');
  return Number.isFinite(t) ? new Date(t).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : '';
}
