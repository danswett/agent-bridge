// Why an entry, or a part of one, is refused: the same codes as the C# library's
// (src/AgentBridge.Roster/Refusal.cs), which docs/protocol/vectors/roster.json names.
export const REFUSAL = Object.freeze({
  json: 'json',
  duplicate: 'duplicate-member',
  unknown: 'unknown-member',
  missing: 'missing-member',
  type: 'type',
  value: 'value',
  version: 'version',
  hostProof: 'host-proof',
  seq: 'seq',
  id: 'id',
  base64: 'base64',
  length: 'length',
  point: 'point',
  der: 'der',
  highS: 'high-s',
  name: 'name',
  account: 'account',
  time: 'time',
  approval: 'approval',
  order: 'order',
  count: 'count',
  device: 'device',
  signer: 'signer',
  cosigner: 'cosigner',
  size: 'size',
  uvAuthenticatorData: 'uv-authenticator-data',
  uvRpId: 'uv-rp-id',
  uvFlags: 'uv-flags',
  uvBackup: 'uv-backup',
  uvClientData: 'uv-client-data',
  uvType: 'uv-type',
  uvChallenge: 'uv-challenge',
  uvOrigin: 'uv-origin',
  uvSignature: 'uv-signature',
});

export class RosterFormatError extends Error {
  constructor(code, detail) {
    super(`${code}: ${detail}`);
    this.name = 'RosterFormatError';
    this.code = code;
  }
}
