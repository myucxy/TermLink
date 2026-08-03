import { createHash } from 'node:crypto';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const vendor = join(root, 'vendor');

const packages = [
  {
    name: '@xterm/xterm',
    version: '6.0.0',
    integrity: 'sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==',
    files: [
      ['lib/xterm.mjs', 'xterm.mjs'],
      ['css/xterm.css', 'xterm.css'],
      ['LICENSE', 'LICENSE.xterm.txt'],
    ],
  },
  {
    name: '@xterm/addon-unicode11',
    version: '0.9.0',
    integrity: 'sha512-FxDnYcyuXhNl+XSqGZL/t0U9eiNb/q3EWT5rYkQT/zuig8Gz/VagnQANKHdDWFM2lTMk9ly0EFQxxxtZUoRetw==',
    files: [
      ['lib/addon-unicode11.mjs', 'addon-unicode11.mjs'],
      ['LICENSE', 'LICENSE.addon-unicode11.txt'],
    ],
  },
];

const lock = JSON.parse(await readFile(join(root, 'package-lock.json'), 'utf8'));
const manifest = {
  generated_by: 'npm run sync-vendor',
  packages: [],
  files: {},
};

await mkdir(vendor, { recursive: true });

for (const dependency of packages) {
  const lockEntry = lock.packages[`node_modules/${dependency.name}`];
  if (!lockEntry || lockEntry.version !== dependency.version || lockEntry.integrity !== dependency.integrity) {
    throw new Error(`Lock mismatch for ${dependency.name}@${dependency.version}`);
  }

  manifest.packages.push({
    name: dependency.name,
    version: dependency.version,
    integrity: dependency.integrity,
    license: 'MIT',
  });

  for (const [sourceName, destinationName] of dependency.files) {
    const source = join(root, 'node_modules', ...dependency.name.split('/'), sourceName);
    const destination = join(vendor, destinationName);
    await copyFile(source, destination);
    const bytes = await readFile(destination);
    manifest.files[destinationName] = {
      bytes: bytes.byteLength,
      sha256: createHash('sha256').update(bytes).digest('hex'),
    };
  }
}

await writeFile(
  join(vendor, 'manifest.json'),
  `${JSON.stringify(manifest, null, 2)}\n`,
  'utf8',
);
