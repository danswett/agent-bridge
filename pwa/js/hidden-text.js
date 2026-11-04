// Text that is not on screen as it runs (#165, #220): characters drawn as nothing or somewhere
// other than where they run, and right-to-left letters, which reorder what is drawn around them.
// The Needs you lane refuses a prompt with either (needs-you.js); every code block, every code
// span, and all of an approval's card draw them as escapes and read left to right (markdown.js,
// ui.md §6.4). The app's twin is src/AgentBridge.App.Core/HiddenText.cs: the same sets, the same
// escapes, so the two never draw one command differently.

// Characters a reader cannot see as they run: format characters (bidi controls, zero-width
// spaces and joiners), controls other than a newline or a tab (a CR too: a shell runs it as part
// of the command), private-use and unassigned code points, lone surrogates, and the line and
// paragraph separators; every default-ignorable code point, drawn as nothing though it is
// there: variation selectors (which can make one name look like another), the combining grapheme
// joiner, Hangul fillers, tags (Unicode's DerivedCoreProperties, Default_Ignorable_Code_Point);
// and what draws blank but a shell takes as part of a word: every space but U+0020 (Zs: U+00A0,
// U+1680, U+2000-U+200A, U+202F, U+205F, U+3000) and the braille blank U+2800, so
// `true\u00A0#; curl … | sh` drew as a comment and bash ran the curl (#546).
const IGNORABLE = '\\u00AD\\u034F\\u061C\\u115F\\u1160\\u17B4\\u17B5\\u180B-\\u180F\\u200B-\\u200F\\u202A-\\u202E\\u2060-\\u206F\\u3164\\uFE00-\\uFE0F\\uFEFF\\uFFA0\\uFFF0-\\uFFF8'
  + '\\u{1BCA0}-\\u{1BCA3}\\u{1D173}-\\u{1D17A}\\u{E0000}-\\u{E0FFF}';
const HIDDEN_CLASS = `[\\p{Cf}\\p{Co}\\p{Cn}\\p{Cs}\\u2028\\u2029\\u2800${IGNORABLE}]|[^\\P{Cc}\\n\\t]|[^\\P{Zs} ]`;
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

// The line endings a shell does not end a line at, as the escapes reveal gives them: a bare
// carriage return (not one of a CRLF), NEL, and the line and paragraph separators. The Markdown
// parser keeps them in their line (markdown.js parseMarkdown), and prose draws them so: split at
// the CR, `echo safe \r#; echo SECOND` drew its second command on a line of its own, looking
// commented out, while bash runs it (#220).
export const revealLineSeparators = (text) => String(text ?? '').replace(/\r(?!\n)|[\u0085\u2028\u2029]/g, escape);

// A host sends an approval's hidden characters and right-to-left letters as escapes (\u202E,
// \u{E0041}; the host's ShownText.Reveal, #220). Each such escape read back as the character it
// stands for, so what is said of a message a host escaped (its notes, the Needs you lane's refusal)
// is what would be said of it as the agent wrote it. Six characters an agent wrote itself count too;
// an escape of anything else is left as it is.
export function unescapeShown(text) {
  return String(text ?? '').replace(/\\u(?:([0-9A-Fa-f]{4})|\{([0-9A-Fa-f]{1,6})\})/g, (written, four, any) => {
    const value = parseInt(four ?? any, 16);
    if (value > 0x10FFFF) { return written; }
    const c = String.fromCodePoint(value);
    return hasHidden(c) || hasRightToLeft(c) ? c : written;
  });
}

export const HIDDEN_NOTE = 'Contains hidden characters';
export const RIGHT_TO_LEFT_NOTE = 'Contains right-to-left text';

// What a drawn block says it held, beside it (ui.md §6.4): an escape can be read past while its
// character still acts. PowerShell ends a `#` comment at a bare CR, so
// `# just a note\u000D Write-Output RAN`, drawn on one line, still reads as a comment, and runs.
export const notesFor = (text) => [hasHidden(text) && HIDDEN_NOTE, hasRightToLeft(text) && RIGHT_TO_LEFT_NOTE].filter(Boolean);
