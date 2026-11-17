import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

const vectors = JSON.parse(readFileSync(new URL('../../docs/protocol/vectors/roster-state.json', import.meta.url)));
const scenario = vectors.scenarios.find((s) => s.name === 'host-sign');

test('browser WebCrypto verifies the roster-authorized host before exposing a registry record', async ({ page }) => {
  await page.goto('/?demo=1');
  const result = await page.evaluate(async ({ account, generation, scenario }) => {
    const { FleetRegistryReader } = await import('/js/fleet-registry-reader.js');
    const { RosterVerifier } = await import('/js/roster/verifier.js');
    const { parseEntry } = await import('/js/roster/entry.js');
    const { base64Url } = await import('/js/roster/codec.js');
    const { frame, utf8 } = await import('/js/roster/framing.js');
    const { fromRaw } = await import('/js/roster/p256.js');
    const roster = new RosterVerifier(account, generation, () => 0, { acceptHostSign: true });
    for (const e of scenario.entries) await roster.offer([await parseEntry(utf8(e.wrapper))]);
    const point = roster.graph.at(roster.state.activeAdd(scenario.devices.H)).body.device.hostSign;
    const d = new Uint8Array(await crypto.subtle.digest('SHA-256', utf8('agent-bridge roster test key H sign')));
    const key = await crypto.subtle.importKey('jwk', { kty: 'EC', crv: 'P-256', x: base64Url(point.slice(1, 33)), y: base64Url(point.slice(33)), d: base64Url(d) },
      { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign']);
    const record = { at: '2026-11-12T18:00:00Z', epoch: '0123456789abcdef', machine: scenario.devices.H, seq: 1, sessions: [], truncated: false, v: 1 };
    const signed = frame(utf8('agent-bridge fleet registry v1'), utf8(JSON.stringify(record)));
    record.sig = base64Url(fromRaw(new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key, signed))));
    const reader = new FleetRegistryReader({ account, generation, roster: () => roster, now: () => Date.parse(record.at) });
    const read = () => reader.read([utf8(JSON.stringify({ machine: record.machine, online: true, record }))]);
    const accepted = await read();
    record.seq = 2;
    return { verified: accepted[0]?.verified, tampered: (await read()).length };
  }, { account: vectors.account, generation: vectors.generation, scenario });
  expect(result).toEqual({ verified: true, tampered: 0 });
});
