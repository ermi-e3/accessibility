import { copyFile, mkdir, readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const source = join(
  root,
  'src',
  'accessibility.Api',
  'wwwroot',
  'sdk',
  'v1',
  'accesslearn-player.min.js',
);
const destination = join(root, 'Savanna-static-pages', 'assets', 'js', 'accesslearn-player.min.js');

const content = await readFile(source);
await mkdir(dirname(destination), { recursive: true });
await copyFile(source, destination);
console.log(`Synced team player to the demo host (${content.byteLength} bytes, ${gzipSync(content).byteLength} gzip).`);
