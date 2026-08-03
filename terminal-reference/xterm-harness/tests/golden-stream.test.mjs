import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { GoldenStreamFormatError, replayGoldenBundle, validateGoldenBundle } from '../src/golden-stream.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const fixturesRoot = join(root, 'testdata', 'terminal-golden-streams');

test('strictly validates and replays the fixed PowerShell bundle by original chunks', async () => {
  const bundle = await loadFixture();
  const writes = [];
  const resizes = [];
  const report = await replayGoldenBundle(bundle, {
    clear() {},
    resize(columns, rows) { resizes.push([columns, rows]); },
    write(sequence, bytes) { writes.push([sequence, Uint8Array.from(bytes)]); },
  });

  assert.equal(report.eventCount, 14);
  assert.equal(report.outputBytes, 702);
  assert.equal(report.exitCode, 17);
  assert.deepEqual(report.finalSize, { columns: 100, rows: 30 });
  assert.deepEqual(resizes, [[80, 25], [100, 30]]);
  assert.deepEqual(writes.map(([sequence, bytes]) => [sequence, bytes.byteLength]), [
    [4, 89], [5, 15], [6, 6], [7, 66], [8, 26], [9, 15], [10, 223], [11, 224], [12, 35], [13, 3],
  ]);
});

test('rejects a hash mismatch before replay', async () => {
  const raw = await loadRawFixture();
  raw.outputBytes = Uint8Array.from(raw.outputBytes);
  raw.outputBytes[0] ^= 0xff;
  await assert.rejects(() => validateGoldenBundle(raw),
    (error) => error instanceof GoldenStreamFormatError && /SHA-256 mismatch/.test(error.message));
});

test('rejects unknown event fields', async () => {
  const raw = await loadRawFixture();
  const lines = new TextDecoder().decode(raw.eventsBytes).trimEnd().split(/\r?\n/);
  const event = JSON.parse(lines[3]);
  event.decoded_text = 'not allowed';
  lines[3] = JSON.stringify(event);
  raw.eventsBytes = new TextEncoder().encode(`${lines.join('\n')}\n`);
  const manifest = JSON.parse(new TextDecoder().decode(raw.manifestBytes));
  manifest.sha256.events = await sha256(raw.eventsBytes);
  raw.manifestBytes = new TextEncoder().encode(`${JSON.stringify(manifest)}\n`);
  await assert.rejects(() => validateGoldenBundle(raw), /contains missing or unknown fields/);
});

for (const caseName of [
  'ansi-unicode-pwsh-v1',
  'smoke-cmd-v1',
  'smoke-pwsh-v1',
  'smoke-wsl-v1',
]) {
  test(`strictly validates the ${caseName} bundle`, async () => {
    const bundle = await loadFixture(caseName);
    assert.equal(bundle.report.caseName, caseName);
    assert.deepEqual(bundle.report.finalSize, { columns: 100, rows: 30 });
    assert.equal(bundle.report.exitCode, 17);
  });
}

async function loadFixture(caseName = 'smoke-powershell-v1') {
  return validateGoldenBundle(await loadRawFixture(caseName));
}

async function loadRawFixture(caseName = 'smoke-powershell-v1') {
  const fixture = join(fixturesRoot, caseName);
  const [manifestBytes, eventsBytes, outputBytes, expectationsBytes] = await Promise.all([
    readFile(join(fixture, 'manifest.json')),
    readFile(join(fixture, 'events.jsonl')),
    readFile(join(fixture, 'output.bin')),
    readFile(join(fixture, 'expectations.json')),
  ]);
  return { manifestBytes, eventsBytes, outputBytes, expectationsBytes };
}

async function sha256(bytes) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  return Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join('');
}
