import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sentAttachments } from '../../js/sent-attachments.js';

const ID = '0123456789abcdef0123456789abcdef';
const windows = (name) => `@C:\\Users\\demo\\AppData\\Local\\AgentBridge\\default\\attachments\\${ID}-${name}`;
const unix = (name) => `@/home/demo/.agent-bridge/default/attachments/${ID}-${name}`;

test('staged references become file names in order and leave the message words intact', () => {
  assert.deepEqual(sentAttachments(`${windows('report.pdf')} ${unix('diagram.png')} What is this?\nKeep this line.`), {
    text: 'What is this?\nKeep this line.', files: ['report.pdf', 'diagram.png'],
  });
  assert.deepEqual(sentAttachments(`See ${unix('notes.md')} next.`), { text: 'See next.', files: ['notes.md'] });
});

test('pasted images alone have names without their staging hash or an empty text row', () => {
  assert.deepEqual(sentAttachments(`${windows('pasted.png')}\n${windows('pasted.jpg')}`), {
    text: '', files: ['Pasted image', 'Pasted image 2'],
  });
});

test('ordinary paths and text that only resembles a staged reference stay verbatim', () => {
  for (const text of [
    '  Keep\nthis spacing.  ', '@C:\\work\\notes.md', '@/home/demo/attachments/report.pdf',
    `https://example.test/attachments/${ID}-report.pdf`, `mail${unix('notes.md')}`,
    `@/home/demo/attachments/${ID}-nested/file.txt`, '<b>plain text</b>',
  ]) {
    assert.deepEqual(sentAttachments(text), { text, files: [] });
  }
});
