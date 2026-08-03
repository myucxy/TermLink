const FORMAT = 'termlink.golden-stream';
const FORMAT_VERSION = 1;
const CLOCK = 'monotonic-microseconds';
const FILES = Object.freeze({
  events: 'events.jsonl',
  output: 'output.bin',
  expectations: 'expectations.json',
});
const MAX_FILE_BYTES = 64 * 1024 * 1024;
const MAX_EVENTS = 1_000_000;
const MAX_COLUMNS = 1_000;
const MAX_ROWS = 1_000;

export class GoldenStreamFormatError extends Error {
  constructor(message) {
    super(message);
    this.name = 'GoldenStreamFormatError';
  }
}
export async function loadGoldenBundle(baseUrl, fetchImpl = globalThis.fetch) {
  if (typeof fetchImpl !== 'function') throw new TypeError('A fetch implementation is required');
  const normalizedBase = new URL(ensureTrailingSlash(baseUrl), globalThis.location?.href ?? 'http://localhost/');
  const names = ['manifest.json', FILES.events, FILES.output, FILES.expectations];
  const responses = await Promise.all(names.map(async (name) => {
    const response = await fetchImpl(new URL(name, normalizedBase), {
      cache: 'no-store',
      credentials: 'same-origin',
    });
    if (!response.ok) throw new GoldenStreamFormatError(`Unable to load ${name}: HTTP ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength > MAX_FILE_BYTES) {
      throw new GoldenStreamFormatError(`${name} exceeds the ${MAX_FILE_BYTES}-byte harness limit`);
    }
    return bytes;
  }));

  return validateGoldenBundle({
    manifestBytes: responses[0],
    eventsBytes: responses[1],
    outputBytes: responses[2],
    expectationsBytes: responses[3],
  });
}

export async function validateGoldenBundle(rawBundle) {
  const manifestBytes = asBytes(rawBundle.manifestBytes, 'manifest.json');
  const eventsBytes = asBytes(rawBundle.eventsBytes, FILES.events);
  const outputBytes = asBytes(rawBundle.outputBytes, FILES.output);
  const expectationsBytes = asBytes(rawBundle.expectationsBytes, FILES.expectations);

  for (const [label, bytes] of [
    ['manifest.json', manifestBytes],
    [FILES.events, eventsBytes],
    [FILES.output, outputBytes],
    [FILES.expectations, expectationsBytes],
  ]) {
    assert(bytes.byteLength <= MAX_FILE_BYTES, `${label} exceeds the harness size limit`);
  }

  const manifest = parseJson(manifestBytes, 'manifest.json');
  validateManifest(manifest);
  await Promise.all([
    verifyHash(FILES.events, manifest.sha256.events, eventsBytes),
    verifyHash(FILES.output, manifest.sha256.output, outputBytes),
    verifyHash(FILES.expectations, manifest.sha256.expectations, expectationsBytes),
  ]);

  const events = parseEvents(eventsBytes);
  const expectations = parseJson(expectationsBytes, FILES.expectations);
  validateExpectations(expectations);
  const observed = validateEvents(manifest, events, outputBytes);

  assert(observed.finalSize.columns === expectations.final_size.columns &&
    observed.finalSize.rows === expectations.final_size.rows,
  'Final terminal size does not match expectations');
  assert(observed.exitCode === expectations.exit_code,
    `Exit code mismatch: expected ${expectations.exit_code}, got ${observed.exitCode}`);

  if (expectations.output_contains_utf8.length > 0) {
    const decodedOutput = new TextDecoder().decode(outputBytes);
    for (const marker of expectations.output_contains_utf8) {
      assert(decodedOutput.includes(marker), `Expected UTF-8 output marker not found: ${marker}`);
    }
  }

  return {
    manifest,
    events,
    expectations,
    outputBytes,
    report: {
      caseName: manifest.case_name,
      eventCount: events.length,
      outputEventCount: observed.outputEventCount,
      outputBytes: outputBytes.byteLength,
      finalSequence: events.at(-1).seq,
      finalSize: observed.finalSize,
      exitCode: observed.exitCode,
      durationMicroseconds: events.at(-1).elapsed_us,
    },
  };
}

export async function replayGoldenBundle(bundle, engine) {
  assertEngine(engine);
  await engine.clear();
  for (const event of bundle.events) {
    if (event.type === 'resize') {
      await engine.resize(event.columns, event.rows);
    } else if (event.type === 'output') {
      const end = event.data_offset + event.data_length;
      await engine.write(event.seq, bundle.outputBytes.subarray(event.data_offset, end));
    }
  }
  return bundle.report;
}

function validateManifest(value) {
  assertPlainObject(value, 'manifest.json');
  assertKeys(value, [
    'format', 'format_version', 'capture_id', 'case_name', 'created_at', 'clock', 'source',
    'initial_size', 'files', 'statistics', 'sha256',
  ], 'manifest.json');
  assert(value.format === FORMAT && value.format_version === FORMAT_VERSION,
    `Unsupported Golden Stream format: ${value.format} v${value.format_version}`);
  assert(value.clock === CLOCK, `Unsupported capture clock: ${value.clock}`);
  assertString(value.capture_id, 'capture_id', 128);
  assert(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value.capture_id),
    'capture_id must be a UUID');
  assertString(value.case_name, 'case_name', 128);
  assert(/^[a-z0-9][a-z0-9._-]*$/.test(value.case_name), 'case_name contains unsupported characters');
  assertString(value.created_at, 'created_at', 128);
  assert(Number.isFinite(Date.parse(value.created_at)), 'created_at must be an RFC 3339 timestamp');

  assertPlainObject(value.source, 'source');
  const sourceKeys = ['platform', 'shell', 'command_line'];
  if (Object.hasOwn(value.source, 'working_directory')) sourceKeys.push('working_directory');
  assertKeys(value.source, sourceKeys, 'source');
  assertString(value.source.platform, 'source.platform', 128);
  assertString(value.source.shell, 'source.shell', 128);
  assertString(value.source.command_line, 'source.command_line', 4096);
  if (Object.hasOwn(value.source, 'working_directory')) {
    assertString(value.source.working_directory, 'source.working_directory', 4096);
  }

  validateSize(value.initial_size, 'initial_size');
  assertPlainObject(value.files, 'files');
  assertKeys(value.files, ['events', 'output', 'expectations'], 'files');
  for (const [key, expected] of Object.entries(FILES)) {
    assert(value.files[key] === expected, `Invalid ${key} file name`);
  }

  assertPlainObject(value.statistics, 'statistics');
  assertKeys(value.statistics, ['event_count', 'output_event_count', 'output_bytes'], 'statistics');
  assertInteger(value.statistics.event_count, 'statistics.event_count', 1, MAX_EVENTS);
  assertInteger(value.statistics.output_event_count, 'statistics.output_event_count', 1, MAX_EVENTS);
  assertInteger(value.statistics.output_bytes, 'statistics.output_bytes', 1, MAX_FILE_BYTES);

  assertPlainObject(value.sha256, 'sha256');
  assertKeys(value.sha256, ['events', 'output', 'expectations'], 'sha256');
  for (const key of Object.keys(FILES)) {
    assert(typeof value.sha256[key] === 'string' && /^[0-9a-f]{64}$/.test(value.sha256[key]),
      `sha256.${key} must be lowercase hexadecimal`);
  }
}

function validateExpectations(value) {
  assertPlainObject(value, FILES.expectations);
  assertKeys(value, [
    'exit_code', 'final_size', 'output_contains_utf8', 'normalized_grid', 'reference_screenshot',
  ], FILES.expectations);
  assertInteger(value.exit_code, 'exit_code', -2147483648, 2147483647);
  validateSize(value.final_size, 'final_size');
  assert(Array.isArray(value.output_contains_utf8), 'output_contains_utf8 must be an array');
  assert(value.output_contains_utf8.length <= 1_000, 'output_contains_utf8 has too many markers');
  value.output_contains_utf8.forEach((marker, index) =>
    assertString(marker, `output_contains_utf8[${index}]`, 4096));
  assert(value.normalized_grid === null || typeof value.normalized_grid === 'object',
    'normalized_grid must be null or an object');
  assert(value.reference_screenshot === null || typeof value.reference_screenshot === 'string',
    'reference_screenshot must be null or a string');
}

function parseEvents(bytes) {
  const text = decodeUtf8(bytes, FILES.events);
  const lines = text.split(/\r?\n/);
  if (lines.at(-1) === '') lines.pop();
  assert(lines.length > 0, 'Golden Stream contains no events');
  assert(lines.length <= MAX_EVENTS, `Golden Stream exceeds ${MAX_EVENTS} events`);
  return lines.map((line, index) => {
    assert(line.length > 0, `Blank events.jsonl line ${index + 1}`);
    try {
      return JSON.parse(line);
    } catch (error) {
      throw new GoldenStreamFormatError(`Invalid events.jsonl line ${index + 1}: ${error.message}`);
    }
  });
}

function validateEvents(manifest, events, outputBytes) {
  assert(events.length === manifest.statistics.event_count, 'Event count does not match manifest');
  assert(outputBytes.byteLength === manifest.statistics.output_bytes,
    'Output byte count does not match manifest');

  let lastElapsed = -1;
  let nextOffset = 0;
  let outputEventCount = 0;
  let startedCount = 0;
  let endedCount = 0;
  let finalSize = null;
  let exitCode = null;

  events.forEach((event, index) => {
    const label = `event seq ${index + 1}`;
    assertPlainObject(event, label);
    assertInteger(event.seq, `${label}.seq`, 1, Number.MAX_SAFE_INTEGER);
    assert(event.seq === index + 1, `Event sequence gap: expected ${index + 1}, got ${event.seq}`);
    assertInteger(event.elapsed_us, `${label}.elapsed_us`, 0, Number.MAX_SAFE_INTEGER);
    assert(event.elapsed_us >= lastElapsed, `Event time moved backwards at seq ${event.seq}`);
    assert(['output', 'resize', 'lifecycle'].includes(event.type),
      `Unsupported event type '${event.type}' at seq ${event.seq}`);

    if (event.type === 'output') {
      assertKeys(event, ['seq', 'elapsed_us', 'type', 'data_offset', 'data_length'], label);
      assertInteger(event.data_offset, `${label}.data_offset`, 0, MAX_FILE_BYTES);
      assertInteger(event.data_length, `${label}.data_length`, 1, MAX_FILE_BYTES);
      assert(event.data_offset === nextOffset, `Invalid output range at seq ${event.seq}`);
      nextOffset += event.data_length;
      assert(nextOffset <= outputBytes.byteLength, `Output range exceeds output.bin at seq ${event.seq}`);
      outputEventCount++;
    } else if (event.type === 'resize') {
      assertKeys(event, ['seq', 'elapsed_us', 'type', 'columns', 'rows', 'reason'], label);
      validateSize(event, label);
      assertString(event.reason, `${label}.reason`, 128);
      finalSize = { columns: event.columns, rows: event.rows };
      if (finalSize && outputEventCount === 0 && startedCount === 1) {
        // The first resize establishes the capture's initial grid.
        if (events.slice(0, index).every((candidate) => candidate.type !== 'resize')) {
          assert(event.columns === manifest.initial_size.columns && event.rows === manifest.initial_size.rows,
            'First resize does not match manifest initial_size');
        }
      }
    } else {
      if (event.state === 'started') {
        assertKeys(event, ['seq', 'elapsed_us', 'type', 'state'], label);
        startedCount++;
      } else if (event.state === 'ended') {
        assertKeys(event, ['seq', 'elapsed_us', 'type', 'state', 'exit_code'], label);
        assertInteger(event.exit_code, `${label}.exit_code`, -2147483648, 2147483647);
        endedCount++;
        exitCode = event.exit_code;
      } else {
        throw new GoldenStreamFormatError(`Unsupported lifecycle state '${event.state}' at seq ${event.seq}`);
      }
    }
    lastElapsed = event.elapsed_us;
  });

  assert(events[0].type === 'lifecycle' && events[0].state === 'started' && startedCount === 1,
    'Golden Stream must contain exactly one starting lifecycle event');
  const last = events.at(-1);
  assert(last.type === 'lifecycle' && last.state === 'ended' && endedCount === 1,
    'Golden Stream must end with exactly one ended lifecycle event');
  assert(nextOffset === outputBytes.byteLength, 'Output events do not cover output.bin exactly');
  assert(outputEventCount === manifest.statistics.output_event_count,
    'Output event count does not match manifest');
  assert(finalSize !== null, 'Golden Stream contains no resize event');
  return { finalSize, exitCode, outputEventCount };
}

async function verifyHash(label, expected, bytes) {
  const cryptoApi = globalThis.crypto?.subtle;
  if (!cryptoApi) throw new GoldenStreamFormatError('Web Crypto SHA-256 is unavailable');
  const digest = new Uint8Array(await cryptoApi.digest('SHA-256', bytes));
  const actual = Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join('');
  assert(actual === expected, `SHA-256 mismatch for ${label}`);
}

function parseJson(bytes, label) {
  try {
    return JSON.parse(decodeUtf8(bytes, label));
  } catch (error) {
    if (error instanceof GoldenStreamFormatError) throw error;
    throw new GoldenStreamFormatError(`Invalid ${label}: ${error.message}`);
  }
}

function decodeUtf8(bytes, label) {
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    throw new GoldenStreamFormatError(`${label} must not contain a UTF-8 BOM`);
  }
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch (error) {
    throw new GoldenStreamFormatError(`${label} is not valid UTF-8: ${error.message}`);
  }
}

function validateSize(value, label) {
  assertPlainObject(value, label);
  if (label === 'initial_size' || label === 'final_size') assertKeys(value, ['columns', 'rows'], label);
  assertInteger(value.columns, `${label}.columns`, 2, MAX_COLUMNS);
  assertInteger(value.rows, `${label}.rows`, 2, MAX_ROWS);
}

function assertEngine(engine) {
  for (const method of ['clear', 'resize', 'write']) {
    if (typeof engine?.[method] !== 'function') throw new TypeError(`Replay engine requires ${method}()`);
  }
}

function assertKeys(value, keys, label) {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  assert(actual.length === expected.length && actual.every((key, index) => key === expected[index]),
    `${label} contains missing or unknown fields`);
}

function assertPlainObject(value, label) {
  assert(value !== null && typeof value === 'object' && !Array.isArray(value), `${label} must be an object`);
}

function assertInteger(value, label, min, max) {
  assert(Number.isSafeInteger(value) && value >= min && value <= max,
    `${label} must be an integer from ${min} to ${max}`);
}

function assertString(value, label, maxLength) {
  assert(typeof value === 'string' && value.length > 0 && value.length <= maxLength,
    `${label} must be a non-empty string up to ${maxLength} characters`);
}

function assert(condition, message) {
  if (!condition) throw new GoldenStreamFormatError(message);
}

function asBytes(value, label) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  throw new TypeError(`${label} must be Uint8Array or ArrayBuffer`);
}

function ensureTrailingSlash(value) {
  const stringValue = String(value);
  return stringValue.endsWith('/') ? stringValue : `${stringValue}/`;
}
