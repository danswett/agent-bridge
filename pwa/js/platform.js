// Agent Bridge PWA: which platform's system face the page is set in, so inline mono can match
// that face's x-height (docs/design/ui.md §4.2, #215). Apple's SF, Android's Roboto, and
// otherwise Segoe UI Variable, the default in design/tokens.css. iPadOS asks for desktop pages
// as a Mac, so the user agent alone says "Macintosh" there too, which is still Apple's face.
// U7 (#168) owns the rest of the platform layer and may move this there.
export function platformOf(userAgent) {
  const ua = String(userAgent ?? '');
  if (/iPhone|iPad|iPod|Macintosh|Mac OS X/.test(ua)) { return 'apple'; }
  if (/Android/.test(ua)) { return 'android'; }
  return 'windows';
}

export function markPlatform(root = document.documentElement, userAgent = navigator.userAgent) {
  root.dataset.platform = platformOf(userAgent);
}