import { Terminal } from '../vendor/xterm.mjs';
import { Unicode11Addon } from '../vendor/addon-unicode11.mjs';
import { loadGoldenBundle, replayGoldenBundle } from './golden-stream.mjs';
import { compareGridDrafts, exportXtermGrid } from './grid-draft.mjs';

const DEFAULT_CASE = 'smoke-powershell-v1';
const TESTDATA_ROOT = '../../testdata/terminal-golden-streams';
const TERMUX_REFERENCE_ROOT = '../../android/terminal-engine-termux/build/termlink-reference';

const elements = {
  caseName: document.querySelector('#case-name'),
  replay: document.querySelector('#replay'),
  status: document.querySelector('#run-status'),
  facts: document.querySelector('#run-facts'),
  terminal: document.querySelector('#terminal'),
  compareStatus: document.querySelector('#compare-status'),
  compareSummary: document.querySelector('#compare-summary'),
  differences: document.querySelector('#differences'),
  downloadGrid: document.querySelector('#download-grid'),
  downloadReport: document.querySelector('#download-report'),
};

const terminal = new Terminal({
  // Required by the pinned Unicode 11 addon; this page is a development-only offline oracle.
  allowProposedApi: true,
  convertEol: false,
  cursorBlink: false,
  cursorStyle: 'block',
  drawBoldTextInBrightColors: true,
  fontFamily: 'Cascadia Mono, JetBrains Mono, Consolas, monospace',
  fontSize: 13,
  letterSpacing: 0,
  lineHeight: 1.08,
  scrollback: 2_000,
  theme: {
    background: '#0b0e12',
    foreground: '#e8edf2',
    cursor: '#f3c969',
    selectionBackground: '#3f5368',
    black: '#0c0c0c',
    red: '#c50f1f',
    green: '#13a10e',
    yellow: '#c19c00',
    blue: '#0037da',
    magenta: '#881798',
    cyan: '#3a96dd',
    white: '#cccccc',
    brightBlack: '#767676',
    brightRed: '#e74856',
    brightGreen: '#16c60c',
    brightYellow: '#f9f1a5',
    brightBlue: '#3b78ff',
    brightMagenta: '#b4009e',
    brightCyan: '#61d6d6',
    brightWhite: '#f2f2f2',
  },
  windowsPty: { backend: 'conpty', buildNumber: 22631 },
});
const unicode11 = new Unicode11Addon();
terminal.loadAddon(unicode11);
terminal.unicode.activeVersion = '11';
terminal.open(elements.terminal);

let currentGrid = null;
let currentReport = null;

elements.caseName.value = new URL(location.href).searchParams.get('case') ?? DEFAULT_CASE;
elements.replay.addEventListener('click', run);
elements.downloadGrid.addEventListener('click', () => downloadJson(currentGrid, `${elements.caseName.value}.xterm-grid.json`));
elements.downloadReport.addEventListener('click', () => downloadJson(currentReport, `${elements.caseName.value}.grid-diff.json`));

await run();

async function run() {
  const caseName = elements.caseName.value.trim();
  if (!/^[a-z0-9][a-z0-9._-]*$/.test(caseName)) {
    showFailure('Case name contains unsupported characters.');
    return;
  }

  setBusy(true);
  try {
    const bundle = await loadGoldenBundle(`${TESTDATA_ROOT}/${caseName}/`);
    terminal.resize(bundle.manifest.initial_size.columns, bundle.manifest.initial_size.rows);
    await replayGoldenBundle(bundle, {
      clear: () => terminal.reset(),
      resize: (columns, rows) => terminal.resize(columns, rows),
      write: (_sequence, bytes) => new Promise((resolve) => terminal.write(bytes, resolve)),
    });

    currentGrid = exportXtermGrid(terminal, {
      case_name: bundle.manifest.case_name,
      capture_id: bundle.manifest.capture_id,
      final_sequence: bundle.report.finalSequence,
    });
    const termuxGrid = await loadTermuxReference(caseName);
    currentReport = compareGridDrafts(termuxGrid, currentGrid);
    renderSuccess(bundle.report, currentReport);
  } catch (error) {
    console.error(error);
    showFailure(error instanceof Error ? error.message : String(error));
  } finally {
    setBusy(false);
  }
}

async function loadTermuxReference(caseName) {
  const response = await fetch(`${TERMUX_REFERENCE_ROOT}/${caseName}.termux-grid.json`, {
    cache: 'no-store',
    credentials: 'same-origin',
  });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`Unable to load Termux reference: HTTP ${response.status}`);
  return response.json();
}

function renderSuccess(report, comparison) {
  elements.status.dataset.tone = 'success';
  elements.status.textContent = 'Verified';
  elements.facts.textContent = `${report.eventCount} events · ${report.outputBytes} bytes · ` +
    `${report.finalSize.columns}×${report.finalSize.rows} · exit ${report.exitCode}`;
  elements.compareStatus.dataset.tone = comparison.status === 'match'
    ? 'success'
    : comparison.status === 'different' ? 'danger' : 'warning';
  elements.compareStatus.textContent = comparison.status === 'match'
    ? 'Grid match'
    : comparison.status === 'different' ? 'Differences found' : 'Reference pending';
  elements.compareSummary.textContent = comparison.summary;
  elements.differences.replaceChildren();

  if (comparison.differences.length === 0) {
    const row = document.createElement('li');
    row.textContent = comparison.status === 'match'
      ? 'No comparable differences.'
      : 'Generate the Termux grid with the documented Gradle task, then replay.';
    elements.differences.append(row);
  } else {
    for (const difference of comparison.differences.slice(0, 20)) {
      const row = document.createElement('li');
      const path = document.createElement('code');
      path.textContent = difference.path;
      const values = document.createElement('span');
      values.textContent = `${displayValue(difference.expected)} → ${displayValue(difference.actual)}`;
      row.append(path, values);
      elements.differences.append(row);
    }
  }
  elements.downloadGrid.disabled = false;
  elements.downloadReport.disabled = false;
}

function showFailure(message) {
  elements.status.dataset.tone = 'danger';
  elements.status.textContent = 'Validation failed';
  elements.facts.textContent = message;
  elements.compareStatus.dataset.tone = 'neutral';
  elements.compareStatus.textContent = 'Not compared';
  elements.compareSummary.textContent = 'The bundle must pass strict validation before rendering.';
  elements.differences.replaceChildren();
  currentGrid = null;
  currentReport = null;
  elements.downloadGrid.disabled = true;
  elements.downloadReport.disabled = true;
}

function setBusy(busy) {
  elements.replay.disabled = busy;
  elements.caseName.disabled = busy;
  if (busy) {
    elements.status.dataset.tone = 'neutral';
    elements.status.textContent = 'Validating';
    elements.facts.textContent = 'Checking hashes, ordering, output ranges and expectations…';
  }
}

function downloadJson(value, filename) {
  if (!value) return;
  const url = URL.createObjectURL(new Blob([`${JSON.stringify(value, null, 2)}\n`], { type: 'application/json' }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

function displayValue(value) {
  const serialized = JSON.stringify(value);
  return serialized.length > 140 ? `${serialized.slice(0, 137)}…` : serialized;
}
