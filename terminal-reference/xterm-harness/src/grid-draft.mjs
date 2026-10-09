export const GRID_FORMAT = 'termlink.terminal-grid-draft';
export const GRID_FORMAT_VERSION = 0;

export function exportXtermGrid(terminal, source) {
  const buffer = terminal.buffer.active;
  const rows = [];
  for (let rowIndex = 0; rowIndex < terminal.rows; rowIndex++) {
    const line = buffer.getLine(buffer.baseY + rowIndex);
    rows.push({
      index: rowIndex,
      text: (line?.translateToString(true, 0, terminal.cols) ?? '').replace(/ +$/u, ''),
      wrapped: line?.isWrapped ?? false,
      style_runs: line ? collectStyleRuns(terminal, line) : [],
    });
  }

  const modes = terminal.modes ?? {};
  return {
    format: GRID_FORMAT,
    format_version: GRID_FORMAT_VERSION,
    engine: { name: 'xterm.js', version: '6.0.0', unicode_version: terminal.unicode.activeVersion },
    source,
    size: { columns: terminal.cols, rows: terminal.rows },
    cursor: {
      column: buffer.cursorX,
      row: buffer.cursorY,
      visible: null,
      style: terminal.options.cursorStyle ?? 'block',
    },
    modes: {
      alternate_buffer: buffer.type === 'alternate',
      application_cursor: nullableBoolean(modes.applicationCursorKeysMode),
      application_keypad: nullableBoolean(modes.applicationKeypadMode),
      mouse_tracking: typeof modes.mouseTrackingMode === 'string'
        ? modes.mouseTrackingMode !== 'none'
        : null,
      reverse_video: null,
    },
    rows,
  };
}

export function compareGridDrafts(reference, candidate, differenceLimit = 200) {
  if (!reference) {
    return {
      status: 'missing-reference',
      summary: 'No reference grid was supplied for comparison.',
      difference_count: 0,
      differences: [],
    };
  }
  validateGridDraft(reference, 'reference');
  validateGridDraft(candidate, 'candidate');

  const differences = [];
  let differenceCount = 0;
  const add = (path, expected, actual) => {
    differenceCount++;
    if (differences.length < differenceLimit) differences.push({ path, expected, actual });
  };

  compareValue('source.case_name', reference.source.case_name, candidate.source.case_name, add);
  compareValue('source.capture_id', reference.source.capture_id, candidate.source.capture_id, add);
  compareValue(
    'source.final_sequence',
    reference.source.final_sequence,
    candidate.source.final_sequence,
    add,
  );
  compareValue('size.columns', reference.size.columns, candidate.size.columns, add);
  compareValue('size.rows', reference.size.rows, candidate.size.rows, add);
  compareValue('cursor.column', reference.cursor.column, candidate.cursor.column, add);
  compareValue('cursor.row', reference.cursor.row, candidate.cursor.row, add);
  compareNullable('cursor.visible', reference.cursor.visible, candidate.cursor.visible, add);
  compareNullable('cursor.style', reference.cursor.style, candidate.cursor.style, add);

  const modeKeys = new Set([...Object.keys(reference.modes), ...Object.keys(candidate.modes)]);
  for (const key of [...modeKeys].sort()) {
    compareNullable(`modes.${key}`, reference.modes[key], candidate.modes[key], add);
  }

  const rowCount = Math.max(reference.rows.length, candidate.rows.length);
  for (let index = 0; index < rowCount; index++) {
    const expectedRow = reference.rows[index];
    const actualRow = candidate.rows[index];
    if (!expectedRow || !actualRow) {
      add(`rows[${index}]`, expectedRow ?? null, actualRow ?? null);
      continue;
    }
    compareValue(`rows[${index}].text`, expectedRow.text, actualRow.text, add);
    compareValue(`rows[${index}].wrapped`, expectedRow.wrapped, actualRow.wrapped, add);
    compareValue(
      `rows[${index}].style_runs`,
      stableJson(comparableStyleRuns(expectedRow, reference.size.columns)),
      stableJson(comparableStyleRuns(actualRow, candidate.size.columns)),
      add,
    );
  }

  return {
    status: differenceCount === 0 ? 'match' : 'different',
    summary: differenceCount === 0
      ? 'Reference and candidate grids match on comparable fields.'
      : `${differenceCount} comparable grid difference${differenceCount === 1 ? '' : 's'} detected.`,
    difference_count: differenceCount,
    truncated: differenceCount > differences.length,
    differences,
  };
}

export function validateGridDraft(grid, label = 'grid') {
  if (!grid || grid.format !== GRID_FORMAT || grid.format_version !== GRID_FORMAT_VERSION) {
    throw new TypeError(`${label} is not a supported terminal grid draft`);
  }
  assertExactKeys(grid, [
    'format', 'format_version', 'engine', 'source', 'size', 'cursor', 'modes', 'rows',
  ], label);
  assertExactKeys(grid.engine, ['name', 'version', 'unicode_version'], `${label}.engine`);
  assertNonEmptyString(grid.engine.name, `${label}.engine.name`);
  assertNonEmptyString(grid.engine.version, `${label}.engine.version`);
  if (grid.engine.unicode_version !== null) {
    assertNonEmptyString(grid.engine.unicode_version, `${label}.engine.unicode_version`);
  }

  assertExactKeys(
    grid.source,
    ['case_name', 'capture_id', 'final_sequence'],
    `${label}.source`,
  );
  if (typeof grid.source.case_name !== 'string' ||
      !/^[a-z0-9][a-z0-9._-]*$/.test(grid.source.case_name)) {
    throw new TypeError(`${label}.source.case_name is invalid`);
  }
  if (typeof grid.source.capture_id !== 'string' ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
        .test(grid.source.capture_id)) {
    throw new TypeError(`${label}.source.capture_id is invalid`);
  }
  assertInteger(grid.source.final_sequence, 1, Number.MAX_SAFE_INTEGER,
    `${label}.source.final_sequence`);

  assertExactKeys(grid.size, ['columns', 'rows'], `${label}.size`);
  assertInteger(grid.size.columns, 2, 1_000, `${label}.size.columns`);
  assertInteger(grid.size.rows, 2, 1_000, `${label}.size.rows`);

  assertExactKeys(grid.cursor, ['column', 'row', 'visible', 'style'], `${label}.cursor`);
  assertInteger(grid.cursor.column, 0, grid.size.columns, `${label}.cursor.column`);
  assertInteger(grid.cursor.row, 0, grid.size.rows - 1, `${label}.cursor.row`);
  assertNullableBoolean(grid.cursor.visible, `${label}.cursor.visible`);
  if (grid.cursor.style !== null) assertNonEmptyString(grid.cursor.style, `${label}.cursor.style`);

  const modeKeys = [
    'alternate_buffer', 'application_cursor', 'application_keypad', 'mouse_tracking',
    'reverse_video',
  ];
  assertExactKeys(grid.modes, modeKeys, `${label}.modes`);
  modeKeys.forEach((key) => assertNullableBoolean(grid.modes[key], `${label}.modes.${key}`));

  if (!Array.isArray(grid.rows) || grid.rows.length !== grid.size.rows) {
    throw new TypeError(`${label} row count does not match its size`);
  }
  grid.rows.forEach((row, rowIndex) => validateRow(row, rowIndex, grid.size.columns, label));
}

function validateRow(row, rowIndex, columns, label) {
  const rowLabel = `${label}.rows[${rowIndex}]`;
  assertExactKeys(row, ['index', 'text', 'wrapped', 'style_runs'], rowLabel);
  if (row.index !== rowIndex) throw new TypeError(`${rowLabel}.index must be ${rowIndex}`);
  if (typeof row.text !== 'string') throw new TypeError(`${rowLabel}.text must be a string`);
  if (typeof row.wrapped !== 'boolean') throw new TypeError(`${rowLabel}.wrapped must be boolean`);
  if (!Array.isArray(row.style_runs) || row.style_runs.length === 0) {
    throw new TypeError(`${rowLabel}.style_runs must cover the row`);
  }

  let nextColumn = 0;
  row.style_runs.forEach((run, runIndex) => {
    const runLabel = `${rowLabel}.style_runs[${runIndex}]`;
    assertExactKeys(run, ['start', 'end', 'fg', 'bg', 'attrs'], runLabel);
    assertInteger(run.start, 0, columns - 1, `${runLabel}.start`);
    assertInteger(run.end, 1, columns, `${runLabel}.end`);
    if (run.start !== nextColumn || run.end <= run.start) {
      throw new TypeError(`${runLabel} must be contiguous and non-empty`);
    }
    validateColor(run.fg, `${runLabel}.fg`);
    validateColor(run.bg, `${runLabel}.bg`);
    const attributeKeys = [
      'bold', 'dim', 'italic', 'underline', 'blink', 'inverse', 'invisible', 'strikethrough',
    ];
    assertExactKeys(run.attrs, attributeKeys, `${runLabel}.attrs`);
    attributeKeys.forEach((key) => {
      if (typeof run.attrs[key] !== 'boolean') {
        throw new TypeError(`${runLabel}.attrs.${key} must be boolean`);
      }
    });
    nextColumn = run.end;
  });
  if (nextColumn !== columns) throw new TypeError(`${rowLabel}.style_runs must cover the row`);
}

function validateColor(color, label) {
  assertExactKeys(color, ['type', 'value'], label);
  if (!['default', 'palette', 'rgb'].includes(color.type)) {
    throw new TypeError(`${label}.type is invalid`);
  }
  const valid = color.type === 'default'
    ? color.value === null
    : color.type === 'palette'
      ? typeof color.value === 'string' && /^(?:[0-9]|[1-9][0-9]{1,2})$/.test(color.value)
      : typeof color.value === 'string' && /^[0-9a-f]{6}$/.test(color.value);
  if (!valid) throw new TypeError(`${label}.value is invalid for ${color.type}`);
}

function assertExactKeys(value, expectedKeys, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new TypeError(`${label} must be an object`);
  }
  const actual = Object.keys(value).sort();
  const expected = [...expectedKeys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    throw new TypeError(`${label} contains missing or unknown fields`);
  }
}

function assertInteger(value, min, max, label) {
  if (!Number.isSafeInteger(value) || value < min || value > max) {
    throw new TypeError(`${label} must be an integer from ${min} to ${max}`);
  }
}

function assertNonEmptyString(value, label) {
  if (typeof value !== 'string' || value.length === 0 || value.length > 256) {
    throw new TypeError(`${label} must be a non-empty string`);
  }
}

function assertNullableBoolean(value, label) {
  if (value !== null && typeof value !== 'boolean') {
    throw new TypeError(`${label} must be boolean or null`);
  }
}

function collectStyleRuns(terminal, line) {
  const runs = [];
  let current = null;
  for (let column = 0; column < terminal.cols; column++) {
    const cell = line.getCell(column);
    const style = cell ? normalizeXtermStyle(cell) : defaultStyle();
    const key = stableJson(style);
    if (!current || current.key !== key) {
      current = { start: column, end: column + 1, ...style, key };
      runs.push(current);
    } else {
      current.end = column + 1;
    }
  }
  return runs.map(({ key, ...run }) => run);
}

function normalizeXtermStyle(cell) {
  return {
    fg: normalizeColor(cell, 'Fg'),
    bg: normalizeColor(cell, 'Bg'),
    attrs: {
      bold: Boolean(cell.isBold()),
      dim: Boolean(cell.isDim()),
      italic: Boolean(cell.isItalic()),
      underline: Boolean(cell.isUnderline()),
      blink: Boolean(cell.isBlink()),
      inverse: Boolean(cell.isInverse()),
      invisible: Boolean(cell.isInvisible()),
      strikethrough: Boolean(cell.isStrikethrough()),
    },
  };
}

function normalizeColor(cell, prefix) {
  if (cell[`is${prefix}Default`]()) return { type: 'default', value: null };
  const value = cell[`get${prefix}Color`]();
  if (cell[`is${prefix}RGB`]()) {
    return { type: 'rgb', value: value.toString(16).padStart(6, '0') };
  }
  return { type: 'palette', value: String(value) };
}

function defaultStyle() {
  return {
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

// Foreground and text attributes on trailing blank cells do not affect pixels. Background does,
// so it remains comparable across the whole row. The draft row text is already right-trimmed.
function comparableStyleRuns(row, columns) {
  const visibleCells = visibleCellMask(row.text, columns);
  const runs = [];
  let sourceIndex = 0;
  let source = row.style_runs[sourceIndex];
  let current = null;
  for (let column = 0; column < columns; column++) {
    while (source.end <= column) source = row.style_runs[++sourceIndex];
    const style = visibleCells[column]
      ? { fg: source.fg, bg: source.bg, attrs: source.attrs }
      : { bg: source.bg };
    const key = stableJson(style);
    if (!current || current.key !== key) {
      current = { start: column, end: column + 1, style, key };
      runs.push(current);
    } else {
      current.end = column + 1;
    }
  }
  return runs.map(({ key, ...run }) => run);
}

function visibleCellMask(text, columns) {
  const visible = Array(columns).fill(false);
  let column = 0;
  for (const character of text) {
    const codePoint = character.codePointAt(0);
    const width = character === '\u200d' || character === '\ufe0f' || /\p{Mark}/u.test(character)
      ? 0
      : isWideCodePoint(codePoint) ? 2 : 1;
    if (character !== ' ') {
      for (let offset = 0; offset < width && column + offset < columns; offset++) {
        visible[column + offset] = true;
      }
    }
    column += width;
    if (column >= columns) break;
  }
  return visible;
}

function isWideCodePoint(codePoint) {
  return codePoint >= 0x1100 && (
    codePoint <= 0x115f || codePoint === 0x2329 || codePoint === 0x232a ||
    (codePoint >= 0x2e80 && codePoint <= 0xa4cf && codePoint !== 0x303f) ||
    (codePoint >= 0xac00 && codePoint <= 0xd7a3) ||
    (codePoint >= 0xf900 && codePoint <= 0xfaff) ||
    (codePoint >= 0xfe10 && codePoint <= 0xfe19) ||
    (codePoint >= 0xfe30 && codePoint <= 0xfe6f) ||
    (codePoint >= 0xff00 && codePoint <= 0xff60) ||
    (codePoint >= 0xffe0 && codePoint <= 0xffe6) ||
    (codePoint >= 0x1f300 && codePoint <= 0x1faff) ||
    (codePoint >= 0x20000 && codePoint <= 0x3fffd)
  );
}

function compareValue(path, expected, actual, add) {
  if (expected !== actual) add(path, expected, actual);
}

function compareNullable(path, expected, actual, add) {
  if (expected !== null && expected !== undefined && actual !== null && actual !== undefined) {
    compareValue(path, expected, actual, add);
  }
}

function nullableBoolean(value) {
  return typeof value === 'boolean' ? value : null;
}

function stableJson(value) {
  return JSON.stringify(sortRecursively(value));
}

function sortRecursively(value) {
  if (Array.isArray(value)) return value.map(sortRecursively);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(
    Object.keys(value).sort().map((key) => [key, sortRecursively(value[key])]),
  );
}
