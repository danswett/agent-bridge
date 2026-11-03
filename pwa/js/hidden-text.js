// Text that is not on screen as it runs (#165, #220): characters drawn as nothing or somewhere
// other than where they run, and right-to-left letters, which reorder what is drawn around them.
// The Needs you lane refuses a prompt with either (needs-you.js); every code block, every code
// span, and all of an approval's card draw them as escapes and read left to right (markdown.js,
// ui.md §6.4). The app's twin is src/AgentBridge.App.Core/HiddenText.cs: the same sets, the same
// escapes, so the two never draw one command differently.

// Characters a reader cannot see as they run: format characters (bidi controls, zero-width
// spaces and joiners), controls other than a newline or a tab (a CR too: a shell runs it as part
// of the command), private-use and unassigned code points, lone surrogates, and the line and
// paragraph separators; and every default-ignorable code point, drawn as nothing though it is
// there: variation selectors (which can make one name look like another), the combining grapheme
// joiner, Hangul fillers, tags (Unicode's DerivedCoreProperties, Default_Ignorable_Code_Point).
const IGNORABLE = '\\u00AD\\u034F\\u061C\\u115F\\u1160\\u17B4\\u17B5\\u180B-\\u180F\\u200B-\\u200F\\u202A-\\u202E\\u2060-\\u206F\\u3164\\uFE00-\\uFE0F\\uFEFF\\uFFA0\\uFFF0-\\uFFF8'
  + '\\u{1BCA0}-\\u{1BCA3}\\u{1D173}-\\u{1D17A}\\u{E0000}-\\u{E0FFF}';
const HIDDEN_CLASS = `[\\p{Cf}\\p{Co}\\p{Cn}\\p{Cs}\\u2028\\u2029${IGNORABLE}]|[^\\P{Cc}\\n\\t]`;
const HIDDEN = new RegExp(HIDDEN_CLASS, 'u');
// The right-to-left blocks: Hebrew to Arabic Extended, their presentation forms, and the
// supplementary planes' right-to-left ranges.
const RTL_CLASS = '[\\u0590-\\u08FF\\uFB1D-\\uFDFF\\uFE70-\\uFEFF\\u{10800}-\\u{10FFF}\\u{1E800}-\\u{1EFFF}]';
const RTL = new RegExp(RTL_CLASS, 'u');
const HIDDEN_ALL = new RegExp(HIDDEN_CLASS, 'gu');
const HIDDEN_OR_RTL_ALL = new RegExp(`${HIDDEN_CLASS}|${RTL_CLASS}`, 'gu');

export const hasHidden = (text) => HIDDEN.test(String(text ?? ''));
export const hasRightToLeft = (text) => RTL.test(String(text ?? ''));

const escape = (c) => {
  const hex = c.codePointAt(0).toString(16).toUpperCase();
  return hex.length > 4 ? `\\u{${hex}}` : `\\u${hex.padStart(4, '0')}`;
};

// Any such character drawn as a visible escape (\u202E), so a line reads in the order it runs:
// the lane's line, and every code block (#220, #296), whose Copy takes the escapes as drawn.
// rightToLeft: right-to-left letters too. Even in a left-to-right line, punctuation between two
// of them joins their run and is drawn reversed (bidi rules W6, N1, L2): `echo א#;ב;rm -rf ~`
// draws as `echo ב;#א;rm -rf ~`, commented out, and runs `rm -rf ~`. The owner's rule (#253,
// decision 3) is to escape them and still offer Approve once; Hebrew or Arabic in code or in an
// approval is the harder to read for it.
export function reveal(text, { rightToLeft = false } = {}) {
  return String(text ?? '').replace(rightToLeft ? HIDDEN_OR_RTL_ALL : HIDDEN_ALL, escape);
}

// Line endings a Markdown parser breaks on but a shell does not, kept as the escapes reveal gives
// them, before anything is parsed: a bare carriage return (not one of a CRLF), NEL, and the line
// and paragraph separators. Split at the CR, `echo safe \r#; echo SECOND` drew its second command
// on a line of its own, looking commented out, while bash runs it (#220).
export const keepLineSeparators = (text) => String(text ?? '').replace(/\r(?!\n)|[\u0085\u2028\u2029]/g, escape);
