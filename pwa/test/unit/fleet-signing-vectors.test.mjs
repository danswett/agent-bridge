import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { hex } from '../../js/roster/codec.js';
import { canonicalRecord, signedBytes, verifyRecord } from './fleet-signing-vector-helpers.mjs';

const v = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/fleet-registry-signing.json', import.meta.url), 'utf8'));
const copy = () => structuredClone(v.record);

test('registry canonical and framed bytes match the shared oracle', () => {
  assert.equal(canonicalRecord(v.record), v.canonical);
  assert.equal(canonicalRecord(v.equivalent), v.canonical);
  assert.equal(hex(signedBytes(v.record, v.context)), v.signedHex);
});

test('the valid host signature verifies but wrong keys and forged or missing signatures do not', async () => {
  assert.equal(await verifyRecord(v.record, v.hostSign, v.context), true);
  assert.equal(await verifyRecord(v.record, v.wrongKey, v.context), false);
  for (const sig of v.refusedSignatures) {
    assert.equal(await verifyRecord({ ...v.record, sig }, v.hostSign, v.context), false, sig);
  }
  const unsigned = copy();
  delete unsigned.sig;
  assert.equal(await verifyRecord(unsigned, v.hostSign, v.context), false);
});

test('every signed field mutation, including a relay-rewritten node, invalidates the signature', async () => {
  for (const mutation of v.mutations) {
    const changed = copy();
    const parts = mutation.path.split('.');
    let parent = changed;
    for (const part of parts.slice(0, -1)) parent = parent[part];
    parent[parts.at(-1)] = mutation.value;
    assert.notEqual(canonicalRecord(changed), v.canonical, mutation.name);
    assert.equal(await verifyRecord(changed, v.hostSign, v.context), false, mutation.name);
  }
});

test('the signature cannot be reused under another protocol context', async () => {
  for (const context of v.otherContexts) {
    assert.equal(await verifyRecord(v.record, v.hostSign, context), false, context);
  }
});

test('an authentic old sequence is cryptographically valid but stale in the vector ordering model', async () => {
  const old = { ...v.record, seq: 1, sig: v.oldSig };
  assert.equal(await verifyRecord(old, v.hostSign, v.context), true);
  assert.ok(old.seq < v.record.seq);
});
