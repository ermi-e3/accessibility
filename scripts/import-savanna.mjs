import { createHash } from 'node:crypto';
import { lstat, mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

const COMMIT = '9e83ac77891708117728ad9fc05301e124cc8ec8';
const ARCHIVE_SHA256 = 'c3152e3bb31b2e8cb6d3a64797782b0c1e305f911bb5122700f67ee38f58a2fd';
const TREE_SHA1 = '4e23b00d814a5ea565fab54facfc57bfc585c3d8';
const ARCHIVE_URL = `https://codeload.github.com/alx-africa/Savanna-static-pages/tar.gz/${COMMIT}`;
const ROOT = fileURLToPath(new URL('../', import.meta.url));
const MAX_ARCHIVE_BYTES = 16 * 1024 * 1024;
const MAX_TAR_BYTES = 20 * 1024 * 1024;

function digest(algorithm, bytes) {
  return createHash(algorithm).update(bytes).digest('hex');
}

async function readArchive(archivePath) {
  if (archivePath) {
    const stat = await lstat(archivePath);
    if (!stat.isFile() || stat.size > MAX_ARCHIVE_BYTES) {
      throw new Error('Local archive must be a regular file no larger than 16 MiB.');
    }
    return readFile(archivePath);
  }
  const response = await fetch(ARCHIVE_URL, { signal: AbortSignal.timeout(120_000) });
  if (!response.ok) throw new Error(`Archive request failed: HTTP ${response.status}.`);
  const advertisedSize = response.headers.get('content-length');
  if (advertisedSize && Number(advertisedSize) > MAX_ARCHIVE_BYTES) {
    await response.body.cancel();
    throw new Error('Advertised archive size exceeds the 16 MiB download limit.');
  }
  const chunks = [];
  let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > MAX_ARCHIVE_BYTES) throw new Error('Archive exceeds the 16 MiB download limit.');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

function unpackArchive(archive) {
  if (digest('sha256', archive) !== ARCHIVE_SHA256) {
    throw new Error('Archive SHA-256 mismatch; nothing imported. Do not bypass the pin.');
  }
  const tar = gunzipSync(archive, { maxOutputLength: MAX_TAR_BYTES });
  const root = `Savanna-static-pages-${COMMIT}/`;
  const files = new Map();
  let hasCommit = false;
  let hasEnd = false;
  for (let offset = 0; offset + 512 <= tar.length;) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) {
      hasEnd = true;
      break;
    }
    const field = (start, end) => header.subarray(start, end).toString('utf8').replace(/\0.*$/s, '');
    const expectedChecksum = parseInt(field(148, 156), 8);
    const checksum = header.reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 32 : byte), 0);
    if (checksum !== expectedChecksum) throw new Error('Invalid tar header checksum.');
    const size = parseInt(field(124, 136), 8);
    if (!Number.isSafeInteger(size) || size < 0 || offset + 512 + size > tar.length) {
      throw new Error('Invalid tar entry size.');
    }
    const name = [field(345, 500), field(0, 100)].filter(Boolean).join('/');
    const type = field(156, 157);
    const bytes = tar.subarray(offset + 512, offset + 512 + size);
    offset += 512 + Math.ceil(size / 512) * 512;
    if (type === 'g') {
      if (bytes.toString('utf8') !== `52 comment=${COMMIT}\n`) {
        throw new Error('Unexpected archive Git commit metadata.');
      }
      hasCommit = true;
      continue;
    }
    if (!name.startsWith(root)) throw new Error(`Unexpected archive root: ${name}`);
    if (type === '5') continue;
    if (type !== '0' && type !== '') throw new Error(`Unsupported tar entry type: ${type}`);
    const path = name.slice(root.length);
    if (!path || /[\\:\x00]/.test(path) || path.split('/').some(part => !part || part === '.' || part === '..')) {
      throw new Error(`Unsafe archive path: ${path}`);
    }
    if (files.has(path) || (parseInt(field(100, 108), 8) & 0o111)) {
      throw new Error(`Duplicate or unexpected executable file: ${path}`);
    }
    files.set(path, bytes);
  }
  if (!hasCommit || !hasEnd || files.size !== 167) throw new Error('Incomplete pinned archive.');
  return files;
}

function gitHash(type, bytes) {
  return createHash('sha1').update(`${type} ${bytes.length}\0`).update(bytes).digest();
}

function gitTree(files, prefix = '') {
  const children = new Map();
  for (const [path, bytes] of files) {
    if (!path.startsWith(prefix)) continue;
    const [name, ...rest] = path.slice(prefix.length).split('/');
    children.set(name, rest.length ? null : bytes);
  }
  // Git sorts directories as if their names ended in '/', not by locale.
  const key = ([name, bytes]) => Buffer.from(name + (bytes === null ? '/' : ''));
  const entries = [...children].sort((a, b) => Buffer.compare(key(a), key(b)));
  const records = entries.map(([name, bytes]) => Buffer.concat([
    Buffer.from(`${bytes === null ? '40000' : '100644'} ${name}\0`),
    bytes === null ? gitTree(files, `${prefix}${name}/`) : gitHash('blob', bytes),
  ]));
  return gitHash('tree', Buffer.concat(records));
}

async function inspectDestination(path, bytes) {
  const parts = ['Savanna-static-pages', ...path.split('/')];
  let destination = ROOT;
  for (let index = 0; index < parts.length; index++) {
    destination = join(destination, parts[index]);
    let stat;
    try {
      stat = await lstat(destination);
    } catch (error) {
      if (error.code === 'ENOENT') continue;
      throw error;
    }
    const isFile = index === parts.length - 1;
    if (stat.isSymbolicLink() || (isFile ? !stat.isFile() : !stat.isDirectory())) {
      throw new Error(`Refusing non-regular destination: ${destination}`);
    }
    if (isFile) return (await readFile(destination)).equals(bytes) ? 'unchanged' : 'modified';
  }
  return 'missing';
}

async function main() {
  let mode = 'import';
  let archivePath;
  const args = process.argv.slice(2);
  while (args.length) {
    const arg = args.shift();
    if (arg === '--help') {
      console.log('Usage: node scripts/import-savanna.mjs [--inspect | --check] [--archive PATH]\n'
        + 'Default: import only missing pinned index/pages/assets files. Never overwrite differing files.\n'
        + '--inspect: validate the archive and report inventory without writing.\n'
        + '--check: require all supplied web files to match the pin; write nothing.\n'
        + '--archive: use an already downloaded, checksum-verified .tar.gz instead of the network.\n'
        + 'Requires Node.js 22+. Local use only; see docs/upstream-savanna.md before redistribution.');
      return;
    }
    if ((arg === '--inspect' || arg === '--check') && mode === 'import') {
      mode = arg.slice(2);
    } else if (arg === '--archive' && !archivePath && args[0] && !args[0].startsWith('--')) {
      archivePath = resolve(args.shift());
    } else {
      throw new Error(`Unknown or conflicting argument: ${arg}. Use --help.`);
    }
  }

  const archive = await readArchive(archivePath);
  const files = unpackArchive(archive);
  const treeSha1 = gitTree(files).toString('hex');
  if (treeSha1 !== TREE_SHA1) throw new Error('Archive files do not match the pinned Git tree.');
  const webFiles = [...files].filter(([path]) =>
    path !== 'assets/.DS_Store' &&
    (path === 'index.html' || path.startsWith('pages/') || path.startsWith('assets/'))
  );
  const summary = {
    mode,
    commit: COMMIT,
    archiveUrl: ARCHIVE_URL,
    archiveBytes: archive.length,
    archiveSha256: ARCHIVE_SHA256,
    gitTreeSha1: treeSha1,
    upstreamFileCount: files.size,
    webFileCount: webFiles.length,
    webBytes: webFiles.reduce((sum, [, bytes]) => sum + bytes.length, 0),
    inventory: Object.fromEntries(['index.html', 'pages/', 'assets/css/', 'assets/fonts/', 'assets/img/', 'assets/js/']
      .map(prefix => [prefix, webFiles.filter(([path]) => path.startsWith(prefix)).length])),
  };
  if (mode === 'inspect') {
    console.log(JSON.stringify(summary, null, 2));
    return;
  }

  // Preflight the whole import before adding anything; preserve concurrent/custom host work.
  const missing = [];
  const modified = [];
  for (const [path, bytes] of webFiles) {
    const state = await inspectDestination(path, bytes);
    if (state === 'modified') modified.push(path);
    if (state === 'missing') missing.push([path, bytes]);
  }
  if (modified.length || (mode === 'check' && missing.length)) {
    throw new Error(`Pinned baseline differs; nothing written.\n${[
      ...modified.map(path => `Modified (preserved): Savanna-static-pages/${path}`),
      ...(mode === 'check' ? missing.map(([path]) => `Missing: Savanna-static-pages/${path}`) : []),
    ].join('\n')}\nReview intentional integration edits separately; the importer never resets them.`);
  }
  for (const [path, bytes] of missing) {
    const destination = join(ROOT, 'Savanna-static-pages', path);
    await mkdir(dirname(destination), { recursive: true });
    await writeFile(destination, bytes, { flag: 'wx' });
  }
  console.log(JSON.stringify({ ...summary, created: missing.length, unchanged: webFiles.length - missing.length }, null, 2));
}

main().catch(error => {
  console.error(`Savanna import failed: ${error.message}`);
  process.exitCode = 1;
});
