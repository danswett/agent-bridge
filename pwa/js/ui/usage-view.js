// The agent usage panel: agent-ha-bridge's usage card, row for row. A folded line with each
// agent's fullest window in its own colour, and per allowance its windows as bars, with an
// even-pace mark, the amounts, when it resets, how old the reading is, and the error when
// there is nothing fresher to show.
import { h } from './dom.js';
import { ago, amount, elapsed, hasReading, headline, isStale, pace, severity, showsError, until } from '../usage.js';

const OPEN = 'agent-bridge.app.usage.open';

export class UsageView {
  #timer = null;

  constructor(section, book, { storage = globalThis.localStorage, now = () => Date.now(), doc = globalThis.document } = {}) {
    Object.assign(this, { section, book, storage, now, doc });
    this.toggle = section.querySelector('.usage-toggle');
    this.summary = section.querySelector('.usage-summary');
    this.body = section.querySelector('.usage-body');
    this.open = this.#read() !== '0';
    this.toggle.addEventListener('click', () => {
      this.open = !this.open;
      try { this.storage?.setItem(OPEN, this.open ? '1' : '0'); } catch { /* a nicety */ }
      this.render();
    });
    book.onChange(() => this.render());
    // Ages and resets move on without any update from a host. Half a minute is the longest
    // interval that cannot leave a stale minute on screen; none at all while hidden.
    const resume = () => {
      if (this.#timer || doc?.visibilityState === 'hidden') { return; }
      this.#timer = setInterval(() => this.render(), 30000);
    };
    doc?.addEventListener?.('visibilitychange', () => {
      if (doc.visibilityState === 'hidden') { clearInterval(this.#timer); this.#timer = null; return; }
      this.render();
      resume();
    });
    resume();
    this.render();
  }

  #read() { try { return this.storage?.getItem(OPEN) ?? null; } catch { return null; } }

  render() {
    const now = this.now();
    const entries = this.book.entries();
    // Shown once any machine offers usage at all.
    this.section.hidden = this.book.size === 0;
    this.toggle.setAttribute('aria-expanded', String(this.open));
    this.body.hidden = !this.open;

    // Each figure in the colour of its own fullest bar, so one agent running short does not
    // turn every agent beside it amber.
    const figures = entries.filter((e) => headline(e) !== null);
    this.summary.replaceChildren(...(figures.length
      ? figures.flatMap((e, i) => [i > 0 ? ' \u00b7 ' : null, h('span', { class: `usage-figure ${severity(headline(e))}`.trim() }, `${e.label} ${Math.round(headline(e))}%`)]).filter(Boolean)
      : ['No usage reported yet']));

    if (!this.open) { return; }
    this.body.replaceChildren(...entries.map((e) => this.#group(e, now)));
  }

  #group(entry, now) {
    const head = h('div', { class: 'usage-head' },
      h('span', { class: 'usage-name' }, entry.label),
      h('span', { class: 'usage-account' }, [entry.account, entry.plan].filter(Boolean).join(' \u00b7 ')),
      h('span', { class: `usage-age${isStale(entry.at, now) ? ' stale' : ''}` }, ago(entry.at, now)));
    return h('div', { class: 'usage-group', dataset: { agent: entry.agent } },
      head,
      hasReading(entry) ? entry.windows.map((w) => this.#window(w, now)) : null,
      showsError(entry, now) ? h('p', { class: 'usage-error' }, entry.error) : null);
  }

  #window(w, now) {
    const level = severity(w.percent);
    const fill = h('div', { class: `usage-fill ${level}`.trim() });
    // CSSOM, not a style attribute: the page's policy allows no inline styles.
    fill.style.width = `${Math.min(100, Math.max(0, w.percent))}%`;
    const gone = elapsed(w, now);
    const bar = h('div', { class: 'usage-bar', role: 'meter', 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-valuenow': w.percent, 'aria-label': `${w.label}: ${w.percent}% used` }, fill);
    if (gone !== null) {
      // Where the spend would be if it were even across the window: two thirds gone means
      // thrift on the 25th and trouble on the 5th, and the bar alone cannot say which.
      const mark = h('div', { class: 'usage-pace', title: `${Math.round(gone)}% of the period gone` });
      mark.style.left = `${gone}%`;
      bar.append(mark);
    }
    const used = amount(w.used);
    const limit = amount(w.limit);
    const resets = until(w.resetsAt, now);
    const verdict = gone === null ? null : pace(w.percent, gone);
    return h('div', { class: 'usage-window', dataset: { key: w.key } },
      h('div', { class: 'usage-top' }, h('span', { class: 'usage-label' }, w.label), h('span', { class: 'usage-percent' }, `${w.percent}%`)),
      bar,
      used || resets ? h('div', { class: 'usage-foot' },
        h('span', { class: 'usage-amount' }, used && limit ? `${used} / ${limit}${w.unit ? ` ${w.unit}` : ''}` : ''),
        h('span', { class: 'usage-reset' }, resets ? `resets ${resets}` : '')) : null,
      // The mark named and read, because a phone cannot hover and a legend nobody reads is
      // not a legend.
      verdict ? h('div', { class: 'usage-pacefoot' },
        h('span', { class: 'usage-pacemark' }, `\u2502 even pace ${Math.round(gone)}%`),
        h('span', { class: `usage-verdict${verdict.ahead ? ' ahead' : ''}` }, verdict.text)) : null);
  }
}
