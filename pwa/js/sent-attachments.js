// AttachmentStore stages safe names with a random 32-hex prefix; delivery mentions each
// absolute path as one whitespace-free @ token. Other paths remain ordinary message text.
const REFERENCE = /^@(?:[a-z]:[\\/]|\/|\\\\)[^\s]*[\\/]attachments[\\/][0-9a-f]{32}-([0-9a-z._-]+)$/i;

export function sentAttachments(message, withRefs = false) {
  const parts = message.split(/(\s+)/);
  const files = [];
  let pasted = 0;
  for (let i = 0; i < parts.length; i += 2) {
    const match = REFERENCE.exec(parts[i]);
    if (!match) { continue; }
    const name = /^pasted\.(png|jpe?g|gif|webp)$/i.test(match[1])
      ? `Pasted image${++pasted > 1 ? ` ${pasted}` : ''}` : match[1];
    files.push(withRefs ? { name, ref: parts[i] } : name);
    parts[i] = '';
    if (i + 1 < parts.length) { parts[i + 1] = ''; }
  }
  return { text: files.length ? parts.join('').trim() : message, files };
}
