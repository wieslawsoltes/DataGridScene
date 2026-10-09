import http from 'node:http';
import path from 'node:path';
import { stat } from 'node:fs/promises';
import { createReadStream } from 'node:fs';

const types = new Map(Object.entries({ '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm', '.dll': 'application/octet-stream', '.dat': 'application/octet-stream', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.woff': 'font/woff', '.ttf': 'font/ttf', '.ico': 'image/x-icon' }));

/** Match GitHub Pages: ordinary static HTTP, without COOP/COEP or special isolation headers. */
export async function startServer(root, port = 4173) {
  root = path.resolve(root);
  const server = http.createServer(async (request, response) => {
    try {
      let pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
      if (pathname === '/') { response.writeHead(302, { location: '/DataGridScene/' }); response.end(); return; }
      if (!pathname.startsWith('/DataGridScene/')) { response.writeHead(404); response.end(); return; }
      pathname = pathname.slice('/DataGridScene/'.length);
      let target = path.resolve(root, pathname || 'index.html');
      if (target !== root && !target.startsWith(root + path.sep)) { response.writeHead(403); response.end(); return; }
      let info = await stat(target);
      if (info.isDirectory()) { target = path.join(target, 'index.html'); info = await stat(target); }
      response.writeHead(200, { 'content-type': types.get(path.extname(target)) ?? 'application/octet-stream', 'content-length': info.size, 'cache-control': 'no-store' });
      if (request.method === 'HEAD') response.end(); else createReadStream(target).pipe(response);
    } catch { response.writeHead(404); response.end('Not found'); }
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });
  return server;
}
