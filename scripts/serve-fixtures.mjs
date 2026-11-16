import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('../Savanna-static-pages/', import.meta.url)));
const preferredPort = Number(process.env.PORT || 4173);
const types = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
  ['.vtt', 'text/vtt; charset=utf-8'],
  ['.wav', 'audio/wav'],
  ['.webm', 'video/webm'],
  ['.ttf', 'font/ttf'],
  ['.woff', 'font/woff'],
  ['.woff2', 'font/woff2'],
]);

async function handleRequest(request, response) {
  try {
    const url = new URL(request.url || '/', `http://${request.headers.host || '127.0.0.1'}`);
    const pathname = decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname);
    let file = resolve(root, `.${pathname}`);
    if (file !== root && !file.startsWith(`${root}${sep}`)) {
      response.writeHead(403).end('Forbidden');
      return;
    }
    if ((await stat(file)).isDirectory()) file = resolve(file, 'index.html');
    const body = await readFile(file);
    const contentType = file.endsWith(`${sep}manifest`)
      ? 'application/json; charset=utf-8'
      : types.get(extname(file).toLowerCase()) || 'application/octet-stream';
    const headers = {
      'Accept-Ranges': 'bytes',
      'Cache-Control': 'no-store',
      'Content-Type': contentType,
      'X-AccessLearn-Fixture': 'true',
    };
    const range = request.headers.range?.match(/^bytes=(\d*)-(\d*)$/);
    if (range) {
      const start = range[1] ? Number(range[1]) : 0;
      const end = range[2] ? Math.min(Number(range[2]), body.length - 1) : body.length - 1;
      if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || start > end || start >= body.length) {
        response.writeHead(416, { 'Content-Range': `bytes */${body.length}` }).end();
        return;
      }
      const partial = body.subarray(start, end + 1);
      response.writeHead(206, {
        ...headers,
        'Content-Length': partial.length,
        'Content-Range': `bytes ${start}-${end}/${body.length}`,
      });
      response.end(request.method === 'HEAD' ? undefined : partial);
      return;
    }
    response.writeHead(200, { ...headers, 'Content-Length': body.length });
    response.end(request.method === 'HEAD' ? undefined : body);
  } catch {
    response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    response.end('Not found');
  }
}

async function isFixtureServer(port) {
  try {
    const response = await fetch(`http://127.0.0.1:${port}/`, {
      method: 'HEAD',
      signal: AbortSignal.timeout(750),
    });
    return response.headers.get('x-accesslearn-fixture') === 'true';
  } catch {
    return false;
  }
}

function listen(port) {
  return new Promise((resolve, reject) => {
    const server = createServer(handleRequest);
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => {
      server.removeAllListeners('error');
      server.on('error', (error) => console.error('Fixture server error:', error));
      resolve(server);
    });
  });
}

async function start() {
  for (let offset = 0; offset < 10; offset += 1) {
    const port = preferredPort + offset;
    if (await isFixtureServer(port)) {
      console.log(`Savanna fixtures are already running at http://127.0.0.1:${port}`);
      return null;
    }
    try {
      const server = await listen(port);
      console.log(`Savanna fixtures ready at http://127.0.0.1:${port}`);
      return server;
    } catch (error) {
      if (error?.code !== 'EADDRINUSE') throw error;
      console.warn(`Port ${port} is in use; trying ${port + 1}.`);
    }
  }
  throw new Error(`No open fixture port found from ${preferredPort} to ${preferredPort + 9}.`);
}

const server = await start();

if (server) {
  for (const signal of ['SIGINT', 'SIGTERM']) {
    process.on(signal, () => server.close(() => process.exit(0)));
  }
}
