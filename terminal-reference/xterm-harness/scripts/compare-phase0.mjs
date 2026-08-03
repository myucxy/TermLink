import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import headlessPackage from '@xterm/headless';
import unicodePackage from '@xterm/addon-unicode11';
import { validateGoldenBundle } from '../src/golden-stream.mjs';
import { compareGridDrafts, exportXtermGrid } from '../src/grid-draft.mjs';

const { Terminal } = headlessPackage;
const { Unicode11Addon } = unicodePackage;
const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const harnessRoot = resolve(scriptDirectory, '..');
const repositoryRoot = resolve(harnessRoot, '..', '..');
const testdataRoot = join(repositoryRoot, 'testdata', 'terminal-golden-streams');
const termuxReferenceRoot = join(
  repositoryRoot,
  'android',
  'terminal-engine-termux',
  'build',
  'termlink-reference',
);

const defaultCases = Object.freeze([
  'ansi-unicode-pwsh-v1',
  'smoke-cmd-v1',
  'smoke-powershell-v1',
  'smoke-pwsh-v1',
  'smoke-wsl-v1',
  'terminal-features-pwsh-v1',
  'tui-fzf-wsl-v1',
  'tui-htop-wsl-v1',
  'tui-less-wsl-v1',
  'tui-vim-wsl-v1',
]);

const options = parseArguments(process.argv.slice(2));
const cases = options.cases.length > 0 ? options.cases : defaultCases;
const reports = [];
for (const caseName of cases) reports.push(await compareCase(caseName));

const differentCheckpoints = reports.reduce(
  (count, report) => count + report.different_checkpoint_count,
  0,
);
const checkpointCount = reports.reduce((count, report) => count + report.checkpoint_count, 0);
const output = {
  format: 'termlink.phase0-grid-comparison',
  format_version: 0,
  generated_at: new Date().toISOString(),
  engines: {
    reference: 'termux-terminal@3df69d1da197dd9bd71a3bafd902dffd720576b4',
    candidate: '@xterm/headless@6.0.0 + @xterm/addon-unicode11@0.9.0',
  },
  summary: {
    case_count: reports.length,
    checkpoint_count: checkpointCount,
    matching_checkpoint_count: checkpointCount - differentCheckpoints,
    different_checkpoint_count: differentCheckpoints,
    compatibility_percent: checkpointCount === 0
      ? 0
      : Number((((checkpointCount - differentCheckpoints) / checkpointCount) * 100).toFixed(4)),
  },
  cases: reports,
};

if (options.output) {
  const outputPath = resolve(harnessRoot, options.output);
  await mkdir(dirname(outputPath), { recursive: true });
  await writeFile(outputPath, `${JSON.stringify(output, null, 2)}\n`, 'utf8');
}

process.stdout.write(`${JSON.stringify(output, null, 2)}\n`);
if (differentCheckpoints > 0) process.exitCode = 2;

async function compareCase(caseName) {
  if (!/^[a-z0-9][a-z0-9._-]*$/.test(caseName)) {
    throw new Error(`Invalid case name: ${caseName}`);
  }
  const bundleDirectory = join(testdataRoot, caseName);
  const [manifestBytes, eventsBytes, outputBytes, expectationsBytes] = await Promise.all([
    readFile(join(bundleDirectory, 'manifest.json')),
    readFile(join(bundleDirectory, 'events.jsonl')),
    readFile(join(bundleDirectory, 'output.bin')),
    readFile(join(bundleDirectory, 'expectations.json')),
  ]);
  const bundle = await validateGoldenBundle({
    manifestBytes,
    eventsBytes,
    outputBytes,
    expectationsBytes,
  });
  const timeline = JSON.parse(await readFile(
    join(termuxReferenceRoot, `${caseName}.termux-timeline.json`),
    'utf8',
  ));
  validateTimeline(timeline, bundle.manifest);
  const references = new Map(timeline.checkpoints.map((checkpoint) => [
    checkpoint.sequence,
    checkpoint.grid,
  ]));

  const terminal = createTerminal(
    bundle.manifest.initial_size.columns,
    bundle.manifest.initial_size.rows,
  );
  const checkpoints = [];
  try {
    for (const event of bundle.events) {
      if (event.type === 'resize') {
        terminal.resize(event.columns, event.rows);
        continue;
      }
      if (event.type !== 'output') continue;

      const end = event.data_offset + event.data_length;
      await writeTerminal(terminal, bundle.outputBytes.subarray(event.data_offset, end));
      const source = {
        case_name: bundle.manifest.case_name,
        capture_id: bundle.manifest.capture_id,
        final_sequence: event.seq,
      };
      const comparison = compareGridDrafts(
        references.get(event.seq) ?? null,
        exportXtermGrid(terminal, source),
        20,
      );
      checkpoints.push({
        sequence: event.seq,
        status: comparison.status,
        difference_count: comparison.difference_count,
        differences: comparison.differences,
      });
    }
  } finally {
    terminal.dispose();
  }

  if (checkpoints.length !== references.size) {
    throw new Error(
      `${caseName}: Termux exported ${references.size} checkpoints, xterm replayed ${checkpoints.length}`,
    );
  }
  const different = checkpoints.filter((checkpoint) => checkpoint.status !== 'match');
  return {
    case_name: caseName,
    capture_id: bundle.manifest.capture_id,
    checkpoint_count: checkpoints.length,
    matching_checkpoint_count: checkpoints.length - different.length,
    different_checkpoint_count: different.length,
    status: different.length === 0 ? 'match' : 'different',
    differences: different,
  };
}

function createTerminal(columns, rows) {
  const terminal = new Terminal({
    allowProposedApi: true,
    cols: columns,
    rows,
    convertEol: false,
    cursorBlink: false,
    cursorStyle: 'block',
    drawBoldTextInBrightColors: true,
    scrollback: 2_000,
    windowsPty: { backend: 'conpty', buildNumber: 22631 },
  });
  terminal.loadAddon(new Unicode11Addon());
  terminal.unicode.activeVersion = '11';
  return terminal;
}

function writeTerminal(terminal, bytes) {
  return new Promise((resolveWrite) => terminal.write(bytes, resolveWrite));
}

function validateTimeline(timeline, manifest) {
  if (timeline?.format !== 'termlink.terminal-grid-timeline-draft' ||
      timeline.format_version !== 0 || !Array.isArray(timeline.checkpoints)) {
    throw new Error(`${manifest.case_name}: invalid Termux checkpoint timeline`);
  }
  if (timeline.source?.case_name !== manifest.case_name ||
      timeline.source?.capture_id !== manifest.capture_id) {
    throw new Error(`${manifest.case_name}: stale Termux checkpoint timeline`);
  }
  const sequences = new Set();
  for (const checkpoint of timeline.checkpoints) {
    if (!Number.isSafeInteger(checkpoint.sequence) || checkpoint.sequence < 1 ||
        sequences.has(checkpoint.sequence)) {
      throw new Error(`${manifest.case_name}: invalid or duplicate checkpoint sequence`);
    }
    sequences.add(checkpoint.sequence);
  }
}

function parseArguments(args) {
  const result = { output: null, cases: [] };
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--output') {
      result.output = args[++index];
      if (!result.output) throw new Error('--output requires a path');
    } else {
      result.cases.push(args[index]);
    }
  }
  return result;
}
