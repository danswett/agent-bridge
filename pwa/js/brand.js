import { BRAND } from '../design/brand.js';

// The product's name, wherever the PWA shows it (#538): the page and its labels, the
// manifest, notifications, the device name other devices see, and every sentence that names
// the product. It is the brand token (design/tokens.json brand, docs/design/brand.md, The name), as
// tools/design-gen generates it into design/brand.js. Scripts import it from here; the page,
// the manifest and the service worker can't, so tools/brand.mjs writes it into them, and
// test/unit/brand.test.mjs fails while they differ.
//
// Never from here: storage keys, IndexedDB and cache names, the worker's scope and messages,
// push subscriptions and the relay protocol keep their agent-bridge identifiers (#541), so a
// renamed app keeps its device key, sign-in, order and notifications.
//
// shortName goes under the icon on a home screen, which cuts anything longer than about 12
// characters; tools/brand.mjs refuses a longer one.
export { BRAND };

// The sentences that name the product, each worded once for every place that says it.
export const UNSUPPORTED_SESSIONS = `doesn't support ${BRAND.name} sessions`;
export const NOTIFICATIONS_BLOCKED = `Notifications are blocked for ${BRAND.name}.`;
