// The relay's admission as the PWA does it (#603): it asks for the challenge with the socket's
// opening, so its hello and proof go together and ready is one round trip away; a relay from
// before that sends the challenge after the hello, and the PWA proves it as it always did.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Relay } from '../../js/relay.js';
import { P256KeyPair, hex, unhex } from '../../js/noise-p256.js';
import { concat, utf8 } from '../../js/bytes.js';
import { deviceIdOf } from '../../js/identity.js';

const mac = async (secret, ...parts) => {
  const k = await crypto.subtle.importKey('raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return hex(new Uint8Array(await crypto.subtle.sign('HMAC', k, concat(...parts))));
};

// A relay's end of the socket: early, it sends the challenge as the socket opens when asked to.
// Every message either way is noted, in order, to count round trips after the opening.
function fakeRelay({ early, feedback = 0 }) {
  const flights = [];
  const relay = { flights, url: null, proofOk: null, feedbackRequest: null };
  class FakeSocket {
    readyState = 0;
    constructor(url) {
      relay.url = url;
      this.eph = null;
      this.nonce = crypto.getRandomValues(new Uint8Array(32));
      setTimeout(async () => {
        this.eph = await P256KeyPair.generate();
        this.readyState = 1;
        // Sent as the socket is accepted, before anything from the device; delivered after the
        // open event, as a browser orders them.
        if (early && url.endsWith('?admit=1')) { this.#reply({ t: 'challenge', eph: hex(this.eph.public), nonce: hex(this.nonce), early: true }); }
        this.onopen?.();
      });
    }

    #reply(m) {
      flights.push('relay');
      setTimeout(() => this.onmessage?.({ data: JSON.stringify(m) }));
    }

    async send(data) {
      flights.push('device');
      const m = JSON.parse(data);
      if (m.t === 'hello') {
        this.pub = unhex(m.pub);
        if (!(early && relay.url.endsWith('?admit=1'))) { this.#reply({ t: 'challenge', eph: hex(this.eph.public), nonce: hex(this.nonce), account: '1' }); }
      } else if (m.t === 'proof') {
        const device = await deviceIdOf(this.pub);
        const secret = await this.eph.dh(this.pub);
        const expected = early
          ? await mac(secret, utf8.encode('agent-bridge-relay-early-proof'), this.nonce)
          : await mac(secret, utf8.encode('agent-bridge-s4-relay-proof'), this.nonce, utf8.encode('1'));
        relay.proofOk = m.mac === expected;
        this.#reply(relay.proofOk
          ? { t: 'ready', device, account: '1', login: 'octo', ...(feedback ? { feedback: { max: feedback } } : {}) }
          : { t: 'refused' });
      } else if (m.t === 'feedback-put') {
        relay.feedbackRequest = m;
        this.#reply({ t: 'feedback-result', id: m.id, url: 'https://blob.example/once', expires: '2026-11-07T01:00:00Z' });
      }
    }

    close() {}
  }
  return { relay, FakeSocket };
}

const roundTrips = (flights) => Math.ceil(flights.filter((f, i) => f !== flights[i - 1]).length / 2);

for (const early of [true, false]) {
  test(`admission to a relay that ${early ? 'sends the challenge as the socket opens' : 'predates that'} takes ${early ? 'one round trip' : 'two round trips'} after the opening`, { timeout: 10000 }, async (t) => {
    const { relay, FakeSocket } = fakeRelay({ early });
    const real = globalThis.WebSocket;
    globalThis.WebSocket = FakeSocket;
    t.after(() => { globalThis.WebSocket = real; });
    const key = await P256KeyPair.generate();

    const r = await new Relay({ base: 'https://relay.example', key, deviceId: await deviceIdOf(key.public), name: 'Phone' }).connect('token');

    assert.equal(relay.url, 'wss://relay.example/v1/ws?admit=1');
    assert.equal(relay.proofOk, true);
    assert.equal(r.login, 'octo');
    // From the device's first message on: the early challenge came with the opening.
    const after = relay.flights.slice(relay.flights.indexOf('device'));
    assert.equal(roundTrips(after), early ? 1 : 2);
  });
}

test('feedback upload asks for a stored-size URL through the matching relay result', async (t) => {
  const { relay, FakeSocket } = fakeRelay({ early: true, feedback: 8192 });
  const real = globalThis.WebSocket;
  globalThis.WebSocket = FakeSocket;
  t.after(() => { globalThis.WebSocket = real; });
  const key = await P256KeyPair.generate();
  const r = await new Relay({ base: 'https://relay.example', key, deviceId: await deviceIdOf(key.public), name: 'Phone' }).connect('token');

  const slot = await r.feedbackPut(4096);

  assert.deepEqual(relay.feedbackRequest, { t: 'feedback-put', size: 4096, id: 1 });
  assert.equal(slot.url, 'https://blob.example/once');
  assert.equal(r.feedbackMaxBytes, 8192);
  assert.equal(r.busy, false);
});
