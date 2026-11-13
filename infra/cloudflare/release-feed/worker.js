// Release-list cache for releases.slipgates.com (#927, #1163).
// Serves only GET /repos/danswett/agent-bridge/releases?per_page=N&page=N, the one GitHub call that is
// rate limited. Packages and feed files are not proxied: clients fetch them from GitHub download URLs.

const UPSTREAM = 'https://api.github.com/repos/danswett/agent-bridge/releases';
const PATH = '/repos/danswett/agent-bridge/releases';
const FRESH_SECONDS = 300;
const STALE_SECONDS = 86400;
const FETCHED_AT = 'x-fetched-at';
const FRESH_FOR = 'x-fresh-for';
const UPSTREAM_ETAG = 'x-upstream-etag';

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

async function bodyTag(body) {
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(body));
  return `"${Array.from(new Uint8Array(hash), (byte) => byte.toString(16).padStart(2, '0')).join('')}"`;
}

function answer(request, body, ageSeconds, etag) {
  const matches = (request.headers.get('if-none-match') ?? '').split(',')
    .some((value) => value.trim() === '*' || value.trim().replace(/^W\//, '') === etag);
  return new Response(matches ? null : body, {
    status: matches ? 304 : 200,
    headers: {
      'content-type': 'application/json',
      'cache-control': 'no-cache',
      age: String(Math.max(0, Math.floor(ageSeconds))),
      etag,
    },
  });
}

async function refresh(perPage, page, token, cachedBody, upstreamEtag) {
  // Reading the body can reject mid-stream; that must fall back to the stale copy or 502 like any other failure.
  try {
    const headers = { accept: 'application/vnd.github+json', 'user-agent': 'agent-bridge-release-feed' };
    if (token) headers.authorization = `Bearer ${token}`;
    if (upstreamEtag) headers['if-none-match'] = upstreamEtag;
    const upstream = await fetch(`${UPSTREAM}?per_page=${perPage}&page=${page}`, { headers });
    // A rejected credential must not carry its validator into the one anonymous retry.
    if (upstream.status === 401 && token) return refresh(perPage, page, null, null, null);
    const freshSeconds = token ? 60 : FRESH_SECONDS;
    if (upstream.status === 304 && cachedBody !== null && upstreamEtag)
      return { body: cachedBody, upstreamEtag: upstream.headers.get('etag') ?? upstreamEtag, freshSeconds };
    if (upstream.status !== 200) return null;
    const body = await upstream.text();
    return Array.isArray(JSON.parse(body)) ? { body, upstreamEtag: upstream.headers.get('etag'), freshSeconds } : null;
  } catch {
    return null;
  }
}

export default {
  async fetch(request, env, ctx) {
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
    let cachedEtag = null;
    const freshSeconds = env.GITHUB_TOKEN && cached?.headers.get(FRESH_FOR) !== String(FRESH_SECONDS) ? 60 : FRESH_SECONDS;
    if (cached) {
      cachedBody = await cached.text();
      cachedAge = (now - Number(cached.headers.get(FETCHED_AT))) / 1000;
      cachedEtag = cached.headers.get('etag') ?? await bodyTag(cachedBody);
      if (cachedAge < freshSeconds) return answer(request, cachedBody, cachedAge, cachedEtag);
    }

    const fresh = await refresh(perPage, page, env.GITHUB_TOKEN, cachedBody, cached?.headers.get(UPSTREAM_ETAG));
    if (fresh !== null) {
      const etag = await bodyTag(fresh.body);
      const store = new Response(fresh.body, {
        headers: {
          'content-type': 'application/json',
          'cache-control': `public, max-age=${STALE_SECONDS}`,
          [FETCHED_AT]: String(now),
          [FRESH_FOR]: String(fresh.freshSeconds),
          etag,
        },
      });
      if (fresh.upstreamEtag) store.headers.set(UPSTREAM_ETAG, fresh.upstreamEtag);
      ctx.waitUntil(cache.put(cacheKey, store));
      return answer(request, fresh.body, 0, etag);
    }

    // The refresh failed: serve a stale copy with its real age, never an empty success.
    if (cachedBody !== null && Number.isFinite(cachedAge)) return answer(request, cachedBody, cachedAge, cachedEtag);
    return failure(502, 'release list unavailable');
  },
};
