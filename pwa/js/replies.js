// Replying to a session (session.reply): what the phone may send, checked before it goes.
// docs/protocol/v1-sessions.md, "session.reply".
//
// checkReply mirrors src/AgentBridge.Protocol/Replies.cs (Replies.Check), case for case and
// with the same wording, so the app never sends a reply the host would refuse for its shape.
// Its last check is the app's own: a request over the message limit would never arrive.
import { utf8Bytes, wellFormed } from './answers.js';
import { AttachmentLimits } from './attachments.js';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const ReplyLimits = Object.freeze({
  // The reply's text, in UTF-8 bytes.
  text: 16 * 1024,
  // The whole request as sent must fit one protocol message (48 KiB), which only text full
  // of control characters (6 bytes of JSON each) could fail to.
  message: 48 * 1024,
});

// C#'s char.IsWhiteSpace and char.IsControl: the Unicode White_Space property and Cc.
const SKIPPED = /[\p{White_Space}\p{Cc}]/u;

// Null when the reply can be sent; otherwise why not, worded as the host words it. `data`
// is the session.reply request data ({ id, request, text, attachments? }).
export function checkReply(data) {
  const attachments = Array.isArray(data.attachments) ? data.attachments : [];
  if (attachments.length > AttachmentLimits.perReply) { return `a reply carries at most ${AttachmentLimits.perReply} attachments`; }
  if (attachments.some((a) => !UUID.test(a?.id ?? ''))) { return 'each attachment must be the UUID of one sent with attachment.start'; }
  if (new Set(attachments.map((a) => a.id.toLowerCase())).size !== attachments.length) { return 'the same attachment is in the reply twice'; }
  const text = typeof data.text === 'string' ? data.text : '';
  // Control characters are typed as spaces, so text of nothing else is empty too. With
  // attachments the words are optional.
  if ([...text].every((c) => SKIPPED.test(c)) && attachments.length === 0) { return 'the reply is empty'; }
  if (utf8Bytes(text) > ReplyLimits.text) { return `the reply is longer than ${ReplyLimits.text / 1024} KiB`; }
  // A leading / or ! would run on the machine as a command, not reach the agent.
  const first = [...text].find((c) => !SKIPPED.test(c));
  if (first === '/') { return 'a reply cannot start with /, which the agent would run as a command; type commands at the machine'; }
  if (first === '!') { return 'a reply cannot start with !, which the agent would run as a shell command; type commands at the machine'; }
  if (utf8Bytes(JSON.stringify({ id: 1, op: 'session.reply', data })) > ReplyLimits.message) { return 'the reply is too large to send'; }
  return null;
}

// The session.reply request data. Text is sent well-formed: a lone surrogate, which the C#
// host cannot read, becomes U+FFFD. Attachments are named by id only; their bytes went first.
export const replyRequest = ({ sessionId, request, text, attachments = [] }) => ({
  id: sessionId, request, text: wellFormed(text),
  ...(attachments.length ? { attachments: attachments.map((a) => ({ id: a.id })) } : {}),
});

// What the host will type: every control character and line or paragraph separator is a
// space, so a reply always arrives as one line (InjectableText.Collapse in C#).
export const typedForm = (text) => text.replace(/[\p{Cc}\u2028\u2029]/gu, ' ');
