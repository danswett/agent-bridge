// Release-list cache for releases.slipgates.com (#927, #1163).
// Serves only GET /repos/danswett/agent-bridge/releases?per_page=N&page=N, the one GitHub call that is
// rate limited. Packages and feed files are not proxied: clients fetch them from GitHub download URLs.

const UPSTREAM = 'https://api.github.com/repos/danswett/agent-bridge/releases';
const PATH = '/repos/danswett/agent-bridge/releases';
const FRESH_SECONDS = 300;
const STALE_SECONDS = 86400;
const FETCHED_AT = 'x-fetched-at';

function failure(status, message) {
  return new Response(JSON.stringify({ error: message }), {
    status,
    headers: { 'content-type': 'application/json', 'cache-control': 'no-store' },
  });
}

function intParam(url, name, fallback, max) {
  const raw = url.searchParams.get(name);
  if (raw === null) return fallback;
  if (!/^\d{1,4}$/.test(raw)) return null;
  const n = Number(raw);
  return n >= 1 && n <= max ? n : null;
}

function answer(body, ageSeconds) {
  return new Response(body, {
    status: 200,
    headers: {
      'content-type': 'application/json',
      'cache-control': 'no-cache',
      age: String(Math.max(0, Math.floor(ageSeconds))),
    },
  });
}

async function refresh(perPage, page) {
  // Reading the body can reject mid-stream; that must fall back to the stale copy or 502 like any other failure.
  try {
    const upstream = await fetch(`${UPSTREAM}?per_page=${perPage}&page=${page}`, {
      headers: { accept: 'application/vnd.github+json', 'user-agent': 'agent-bridge-release-feed' },
    });
    if (upstream.status !== 200) return null;
    const body = await upstream.text();
    return Array.isArray(JSON.parse(body)) ? body : null;
  } catch {
    return null;
  }
}

export default {
  async fetch(request, _env, ctx) {
    const url = new URL(request.url);
    if (request.method !== 'GET') return failure(405, 'method not allowed');
    if (url.pathname !== PATH) return failure(404, 'not found');

    const perPage = intParam(url, 'per_page', 30, 100);
    const page = intParam(url, 'page', 1, 1000);
    if (perPage === null || page === null) return failure(400, 'bad per_page or page');

    // per_page and page are both part of the key, so different pages never share an answer.
    const cacheKey = new Request(`https://releases-cache.invalid${PATH}?per_page=${perPage}&page=${page}`);
    const cache = caches.default;
    const cached = await cache.match(cacheKey);
    const now = Date.now();
    let cachedBody = null;
    let cachedAge = Infinity;
    if (cached) {
      cachedBody = await cached.text();
      cachedAge = (now - Number(cached.headers.get(FETCHED_AT))) / 1000;
      if (cachedAge < FRESH_SECONDS) return answer(cachedBody, cachedAge);
    }

    const fresh = await refresh(perPage, page);
    if (fresh !== null) {
      const store = new Response(fresh, {
        headers: {
          'content-type': 'application/json',
          'cache-control': `public, max-age=${STALE_SECONDS}`,
          [FETCHED_AT]: String(now),
        },
      });
      ctx.waitUntil(cache.put(cacheKey, store));
      return answer(fresh, 0);
    }

    // The refresh failed: serve a stale copy with its real age, never an empty success.
    if (cachedBody !== null && Number.isFinite(cachedAge)) return answer(cachedBody, cachedAge);
    return failure(502, 'release list unavailable');
  },
};
