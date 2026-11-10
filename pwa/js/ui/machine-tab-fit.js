// Which machine tabs show and which wait under "N more" (ui.md §6.1, #351): the same rules as the
// Windows app's MachineTabFit.Split (#290), pure so a unit test can run them without a layout.
//
// widths: each tab's natural width, already cut to the longest a name may be. selected: the chosen
// tab's index (or -1). room: the bar's width. more: More's width. gap: the space between two tabs
// and before More.
// Returns { shown, more, chosenWidth }: the tabs' indexes in their order, the rest in theirs, and
// the width the chosen tab is cut to when even it alone would push More past the bar's edge (null
// when it keeps its own). A tab is never moved: the chosen one shows in its own place.
export function splitTabs(widths, selected, room, more, gap = 0) {
  const all = widths.map((_, i) => i);
  const total = widths.reduce((a, w) => a + w, 0) + gap * Math.max(0, widths.length - 1);
  if (total <= room) { return { shown: all, more: [], chosenWidth: null }; }

  // Something waits under More, which takes its own width first.
  let left = room - more;
  const shown = new Set();
  let chosenWidth = null;
  const chosen = selected >= 0 && selected < widths.length;
  const order = chosen ? [selected, ...all.filter((i) => i !== selected)] : all;
  for (const i of order) {
    // Each tab shown takes one gap: the ones between tabs, and the one before More.
    let need = widths[i] + gap;
    if (need > left && i === selected && shown.size === 0) {
      // The chosen tab shows even where nothing fits beside More, cut to what More leaves.
      chosenWidth = Math.max(0, left - gap);
      need = chosenWidth + gap;
    } else if (need > left) { break; }
    shown.add(i);
    left -= need;
  }
  return { shown: all.filter((i) => shown.has(i)), more: all.filter((i) => !shown.has(i)), chosenWidth };
}
