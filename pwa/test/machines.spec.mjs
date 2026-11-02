// Every machine at once, in demo mode with more than one simulated machine (?machines=2 or 3):
// All, in the machine tabs above the sessions and on the Machines page, as the Windows app's
// picker offers it; every machine's
// sessions in one list, newest first, each card naming its machine; what each card sends going
// to its own machine; a read-only shadow machine beside the others; a machine that can't be
// reached saying so on its own; and the launch panel asking which machine to launch on.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

const LAPTOP = 'demo-laptop';
const SHADOW = 'demo-shadow';

const card = (page, host, id) => page.locator(`.session[data-host-id="${host}"][data-id="${id}"]`);
const row = (page, id) => page.locator(`#machine-list [data-device-id="${id}"] button`);
// The machine tabs above the sessions: All, then each machine.
const tab = (page, id) => page.locator(`#machine-tabs [data-device-id="${id}"]`);
const machine = (page, id, field) => page.evaluate(([id, field]) => window.agentBridgeDemo.machines[id][field], [id, field]);

async function openDemo(page, { machines = 3 } = {}) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=200&machines=${machines}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

test('with more than one machine, every machine is shown at first: one list, newest first, each card naming its machine', async ({ page }) => {
  await openDemo(page);
  await expect(tab(page, 'all')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('#machine-tabs [role="tab"]')).toHaveText(['All', 'Demo machine', 'Laptop', 'DEV-VM1 (shadow)']);
  await expect(row(page, 'all')).toHaveAttribute('aria-current', 'true');
  await expect(row(page, 'all').locator('.machine-name')).toHaveText('All machines');
  await expect(page.locator('#machine-list li').first()).toHaveAttribute('data-device-id', 'all');
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on all machines');

  const all = await page.evaluate(() => window.agentBridgeDemo.store.list().map((e) => ({ host: e.hostId, id: e.session.id })));
  expect(new Set(all.map((e) => e.host))).toEqual(new Set(['demo', LAPTOP, SHADOW]));
  await expect(page.locator('.session')).toHaveCount(all.length);
  await expect(page.locator('#session-count')).toHaveText(`${all.length} live on 3 machines`);
  const drawn = () => page.locator('.session').evaluateAll((cards) => cards.map((c) => ({ host: c.dataset.hostId, id: c.dataset.id })));
  // In your order (#164), first seen: each machine's sessions newest first, placed as each
  // machine finished sending them.
  await expect(page.locator('#session-sort')).toHaveValue('yours');
  const yours = await drawn();
  expect(new Set(yours.map((e) => `${e.host}/${e.id}`))).toEqual(new Set(all.map((e) => `${e.host}/${e.id}`)));
  for (const host of ['demo', LAPTOP, SHADOW]) {
    expect(yours.filter((e) => e.host === host)).toEqual(all.filter((e) => e.host === host));
  }
  // Most recent: as the app sorted them before, most recently updated first, whichever machine
  // they are on.
  await page.locator('#session-sort').selectOption('recent');
  await expect.poll(drawn).toEqual(all);
  const updated = await page.evaluate(() => window.agentBridgeDemo.store.list().map((e) => e.session.updatedAt));
  expect(updated).toEqual([...updated].sort().reverse());

  await expect(card(page, LAPTOP, 'laptop-ask-1').locator('.meta .machine-label')).toHaveText('Laptop');
  await expect(card(page, SHADOW, 'shadow-ask-1').locator('.meta .machine-label')).toHaveText('DEV-VM1 (shadow)');
  await expect(card(page, 'demo', '6f1c2b7e-claude-example').locator('.meta .machine-label')).toHaveText('Demo machine');

  // The tabs say what is shown, and the app bar how many are online.
  await expect(page.locator('#machine-tabs [aria-selected="true"]')).toHaveText('All');
  await expect(page.locator('#online')).toHaveAttribute('aria-label', '3 machines online');
});

test('picking one machine shows only its sessions, unlabelled, and the choice is remembered, as All is', async ({ page }) => {
  await openDemo(page);
  await tab(page, LAPTOP).click();
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on Laptop');
  await expect(page.locator('.session')).toHaveCount(2);
  await expect(page.locator(`.session:not([data-host-id="${LAPTOP}"])`)).toHaveCount(0);
  await expect(page.locator('.machine-label')).toHaveCount(0);
  await expect(page.locator('#session-count')).toHaveText('2 live');
  await expect(tab(page, LAPTOP)).toHaveAttribute('aria-selected', 'true');
  await expect(tab(page, LAPTOP)).toBeFocused();
  await expect(tab(page, 'all')).toHaveAttribute('aria-selected', 'false');
  await expect(row(page, LAPTOP)).toHaveAttribute('aria-current', 'true');
  await expect(row(page, 'all')).not.toHaveAttribute('aria-current', 'true');

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on Laptop');
  await expect(page.locator('.session')).toHaveCount(2);

  // Picked on the Machines page, as well: it goes back to the sessions.
  await go(page, 'machines');
  await row(page, 'all').click();
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on all machines');
  await expect(page.locator('#session-list')).toBeVisible();
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(row(page, 'all')).toHaveAttribute('aria-current', 'true');
  await expect(tab(page, 'all')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator(`.session[data-host-id="${LAPTOP}"]`)).toHaveCount(2);
});

test('an answer, a reply with files, and End session from a card go to the machine the card is on', async ({ page }) => {
  await openDemo(page);
  const asked = card(page, LAPTOP, 'laptop-ask-1');
  await asked.locator('.choice[data-value="now"]').click();
  await expect(asked.locator('.answer-receipt')).toHaveText('Your answer: Delivered. The session has your answer.');
  expect((await machine(page, LAPTOP, 'typed')).map((t) => t.said)).toEqual(['answer: "now"']);
  expect(await machine(page, 'demo', 'answerLog')).toEqual([]);

  // A small file through the channel and a large one through the relay's blob store, both
  // for the laptop.
  const idle = card(page, LAPTOP, 'laptop-copilot-1');
  const reply = idle.locator('form.reply');
  await reply.locator('input.file-picker').setInputFiles([
    { name: 'notes.md', mimeType: 'text/markdown', buffer: Buffer.alloc(40, 'a') },
    { name: 'photo.jpg', mimeType: 'image/jpeg', buffer: Buffer.alloc(400 * 1024, 7) },
  ]);
  await reply.getByRole('textbox', { name: 'Reply' }).fill('see these');
  await reply.getByRole('button', { name: 'Send' }).click();
  await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.');
  expect((await machine(page, LAPTOP, 'typedReplies')).map((r) => r.files.map((f) => f.name))).toEqual([['notes.md', 'photo.jpg']]);
  expect(await page.evaluate(() => window.agentBridgeDemo.blobs.log.filter((l) => l.op === 'put').map((l) => l.to))).toEqual([LAPTOP]);
  expect(await machine(page, 'demo', 'replyLog')).toEqual([]);
  expect(await machine(page, 'demo', 'attachmentLog')).toEqual([]);

  // The laptop's session is working on the reply now: ending it takes two presses.
  const end = idle.locator('.end-session .end-button');
  await end.click();
  await expect(end).toHaveAttribute('aria-label', /press again to confirm/);
  await end.click();
  await expect(idle).toHaveCount(0);
  expect((await machine(page, LAPTOP, 'stopLog')).length).toBe(2);
  expect(await machine(page, 'demo', 'stopLog')).toEqual([]);
});

test('a read-only shadow machine\'s cards take nothing from the phone and say why, beside machines that do', async ({ page }) => {
  await openDemo(page);
  const shadow = card(page, SHADOW, 'shadow-ask-1');
  await expect(shadow.locator('.question')).toHaveAttribute('data-answerable', 'false');
  await expect(shadow.locator('.question')).toContainText('agent-ha-bridge is running for this user');
  await expect(shadow.locator('form.reply')).toHaveCount(0);
  await expect(shadow.locator('.end-session')).toHaveCount(0);
  await expect(card(page, LAPTOP, 'laptop-ask-1').locator('.question')).toHaveAttribute('data-answerable', 'true');
  await go(page, 'machines');
  await expect(row(page, SHADOW).locator('.machine-status')).toContainText('View only');
});

test('a machine that can\'t be reached says so in a line of its own, keeps its cards, and the others go on', async ({ page }) => {
  await openDemo(page, { machines: 2 });
  await page.evaluate(() => {
    const laptop = window.agentBridgeDemo.machines['demo-laptop'];
    laptop.failOpen = 'the handshake timed out';
    laptop.disconnect();
  });
  const note = page.locator(`#host-state [data-host-id="${LAPTOP}"]`);
  await expect(note).toHaveText("Couldn't reach Laptop: the handshake timed out. Trying again.");
  await expect(page.locator(`.session[data-host-id="${LAPTOP}"]`)).toHaveCount(2);
  await expect(page.locator('#host-state .host-note')).toHaveCount(1);
  // The other machine still answers.
  const asked = card(page, 'demo', '02d9e5d5-6b35-49bd-b083-39572fbff648');
  await expect(asked.locator('.question')).toHaveAttribute('data-answerable', 'true');

  // Back: picked again, it is tried at once rather than at the next retry.
  await page.evaluate(() => { window.agentBridgeDemo.machines['demo-laptop'].failOpen = null; });
  await tab(page, 'all').click();
  await expect(note).toHaveCount(0);
  await expect(page.locator('#host-state')).toBeHidden();
});

test('a machine that leaves takes its sessions with it, and brings them back when it returns', async ({ page }) => {
  await openDemo(page, { machines: 2 });
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo-laptop', false));
  await expect(page.locator(`.session[data-host-id="${LAPTOP}"]`)).toHaveCount(0);
  await expect(row(page, 'all')).toHaveCount(0);
  await expect(page.locator('#machine-tabs')).toBeHidden();
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on Demo machine');
  await expect(page.locator('.machine-label')).toHaveCount(0);
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo-laptop', true));
  await expect(page.locator(`.session[data-host-id="${LAPTOP}"]`)).toHaveCount(2);
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on all machines');
});

test('with every machine shown, the launch panel asks which machine to launch on, and starts from the one last used', async ({ page }) => {
  await openDemo(page);
  const panel = page.locator('#launch');
  await go(page, 'launch');
  const which = panel.locator('select[name="machine"]');
  await expect(which).toBeVisible();
  // The shadow machine takes no launches, so it is not offered.
  await expect(which.locator('option')).toHaveText(['Demo machine', 'Laptop']);
  await expect(which).toHaveValue('demo');
  await which.selectOption(LAPTOP);
  await expect(panel.locator('.launch-title')).toHaveText('Start a session on Laptop');
  await expect(panel.locator('[name="agent"]')).toBeVisible();
  await panel.locator('[name="prompt"]').fill('Check the laptop backups');
  await panel.getByRole('button', { name: 'Launch' }).click();
  await expect(panel.locator('.launch-status')).toHaveText('Session started in a new worktree.');
  expect((await machine(page, LAPTOP, 'launchLog')).length).toBe(1);
  expect(await machine(page, 'demo', 'launchLog')).toEqual([]);
  const started = (await machine(page, LAPTOP, 'launchLog'))[0];
  expect(started.prompt).toBe('Check the laptop backups');
  // The new session arrives on the laptop's list, and is jumped to.
  await expect(page.locator(`.session[data-host-id="${LAPTOP}"].linked`)).toHaveCount(1);

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await go(page, 'launch');
  await expect(page.locator('#launch select[name="machine"]')).toHaveValue(LAPTOP);

  // One machine chosen: its panel asks nothing.
  await go(page, 'sessions');
  await tab(page, 'demo').click();
  await go(page, 'launch');
  await expect(page.locator('#launch select[name="machine"]')).toBeHidden();
  await expect(page.locator('#launch .launch-title')).toHaveText('Start a session on Demo machine');
});

test('a card\'s fold is remembered for its machine and session, so a session of the same id elsewhere keeps its own', async ({ page }) => {
  await openDemo(page);
  await card(page, LAPTOP, 'laptop-copilot-1').locator('.fold').click();
  const folds = await page.evaluate(() => Object.fromEntries(Object.keys(localStorage).filter((k) => k.startsWith('agent-bridge.app.fold.')).map((k) => [k.slice(22), JSON.parse(localStorage.getItem(k))])));
  expect(folds['demo-laptop/laptop-copilot-1']).toEqual({ folded: true });
  // Every card's record, its question's included, is its machine's.
  expect(Object.keys(folds).every((k) => /^demo(-laptop|-shadow)?\//.test(k))).toBe(true);
});

test('the launch machine picked holds even where the device cannot store it', async ({ page }) => {
  await page.addInitScript(() => {
    const set = Storage.prototype.setItem;
    Storage.prototype.setItem = function (k, v) { if (String(k).endsWith('launchHost')) { throw new DOMException('full', 'QuotaExceededError'); } return set.call(this, k, v); };
  });
  await openDemo(page);
  const panel = page.locator('#launch');
  await go(page, 'launch');
  await panel.locator('select[name="machine"]').selectOption(LAPTOP);
  await expect(panel.locator('select[name="machine"]')).toHaveValue(LAPTOP);
  await expect(panel.locator('.launch-title')).toHaveText('Start a session on Laptop');
});

test('a machine remembered from a demo with more machines says it is offline, and the demo still starts', async ({ page }) => {
  await openDemo(page);
  await tab(page, LAPTOP).click();
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('#host-state')).toContainText('is offline');
  // Still chosen while it is away, and said to be.
  await expect(tab(page, LAPTOP)).toHaveAttribute('aria-selected', 'true');
  await expect(tab(page, LAPTOP)).toHaveAccessibleName(/, offline$/);
  await tab(page, 'demo').click();
  await expect(page.locator('.session')).toHaveCount(4);
});

test('a launch resent when its machine comes back does not take the picker from a machine picked since', async ({ page }) => {
  await openDemo(page, { machines: 2 });
  const panel = page.locator('#launch');
  await go(page, 'launch');
  const which = panel.locator('select[name="machine"]');
  await which.selectOption(LAPTOP);
  await expect(panel.locator('[name="agent"]')).toBeVisible();
  // The laptop drops away just as Launch is pressed: the launch waits for it.
  await page.evaluate(() => {
    const laptop = window.agentBridgeDemo.machines['demo-laptop'];
    laptop.failOpen = 'the handshake timed out';
    laptop.disconnect();
  });
  await expect(page.locator(`#host-state [data-host-id="${LAPTOP}"]`)).toBeAttached();
  await panel.getByRole('button', { name: 'Launch' }).click();
  await expect(panel).toHaveAttribute('data-launch', 'waiting');
  await which.selectOption('demo');
  await expect(panel.locator('.launch-title')).toHaveText('Start a session on Demo machine');

  await page.evaluate(() => { window.agentBridgeDemo.machines['demo-laptop'].failOpen = null; });
  await go(page, 'sessions');
  await tab(page, 'all').click();
  await expect.poll(() => machine(page, LAPTOP, 'launchLog').then((l) => l.length)).toBe(1);
  await go(page, 'launch');
  // Drawn again, as any later change in the machines would.
  await page.evaluate(() => window.agentBridgeDemo.retarget());
  await expect(which).toHaveValue('demo');
  await expect(panel.locator('.launch-title')).toHaveText('Start a session on Demo machine');
});

// The Machines page on a phone (#213): a read-only machine, one with an update and one offline
// together. Each row is two lines, its icon and state on the first; what more it has sits inside
// it, in its text column; one button style, at the start of that column; no sentence and no id.
test('on a phone the Machines page keeps every row to two aligned lines, with its updates and actions inside it in one column', async ({ page }) => {
  await page.goto('?demo=1&static=1&machines=3&updates=1&offline=1#/machines');
  await page.waitForSelector('html[data-demo-ready]');
  const rowOf = (id) => page.locator(`#machine-list > li[data-device-id="${id}"]`);
  await expect(rowOf('demo').locator('.machine-update-line')).toHaveText('0.2.0 is available (running 0.1.0).');
  await expect(page.locator('#machine-list .machine.offline')).toHaveCount(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);

  const box = async (l) => (await l.boundingBox());
  for (const id of ['all', 'demo', LAPTOP, SHADOW, 'd0e1f2a3b4c5d6e7f8091a2b3c4d5e6f']) {
    const row = rowOf(id);
    const name = await box(row.locator('.machine-name'));
    const icon = await box(row.locator('svg.icon').first());
    const state = await box(row.locator('.machine-state'));
    const sub = await box(row.locator('.machine-sub'));
    // Icon and state on line 1, with the name, never on the row's middle.
    expect(Math.abs(icon.y + icon.height / 2 - (name.y + name.height / 2))).toBeLessThanOrEqual(2);
    expect(Math.abs(state.y + state.height - (name.y + name.height))).toBeLessThanOrEqual(3);
    // Line 2 is one line: at most two facts, no sentence.
    expect(sub.height).toBeLessThanOrEqual(20);
    expect(sub.y).toBeGreaterThan(name.y);
  }
  // A read-only machine says so in two words; the host's reason is the line's tooltip.
  await expect(rowOf(SHADOW).locator('.machine-flag')).toContainText('View only');
  await expect(rowOf(SHADOW).locator('.machine-status')).toHaveAttribute('title', /view only: agent-ha-bridge is running/);
  // No id on any row.
  await expect(page.locator('#machine-list .machine-id')).toHaveCount(0);
  for (const id of ['demo', LAPTOP, SHADOW]) { await expect(rowOf(id).locator('.machine-sub')).not.toContainText(id.slice(0, 8)); }

  // The update is inside its machine's row, and every action starts at the rows' text column.
  const row = await box(rowOf('demo'));
  const update = await box(rowOf('demo').locator('.machine-update'));
  expect(update.y + update.height).toBeLessThanOrEqual(row.y + row.height);
  const column = (await box(rowOf('demo').locator('.machine-name'))).x;
  const buttons = [rowOf('demo').locator('.machine-update-check'), page.locator('.machine-forget'), page.locator('.machine-check-all')];
  const sizes = [];
  for (const b of buttons) {
    const r = await box(b);
    expect(Math.abs(r.x - column)).toBeLessThanOrEqual(1);
    sizes.push(Math.round(r.height));
    await expect(b).toHaveClass(/\bbutton secondary small\b/);
  }
  expect(new Set(sizes).size).toBe(1);
  expect(sizes[0]).toBeGreaterThanOrEqual(44);

  // The sessions page's title row: the heading and the sort menu on one line.
  await go(page, 'sessions');
  const heading = await box(page.locator('#sessions-h'));
  const sort = await box(page.locator('#session-sort'));
  expect(Math.abs(heading.y + heading.height / 2 - (sort.y + sort.height / 2))).toBeLessThanOrEqual(8);
  expect(sort.x + sort.width).toBeLessThanOrEqual(390);
});
