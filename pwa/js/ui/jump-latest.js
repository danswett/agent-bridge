// The "Jump to latest" pill's rules (ui.md §6.4 H3 and H3a, #1720): when it shows, and what it says.

// More than a screen above the end of the conversation (exactly a screen is not more). From what the
// view already remembers of its position, so deciding it reads no layout.
export const awayFrom = ({ top, max, client }) => max - top > client;

// What it says: a question waiting at the end first (H3a), then what arrived meanwhile (H3), else just the way back.
export function jumpText({ count = 0, question = false } = {}) {
  if (question) { return 'Question · Jump to latest'; }
  return count > 0 ? `${count} new · Jump to latest` : 'Jump to latest';
}
