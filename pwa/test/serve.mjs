// A static file server for the tests: serves a built site (tools/build-site.mjs) under a
// path prefix, the way GitHub Pages serves it under /agent-bridge/app/.
//   node test/serve.mjs --port 8790 [--relay <origin>]   builds into .site and serves it
import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { buildSite } from '../tools/build-site.mjs';

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.svg': 'image/svg+xml',
  '.webmanifest': 'application/manifest+json', '.png': 'image/png',
};

export const APP_PATH = '/agent-bridge/app/';

export function startStaticServer({ dir, port = 0, prefix = APP_PATH }) {
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://x');
      if (url.pathname === prefix.slice(0, -1)) { res.writeHead(301, { location: prefix }).end(); return; }
      if (!url.pathname.startsWith(prefix)) { res.writeHead(404).end(); return; }
      let rel = decodeURIComponent(url.pathname.slice(prefix.length));
      if (rel === '' || rel.endsWith('/')) { rel += 'index.html'; }
      const file = path.resolve(dir, rel);
      if (!file.startsWith(path.resolve(dir) + path.sep)) { res.writeHead(403).end(); return; }
      if (!(await stat(file)).isFile()) { throw new Error('not a file'); }
      res.writeHead(200, { 'content-type': TYPES[path.extname(file)] ?? 'application/octet-stream', 'cache-control': 'no-cache' });
      res.end(await readFile(file));
    } catch {
      res.writeHead(404).end();
    }
  });
  return new Promise((resolve) => server.listen(port, '127.0.0.1', () => resolve(server)));
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const arg = (name) => { const k = process.argv.indexOf(name); return k > 0 ? process.argv[k + 1] : undefined; };
  const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
  const dir = buildSite({ out: path.join(root, '.site', 'demo'), relay: arg('--relay') ?? '' });
  const server = await startStaticServer({ dir, port: Number(arg('--port') ?? 8790) });
  console.log(`serving ${dir} at http://127.0.0.1:${server.address().port}${APP_PATH}`);
}
