// A stub MCP server that asks one form (an elicitation) of a chosen shape the moment the
// Copilot CLI has started it, so a form can be drawn without a model call or any quota.
// Usage: node stub-mcp.js <shape> [delaySeconds]
// Shapes: choice, choice-text, choice-yesno-text, number, long-title, tall, other
// Only stub text is ever drawn. Nothing here reads files, the network or the environment.
'use strict';
const fs = require('fs');
const path = require('path');

// node stub-mcp.js --print <shape> writes the form's arguments (message and requestedSchema)
// and exits, so the driver reads the form the host would read.
const printing = process.argv[2] === '--print';
const shape = (printing ? process.argv[3] : process.argv[2]) || 'choice';
const delayMs = Math.round(parseFloat(process.argv[3] || '8') * 1000);
const logPath = process.env.FORMS_SOAK_STUB_LOG || path.join(__dirname, 'stub-mcp.log');
const log = m => { try { fs.appendFileSync(logPath, `${new Date().toISOString()} ${m}\n`); } catch { /* best effort */ } };

const para = 'This is a long question drawn the way an agent asks one when it explains its reasoning at length before it asks, so that the form grows taller than the console window that shows it.';
const choice = { type: 'string', title: 'Which approach', enum: ['first', 'second', 'third'] };
const title = 'Which of these several approaches would you like the agent to take for the work described above, given that the first is the quickest, the second is the safest and the third is the most thorough';

const shapes = {
  choice: { message: 'Pick one of the stub options.', fields: { choice } },
  'choice-text': { message: 'Pick one and add a note.', fields: { choice, notes: { type: 'string', title: 'Notes' } } },
  'choice-yesno-text': { message: 'Pick one, say yes or no, and add a note.', fields: { choice, proceed: { type: 'boolean', title: 'Proceed now' }, notes: { type: 'string', title: 'Notes' } } },
  number: { message: 'How many stub items?', fields: { count: { type: 'number', title: 'Count' } } },
  'long-title': { message: 'A long field title.', fields: { choice: { ...choice, title } } },
  tall: { message: Array.from({ length: 12 }, (_, i) => `${i + 1}. ${para}`).join('\n'), fields: { choice, proceed: { type: 'boolean', title: 'Proceed now' }, notes: { type: 'string', title: 'Notes' } } },
  // The host answers "Other" by the {other:"text"} value and refuses it before any key.
  other: { message: 'Pick one or type your own.', fields: { choice } },
};
const spec = shapes[shape];
if (!spec) { process.stderr.write(`unknown shape ${shape}\n`); process.exit(2); }

const params = { message: spec.message, requestedSchema: { type: 'object', properties: spec.fields, required: Object.keys(spec.fields).slice(0, 1) } };
if (printing) { process.stdout.write(JSON.stringify(params)); process.exit(0); }

const send = o => process.stdout.write(JSON.stringify(o) + '\n');
let buf = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', d => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (!line) continue;
    let m;
    try { m = JSON.parse(line); } catch { continue; }
    log('in ' + line.slice(0, 300));
    if (m.method === 'initialize') {
      send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'Copilot', version: '1.0.0' } } });
    } else if (m.method === 'notifications/initialized') {
      setTimeout(() => {
        log('ask ' + shape);
        send({ jsonrpc: '2.0', id: 'ask1', method: 'elicitation/create', params });
      }, delayMs);
    } else if (m.method === 'tools/list') {
      send({ jsonrpc: '2.0', id: m.id, result: { tools: [] } });
    } else if (m.id === 'ask1') {
      log('answer ' + JSON.stringify(m.result ?? m.error).slice(0, 300));
    } else if (m.id !== undefined && m.method) {
      send({ jsonrpc: '2.0', id: m.id, result: {} });
    }
  }
});
process.stdin.on('end', () => process.exit(0));
