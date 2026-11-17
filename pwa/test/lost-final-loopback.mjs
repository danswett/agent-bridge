export function lostFinalLoopback(transport, phase, schedule = setTimeout) {
  if (!['baseline', 'faults'].includes(phase)) { throw new Error('Unknown loopback phase'); }
  const send = transport.send.bind(transport), deliver = transport.deliver.bind(transport);
  const seen = new Set(), selected = new Set(), stats = { dropped: 0, requests: [] };
  transport.send = (frame) => {
    if (frame.op === 'session.reply') {
      stats.requests.push(frame.data.request);
      if (phase === 'faults' && !seen.has(frame.data.request)) { selected.add(frame.id); }
      seen.add(frame.data.request);
    }
    return new Promise((resolve) => schedule(resolve, 200)).then(() => send(frame));
  };
  transport.deliver = (frame) => {
    if (frame.re != null && typeof frame.ok === 'boolean' && selected.delete(frame.re)) { stats.dropped++; return; }
    schedule(() => deliver(frame), 200);
  };
  return stats;
}

export async function sampleLoopback(phase) {
  const [{ DemoHost }, { HostClient }, { ReplyService }] = await Promise.all([
    import('/js/demo.js'), import('/js/host-client.js'), import('/js/reply-service.js'),
  ]);
  const id = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
  const host = new DemoHost({ sessions: [{ id, agent: 'copilot', status: 'idle', activity: {} }] },
    { answerDelayMs: 0, progress: false, history: false });
  const transport = host.connect(), stats = lostFinalLoopback(transport, phase);
  const client = new HostClient(transport, {});
  const service = new ReplyService({ current: () => ({ hostId: 'demo', client }) });
  let deadline;
  try {
    await client.hello();
    const start = performance.now();
    const result = await new Promise((resolve, reject) => {
      deadline = setTimeout(() => reject(new Error(`${phase} exceeded 120 s`)), 120000);
      service.watch('demo', id, (entry) => {
        if (entry.state === 'done') { resolve({ outcome: entry.outcome, ms: performance.now() - start }); }
        if (entry.state === 'failed') { reject(new Error(entry.reason)); }
      });
      service.submit({ hostId: 'demo', sessionId: id, text: 'Lost-final reconstruction fixture' });
    });
    return { phase, ...result, ...stats, hostCopies: host.typedReplies.length };
  } finally { clearTimeout(deadline); service.clear(); client.close(); }
}
