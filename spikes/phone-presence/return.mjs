export const mailboxBase = 'https://proof-mailbox.slipgates.com';
export const phoneOrigin = 'https://proof.slipgates.com';
export async function postEvidence(id, evidence, { fetch: send = globalThis.fetch, signal } = {}) {
  if (typeof id !== 'string' || !/^[0-9a-f]{32}$/.test(id)) throw new Error('Refused: invalid mailbox session.');
  const body = JSON.stringify(evidence);
  if (new TextEncoder().encode(body).length > 4096) throw new Error('Refused: evidence exceeds 4096 bytes.');
  for (let attempt = 0; attempt < 2; attempt++) {
    let response;
    try { response = await send(`${mailboxBase}/s/${id}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body, redirect: 'error', cache: 'no-store', signal }); }
    catch (error) {
      if (signal?.aborted || !(error instanceof TypeError) || attempt === 1) throw error;
      continue;
    }
    if (response.status !== 201) throw new Error(`Refused: automatic return HTTP ${response.status}.`);
    return;
  }
}
