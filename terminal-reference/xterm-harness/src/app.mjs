import { Terminal } from '../vendor/xterm.mjs';
import { Unicode11Addon } from '../vendor/addon-unicode11.mjs';
import { loadGoldenBundle, replayGoldenBundle } from './golden-stream.mjs';
import { exportXtermGrid } from './grid-draft.mjs';

const DEFAULT_CASE = 'smoke-powershell-v1';
const TESTDATA_ROOT = '../../testdata/terminal-golden-streams';

const elements = {
  caseName: document.querySelector('#case-name'),
  replay: document.querySelector('#replay'),
  status: document.querySelector('#run-status'),
  facts: document.querySelector('#run-facts'),
  terminal: document.querySelector('#terminal'),
  downloadGrid: document.querySelector('#download-grid'),
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

elements.caseName.value = new URL(location.href).searchParams.get('case') ?? DEFAULT_CASE;
elements.replay.addEventListener('click', run);
elements.downloadGrid.addEventListener('click', () => downloadJson(currentGrid, `${elements.caseName.value}.xterm-grid.json`));

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
    renderSuccess(bundle.report);
  } catch (error) {
    console.error(error);
    showFailure(error instanceof Error ? error.message : String(error));
  } finally {
    setBusy(false);
  }
}

function renderSuccess(report) {
  elements.status.dataset.tone = 'success';
  elements.status.textContent = 'Verified';
  elements.facts.textContent = `${report.eventCount} events · ${report.outputBytes} bytes · ` +
    `${report.finalSize.columns}×${report.finalSize.rows} · exit ${report.exitCode}`;
  elements.downloadGrid.disabled = false;
}

function showFailure(message) {
  elements.status.dataset.tone = 'danger';
  elements.status.textContent = 'Validation failed';
  elements.facts.textContent = message;
  currentGrid = null;
  elements.downloadGrid.disabled = true;
}

function setBusy(busy) {
  elements.replay.disabled = busy;
  elements.caseName.disabled = busy;
  if (busy) {
    currentGrid = null;
    elements.downloadGrid.disabled = true;
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
