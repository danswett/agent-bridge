import { test } from 'node:test';
import assert from 'node:assert/strict';
import { markPlatform, platformOf } from '../../js/platform.js';

test('an iPhone, an iPad asking as a Mac, and a Mac are all set in Apple\'s face', () => {
  for (const ua of [
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
  ]) { assert.equal(platformOf(ua), 'apple', ua); }
});

test('an Android phone is set in Roboto, and Windows or anything unknown in Segoe UI Variable', () => {
  assert.equal(platformOf('Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Mobile Safari/537.36'), 'android');
  assert.equal(platformOf('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36 Edg/131.0'), 'windows');
  assert.equal(platformOf(undefined), 'windows');
});

test('the page names its platform on the root element, where tokens.css picks the matching ratio', () => {
  const root = { dataset: {} };
  markPlatform(root, 'Mozilla/5.0 (Linux; Android 15)');
  assert.equal(root.dataset.platform, 'android');
});