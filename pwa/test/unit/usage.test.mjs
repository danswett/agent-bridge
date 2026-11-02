import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readUsage, mergeUsage, severity, until, ago, elapsed, pace, showsError, machineStatus, isStale, amount, readOn, UsageBook } from '../../js/usage.js';

const NOW = Date.parse('2026-10-28T20:00:00.000Z');
const iso = (ms) => new Date(NOW + ms).toISOString();
const H = 3600000;

const reading = (extra = {}, windows = [{ key: 'plan', label: 'Plan', percent: 40 }]) => ({ agent: 'copilot', label: 'GitHub Copilot', account: 'octo', source: 'api', measuredAt: iso(-60000), windows, ...extra });
const failure = (extra = {}) => ({ agent: 'copilot', label: 'GitHub Copilot', account: 'octo', source: 'none', measuredAt: iso(0), windows: [], error: 'gh is not installed', ...extra });

test('a report from a newer host reads, with unknown agents and sources made safe and unusable windows dropped', () => {
  const r = readUsage({ agents: [{ agent: 'gemini', label: 'Gemini', source: 'oracle', windows: [{ key: 'x', percent: 'lots' }, { key: 'd', label: 'Daily', percent: 5 }], measuredAt: iso(0) }, 7, null] });
  assert.equal(r.agents.length, 1);
  assert.equal(r.agents[0].agent, 'unknown');
  assert.equal(r.agents[0].label, 'Gemini');
  assert.equal(r.agents[0].source, 'none');
  assert.deepEqual(r.agents[0].windows.map((w) => w.label), ['Daily']);
  assert.deepEqual(readUsage(null), { agents: [] });
});

test('one allowance read by two machines is one row, and the fresher reading wins', () => {
  const older = { agents: [reading({ measuredAt: iso(-H) }, [{ key: 'plan', label: 'Plan', percent: 30 }])] };
  const newer = { agents: [reading({ account: 'OCTO' }, [{ key: 'plan', label: 'Plan', percent: 41 }])] };
  const rows = mergeUsage([older, newer].map(readUsage));
  assert.equal(rows.length, 1);
  assert.equal(rows[0].windows[0].percent, 41);
});

test('a reading outranks a newer failure, which lends it its error', () => {
  const rows = mergeUsage([{ agents: [reading()] }, { agents: [failure()] }].map(readUsage));
  assert.equal(rows.length, 1);
  assert.equal(rows[0].windows[0].percent, 40);
  assert.equal(rows[0].error, 'gh is not installed');
  // An older failure says nothing about the newer reading.
  const settled = mergeUsage([{ agents: [reading()] }, { agents: [failure({ measuredAt: iso(-H) })] }].map(readUsage));
  assert.equal(settled[0].error, null);
});

test('two accounts no vendor names are kept apart by a few characters of their keys', () => {
  const claude = (key, percent) => ({ agent: 'claude', label: 'Claude Code', accountKey: key, source: 'api', measuredAt: iso(0), windows: [{ key: 'session', label: 'Session (5h)', percent }] });
  const rows = mergeUsage([{ agents: [claude('aaaa1111bbbb', 10)] }, { agents: [claude('cccc2222dddd', 20)] }].map(readUsage));
  assert.deepEqual(rows.map((r) => r.account), ['#cccc', '#aaaa']);
  // A lone row needs no name it was never given.
  assert.equal(mergeUsage([readUsage({ agents: [claude('aaaa1111bbbb', 10)] })])[0].account, null);
});

test('a further Copilot account is a row of its own under its login, and one machine reading it as its own account is the same row', () => {
  // DSWETT-DEV-VM1 is signed in as octo and lists dswett_microsoft as a further account; the
  // laptop is signed in as dswett_microsoft itself, and its failed read lends the row its error.
  const vm = { agents: [reading(), reading({ account: 'DSwett_Microsoft', slot: 2, measuredAt: iso(-H) }, [{ key: 'plan', label: 'Plan', percent: 12 }])] };
  const laptop = { agents: [failure({ account: 'dswett_microsoft' })] };
  const rows = mergeUsage([vm, laptop].map(readUsage));
  assert.deepEqual(rows.map((r) => [r.account, r.windows[0]?.percent ?? null, r.error]), [['octo', 40, null], ['DSwett_Microsoft', 12, 'gh is not installed']]);
  // And a further account no machine can read still has its row, saying why.
  const unread = mergeUsage([readUsage({ agents: [reading(), failure({ account: 'monalisa', slot: 3, error: 'No Copilot credential was available for monalisa.' })] })]);
  assert.deepEqual(unread.map((r) => [r.account, r.error]), [['octo', null], ['monalisa', 'No Copilot credential was available for monalisa.']]);
});

test('rows come fullest window first', () => {
  const r = readUsage({ agents: [
    reading({ agent: 'codex', label: 'Codex', account: null }, [{ key: 'primary', label: '5h', percent: 5 }, { key: 'secondary', label: 'Weekly', percent: 91 }]),
    reading({}, [{ key: 'plan', label: 'Plan', percent: 79 }]),
  ] });
  assert.deepEqual(mergeUsage([r]).map((e) => e.agent), ['codex', 'copilot']);
});

test('colours turn amber at 75% and red at 90%', () => {
  assert.deepEqual([74.9, 75, 89.9, 90, 120, NaN].map(severity), ['', 'warn', 'warn', 'crit', 'crit', '']);
});

test('resets read as days, hours or minutes, and a reset gone by is due', () => {
  assert.equal(until(iso(25 * 24 * H), NOW), 'in 25d');
  assert.equal(until(iso(4 * H), NOW), 'in 4h');
  assert.equal(until(iso(12 * 60000), NOW), 'in 12m');
  assert.equal(until(iso(20000), NOW), 'in 1m');
  assert.equal(until(iso(-1000), NOW), 'due');
  assert.equal(until(null, NOW), '');
});

test('ages read as the card reads them, and an hour old is stale', () => {
  assert.equal(ago(NOW - 89000, NOW), 'just now');
  assert.equal(ago(NOW - 37 * 60000, NOW), '37m ago');
  assert.equal(ago(NOW - 5 * H, NOW), '5h ago');
  assert.equal(ago(NOW - 3 * 24 * H, NOW), '3d ago');
  assert.equal(isStale(NOW - H, NOW), false);
  assert.equal(isStale(NOW - H - 1, NOW), true);
});

test('pace is worked out when drawn, only with both ends of the window, and a tenth either way is on pace', () => {
  const w = { startsAt: iso(-10 * 24 * H), resetsAt: iso(20 * 24 * H) };
  assert.equal(elapsed(w, NOW), 33.3);
  assert.equal(elapsed({ resetsAt: w.resetsAt }, NOW), null);
  assert.equal(elapsed({ startsAt: iso(H), resetsAt: iso(2 * H) }, NOW), 0);
  assert.deepEqual(pace(78.9, 33.3), { text: '46% ahead of pace', ahead: true });
  assert.deepEqual(pace(10, 33.3), { text: '23% under pace', ahead: false });
  assert.deepEqual(pace(40, 33.3), { text: 'on pace', ahead: false });
});

test('an error is shown unless there is a reading that is not stale', () => {
  const fresh = { error: 'x', windows: [{}], at: NOW - 60000 };
  assert.equal(showsError(fresh, NOW), false);
  assert.equal(showsError({ ...fresh, at: NOW - 2 * H }, NOW), true);
  assert.equal(showsError({ error: 'x', windows: [], at: NOW }, NOW), true);
  assert.equal(showsError({ error: null, windows: [], at: NOW }, NOW), false);
});

test('a machine reads as its sessions and version, marked dev when built from a working copy', () => {
  assert.equal(machineStatus({ sessions: 3, hostVersion: '0.1.0', dev: true }), '3 sessions \u00b7 0.1.0 (dev)');
  assert.equal(machineStatus({ sessions: 1, hostVersion: '0.2.0' }), '1 session \u00b7 0.2.0');
  assert.equal(machineStatus({ hostVersion: '0.0.9' }), '0.0.9');
  assert.equal(machineStatus({}), '');
});

test('amounts are whole and grouped', () => {
  assert.equal(amount(1183313.4), '1,183,313');
  assert.equal(amount(null), '');
});

test('the book keeps each machine latest report and tells listeners', () => {
  const book = new UsageBook();
  let heard = 0;
  book.onChange(() => { heard++; });
  book.set('m1', { agents: [reading()] });
  book.set('m1', { agents: [reading({}, [{ key: 'plan', label: 'Plan', percent: 50 }])] });
  book.set('m2', { agents: [] });
  assert.equal(book.size, 2);
  assert.equal(book.entries()[0].windows[0].percent, 50);
  book.clear();
  assert.equal(book.size, 0);
  assert.equal(heard, 4);
});

const claude = (extra = {}) => ({ agent: 'claude', label: 'Claude Code', accountKey: '3f9a2c71b0de', plan: 'max', source: 'api', measuredAt: iso(-H), windows: [{ key: 'weekly_all', label: 'Weekly', percent: 41.3 }], ...extra });

test('a kept reading outlives the machine that read it, saying which machine that was, and nothing is kept that was never seen', () => {
  let kept = null;
  const book = new UsageBook({ now: () => NOW });
  book.useCache({ entries: [], save: (e) => { kept = e; } });
  book.set('vm-id', { agents: [claude()] }, 'Rehearsal VM');
  book.set('desk-id', { agents: [reading()] }, 'Desk');
  assert.deepEqual(kept.map((e) => [e.agent, e.readBy.name]).sort(), [['claude', 'Rehearsal VM'], ['copilot', 'Desk']]);

  // Another visit: no machine has said anything yet, and the VM never will.
  const later = new UsageBook({ now: () => NOW + 2 * H });
  later.useCache({ entries: JSON.parse(JSON.stringify(kept)), save: () => {} });
  assert.equal(later.size, 0);
  assert.equal(later.empty, false);
  const row = later.entries().find((e) => e.agent === 'claude');
  assert.deepEqual([row.windows[0].percent, readOn(row), ago(row.at, NOW + 2 * H), isStale(row.at, NOW + 2 * H)], [41.3, 'read on Rehearsal VM', '3h ago', true]);
  assert.deepEqual(new UsageBook().entries(), []);
});

test('a reading a host passed on keeps the machine that took it, and a kept one a fortnight old goes', () => {
  const book = new UsageBook({ now: () => NOW });
  let kept = null;
  book.useCache({ entries: [claude({ accountKey: 'aaaabbbbcccc', measuredAt: iso(-15 * 24 * H), readBy: { id: 'old', name: 'Old' } })], save: (e) => { kept = e; } });
  book.set('desk-id', { agents: [claude({ readBy: { id: 'a'.repeat(32), name: 'Rehearsal VM' } })] }, 'Desk');
  assert.deepEqual(book.entries().map(readOn), ['read on Rehearsal VM']);
  assert.deepEqual(kept.map((e) => e.accountKey), ['3f9a2c71b0de']);
});

test('a kept reading is not shown once it is two weeks old, though nothing has been heard since', () => {
  let now = NOW;
  const book = new UsageBook({ now: () => now });
  book.useCache({ entries: [claude({ measuredAt: iso(-13 * 24 * H), readBy: { id: 'vm-id', name: 'Rehearsal VM' } })], save: () => {} });
  assert.equal(book.entries().length, 1);
  now += 2 * 24 * H;
  assert.deepEqual(book.entries(), []);
  assert.equal(book.empty, true);
});

test('a forgotten machine takes what it read with it, even where another host passes it on, and signing out keeps nothing', () => {
  const book = new UsageBook({ now: () => NOW });
  let kept = null;
  const byVm = { readBy: { id: 'vm-id', name: 'Rehearsal VM' } };
  book.useCache({ entries: [claude(byVm)], save: (e) => { kept = e; } });
  book.set('desk-id', { agents: [reading()] }, 'Desk');
  book.delete('vm-id', { forget: true });
  assert.deepEqual(book.entries().map((e) => e.agent), ['copilot']);
  assert.deepEqual(kept.map((e) => e.agent), ['copilot']);
  // The desk has not heard yet, and passes the VM's reading on.
  book.set('desk-id', { agents: [reading(), claude(byVm)] }, 'Desk');
  assert.deepEqual(book.entries().map((e) => e.agent), ['copilot']);
  assert.deepEqual(kept.map((e) => e.agent), ['copilot']);
  // Back, and the account's again.
  book.set('vm-id', { agents: [claude()] }, 'Rehearsal VM');
  assert.deepEqual(book.entries().map((e) => e.agent).sort(), ['claude', 'copilot']);
  book.clear();
  assert.equal(book.empty, true);
  const before = kept;
  book.set('vm-id', { agents: [claude({ accountKey: 'aaaabbbbcccc' })] }, 'Rehearsal VM');
  assert.equal(kept, before);
});

test('a reading past two weeks does not hide a current failure of the same allowance, shown or kept', () => {
  const book = new UsageBook({ now: () => NOW });
  let kept = null;
  book.useCache({ entries: [], save: (e) => { kept = e; } });
  // A host still passes on a reading taken a fortnight and a day ago; another has just failed to read.
  book.set('laptop-id', { agents: [claude({ measuredAt: iso(-15 * 24 * H), readBy: { id: 'vm-id', name: 'Rehearsal VM' } })] }, 'Laptop');
  book.set('desk-id', { agents: [claude({ windows: [], source: 'none', measuredAt: iso(-60000), error: 'rate limited' })] }, 'Desk');
  assert.deepEqual(book.entries().map((e) => e.error), ['rate limited']);
  assert.deepEqual(kept.map((e) => e.error), ['rate limited']);
});

test('a machine forgotten while what was kept is being read is not brought back by it, and the store hears it went', () => {
  const book = new UsageBook({ now: () => NOW });
  let kept = null;
  book.delete('vm-id', { forget: true });
  book.useCache({ entries: [claude({ readBy: { id: 'vm-id', name: 'Rehearsal VM' } })], save: (e) => { kept = e; } });
  assert.deepEqual(book.entries(), []);
  assert.deepEqual(kept, []);
});

test('a machine forgotten while the page was closed goes once the roster is known, whichever comes first', () => {
  const byGone = { readBy: { id: 'gone-id', name: 'Old laptop' } };
  const byVm = { readBy: { id: 'vm-id', name: 'Rehearsal VM' } };
  for (const rosterFirst of [false, true]) {
    const book = new UsageBook({ now: () => NOW });
    let kept = null;
    if (rosterFirst) { book.roster(['desk-id', 'vm-id']); }
    book.useCache({ entries: [claude(byGone), claude({ ...byVm, accountKey: 'aaaabbbbcccc' })], save: (e) => { kept = e; } });
    if (!rosterFirst) { book.roster(['desk-id', 'vm-id']); }
    assert.deepEqual(book.entries().map(readOn), ['read on Rehearsal VM']);
    assert.deepEqual(kept.map(readOn), ['read on Rehearsal VM']);
  }
  // A relay without a roster says nothing either way.
  const book = new UsageBook({ now: () => NOW });
  book.useCache({ entries: [claude(byGone)], save: () => {} });
  book.roster(null);
  assert.equal(book.entries().length, 1);
});

test('a failure kept beside a reading keeps its own time, and stands once the reading has passed two weeks', () => {
  let kept = null;
  const book = new UsageBook({ now: () => NOW });
  book.useCache({ entries: [], save: (e) => { kept = e; } });
  book.set('vm-id', { agents: [claude({ measuredAt: iso(-14 * 24 * H + H) })] }, 'Rehearsal VM');
  book.set('desk-id', { agents: [claude({ windows: [], source: 'none', measuredAt: iso(-60000), error: 'rate limited' })] }, 'Desk');
  assert.deepEqual(kept.map((e) => [hasWindows(e), e.measuredAt]), [[true, iso(-14 * 24 * H + H)], [false, iso(-60000)]]);

  // Two hours on, another visit: the reading has gone, its failure has not.
  const later = new UsageBook({ now: () => NOW + 2 * H });
  later.useCache({ entries: JSON.parse(JSON.stringify(kept)), save: () => {} });
  assert.deepEqual(later.entries().map((e) => e.error), ['rate limited']);
});

const hasWindows = (e) => e.windows.length > 0;

test('a machine a roster leaves out stays out once there is no roster, until one lists it again', () => {
  const book = new UsageBook({ now: () => NOW });
  book.useCache({ entries: [], save: () => {} });
  // The desk has not heard yet that the old laptop was forgotten, and passes its reading on.
  book.set('desk-id', { agents: [claude({ readBy: { id: 'gone-id', name: 'Old laptop' } })] }, 'Desk');
  book.roster(['desk-id']);
  assert.deepEqual(book.entries(), []);
  book.roster(null);
  assert.deepEqual(book.entries(), []);
  book.roster(['desk-id', 'gone-id']);
  assert.deepEqual(book.entries().map(readOn), ['read on Old laptop']);
});

test('a machine whose discovery is held says so on its status line, as agent-ha-bridge did', () => {
  assert.equal(machineStatus({ sessions: 2, hostVersion: '0.1.0', held: 'an unrecognised copilot process (pid 4120)' }),
    '2 sessions \u00b7 0.1.0 \u00b7 discovery held: an unrecognised copilot process (pid 4120)');
  assert.equal(machineStatus({ sessions: 2, hostVersion: '0.1.0', held: null }), '2 sessions \u00b7 0.1.0');
});

test('a machine that takes nothing from the phone says so on its status line, and why', () => {
  assert.equal(machineStatus({ sessions: 2, hostVersion: '0.1.0', readOnly: 'agent-ha-bridge is running for this user' }),
    '2 sessions \u00b7 0.1.0 \u00b7 read-only: agent-ha-bridge is running for this user');
});
