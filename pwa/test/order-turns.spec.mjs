// #1642: the conversation is in the order things happened. A reply that finishes after the next
// message has arrived stays under its own prompt; nothing a turn said lands under a later header.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const ID = 'signin-1';
const turn = (n) => `${ID}-turn-${n}`;

// Activity as the host says it: the latest turn, the turn the reply text belongs to, and the words.
const say = (page, { turns, turn: latest, responseTurn, userMessage, response = null, reasoning = null }) => page.evaluate(({ ID, turns, latest, responseTurn, userMessage, response, reasoning }) => {
  const { host } = window.agentBridgeDemo;
  const s = host.sessions.get(ID);
  host.upsert({ ...s, status: 'working', activity: { ...s.activity, turnCount: turns, turn: latest, responseTurn, userMessage, response, reasoning, own: undefined, source: undefined } });
}, { ID, turns, latest, responseTurn, userMessage, response, reasoning });

const inOrder = async (page, ...texts) => {
  const body = await view(page, ID).locator('.stream-scroll').innerText();
  const at = texts.map((t) => body.indexOf(t));
  expect(at, body).not.toContain(-1);
  expect(at, body).toEqual([...at].sort((a, b) => a - b));
  for (const t of texts) { expect(body.split(t).length - 1, `${t} drawn once`).toBe(1); }
};

// The host holds only turn A, with a prompt and nothing stored yet, so what is drawn is what activity said.
async function start(page) {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate((ID) => {
    window.agentBridgeDemo.host.historyTurns.set(ID, [{ id: `${ID}-turn-1`, at: new Date(Date.now() - 60000).toISOString(), prompt: { text: 'Prompt A', from: 'person' }, parts: [], partsMore: 0, truncated: false, unreadable: false }]);
  }, ID);
  await open(page, ID);
}

test('a reply that ends after the owner message arrives stays under its own prompt', async ({ page }) => {
  await start(page);
  await say(page, { turns: 1, turn: turn(1), responseTurn: turn(1), userMessage: 'Prompt A', response: 'Reply A starts' });
  await expect(view(page, ID)).toContainText('Reply A starts');
  await say(page, { turns: 2, turn: turn(2), responseTurn: turn(1), userMessage: 'Message B', response: 'Reply A starts and ends' });
  await expect(view(page, ID)).toContainText('Message B');
  await expect(view(page, ID)).toContainText('Reply A starts and ends');
  await inOrder(page, 'Prompt A', 'Reply A starts and ends', 'Message B');
  await say(page, { turns: 2, turn: turn(2), responseTurn: turn(2), userMessage: 'Message B', response: 'Reply B text', reasoning: 'Thinking B' });
  await expect(view(page, ID)).toContainText('Reply B text');
  await inOrder(page, 'Prompt A', 'Reply A starts and ends', 'Message B', 'Reply B text');
});

test('two prompts back to back keep each reply under its own prompt', async ({ page }) => {
  await start(page);
  await say(page, { turns: 1, turn: turn(1), responseTurn: turn(1), userMessage: 'Prompt A', response: 'Reply A final' });
  await say(page, { turns: 2, turn: turn(2), responseTurn: turn(1), userMessage: 'Prompt B', response: 'Reply A final' });
  await say(page, { turns: 3, turn: turn(3), responseTurn: turn(2), userMessage: 'Prompt C', response: 'Reply B final' });
  await expect(view(page, ID)).toContainText('Prompt C');
  await expect(view(page, ID)).toContainText('Reply B final');
  await inOrder(page, 'Prompt A', 'Reply A final', 'Prompt B', 'Reply B final', 'Prompt C');
});
