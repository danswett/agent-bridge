// Settings > This app (#1246): the release id and build date the app was stamped with
// (tools/build-site.mjs), so the owner and Quality can say which build was tested. Nothing is
// fetched here; it only reads what the build wrote into config.js.
import { copyText } from '../markdown.js';

const SHA = /^[0-9a-f]{40}$/;

// "64a4019 · built Nov 7, 2026": the 7-character id (a local build's `dev` as it is) and the date in
// the person's locale. An unreadable or missing date is left out rather than guessed.
export function releaseLine(version, builtAt, locale) {
  const id = typeof version === 'string' && SHA.test(version) ? version.slice(0, 7) : (typeof version === 'string' && version ? version : 'dev');
  const when = builtAt ? new Date(builtAt) : null;
  const date = when && !Number.isNaN(when.getTime())
    ? `· built ${new Intl.DateTimeFormat(locale, { year: 'numeric', month: 'short', day: 'numeric' }).format(when)}`
    : '';
  return { id, date, copy: date ? `${id} ${date}` : id };
}

export function mountRelease({ version, builtAt, doc = document, win = window } = {}) {
  const $ = (id) => doc.getElementById(id);
  const copy = $('release-copy');
  if (!copy || !$('release-id')) { return null; }
  const line = releaseLine(version, builtAt, win.navigator?.language);
  $('release-id').textContent = line.id;
  $('release-date').textContent = line.date;
  copy.addEventListener('click', async () => {
    $('release-status').textContent = await copyText(line.copy, doc) ? 'Copied' : "Couldn't copy. Select the version and copy it instead.";
  });
  return line;
}
