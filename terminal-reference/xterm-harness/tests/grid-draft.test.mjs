import assert from 'node:assert/strict';
import test from 'node:test';
import {
  compareGridDrafts,
  exportXtermGrid,
  GRID_FORMAT,
  GRID_FORMAT_VERSION,
} from '../src/grid-draft.mjs';

test('reports a match for equivalent comparable fields', () => {
  const grid = fixtureGrid();
  assert.equal(compareGridDrafts(grid, structuredClone(grid)).status, 'match');
});

test('reports text, cursor and mode differences without comparing engine metadata', () => {
  const reference = fixtureGrid();
  const candidate = structuredClone(reference);
  candidate.engine = { name: 'xterm.js', version: '6.0.0', unicode_version: '11' };
  candidate.cursor.column = 2;
  candidate.modes.alternate_buffer = true;
  candidate.rows[0].text = 'different';
  const report = compareGridDrafts(reference, candidate);
  assert.equal(report.status, 'different');
  assert.deepEqual(report.differences.map((entry) => entry.path), [
    'cursor.column', 'modes.alternate_buffer', 'rows[0].text',
  ]);
});

test('makes a missing reference grid explicit', () => {
  assert.equal(compareGridDrafts(null, fixtureGrid()).status, 'missing-reference');
});

test('rejects a stale reference from a different capture', () => {
  const reference = fixtureGrid();
  const candidate = structuredClone(reference);
  candidate.source.capture_id = '00000000-0000-4000-8000-000000000002';
  const report = compareGridDrafts(reference, candidate);
  assert.equal(report.status, 'different');
  assert.deepEqual(report.differences.map((entry) => entry.path), ['source.capture_id']);
});

test('exports the visible grid from baseY instead of the start of scrollback', () => {
  const requestedLines = [];
  const terminal = {
    rows: 2,
    cols: 2,
    options: { cursorStyle: 'block' },
    unicode: { activeVersion: '11' },
    modes: {},
    buffer: {
      active: {
        type: 'normal',
        baseY: 3,
        cursorX: 0,
        cursorY: 1,
        getLine(index) {
          requestedLines.push(index);
          return {
            isWrapped: false,
            translateToString: () => `line-${index}`,
            getCell: () => null,
          };
        },
      },
    },
  };
  const exported = exportXtermGrid(terminal, fixtureGrid().source);
  assert.deepEqual(requestedLines, [3, 4]);
  assert.deepEqual(exported.rows.map((row) => row.text), ['line-3', 'line-4']);
});

test('rejects style runs that do not cover the full row', () => {
  const reference = fixtureGrid();
  const candidate = structuredClone(reference);
  candidate.rows[0].style_runs[0].end = 1;
  assert.throws(() => compareGridDrafts(reference, candidate), /must cover the row/);
});

test('ignores non-rendering foreground differences on trailing blank cells', () => {
  const reference = fixtureGrid();
  const candidate = structuredClone(reference);
  candidate.rows[1].style_runs[0].fg = { type: 'palette', value: '1' };
  assert.equal(compareGridDrafts(reference, candidate).status, 'match');
});

test('ignores non-rendering foreground differences on leading blank cells', () => {
  const reference = fixtureGrid();
  reference.rows[0].text = ' o';
  const candidate = structuredClone(reference);
  candidate.rows[0].style_runs = [
    { ...styleRun(), end: 1, fg: { type: 'palette', value: '1' } },
    { ...styleRun(), start: 1 },
  ];
  assert.equal(compareGridDrafts(reference, candidate).status, 'match');
});

test('reports background differences on trailing blank cells', () => {
  const reference = fixtureGrid();
  const candidate = structuredClone(reference);
  candidate.rows[1].style_runs[0].bg = { type: 'palette', value: '1' };
  const report = compareGridDrafts(reference, candidate);
  assert.equal(report.status, 'different');
  assert.deepEqual(report.differences.map((entry) => entry.path), ['rows[1].style_runs']);
});

function fixtureGrid() {
  return {
    format: GRID_FORMAT,
    format_version: GRID_FORMAT_VERSION,
    engine: { name: 'reference-fixture', version: 'test', unicode_version: null },
    source: {
      case_name: 'case',
      capture_id: '00000000-0000-4000-8000-000000000001',
      final_sequence: 3,
    },
    size: { columns: 2, rows: 2 },
    cursor: { column: 0, row: 1, visible: true, style: 'block' },
    modes: {
      alternate_buffer: false,
      application_cursor: false,
      application_keypad: false,
      mouse_tracking: false,
      reverse_video: false,
    },
    rows: [
      { index: 0, text: 'ok', wrapped: false, style_runs: [styleRun()] },
      { index: 1, text: '', wrapped: false, style_runs: [styleRun()] },
    ],
  };
}

function styleRun() {
  return {
    start: 0,
    end: 2,
    fg: { type: 'default', value: null },
    bg: { type: 'default', value: null },
    attrs: {
      bold: false,
      dim: false,
      italic: false,
      underline: false,
      blink: false,
      inverse: false,
      invisible: false,
      strikethrough: false,
    },
  };
}
