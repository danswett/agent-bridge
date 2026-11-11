// Settings > This app (#1246): the running release and the release controller's manual check.
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

export function mountRelease({ version, builtAt, check, doc = document, win = window } = {}) {
  const $ = (id) => doc.getElementById(id);
  const copy = $('release-copy');
  if (!copy || !$('release-id')) { return null; }
  const line = releaseLine(version, builtAt, win.navigator?.language);
  $('release-id').textContent = line.id;
  $('release-date').textContent = line.date;
  copy.addEventListener('click', async () => {
    $('release-status').textContent = await copyText(line.copy, doc) ? 'Copied' : "Couldn't copy. Select the version and copy it instead.";
  });
  $('release-check').addEventListener('click', () => check());
  $('release-reload').addEventListener('click', () => win.location.reload());
  const render = ({ phase, latest, error }) => {
    const newer = latest && latest.version !== version;
    $('release-newer').hidden = !newer;
    if (newer) {
      const next = releaseLine(latest.version, latest.builtAt, win.navigator?.language);
      $('release-newer-id').textContent = next.id;
      $('release-newer-date').textContent = next.date;
    }
    const button = $('release-check');
    button.hidden = phase === 'installing' || phase === 'ready';
    button.disabled = phase === 'checking';
    button.setAttribute('aria-busy', String(phase === 'checking'));
    button.textContent = phase === 'checking' ? 'Checking…' : 'Check for updates';
    $('release-reload').hidden = phase !== 'ready';
    $('release-result').textContent = error || ({
      current: 'Up to date',
      installing: 'Installing the new version…',
      ready: 'A new version of the app is ready.',
    }[phase] ?? '');
  };
  return { ...line, render };
}
