use crate::osc::HyperlinkRef;
use crate::unicode_width::{grapheme_width, scalar_width};
use crate::{
    Attributes, Cell, Color, MAX_CELLS, MAX_GRAPHEME_BYTES, TerminalError, UnderlineStyle,
    cell_index,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct SavedCursor {
    pub(super) row: u32,
    pub(super) column: u32,
    pub(super) wrap_pending: bool,
    pub(super) attributes: Attributes,
    pub(super) protected: bool,
    pub(super) origin_mode: bool,
    pub(super) auto_wrap: bool,
}

impl Default for SavedCursor {
    fn default() -> Self {
        Self {
            row: 0,
            column: 0,
            wrap_pending: false,
            attributes: Attributes::default(),
            protected: false,
            origin_mode: false,
            auto_wrap: true,
        }
    }
}

#[derive(Clone, Debug)]
pub(super) struct Screen {
    pub(super) columns: u32,
    pub(super) rows: u32,
    pub(super) cells: Vec<Cell>,
    pub(super) soft_wrapped: Vec<bool>,
    pub(super) meaningful_end: Vec<u32>,
    pub(super) row_metadata: Vec<RowMetadata>,
    pub(super) next_logical_line_id: u64,
    // Monotonic count of full-viewport rows captured into primary scrollback.
    // Snapshot V2.10 persists it with a retained primary prefix; legacy snapshot
    // profiles and semantic equality omit it. Renderers use it only to keep a
    // history viewport anchored while new output arrives.
    pub(super) scrollback_generation_rows: u64,
    pub(super) storage: StoragePolicy,
    pub(super) cursor_row: u32,
    pub(super) cursor_column: u32,
    pub(super) wrap_pending: bool,
    pub(super) attributes: Attributes,
    pub(super) protected: bool,
    pub(super) saved: SavedCursor,
}

// Runtime row anchors deliberately remain outside Snapshot V2.0. Screen equality keeps
// its V2.0 semantic meaning until a later snapshot version makes anchors persistent.
impl PartialEq for Screen {
    fn eq(&self, other: &Self) -> bool {
        self.columns == other.columns
            && self.rows == other.rows
            && self.cells == other.cells
            && self.soft_wrapped == other.soft_wrapped
            && self.meaningful_end == other.meaningful_end
            && self.storage == other.storage
            && self.cursor_row == other.cursor_row
            && self.cursor_column == other.cursor_column
            && self.wrap_pending == other.wrap_pending
            && self.attributes == other.attributes
            && self.protected == other.protected
            && self.saved == other.saved
    }
}

impl Eq for Screen {}

pub(super) const ROW_FLAG_HEAD_TRUNCATED: u8 = 1;
pub(super) const MAX_HISTORY_ROWS: u32 = 10_000;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct StoragePolicy {
    requested_history_rows: u32,
    cell_cap: usize,
}

impl Default for StoragePolicy {
    fn default() -> Self {
        Self {
            requested_history_rows: 0,
            cell_cap: MAX_CELLS,
        }
    }
}

impl StoragePolicy {
    pub(super) fn new(requested_history_rows: u32, cell_cap: usize) -> Self {
        Self {
            requested_history_rows,
            cell_cap: cell_cap.min(MAX_CELLS),
        }
    }

    fn history_capacity(self, columns: u32, visible_rows: u32) -> u32 {
        let columns = usize::try_from(columns).expect("bounded columns");
        let cell_limited_rows = self.cell_cap / columns;
        let visible_rows = usize::try_from(visible_rows).expect("bounded rows");
        let cell_limited_history = cell_limited_rows.saturating_sub(visible_rows);
        self.requested_history_rows
            .min(MAX_HISTORY_ROWS)
            .min(u32::try_from(cell_limited_history).unwrap_or(u32::MAX))
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(super) struct RowMetadata {
    pub(super) logical_line_id: u64,
    pub(super) logical_cell_offset: u32,
    pub(super) sealed_revision: u64,
    pub(super) flags: u8,
}

impl RowMetadata {
    fn line_head(logical_line_id: u64) -> Self {
        Self {
            logical_line_id,
            ..Self::default()
        }
    }

    fn continuation_of(previous: Self, previous_meaningful_end: u32) -> Self {
        Self {
            logical_line_id: previous.logical_line_id,
            logical_cell_offset: previous
                .logical_cell_offset
                .checked_add(previous_meaningful_end)
                .expect("logical line width is cell-bounded"),
            sealed_revision: previous.sealed_revision,
            flags: 0,
        }
    }

    fn mark_head_truncated(&mut self) {
        self.flags |= ROW_FLAG_HEAD_TRUNCATED;
    }

    pub(super) fn is_head_truncated(self) -> bool {
        self.flags & ROW_FLAG_HEAD_TRUNCATED != 0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum ScrollCause {
    #[cfg(test)]
    Ordinary,
    Output(u64),
}

impl ScrollCause {
    fn output_revision(self) -> Option<u64> {
        match self {
            #[cfg(test)]
            Self::Ordinary => None,
            Self::Output(revision) => Some(revision),
        }
    }
}

#[allow(dead_code)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(super) struct RectangleAttributeChange {
    pub(super) bold: Option<bool>,
    pub(super) faint: Option<bool>,
    pub(super) italic: Option<bool>,
    pub(super) underline_style: Option<UnderlineStyle>,
    pub(super) blink: Option<bool>,
    pub(super) inverse: Option<bool>,
    pub(super) conceal: Option<bool>,
    pub(super) strikethrough: Option<bool>,
    pub(super) overline: Option<bool>,
    pub(super) foreground: Option<Color>,
    pub(super) background: Option<Color>,
    pub(super) underline_color: Option<Color>,
}

#[allow(dead_code)]
impl RectangleAttributeChange {
    fn apply(self, attributes: &mut Attributes) {
        if let Some(bold) = self.bold {
            attributes.bold = bold;
        }
        if let Some(faint) = self.faint {
            attributes.faint = faint;
        }
        if let Some(italic) = self.italic {
            attributes.italic = italic;
        }
        if let Some(underline_style) = self.underline_style {
            attributes.underline_style = underline_style;
        }
        if let Some(blink) = self.blink {
            attributes.blink = blink;
        }
        if let Some(inverse) = self.inverse {
            attributes.inverse = inverse;
        }
        if let Some(conceal) = self.conceal {
            attributes.conceal = conceal;
        }
        if let Some(strikethrough) = self.strikethrough {
            attributes.strikethrough = strikethrough;
        }
        if let Some(overline) = self.overline {
            attributes.overline = overline;
        }
        if let Some(foreground) = self.foreground {
            attributes.foreground = foreground;
        }
        if let Some(background) = self.background {
            attributes.background = background;
        }
        if let Some(underline_color) = self.underline_color {
            attributes.underline_color = underline_color;
        }
    }
}

#[allow(dead_code)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(super) struct RectangleAttributeToggle {
    pub(super) bold: bool,
    pub(super) faint: bool,
    pub(super) italic: bool,
    pub(super) underline_style: Option<UnderlineStyle>,
    pub(super) blink: bool,
    pub(super) inverse: bool,
    pub(super) conceal: bool,
    pub(super) strikethrough: bool,
    pub(super) overline: bool,
}

#[allow(dead_code)]
impl RectangleAttributeToggle {
    fn apply(self, attributes: &mut Attributes) {
        if self.bold {
            attributes.bold = !attributes.bold;
        }
        if self.faint {
            attributes.faint = !attributes.faint;
        }
        if self.italic {
            attributes.italic = !attributes.italic;
        }
        if let Some(target) = self.underline_style {
            attributes.underline_style = match attributes.underline_style {
                UnderlineStyle::None => target,
                current if current == target => UnderlineStyle::None,
                current => current,
            };
        }
        if self.blink {
            attributes.blink = !attributes.blink;
        }
        if self.inverse {
            attributes.inverse = !attributes.inverse;
        }
        if self.conceal {
            attributes.conceal = !attributes.conceal;
        }
        if self.strikethrough {
            attributes.strikethrough = !attributes.strikethrough;
        }
        if self.overline {
            attributes.overline = !attributes.overline;
        }
    }
}

#[derive(Clone, Copy)]
struct Rectangle {
    top: u32,
    left: u32,
    bottom: u32,
    right: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct LogicalAnchor {
    logical_line_id: u64,
    logical_cell_offset: u32,
}

#[derive(Clone)]
struct LogicalCell {
    cell: Cell,
    old_offset: u32,
}

struct LogicalLine {
    metadata: RowMetadata,
    cells: Vec<LogicalCell>,
    old_width: u32,
}

impl Screen {
    pub(super) fn new(columns: u32, rows: u32) -> Self {
        Self::new_with_history_capacity(columns, rows, 0, MAX_CELLS)
    }

    pub(super) fn new_with_history_capacity(
        columns: u32,
        rows: u32,
        requested_history_rows: u32,
        cell_cap: usize,
    ) -> Self {
        let soft_wrapped = vec![false; usize::try_from(rows).expect("bounded rows")];
        let meaningful_end = vec![0; usize::try_from(rows).expect("bounded rows")];
        let (row_metadata, next_logical_line_id) =
            Self::metadata_from_visible_rows(&soft_wrapped, &meaningful_end);
        let storage = StoragePolicy::new(
            requested_history_rows,
            cell_cap.max(super::cell_count(columns, rows)),
        );
        Self {
            columns,
            rows,
            cells: vec![Cell::default(); super::cell_count(columns, rows)],
            soft_wrapped,
            meaningful_end,
            row_metadata,
            next_logical_line_id,
            scrollback_generation_rows: 0,
            storage,
            cursor_row: 0,
            cursor_column: 0,
            wrap_pending: false,
            attributes: Attributes::default(),
            protected: false,
            saved: SavedCursor::default(),
        }
    }

    pub(super) fn stored_rows(&self) -> u32 {
        u32::try_from(self.soft_wrapped.len()).expect("stored row count is bounded")
    }

    pub(super) fn viewport_top(&self) -> u32 {
        self.stored_rows() - self.rows
    }

    pub(super) fn history_capacity(&self) -> u32 {
        self.storage_policy()
            .history_capacity(self.columns, self.rows)
    }

    pub(super) fn requested_history_rows(&self) -> u32 {
        self.storage.requested_history_rows
    }

    pub(super) fn scrollback_generation_rows(&self) -> u64 {
        self.scrollback_generation_rows
    }

    /// Creates one renderer-only window `offset_rows` above the live tail.
    /// The returned screen owns exactly the visible row count and contains no
    /// mutable history prefix of its own.
    pub(super) fn render_window(&self, offset_rows: u32) -> Self {
        let offset_rows = offset_rows.min(self.viewport_top());
        let first_row = self.viewport_top() - offset_rows;
        let end_row = first_row + self.rows;
        let first_cell = cell_index(self.columns, first_row, 0);
        let end_cell = cell_index(self.columns, end_row, 0);
        let first_row = usize::try_from(first_row).expect("bounded render-window row");
        let end_row = usize::try_from(end_row).expect("bounded render-window row");

        let mut window = Self::new(self.columns, self.rows);
        window
            .cells
            .clone_from_slice(&self.cells[first_cell..end_cell]);
        window
            .soft_wrapped
            .clone_from_slice(&self.soft_wrapped[first_row..end_row]);
        window
            .meaningful_end
            .clone_from_slice(&self.meaningful_end[first_row..end_row]);
        window
            .row_metadata
            .clone_from_slice(&self.row_metadata[first_row..end_row]);
        // A renderer projection owns no rows outside its selected window. If
        // the first selected row continues a logical line whose head lives in
        // the discarded prefix, make that boundary explicit so validation and
        // frame export do not mistake the projection for a corrupt Screen.
        if window.row_metadata[0].logical_cell_offset != 0 {
            window.row_metadata[0].mark_head_truncated();
        }
        // Likewise, a historical projection can end in the middle of a logical
        // line. The next row is outside this immutable renderer window, so its
        // final row must be a local hard boundary.
        if let Some(last) = window.soft_wrapped.last_mut() {
            *last = false;
        }
        window.next_logical_line_id = self.next_logical_line_id;
        window.scrollback_generation_rows = self.scrollback_generation_rows;
        if offset_rows == 0 {
            window.cursor_row = self.cursor_row;
            window.cursor_column = self.cursor_column;
            window.wrap_pending = self.wrap_pending;
            window.attributes = self.attributes;
            window.protected = self.protected;
            window.saved = self.saved;
        }
        window
    }

    pub(super) fn visible_stored_row(&self, row: u32) -> u32 {
        debug_assert!(row < self.rows);
        self.viewport_top() + row
    }

    fn visible_stored_boundary(&self, row: u32) -> u32 {
        debug_assert!(row <= self.rows);
        self.viewport_top() + row
    }

    pub(super) fn visible_cell_index(&self, row: u32, column: u32) -> usize {
        debug_assert!(column < self.columns);
        cell_index(self.columns, self.visible_stored_row(row), column)
    }

    pub(super) fn visible_cells(&self) -> &[Cell] {
        let start = cell_index(self.columns, self.viewport_top(), 0);
        &self.cells[start..]
    }

    fn visible_row_index(&self, row: u32) -> usize {
        usize::try_from(self.visible_stored_row(row)).expect("bounded row")
    }

    fn visible_row_metadata(&self, row: u32) -> RowMetadata {
        self.row_metadata[self.visible_row_index(row)]
    }

    fn visible_row_metadata_mut(&mut self, row: u32) -> &mut RowMetadata {
        let row = self.visible_row_index(row);
        &mut self.row_metadata[row]
    }

    fn storage_policy(&self) -> StoragePolicy {
        self.storage
    }

    fn apply_storage_policy(&mut self, storage: StoragePolicy) {
        self.storage = storage;
    }

    pub(super) fn metadata_from_visible_rows(
        soft_wrapped: &[bool],
        meaningful_end: &[u32],
    ) -> (Vec<RowMetadata>, u64) {
        debug_assert_eq!(soft_wrapped.len(), meaningful_end.len());
        let mut metadata = Vec::with_capacity(soft_wrapped.len());
        let mut logical_line_id = 0_u64;
        let mut logical_cell_offset = 0_u32;
        for (&soft_wrapped, &meaningful_end) in soft_wrapped.iter().zip(meaningful_end) {
            metadata.push(RowMetadata {
                logical_line_id,
                logical_cell_offset,
                sealed_revision: 0,
                flags: 0,
            });
            if soft_wrapped {
                logical_cell_offset = logical_cell_offset
                    .checked_add(meaningful_end)
                    .expect("visible logical line is cell-bounded");
            } else {
                logical_line_id = logical_line_id
                    .checked_add(1)
                    .expect("visible row count fits a logical line id");
                logical_cell_offset = 0;
            }
        }
        let next_logical_line_id = metadata
            .last()
            .map_or(0, |row| row.logical_line_id.saturating_add(1));
        (metadata, next_logical_line_id)
    }

    pub(super) fn reset(&mut self) {
        let Some(row_metadata) = self.fresh_line_heads(self.rows) else {
            return;
        };
        self.cells = vec![Cell::default(); super::cell_count(self.columns, self.rows)];
        self.soft_wrapped = vec![false; usize::try_from(self.rows).expect("bounded rows")];
        self.meaningful_end = vec![0; usize::try_from(self.rows).expect("bounded rows")];
        self.row_metadata = row_metadata;
        self.cursor_row = 0;
        self.cursor_column = 0;
        self.wrap_pending = false;
        self.attributes = Attributes::default();
        self.protected = false;
        self.saved = SavedCursor::default();
    }

    pub(super) fn resize_crop(&mut self, columns: u32, rows: u32) {
        if self.columns == columns && self.rows == rows {
            return;
        }
        let storage = self.storage_policy();
        self.normalize_row_metadata();
        let history_rows = self
            .viewport_top()
            .min(storage.history_capacity(columns, rows));
        let copy_rows = self.rows.min(rows);
        let copy_columns = self.columns.min(columns);
        let old_soft_wrapped = self.soft_wrapped.clone();
        let old_meaningful_end = self.meaningful_end.clone();
        let old_metadata = self.row_metadata.clone();
        let old_viewport = self.viewport_top();
        let mut source_rows = Vec::with_capacity(
            usize::try_from(history_rows + copy_rows).expect("bounded row count"),
        );
        source_rows.extend(old_viewport - history_rows..old_viewport);
        source_rows.extend(old_viewport..old_viewport + copy_rows);

        let stored_rows = history_rows + rows;
        let mut resized = vec![Cell::default(); super::cell_count(columns, stored_rows)];
        let mut meaningful_end = vec![0; usize::try_from(stored_rows).expect("bounded rows")];
        let mut row_metadata =
            Vec::with_capacity(usize::try_from(stored_rows).expect("bounded rows"));
        for (target_row, &source_row) in source_rows.iter().enumerate() {
            let source_index = usize::try_from(source_row).expect("bounded row");
            meaningful_end[target_row] = old_meaningful_end[source_index].min(columns);
            if source_index == 0 || !old_soft_wrapped[source_index - 1] {
                row_metadata.push(old_metadata[source_index]);
            } else if target_row == 0 {
                let mut metadata = old_metadata[source_index];
                metadata.mark_head_truncated();
                row_metadata.push(metadata);
            } else {
                let Some(logical_line_id) = self.allocate_logical_line_id() else {
                    return;
                };
                row_metadata.push(RowMetadata::line_head(logical_line_id));
            }
            for column in 0..copy_columns {
                resized[cell_index(
                    columns,
                    u32::try_from(target_row).expect("bounded row"),
                    column,
                )] = self.cells[cell_index(self.columns, source_row, column)].clone();
            }
        }
        let Some(fresh_metadata) = self.fresh_line_heads(rows - copy_rows) else {
            return;
        };
        row_metadata.extend(fresh_metadata);
        self.columns = columns;
        self.rows = rows;
        self.cells = resized;
        self.soft_wrapped = vec![false; usize::try_from(stored_rows).expect("bounded rows")];
        self.meaningful_end = meaningful_end;
        self.row_metadata = row_metadata;
        self.apply_storage_policy(storage);
        self.cursor_row = self.cursor_row.min(rows - 1);
        self.cursor_column = self.cursor_column.min(columns - 1);
        self.saved.row = self.saved.row.min(rows - 1);
        self.saved.column = self.saved.column.min(columns - 1);
        self.repair_all_rows();
        self.snap_cursors_from_continuations();
    }

    pub(super) fn resize_reflow(&mut self, columns: u32, rows: u32) -> Result<(), TerminalError> {
        if self.columns == columns && self.rows == rows {
            return Ok(());
        }
        let storage = self.storage_policy();
        self.normalize_row_metadata();
        self.validate_reflow_target(columns)?;
        let cursor = self.logical_position(self.cursor_row, self.cursor_column, self.wrap_pending);
        let saved =
            self.logical_position(self.saved.row, self.saved.column, self.saved.wrap_pending);
        let viewport = self.logical_position_stored(self.viewport_top(), 0, false);
        let lines = self.logical_lines(cursor, saved);
        let mut cells = Vec::new();
        let mut soft_wrapped = Vec::new();
        let mut meaningful_end = Vec::new();
        let mut row_metadata = Vec::new();
        let mut new_cursor = None;
        let mut new_saved = None;
        let mut new_viewport = None;

        for line in &lines {
            let first_row = soft_wrapped.len();
            let layout = layout_line(line, columns)?;
            if cursor.logical_line_id == line.metadata.logical_line_id {
                new_cursor = Some(layout.position(cursor.logical_cell_offset, first_row, columns));
            }
            if saved.logical_line_id == line.metadata.logical_line_id {
                new_saved = Some(layout.position(saved.logical_cell_offset, first_row, columns));
            }
            if viewport.logical_line_id == line.metadata.logical_line_id {
                new_viewport =
                    Some(layout.position(viewport.logical_cell_offset, first_row, columns));
            }
            for (row_index, (row_cells, metadata)) in
                layout.rows.into_iter().zip(layout.row_metadata).enumerate()
            {
                cells.extend(row_cells);
                soft_wrapped.push(row_index + 1 < layout.row_count);
                meaningful_end.push(layout.meaningful_end[row_index]);
                row_metadata.push(metadata);
            }
        }
        let target_rows = usize::try_from(rows).expect("bounded rows");
        let cursor_before_window = new_cursor.unwrap_or((0, 0, false));
        let mapped_viewport = usize::try_from(new_viewport.unwrap_or((0, 0, false)).0)
            .expect("bounded row count")
            .min(soft_wrapped.len().saturating_sub(1));
        let overflow_rows = soft_wrapped
            .len()
            .saturating_sub(mapped_viewport.saturating_add(target_rows));
        let cursor_after_viewport = usize::try_from(cursor_before_window.0)
            .expect("bounded cursor row")
            .saturating_sub(mapped_viewport);
        let viewport_shift = overflow_rows.min(cursor_after_viewport);
        let viewport = mapped_viewport + viewport_shift;

        while soft_wrapped.len() < viewport.saturating_add(target_rows) {
            cells.extend(vec![
                Cell::default();
                usize::try_from(columns).expect("bounded columns")
            ]);
            soft_wrapped.push(false);
            meaningful_end.push(0);
            row_metadata.push(RowMetadata::line_head(
                self.allocate_logical_line_id()
                    .ok_or(TerminalError::LimitExceeded)?,
            ));
        }

        let history_capacity = usize::try_from(storage.history_capacity(columns, rows))
            .expect("bounded history capacity");
        let retained_start = viewport.saturating_sub(history_capacity);
        let retained_history = viewport - retained_start;
        let retained_end = viewport + target_rows;
        cells.truncate(retained_end * usize::try_from(columns).expect("bounded columns"));
        soft_wrapped.truncate(retained_end);
        meaningful_end.truncate(retained_end);
        row_metadata.truncate(retained_end);
        soft_wrapped[retained_end - 1] = false;
        if retained_start > 0 {
            let first_kept_id = row_metadata[retained_start].logical_line_id;
            let truncated_logical_head = row_metadata[..retained_start]
                .iter()
                .any(|metadata| metadata.logical_line_id == first_kept_id);
            cells.drain(..retained_start * usize::try_from(columns).expect("bounded columns"));
            soft_wrapped.drain(..retained_start);
            meaningful_end.drain(..retained_start);
            row_metadata.drain(..retained_start);
            if truncated_logical_head {
                row_metadata[0].mark_head_truncated();
            }
        }
        if cells.len() > storage.cell_cap || cells.len() > MAX_CELLS {
            return Err(TerminalError::LimitExceeded);
        }
        self.columns = columns;
        self.rows = rows;
        self.cells = cells;
        self.soft_wrapped = soft_wrapped;
        self.meaningful_end = meaningful_end;
        self.row_metadata = row_metadata;
        self.apply_storage_policy(storage);
        let (cursor_row, cursor_column, cursor_wrap) =
            window_anchored_position(cursor_before_window, viewport, target_rows);
        self.cursor_row = cursor_row;
        self.cursor_column = cursor_column;
        self.wrap_pending = cursor_wrap;
        let (saved_row, saved_column, saved_wrap) =
            window_anchored_position(new_saved.unwrap_or((0, 0, false)), viewport, target_rows);
        self.saved.row = saved_row;
        self.saved.column = saved_column;
        self.saved.wrap_pending = saved_wrap;
        self.cursor_row = self.cursor_row.min(rows - 1);
        self.saved.row = self.saved.row.min(rows - 1);
        debug_assert_eq!(
            self.viewport_top(),
            u32::try_from(retained_history).expect("bounded history rows")
        );
        Ok(())
    }

    pub(super) fn validate_reflow_target(&self, columns: u32) -> Result<(), TerminalError> {
        let mut total_rows = 0_u64;
        let mut line_rows = 1_u64;
        let mut column = 0_u32;
        let mut wrap = false;
        for row in 0..self.stored_rows() {
            let row_index = usize::try_from(row).expect("bounded row");
            for source_column in 0..self.meaningful_end[row_index] {
                let cell = &self.cells[cell_index(self.columns, row, source_column)];
                if cell.continuation {
                    continue;
                }
                let width = if u32::from(cell.width) > columns {
                    1
                } else {
                    u32::from(cell.width)
                };
                if wrap || column + width > columns {
                    line_rows = line_rows
                        .checked_add(1)
                        .ok_or(TerminalError::LimitExceeded)?;
                    column = 0;
                    wrap = false;
                }
                column += width;
                if column == columns {
                    column = columns - 1;
                    wrap = true;
                }
            }
            let next_row_starts_new_line =
                self.row_metadata.get(row_index + 1).is_some_and(|next| {
                    next.logical_line_id != self.row_metadata[row_index].logical_line_id
                });
            if !self.soft_wrapped[row_index] || next_row_starts_new_line {
                total_rows = total_rows
                    .checked_add(line_rows)
                    .ok_or(TerminalError::LimitExceeded)?;
                if total_rows.saturating_mul(u64::from(columns))
                    > u64::try_from(self.storage_policy().cell_cap).expect("cell limit fits u64")
                {
                    return Err(TerminalError::LimitExceeded);
                }
                line_rows = 1;
                column = 0;
                wrap = false;
            }
        }
        Ok(())
    }

    pub(super) fn save_cursor(&mut self, origin_mode: bool, auto_wrap: bool) {
        self.saved = SavedCursor {
            row: self.cursor_row,
            column: self.cursor_column,
            wrap_pending: self.wrap_pending,
            attributes: self.attributes,
            protected: self.protected,
            origin_mode,
            auto_wrap,
        };
    }

    pub(super) fn restore_cursor(&mut self) -> (bool, bool) {
        self.cursor_row = self.saved.row.min(self.rows - 1);
        self.cursor_column = self.saved.column.min(self.columns - 1);
        self.wrap_pending = self.saved.wrap_pending;
        self.attributes = self.saved.attributes;
        self.protected = self.saved.protected;
        if !self.wrap_pending {
            self.snap_cursor_from_continuation();
        }
        (self.saved.origin_mode, self.saved.auto_wrap)
    }

    pub(super) fn line_feed_in_rectangle_with_scroll_cause(
        &mut self,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        cause: ScrollCause,
    ) {
        let _ = self.line_feed_in_rectangle_with_wrap(false, top, bottom, left, right, cause);
    }

    #[cfg(test)]
    pub(super) fn reverse_index(&mut self, top: u32, bottom: u32) {
        self.reverse_index_in_rectangle(top, bottom, 0, self.columns);
    }

    pub(super) fn reverse_index_in_rectangle(
        &mut self,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        let rectangle = self.clipped_rectangle(top, left, bottom, right);
        if rectangle.is_some_and(|rectangle| {
            self.cursor_row == rectangle.top
                && self.cursor_column >= rectangle.left
                && self.cursor_column < rectangle.right
        }) {
            let rectangle = rectangle.expect("checked rectangle");
            self.scroll_down_rectangle(
                1,
                rectangle.top,
                rectangle.bottom,
                rectangle.left,
                rectangle.right,
            );
        } else if self.cursor_row > 0 {
            self.cursor_row -= 1;
        }
        self.snap_cursor_from_continuation();
    }

    pub(super) fn backspace(&mut self) {
        self.prepare_for_mutation();
        self.cursor_column = self.cursor_column.saturating_sub(1);
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
    }

    #[cfg(test)]
    pub(super) fn horizontal_tab(&mut self) {
        self.prepare_for_mutation();
        let next = ((self.cursor_column / 8) + 1).saturating_mul(8);
        self.cursor_column = next.min(self.columns - 1);
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
    }

    #[cfg(test)]
    pub(super) fn print(&mut self, character: char) -> Result<(), TerminalError> {
        self.print_with_mode(character, false, true, 0, self.rows, None)
    }

    #[cfg(test)]
    pub(super) fn print_insert(&mut self, character: char) -> Result<(), TerminalError> {
        self.print_with_mode(character, true, true, 0, self.rows, None)
    }

    #[cfg(test)]
    pub(super) fn print_modes(
        &mut self,
        character: char,
        insert_mode: bool,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
    ) -> Result<(), TerminalError> {
        self.print_with_mode(character, insert_mode, auto_wrap, top, bottom, None)
    }

    #[cfg(test)]
    pub(super) fn print_modes_with_hyperlink(
        &mut self,
        character: char,
        insert_mode: bool,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        hyperlink: Option<HyperlinkRef>,
    ) -> Result<(), TerminalError> {
        self.print_with_mode(character, insert_mode, auto_wrap, top, bottom, hyperlink)
    }

    #[cfg(test)]
    fn print_with_mode(
        &mut self,
        character: char,
        insert_mode: bool,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        hyperlink: Option<HyperlinkRef>,
    ) -> Result<(), TerminalError> {
        self.print_with_mode_and_scroll_cause(
            character,
            insert_mode,
            auto_wrap,
            top,
            bottom,
            hyperlink,
            ScrollCause::Ordinary,
        )
    }

    #[allow(clippy::too_many_arguments)]
    #[cfg(test)]
    pub(super) fn print_with_mode_and_scroll_cause(
        &mut self,
        character: char,
        insert_mode: bool,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        hyperlink: Option<HyperlinkRef>,
        scroll_cause: ScrollCause,
    ) -> Result<(), TerminalError> {
        self.print_in_rectangle_with_mode_and_scroll_cause(
            character,
            insert_mode,
            auto_wrap,
            top,
            bottom,
            0,
            self.columns,
            hyperlink,
            scroll_cause,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub(super) fn print_in_rectangle_with_mode_and_scroll_cause(
        &mut self,
        mut character: char,
        insert_mode: bool,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        hyperlink: Option<HyperlinkRef>,
        scroll_cause: ScrollCause,
    ) -> Result<(), TerminalError> {
        self.prepare_for_mutation();
        let rectangle = self.clipped_rectangle(top, left, bottom, right);
        let (line_left, line_right) = rectangle
            .filter(|rectangle| {
                self.cursor_row >= rectangle.top
                    && self.cursor_row < rectangle.bottom
                    && self.cursor_column >= rectangle.left
                    && self.cursor_column < rectangle.right
            })
            .map_or((0, self.columns), |rectangle| {
                (rectangle.left, rectangle.right)
            });
        let mut width = scalar_width(character);
        if width == 0 {
            return self.append_combining(
                character,
                auto_wrap,
                top,
                bottom,
                left,
                right,
                line_left,
                line_right,
                scroll_cause,
            );
        }
        if width == 2 && line_right - line_left < 2 {
            return Err(TerminalError::Unsupported(
                "wide grapheme in one-column terminal",
            ));
        }
        if width == 2 && self.previous_grapheme_ends_with_joiner(auto_wrap, line_left, line_right) {
            return self.append_joined_scalar(
                character,
                auto_wrap,
                top,
                bottom,
                left,
                right,
                line_left,
                line_right,
                scroll_cause,
            );
        }
        if self.wrap_pending || (width == 2 && self.cursor_column + 1 >= line_right) {
            if auto_wrap {
                if !self.line_feed_in_rectangle_with_wrap(
                    true,
                    top,
                    bottom,
                    left,
                    right,
                    scroll_cause,
                ) {
                    return Err(TerminalError::LimitExceeded);
                }
                self.cursor_column = line_left;
            } else {
                self.wrap_pending = false;
                if width == 2 && self.cursor_column + 1 >= line_right {
                    character = '\u{fffd}';
                    width = 1;
                }
            }
        }
        if insert_mode {
            let erase_attributes = self.standard_erase_attributes();
            self.insert_blank_cells_in_range(
                self.cursor_row,
                self.cursor_column,
                line_right,
                u32::from(width),
                erase_attributes,
            );
        }
        self.clear_grapheme_at(self.cursor_row, self.cursor_column);
        let index = self.visible_cell_index(self.cursor_row, self.cursor_column);
        self.cells[index] = Cell::lead_with_presentation(
            character,
            width,
            self.attributes,
            self.protected,
            hyperlink,
        );
        if width == 2 {
            self.clear_grapheme_at(self.cursor_row, self.cursor_column + 1);
            self.cells[index + 1] = Cell::continuation_from(&self.cells[index]);
        }
        let end = self.cursor_column + u32::from(width);
        let row = self.visible_row_index(self.cursor_row);
        self.meaningful_end[row] = self.meaningful_end[row].max(end);
        self.refresh_continuation_metadata(row);
        if end >= line_right {
            self.cursor_column = line_right - 1;
            self.wrap_pending = auto_wrap;
        } else {
            self.cursor_column = end;
            self.wrap_pending = false;
        }
        Ok(())
    }

    #[cfg(test)]
    pub(super) fn insert_characters(&mut self, count: u16) {
        self.insert_characters_in_rectangle(count, 0, self.rows, 0, self.columns);
    }

    pub(super) fn insert_characters_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }
        let count = normalized_count(count, rectangle.right - self.cursor_column);
        let erase_attributes = self.standard_erase_attributes();
        self.insert_blank_cells_in_range(
            self.cursor_row,
            self.cursor_column,
            rectangle.right,
            count,
            erase_attributes,
        );
    }

    #[cfg(test)]
    pub(super) fn delete_characters(&mut self, count: u16) {
        self.delete_characters_in_rectangle(count, 0, self.rows, 0, self.columns);
    }

    pub(super) fn delete_characters_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }
        let column = self.cursor_column;
        let count = normalized_count(count, rectangle.right - column);
        let mut row = self.row_cells(self.cursor_row);
        let mut meaningful = self.row_meaningful(self.cursor_row);
        let start = usize::try_from(column).expect("bounded column");
        let right = usize::try_from(rectangle.right).expect("bounded column");
        let count = usize::try_from(count).expect("bounded count");
        row[start..right].rotate_left(count);
        meaningful[start..right].rotate_left(count);
        let tail = right - count;
        let erase_attributes = self.standard_erase_attributes();
        row[tail..right].fill(Cell::blank(erase_attributes));
        meaningful[tail..right].fill(true);
        repair_horizontal_boundaries(&mut row, &mut meaningful, start, right);
        self.store_repaired_row_with_blank_attributes(
            self.cursor_row,
            row,
            meaningful,
            erase_attributes,
        );
    }

    pub(super) fn insert_columns_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }

        let column = self.cursor_column;
        let count = normalized_count(count, rectangle.right - column);
        let erase_attributes = self.standard_erase_attributes();
        for row in rectangle.top..rectangle.bottom {
            self.insert_blank_cells_in_range(row, column, rectangle.right, count, erase_attributes);
        }
    }

    pub(super) fn delete_columns_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }

        let column = self.cursor_column;
        let count = normalized_count(count, rectangle.right - column);
        let start = usize::try_from(column).expect("bounded column");
        let right = usize::try_from(rectangle.right).expect("bounded column");
        let count = usize::try_from(count).expect("bounded count");
        let tail = right - count;
        let erase_attributes = self.standard_erase_attributes();
        for row_index in rectangle.top..rectangle.bottom {
            let mut row = self.row_cells(row_index);
            let mut meaningful = self.row_meaningful(row_index);
            row[start..right].rotate_left(count);
            meaningful[start..right].rotate_left(count);
            row[tail..right].fill(Cell::blank(erase_attributes));
            meaningful[tail..right].fill(true);
            repair_horizontal_boundaries(&mut row, &mut meaningful, start, right);
            self.store_repaired_row_with_blank_attributes(
                row_index,
                row,
                meaningful,
                erase_attributes,
            );
        }
    }

    #[cfg(test)]
    pub(super) fn erase_characters(&mut self, count: u16) {
        self.erase_characters_in_rectangle(count, 0, self.rows, 0, self.columns);
    }

    pub(super) fn erase_characters_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }
        let column = self.cursor_column;
        let count = normalized_count(count, rectangle.right - column);
        let mut row = self.row_cells(self.cursor_row);
        let mut meaningful = self.row_meaningful(self.cursor_row);
        let mut start = usize::try_from(column).expect("bounded column");
        let mut end = start + usize::try_from(count).expect("bounded count");
        if row[start].continuation {
            start -= 1;
        }
        let right = usize::try_from(rectangle.right).expect("bounded column");
        if end < right && row[end].continuation {
            end += 1;
        }
        let erase_attributes = self.standard_erase_attributes();
        row[start..end].fill(Cell::blank(erase_attributes));
        meaningful[start..end].fill(true);
        self.store_repaired_row_with_blank_attributes(
            self.cursor_row,
            row,
            meaningful,
            erase_attributes,
        );
    }

    #[cfg(test)]
    pub(super) fn insert_lines(&mut self, count: u16, top: u32, bottom: u32) {
        self.insert_lines_in_rectangle(count, top, bottom, 0, self.columns);
    }

    pub(super) fn insert_lines_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }
        let count = normalized_count(count, rectangle.bottom - self.cursor_row);
        let rectangle = Rectangle {
            top: self.cursor_row,
            ..rectangle
        };
        let erase_attributes = self.standard_erase_attributes();
        if rectangle.left == 0 && rectangle.right == self.columns {
            self.shift_rows_down(rectangle.top, rectangle.bottom, count);
        } else {
            self.shift_rectangle_vertically(rectangle, count, false, erase_attributes);
        }
    }

    #[cfg(test)]
    pub(super) fn delete_lines(&mut self, count: u16, top: u32, bottom: u32) {
        self.delete_lines_in_rectangle(count, top, bottom, 0, self.columns);
    }

    pub(super) fn delete_lines_in_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if self.cursor_row < rectangle.top
            || self.cursor_row >= rectangle.bottom
            || self.cursor_column < rectangle.left
            || self.cursor_column >= rectangle.right
        {
            return;
        }
        let count = normalized_count(count, rectangle.bottom - self.cursor_row);
        let rectangle = Rectangle {
            top: self.cursor_row,
            ..rectangle
        };
        let erase_attributes = self.standard_erase_attributes();
        if rectangle.left == 0 && rectangle.right == self.columns {
            self.shift_rows_up(rectangle.top, rectangle.bottom, count);
        } else {
            self.shift_rectangle_vertically(rectangle, count, true, erase_attributes);
        }
    }

    #[cfg(test)]
    pub(super) fn scroll_up_region(&mut self, count: u16, top: u32, bottom: u32) {
        self.scroll_up_region_with_cause(count, top, bottom, ScrollCause::Ordinary);
    }

    pub(super) fn scroll_up_region_with_cause(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        cause: ScrollCause,
    ) {
        self.prepare_for_mutation();
        let Some((top, bottom)) = self.vertical_region(top, bottom) else {
            return;
        };
        let count = normalized_count(count, bottom - top);
        if cause.output_revision().is_some()
            && top == 0
            && bottom == self.rows
            && self.history_capacity() > 0
        {
            if self.capture_full_viewport_scroll(count)
                && let Some(revision) = cause.output_revision()
            {
                self.seal_history(revision);
            }
        } else {
            let _ = self.shift_rows_up(top, bottom, count);
        }
    }

    pub(super) fn scroll_down_region(&mut self, count: u16, top: u32, bottom: u32) {
        self.prepare_for_mutation();
        let Some((top, bottom)) = self.vertical_region(top, bottom) else {
            return;
        };
        let count = normalized_count(count, bottom - top);
        self.shift_rows_down(top, bottom, count);
    }

    #[cfg(test)]
    pub(super) fn scroll_up_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        self.scroll_up_rectangle_with_cause(count, top, bottom, left, right, ScrollCause::Ordinary);
    }

    pub(super) fn scroll_up_rectangle_with_cause(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        cause: ScrollCause,
    ) {
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if rectangle.left == 0 && rectangle.right == self.columns {
            self.scroll_up_region_with_cause(count, rectangle.top, rectangle.bottom, cause);
            return;
        }
        self.prepare_for_mutation();
        let count = normalized_count(count, rectangle.bottom - rectangle.top);
        let erase_attributes = self.standard_erase_attributes();
        self.shift_rectangle_vertically(rectangle, count, true, erase_attributes);
    }

    pub(super) fn scroll_down_rectangle(
        &mut self,
        count: u16,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) {
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if rectangle.left == 0 && rectangle.right == self.columns {
            self.scroll_down_region(count, rectangle.top, rectangle.bottom);
            return;
        }
        self.prepare_for_mutation();
        let count = normalized_count(count, rectangle.bottom - rectangle.top);
        let erase_attributes = self.standard_erase_attributes();
        self.shift_rectangle_vertically(rectangle, count, false, erase_attributes);
    }

    pub(super) fn erase_rectangle(&mut self, top: u32, left: u32, bottom: u32, right: u32) {
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        self.prepare_for_mutation();
        for row_index in rectangle.top..rectangle.bottom {
            let mut row = self.row_cells(row_index);
            let mut meaningful = self.row_meaningful(row_index);
            let (start, end) = expanded_rectangle_columns(&row, rectangle.left, rectangle.right);
            let erase_attributes = Attributes {
                foreground: self.attributes.foreground,
                background: self.attributes.background,
                ..Attributes::default()
            };
            row[start..end].fill(Cell::blank(erase_attributes));
            meaningful[start..end].fill(true);
            self.store_repaired_row(row_index, row, meaningful);
        }
    }

    pub(super) fn selective_erase_rectangle(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
    ) -> Result<(), TerminalError> {
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return Ok(());
        };
        self.prepare_for_mutation();
        for row_index in rectangle.top..rectangle.bottom {
            let mut row = self.row_cells(row_index);
            let mut meaningful = self.row_meaningful(row_index);
            let (start, end) = expanded_rectangle_columns(&row, rectangle.left, rectangle.right);
            let mut column = start;
            while column < end {
                if row[column].continuation {
                    column += 1;
                    continue;
                }
                let width = usize::from(row[column].width.max(1));
                if !row[column].protected {
                    for target in column..(column + width).min(row.len()) {
                        let attributes = row[target].attributes;
                        let protected = row[target].protected;
                        row[target] = Cell::blank(attributes);
                        row[target].protected = protected;
                        meaningful[target] = true;
                    }
                }
                column += width;
            }
            self.store_repaired_row(row_index, row, meaningful);
        }
        Ok(())
    }

    pub(super) fn fill_rectangle_with_hyperlink(
        &mut self,
        character: char,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        hyperlink: Option<HyperlinkRef>,
    ) -> Result<(), TerminalError> {
        if character.is_control() || scalar_width(character) != 1 {
            return Err(TerminalError::Unsupported(
                "rectangle fill requires a narrow graphic character",
            ));
        }
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return Ok(());
        };
        self.prepare_for_mutation();
        let fill =
            Cell::lead_with_presentation(character, 1, self.attributes, self.protected, hyperlink);
        let selected_start = usize::try_from(rectangle.left).expect("bounded rectangle column");
        let selected_end = usize::try_from(rectangle.right).expect("bounded rectangle column");
        for row_index in rectangle.top..rectangle.bottom {
            let mut row = self.row_cells(row_index);
            let mut meaningful = self.row_meaningful(row_index);
            let (start, end) = expanded_rectangle_columns(&row, rectangle.left, rectangle.right);
            row[start..end].fill(Cell::blank(self.attributes));
            row[selected_start..selected_end].fill(fill.clone());
            meaningful[start..end].fill(true);
            self.store_repaired_row(row_index, row, meaningful);
        }
        Ok(())
    }

    pub(super) fn copy_rectangle(
        &mut self,
        source_top: u32,
        source_left: u32,
        source_bottom: u32,
        source_right: u32,
        destination_top: u32,
        destination_left: u32,
    ) {
        let Some(source) =
            self.clipped_rectangle(source_top, source_left, source_bottom, source_right)
        else {
            return;
        };
        if destination_top >= self.rows || destination_left >= self.columns {
            return;
        }

        let copy_rows = (source.bottom - source.top).min(self.rows - destination_top);
        let copy_columns = (source.right - source.left).min(self.columns - destination_left);
        if copy_rows == 0 || copy_columns == 0 {
            return;
        }

        let source_left = usize::try_from(source.left).expect("bounded rectangle column");
        let source_right =
            source_left + usize::try_from(copy_columns).expect("bounded rectangle width");
        let copy_columns = usize::try_from(copy_columns).expect("bounded rectangle width");
        let mut staged_rows =
            Vec::with_capacity(usize::try_from(copy_rows).expect("bounded rectangle height"));
        for row_offset in 0..copy_rows {
            let source_row = source.top + row_offset;
            let row = self.row_cells(source_row);
            let meaningful = self.row_meaningful(source_row);
            let mut copied_cells = row[source_left..source_right].to_vec();
            let mut copied_meaningful = meaningful[source_left..source_right].to_vec();
            repair_horizontal_boundaries(
                &mut copied_cells,
                &mut copied_meaningful,
                0,
                copy_columns,
            );
            staged_rows.push((copied_cells, copied_meaningful));
        }

        self.prepare_for_mutation();
        let destination_left =
            usize::try_from(destination_left).expect("bounded destination column");
        let destination_right = destination_left + copy_columns;
        for (row_offset, (copied_cells, copied_meaningful)) in staged_rows.into_iter().enumerate() {
            let destination_row = destination_top
                + u32::try_from(row_offset).expect("bounded destination row offset");
            let mut row = self.row_cells(destination_row);
            let mut meaningful = self.row_meaningful(destination_row);
            row[destination_left..destination_right].clone_from_slice(&copied_cells);
            meaningful[destination_left..destination_right].copy_from_slice(&copied_meaningful);
            self.store_repaired_row(destination_row, row, meaningful);
        }
    }

    #[cfg(test)]
    pub(super) fn fill_rectangle(
        &mut self,
        character: char,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
    ) -> Result<(), TerminalError> {
        self.fill_rectangle_with_hyperlink(character, top, left, bottom, right, None)
    }

    #[allow(dead_code)]
    pub(super) fn change_rectangle_attributes_with_extent(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        change: RectangleAttributeChange,
        rectangle_extent: bool,
    ) {
        self.transform_rectangle_attributes_with_extent(
            top,
            left,
            bottom,
            right,
            rectangle_extent,
            |attributes| change.apply(attributes),
        );
    }

    #[cfg(test)]
    pub(super) fn change_rectangle_attributes(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        change: RectangleAttributeChange,
    ) {
        self.change_rectangle_attributes_with_extent(top, left, bottom, right, change, true);
    }

    #[allow(dead_code)]
    pub(super) fn reverse_rectangle_attributes_with_extent(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        toggle: RectangleAttributeToggle,
        rectangle_extent: bool,
    ) {
        self.transform_rectangle_attributes_with_extent(
            top,
            left,
            bottom,
            right,
            rectangle_extent,
            |attributes| toggle.apply(attributes),
        );
    }

    pub(super) fn transform_rectangle_attributes_with_extent(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        rectangle_extent: bool,
        transform: impl FnMut(&mut Attributes),
    ) {
        self.transform_attribute_extent(top, left, bottom, right, rectangle_extent, transform);
    }

    #[cfg(test)]
    pub(super) fn reverse_rectangle_attributes(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        toggle: RectangleAttributeToggle,
    ) {
        self.reverse_rectangle_attributes_with_extent(top, left, bottom, right, toggle, true);
    }

    pub(super) fn set_protected(&mut self, protected: bool) {
        self.protected = protected;
    }

    pub(super) fn cancel_pending_wrap(&mut self) {
        self.wrap_pending = false;
    }

    pub(super) fn position_cursor(&mut self, row: u32, column: u32) {
        self.prepare_for_mutation();
        self.cursor_row = row.min(self.rows - 1);
        self.cursor_column = column.min(self.columns - 1);
        self.wrap_pending = false;
        self.snap_cursor_from_continuation();
    }

    pub(super) fn erase_display(&mut self, mode: u16) -> Result<(), TerminalError> {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        let visible_start = self.visible_cell_index(0, 0);
        let cursor = self.visible_cell_index(self.cursor_row, self.cursor_column);
        let visible_end = visible_start + self.visible_cells().len();
        match mode {
            0 => self.erase_range(cursor, visible_end),
            1 => self.erase_range(visible_start, cursor + 1),
            2 => self.erase_range(visible_start, visible_end),
            3 => {
                let history_rows = self.viewport_top();
                self.drain_stored_prefix(history_rows);
                self.normalize_row_metadata();
            }
            _ => return Err(TerminalError::Unsupported("erase display mode")),
        }
        Ok(())
    }

    pub(super) fn erase_line(&mut self, mode: u16) -> Result<(), TerminalError> {
        self.prepare_for_mutation();
        self.wrap_pending = false;
        let line_start = self.visible_cell_index(self.cursor_row, 0);
        let cursor =
            line_start + usize::try_from(self.cursor_column).expect("bounded cursor column");
        let line_end = line_start + usize::try_from(self.columns).expect("bounded column count");
        match mode {
            0 => self.erase_range(cursor, line_end),
            1 => self.erase_range(line_start, cursor + 1),
            2 => self.erase_range(line_start, line_end),
            _ => return Err(TerminalError::Unsupported("erase line mode")),
        }
        Ok(())
    }

    pub(super) fn validate(&self) -> Result<(), TerminalError> {
        let storage = self.storage_policy();
        if self.cells.len()
            != self.soft_wrapped.len() * usize::try_from(self.columns).expect("bounded columns")
            || self.meaningful_end.len() != self.soft_wrapped.len()
            || self.row_metadata.len() != self.soft_wrapped.len()
            || self.soft_wrapped.is_empty()
            || self.cells.len() > MAX_CELLS
            || self.cells.len() > storage.cell_cap
            || self.soft_wrapped.len() < usize::try_from(self.rows).expect("bounded rows")
        {
            return Err(TerminalError::MalformedSnapshot);
        }
        if self.viewport_top() > self.history_capacity()
            || self.soft_wrapped.last().copied().unwrap_or(true)
            || self.cursor_row >= self.rows
            || self.cursor_column >= self.columns
            || self.saved.row >= self.rows
            || self.saved.column >= self.columns
        {
            return Err(TerminalError::MalformedSnapshot);
        }
        let cursor = self.visible_cell_index(self.cursor_row, self.cursor_column);
        let saved = self.visible_cell_index(self.saved.row, self.saved.column);
        if (!self.wrap_pending && self.cells[cursor].continuation)
            || (!self.saved.wrap_pending && self.cells[saved].continuation)
        {
            return Err(TerminalError::MalformedSnapshot);
        }
        for row in 0..self.stored_rows() {
            let row_index = usize::try_from(row).expect("bounded row");
            let meaningful_end = self.meaningful_end[row_index];
            let metadata = self.row_metadata[row_index];
            if meaningful_end > self.columns
                || metadata.logical_line_id >= self.next_logical_line_id
            {
                return Err(TerminalError::MalformedSnapshot);
            }
            if metadata.flags & !ROW_FLAG_HEAD_TRUNCATED != 0 {
                return Err(TerminalError::MalformedSnapshot);
            }
            let split_at_visible_boundary = row == self.viewport_top()
                && row_index > 0
                && self.row_metadata[row_index - 1].logical_line_id != metadata.logical_line_id;
            if row_index > 0 && self.soft_wrapped[row_index - 1] && !split_at_visible_boundary {
                let previous = self.row_metadata[row_index - 1];
                let Some(expected_offset) = previous
                    .logical_cell_offset
                    .checked_add(self.meaningful_end[row_index - 1])
                else {
                    return Err(TerminalError::MalformedSnapshot);
                };
                if metadata.logical_line_id != previous.logical_line_id
                    || metadata.logical_cell_offset != expected_offset
                    || metadata.sealed_revision != previous.sealed_revision
                    || metadata.flags != 0
                {
                    return Err(TerminalError::MalformedSnapshot);
                }
            } else {
                if metadata.logical_cell_offset != 0 && !metadata.is_head_truncated() {
                    return Err(TerminalError::MalformedSnapshot);
                }
                for previous_row in 0..row_index {
                    if (previous_row == 0 || !self.soft_wrapped[previous_row - 1])
                        && self.row_metadata[previous_row].logical_line_id
                            == metadata.logical_line_id
                    {
                        return Err(TerminalError::MalformedSnapshot);
                    }
                }
            }
            for column in 0..self.columns {
                let index = cell_index(self.columns, row, column);
                let cell = &self.cells[index];
                if cell.continuation {
                    if column == 0
                        || cell.width != 0
                        || cell.character != ' '
                        || !cell.combining.is_empty()
                        || self.cells[index - 1].continuation
                        || self.cells[index - 1].width != 2
                        || self.cells[index - 1].attributes != cell.attributes
                        || self.cells[index - 1].protected != cell.protected
                        || self.cells[index - 1].hyperlink != cell.hyperlink
                    {
                        return Err(TerminalError::MalformedSnapshot);
                    }
                } else if scalar_width(cell.character) == 0
                    || cell.width != grapheme_width(cell.character, &cell.combining)
                    || cell.character.is_control()
                    || cell.grapheme_bytes() > MAX_GRAPHEME_BYTES
                    || (cell.width == 2
                        && (column + 1 >= self.columns || !self.cells[index + 1].continuation))
                    || !valid_combining_text(&cell.combining)
                {
                    return Err(TerminalError::MalformedSnapshot);
                }
                if column >= meaningful_end
                    && (cell.continuation
                        || cell.character != ' '
                        || !cell.combining.is_empty()
                        || cell.width != 1
                        || cell.protected
                        || cell.hyperlink.is_some())
                {
                    return Err(TerminalError::MalformedSnapshot);
                }
            }
        }
        Ok(())
    }

    fn allocate_logical_line_id(&mut self) -> Option<u64> {
        while self
            .row_metadata
            .iter()
            .any(|metadata| metadata.logical_line_id == self.next_logical_line_id)
        {
            self.next_logical_line_id = self.next_logical_line_id.checked_add(1)?;
        }
        let candidate = self.next_logical_line_id;
        self.next_logical_line_id = self.next_logical_line_id.checked_add(1)?;
        Some(candidate)
    }

    fn fresh_line_heads(&mut self, count: u32) -> Option<Vec<RowMetadata>> {
        let mut metadata = Vec::with_capacity(usize::try_from(count).expect("bounded row count"));
        for _ in 0..count {
            metadata.push(RowMetadata::line_head(self.allocate_logical_line_id()?));
        }
        Some(metadata)
    }

    fn normalize_row_metadata(&mut self) {
        self.normalize_row_metadata_from(0);
    }

    fn normalize_row_metadata_from(&mut self, first_row: u32) {
        debug_assert_eq!(self.row_metadata.len(), self.soft_wrapped.len());
        debug_assert_eq!(self.row_metadata.len(), self.meaningful_end.len());
        let first_row = usize::try_from(first_row).expect("bounded row").max(1);
        for row in first_row..self.row_metadata.len() {
            if self.soft_wrapped[row - 1] {
                self.row_metadata[row] = RowMetadata::continuation_of(
                    self.row_metadata[row - 1],
                    self.meaningful_end[row - 1],
                );
            }
        }
    }

    fn refresh_continuation_metadata(&mut self, mut row: usize) {
        while row + 1 < self.row_metadata.len() && self.soft_wrapped[row] {
            self.row_metadata[row + 1] =
                RowMetadata::continuation_of(self.row_metadata[row], self.meaningful_end[row]);
            row += 1;
        }
    }

    fn prepare_for_mutation(&mut self) {
        self.normalize_row_metadata_from(self.viewport_top());
    }

    fn insert_blank_cells_in_range(
        &mut self,
        row_index: u32,
        column: u32,
        right: u32,
        count: u32,
        blank_attributes: Attributes,
    ) {
        let mut row = self.row_cells(row_index);
        let mut meaningful = self.row_meaningful(row_index);
        let start = usize::try_from(column).expect("bounded column");
        let end = usize::try_from(right).expect("bounded column");
        let count = usize::try_from(count.min(right - column)).expect("bounded count");
        row[start..end].rotate_right(count);
        meaningful[start..end].rotate_right(count);
        row[start..start + count].fill(Cell::blank(blank_attributes));
        meaningful[start..start + count].fill(true);
        repair_horizontal_boundaries(&mut row, &mut meaningful, start, end);
        self.store_repaired_row_with_blank_attributes(row_index, row, meaningful, blank_attributes);
    }

    fn standard_erase_attributes(&self) -> Attributes {
        Attributes {
            foreground: self.attributes.foreground,
            background: self.attributes.background,
            ..Attributes::default()
        }
    }

    fn row_cells(&self, row: u32) -> Vec<Cell> {
        let start = self.visible_cell_index(row, 0);
        let end = start + usize::try_from(self.columns).expect("bounded columns");
        self.cells[start..end].to_vec()
    }

    fn row_meaningful(&self, row: u32) -> Vec<bool> {
        let end = self.meaningful_end[self.visible_row_index(row)];
        (0..self.columns).map(|column| column < end).collect()
    }

    fn store_repaired_row(&mut self, row_index: u32, row: Vec<Cell>, meaningful: Vec<bool>) {
        self.store_repaired_row_with_blank_attributes(row_index, row, meaningful, self.attributes);
    }

    fn store_repaired_row_with_blank_attributes(
        &mut self,
        row_index: u32,
        mut row: Vec<Cell>,
        mut meaningful: Vec<bool>,
        blank_attributes: Attributes,
    ) {
        repair_row(&mut row, &mut meaningful, blank_attributes);
        let start = self.visible_cell_index(row_index, 0);
        let end = start + row.len();
        self.cells[start..end].clone_from_slice(&row);
        let meaningful_end = meaningful
            .iter()
            .rposition(|is_meaningful| *is_meaningful)
            .map_or(0, |column| {
                u32::try_from(column + 1).expect("bounded meaningful end")
            });
        let row_index = self.visible_row_index(row_index);
        self.meaningful_end[row_index] = meaningful_end;
        self.refresh_continuation_metadata(row_index);
    }

    fn clipped_rectangle(&self, top: u32, left: u32, bottom: u32, right: u32) -> Option<Rectangle> {
        let rectangle = Rectangle {
            top: top.min(self.rows),
            left: left.min(self.columns),
            bottom: bottom.min(self.rows),
            right: right.min(self.columns),
        };
        (rectangle.top < rectangle.bottom && rectangle.left < rectangle.right).then_some(rectangle)
    }

    fn shift_rectangle_vertically(
        &mut self,
        rectangle: Rectangle,
        count: u32,
        upward: bool,
        blank_attributes: Attributes,
    ) {
        let left = usize::try_from(rectangle.left).expect("bounded rectangle column");
        let right = usize::try_from(rectangle.right).expect("bounded rectangle column");
        let height = rectangle.bottom - rectangle.top;
        let source_rows: Vec<_> = (rectangle.top..rectangle.bottom)
            .map(|row| {
                let cells = self.row_cells(row)[left..right].to_vec();
                let meaningful = self.row_meaningful(row)[left..right].to_vec();
                (cells, meaningful)
            })
            .collect();

        for offset in 0..height {
            let row_index = rectangle.top + offset;
            let mut row = self.row_cells(row_index);
            let mut meaningful = self.row_meaningful(row_index);
            let source_offset = if upward {
                offset.checked_add(count).filter(|source| *source < height)
            } else {
                offset.checked_sub(count)
            };
            if let Some(source_offset) = source_offset {
                let (source_cells, source_meaningful) =
                    &source_rows[usize::try_from(source_offset).expect("bounded source row")];
                row[left..right].clone_from_slice(source_cells);
                meaningful[left..right].copy_from_slice(source_meaningful);
            } else {
                row[left..right].fill(Cell::blank(blank_attributes));
                meaningful[left..right].fill(false);
            }

            // A grapheme split by a horizontal margin cannot move independently. Clear the
            // selected half here; row repair clears its now-orphaned counterpart outside.
            repair_horizontal_boundaries(&mut row, &mut meaningful, left, right);
            self.store_repaired_row_with_blank_attributes(
                row_index,
                row,
                meaningful,
                blank_attributes,
            );
        }
    }

    fn transform_attribute_extent(
        &mut self,
        top: u32,
        left: u32,
        bottom: u32,
        right: u32,
        rectangle_extent: bool,
        mut transform: impl FnMut(&mut Attributes),
    ) {
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return;
        };
        if rectangle_extent || rectangle.top + 1 == rectangle.bottom {
            for row in rectangle.top..rectangle.bottom {
                self.transform_attribute_span(
                    row,
                    rectangle.left,
                    rectangle.right,
                    rectangle_extent,
                    &mut transform,
                );
            }
            return;
        }
        self.transform_attribute_span(
            rectangle.top,
            rectangle.left,
            self.columns,
            false,
            &mut transform,
        );
        for row in rectangle.top + 1..rectangle.bottom - 1 {
            self.transform_attribute_span(row, 0, self.columns, false, &mut transform);
        }
        self.transform_attribute_span(
            rectangle.bottom - 1,
            0,
            rectangle.right,
            false,
            &mut transform,
        );
    }

    fn transform_attribute_span(
        &mut self,
        row_index: u32,
        left: u32,
        right: u32,
        materialize: bool,
        transform: &mut impl FnMut(&mut Attributes),
    ) {
        let row_start = self.visible_cell_index(row_index, 0);
        let row_end = row_start + usize::try_from(self.columns).expect("bounded columns");
        let (start, end) = expanded_rectangle_columns(&self.cells[row_start..row_end], left, right);
        let meaningful_end =
            usize::try_from(self.meaningful_end[self.visible_row_index(row_index)])
                .expect("bounded meaningful end");
        for (column, cell) in self.cells[row_start + start..row_start + end]
            .iter_mut()
            .enumerate()
        {
            if materialize || start + column < meaningful_end {
                transform(&mut cell.attributes);
            }
        }
    }

    fn vertical_region(&self, top: u32, bottom: u32) -> Option<(u32, u32)> {
        let top = top.min(self.rows);
        let bottom = bottom.min(self.rows);
        (top < bottom).then_some((top, bottom))
    }

    fn shift_rows_up(&mut self, top: u32, bottom: u32, count: u32) -> bool {
        let erase_attributes = self.standard_erase_attributes();
        let blank_meaningful_end = if erase_attributes == Attributes::default() {
            0
        } else {
            self.columns
        };
        let stored_top = self.visible_stored_boundary(top);
        let stored_bottom = self.visible_stored_boundary(bottom);
        let source_row = usize::try_from(stored_top + count).expect("bounded source row");
        let source_was_continuation = source_row > 0 && self.soft_wrapped[source_row - 1];
        let outside_row = usize::try_from(stored_bottom).expect("bounded outside row");
        let outside_was_continuation = outside_row < self.row_metadata.len()
            && outside_row > 0
            && self.soft_wrapped[outside_row - 1];
        let split_line_id = if stored_top > 0 && source_was_continuation {
            let Some(logical_line_id) = self.allocate_logical_line_id() else {
                return false;
            };
            Some(logical_line_id)
        } else {
            None
        };
        let outside_split_line_id = if outside_was_continuation {
            let Some(logical_line_id) = self.allocate_logical_line_id() else {
                return false;
            };
            Some(logical_line_id)
        } else {
            None
        };
        let Some(blank_metadata) = self.fresh_line_heads(count) else {
            return false;
        };
        let width = usize::try_from(self.columns).expect("bounded columns");
        let top_index = cell_index(self.columns, stored_top, 0);
        let bottom_index = cell_index(self.columns, stored_bottom, 0);
        let shifted_cells = usize::try_from(count).expect("bounded row count") * width;
        self.cells[top_index..bottom_index].rotate_left(shifted_cells);
        self.cells[bottom_index - shifted_cells..bottom_index].fill(Cell::blank(erase_attributes));

        let top = usize::try_from(stored_top).expect("bounded row");
        let bottom = usize::try_from(stored_bottom).expect("bounded row");
        let count = usize::try_from(count).expect("bounded row count");
        self.soft_wrapped[top..bottom].rotate_left(count);
        self.meaningful_end[top..bottom].rotate_left(count);
        self.row_metadata[top..bottom].rotate_left(count);
        self.soft_wrapped[bottom - count..bottom].fill(false);
        self.meaningful_end[bottom - count..bottom].fill(blank_meaningful_end);
        self.row_metadata[bottom - count..bottom].copy_from_slice(&blank_metadata);
        if u32::try_from(top).expect("bounded row") > self.viewport_top() {
            self.soft_wrapped[top - 1] = false;
        }
        if bottom - count > top {
            self.soft_wrapped[bottom - count - 1] = false;
        }
        if let Some(logical_line_id) = split_line_id {
            self.row_metadata[top] = RowMetadata::line_head(logical_line_id);
        } else if source_was_continuation {
            self.row_metadata[top].mark_head_truncated();
        }
        if let Some(logical_line_id) = outside_split_line_id {
            self.row_metadata[bottom] = RowMetadata::line_head(logical_line_id);
        }
        self.normalize_row_metadata();
        true
    }

    fn shift_rows_down(&mut self, top: u32, bottom: u32, count: u32) -> bool {
        let erase_attributes = self.standard_erase_attributes();
        let blank_meaningful_end = if erase_attributes == Attributes::default() {
            0
        } else {
            self.columns
        };
        let stored_top = self.visible_stored_boundary(top);
        let stored_bottom = self.visible_stored_boundary(bottom);
        let top_index = usize::try_from(stored_top).expect("bounded top row");
        let source_was_continuation = top_index > 0 && self.soft_wrapped[top_index - 1];
        let outside_row = usize::try_from(stored_bottom).expect("bounded outside row");
        let outside_was_continuation = outside_row < self.row_metadata.len()
            && outside_row > 0
            && self.soft_wrapped[outside_row - 1];
        let split_line_id = if source_was_continuation {
            let Some(logical_line_id) = self.allocate_logical_line_id() else {
                return false;
            };
            Some(logical_line_id)
        } else {
            None
        };
        let outside_split_line_id = if outside_was_continuation {
            let Some(logical_line_id) = self.allocate_logical_line_id() else {
                return false;
            };
            Some(logical_line_id)
        } else {
            None
        };
        let Some(blank_metadata) = self.fresh_line_heads(count) else {
            return false;
        };
        let width = usize::try_from(self.columns).expect("bounded columns");
        let top_cell_index = cell_index(self.columns, stored_top, 0);
        let bottom_index = cell_index(self.columns, stored_bottom, 0);
        let shifted_cells = usize::try_from(count).expect("bounded row count") * width;
        self.cells[top_cell_index..bottom_index].rotate_right(shifted_cells);
        self.cells[top_cell_index..top_cell_index + shifted_cells]
            .fill(Cell::blank(erase_attributes));

        let top = top_index;
        let bottom = usize::try_from(stored_bottom).expect("bounded row");
        let count = usize::try_from(count).expect("bounded row count");
        self.soft_wrapped[top..bottom].rotate_right(count);
        self.meaningful_end[top..bottom].rotate_right(count);
        self.row_metadata[top..bottom].rotate_right(count);
        self.soft_wrapped[top..top + count].fill(false);
        self.meaningful_end[top..top + count].fill(blank_meaningful_end);
        self.row_metadata[top..top + count].copy_from_slice(&blank_metadata);
        if u32::try_from(top).expect("bounded row") > self.viewport_top() {
            self.soft_wrapped[top - 1] = false;
        }
        self.soft_wrapped[bottom - 1] = false;
        if let Some(logical_line_id) = split_line_id {
            self.row_metadata[top + count] = RowMetadata::line_head(logical_line_id);
        }
        if let Some(logical_line_id) = outside_split_line_id {
            self.row_metadata[bottom] = RowMetadata::line_head(logical_line_id);
        }
        self.normalize_row_metadata();
        true
    }

    fn capture_full_viewport_scroll(&mut self, count: u32) -> bool {
        debug_assert!(count > 0 && count <= self.rows);
        let erase_attributes = self.standard_erase_attributes();
        let blank_meaningful_end = if erase_attributes == Attributes::default() {
            0
        } else {
            self.columns
        };
        let Some(blank_metadata) = self.fresh_line_heads(count) else {
            return false;
        };
        let history_rows = self.viewport_top();
        let retained_history = history_rows
            .checked_add(count)
            .expect("stored rows are cell-bounded")
            .min(self.history_capacity());
        let drop_rows = history_rows + count - retained_history;
        self.drain_stored_prefix(drop_rows);

        if let Some(soft_wrapped) = self.soft_wrapped.last_mut() {
            *soft_wrapped = false;
        }
        let blank_cells = usize::try_from(count).expect("bounded row count")
            * usize::try_from(self.columns).expect("bounded columns");
        self.cells.extend(std::iter::repeat_n(
            Cell::blank(erase_attributes),
            blank_cells,
        ));
        self.soft_wrapped.extend(std::iter::repeat_n(
            false,
            usize::try_from(count).expect("bounded row count"),
        ));
        self.meaningful_end.extend(std::iter::repeat_n(
            blank_meaningful_end,
            usize::try_from(count).expect("bounded row count"),
        ));
        self.row_metadata.extend(blank_metadata);
        self.scrollback_generation_rows = self
            .scrollback_generation_rows
            .saturating_add(u64::from(count));
        debug_assert_eq!(self.viewport_top(), retained_history);
        debug_assert!(self.cells.len() <= self.storage_policy().cell_cap);
        true
    }

    fn drain_stored_prefix(&mut self, rows: u32) {
        if rows == 0 {
            return;
        }
        let rows = usize::try_from(rows).expect("bounded row count");
        let first_kept_id = self.row_metadata[rows].logical_line_id;
        let truncated_head = self.row_metadata[..rows]
            .iter()
            .any(|metadata| metadata.logical_line_id == first_kept_id);
        let cells = rows * usize::try_from(self.columns).expect("bounded columns");
        self.cells.drain(..cells);
        self.soft_wrapped.drain(..rows);
        self.meaningful_end.drain(..rows);
        self.row_metadata.drain(..rows);
        if truncated_head {
            self.row_metadata[0].mark_head_truncated();
        }
    }

    fn seal_history(&mut self, revision: u64) {
        let history_end = usize::try_from(self.viewport_top()).expect("bounded history rows");
        let mut start = 0;
        while start < history_end {
            let logical_line_id = self.row_metadata[start].logical_line_id;
            let mut end = start + 1;
            while end < self.row_metadata.len()
                && self.row_metadata[end].logical_line_id == logical_line_id
            {
                end += 1;
            }
            if end <= history_end && !self.soft_wrapped[end - 1] {
                for metadata in &mut self.row_metadata[start..end] {
                    if metadata.sealed_revision == 0 {
                        metadata.sealed_revision = revision;
                    }
                }
            }
            start = end;
        }
    }

    fn line_feed_with_wrap(
        &mut self,
        soft_wrap: bool,
        top: u32,
        bottom: u32,
        cause: ScrollCause,
    ) -> bool {
        self.prepare_for_mutation();
        let row = self.visible_row_index(self.cursor_row);
        let continuation = RowMetadata::continuation_of(
            self.visible_row_metadata(self.cursor_row),
            self.meaningful_end[row],
        );
        let previous_soft_wrap = self.soft_wrapped[row];
        let scrolls_region =
            self.cursor_row + 1 == bottom && self.cursor_row >= top && bottom <= self.rows;
        let split_line = if !soft_wrap && previous_soft_wrap && self.cursor_row + 1 < self.rows {
            let Some(logical_line_id) = self.allocate_logical_line_id() else {
                return false;
            };
            Some((row + 1, logical_line_id))
        } else {
            None
        };
        self.soft_wrapped[row] = soft_wrap;
        self.wrap_pending = false;
        if scrolls_region {
            let shifted = if cause.output_revision().is_some()
                && top == 0
                && bottom == self.rows
                && self.history_capacity() > 0
            {
                self.capture_full_viewport_scroll(1)
            } else {
                self.shift_rows_up(top, bottom, 1)
            };
            if !shifted {
                self.soft_wrapped[row] = previous_soft_wrap;
                return false;
            }
        } else if self.cursor_row + 1 < self.rows {
            self.cursor_row += 1;
        }
        let cursor_row = self.visible_row_index(self.cursor_row);
        if let Some((split_row, logical_line_id)) = split_line {
            self.row_metadata[split_row] = RowMetadata::line_head(logical_line_id);
        } else if soft_wrap && cursor_row > 0 && self.soft_wrapped[cursor_row - 1] {
            *self.visible_row_metadata_mut(self.cursor_row) = continuation;
        }
        self.normalize_row_metadata_from(self.viewport_top());
        if let Some(revision) = cause.output_revision() {
            self.seal_history(revision);
        }
        true
    }

    fn line_feed_in_rectangle_with_wrap(
        &mut self,
        soft_wrap: bool,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        cause: ScrollCause,
    ) -> bool {
        let Some(rectangle) = self.clipped_rectangle(top, left, bottom, right) else {
            return self.line_feed_with_wrap(soft_wrap, top, bottom, cause);
        };
        if rectangle.left == 0 && rectangle.right == self.columns {
            return self.line_feed_with_wrap(soft_wrap, rectangle.top, rectangle.bottom, cause);
        }

        self.prepare_for_mutation();
        let source_row = self.visible_row_index(self.cursor_row);
        let continuation = RowMetadata::continuation_of(
            self.visible_row_metadata(self.cursor_row),
            self.meaningful_end[source_row],
        );
        let previous_soft_wrap = self.soft_wrapped[source_row];
        let split_line_id =
            if !soft_wrap && previous_soft_wrap && source_row + 1 < self.row_metadata.len() {
                let Some(logical_line_id) = self.allocate_logical_line_id() else {
                    return false;
                };
                Some(logical_line_id)
            } else {
                None
            };
        self.wrap_pending = false;
        let inside_rectangle = self.cursor_row >= rectangle.top
            && self.cursor_row < rectangle.bottom
            && self.cursor_column >= rectangle.left
            && self.cursor_column < rectangle.right;
        if inside_rectangle && self.cursor_row + 1 == rectangle.bottom {
            if !soft_wrap {
                self.soft_wrapped[source_row] = false;
            }
            let erase_attributes = self.standard_erase_attributes();
            self.shift_rectangle_vertically(rectangle, 1, true, erase_attributes);
        } else if self.cursor_row + 1 < self.rows {
            if !inside_rectangle || !soft_wrap {
                self.soft_wrapped[source_row] = soft_wrap;
            }
            self.cursor_row += 1;
            if !inside_rectangle && soft_wrap {
                *self.visible_row_metadata_mut(self.cursor_row) = continuation;
            }
        } else {
            self.soft_wrapped[source_row] = false;
        }
        if let Some(logical_line_id) = split_line_id {
            self.row_metadata[source_row + 1] = RowMetadata::line_head(logical_line_id);
        }
        self.normalize_row_metadata_from(self.viewport_top());
        true
    }

    #[allow(clippy::too_many_arguments)]
    fn append_combining(
        &mut self,
        character: char,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        line_left: u32,
        line_right: u32,
        scroll_cause: ScrollCause,
    ) -> Result<(), TerminalError> {
        let Some(index) = self.previous_lead_index(auto_wrap, line_left, line_right) else {
            return Ok(());
        };
        let cell = &mut self.cells[index];
        if cell.grapheme_bytes() + character.len_utf8() > MAX_GRAPHEME_BYTES {
            return Err(TerminalError::Unsupported("grapheme size limit"));
        }
        cell.combining.push(character);
        if character == '\u{fe0f}' {
            self.upgrade_grapheme_to_wide(
                index,
                auto_wrap,
                top,
                bottom,
                left,
                right,
                line_left,
                line_right,
                scroll_cause,
            )?;
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn append_joined_scalar(
        &mut self,
        character: char,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        line_left: u32,
        line_right: u32,
        scroll_cause: ScrollCause,
    ) -> Result<(), TerminalError> {
        let index = self
            .previous_lead_index(auto_wrap, line_left, line_right)
            .ok_or(TerminalError::Unsupported("orphan joined scalar"))?;
        let cell = &mut self.cells[index];
        if cell.grapheme_bytes() + character.len_utf8() > MAX_GRAPHEME_BYTES {
            return Err(TerminalError::Unsupported("grapheme size limit"));
        }
        cell.combining.push(character);
        if scalar_width(character) == 2 {
            self.upgrade_grapheme_to_wide(
                index,
                auto_wrap,
                top,
                bottom,
                left,
                right,
                line_left,
                line_right,
                scroll_cause,
            )?;
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn upgrade_grapheme_to_wide(
        &mut self,
        index: usize,
        auto_wrap: bool,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
        line_left: u32,
        line_right: u32,
        scroll_cause: ScrollCause,
    ) -> Result<(), TerminalError> {
        if self.cells[index].width == 2 {
            return Ok(());
        }
        if line_right - line_left < 2 {
            degrade_cell_to_replacement(&mut self.cells[index]);
            return Ok(());
        }
        let stored_row =
            u32::try_from(index / usize::try_from(self.columns).expect("bounded width"))
                .expect("bounded row");
        let row = stored_row - self.viewport_top();
        let column = u32::try_from(index % usize::try_from(self.columns).expect("bounded width"))
            .expect("bounded column");
        if column + 1 >= line_right {
            if !auto_wrap {
                degrade_cell_to_replacement(&mut self.cells[index]);
                self.wrap_pending = false;
                return Ok(());
            }
            let grapheme = self.cells[index].clone();
            self.clear_grapheme_at(row, column);
            if !self.line_feed_in_rectangle_with_wrap(true, top, bottom, left, right, scroll_cause)
            {
                return Err(TerminalError::LimitExceeded);
            }
            self.cursor_column = line_left;
            let target = self.visible_cell_index(self.cursor_row, line_left);
            self.clear_grapheme_at(self.cursor_row, line_left);
            self.clear_grapheme_at(self.cursor_row, line_left + 1);
            self.cells[target] = Cell {
                width: 2,
                ..grapheme
            };
            self.cells[target + 1] = Cell::continuation_from(&self.cells[target]);
            let row_index = self.visible_row_index(self.cursor_row);
            self.meaningful_end[row_index] = self.meaningful_end[row_index].max(line_left + 2);
            self.refresh_continuation_metadata(row_index);
            if line_left + 2 >= line_right {
                self.cursor_column = line_right - 1;
                self.wrap_pending = true;
            } else {
                self.cursor_column = line_left + 2;
                self.wrap_pending = false;
            }
        } else {
            self.clear_grapheme_at(row, column + 1);
            self.cells[index].width = 2;
            self.cells[index + 1] = Cell::continuation_from(&self.cells[index]);
            let row_index = self.visible_row_index(row);
            self.meaningful_end[row_index] = self.meaningful_end[row_index].max(column + 2);
            self.refresh_continuation_metadata(row_index);
            if column + 2 >= line_right {
                self.cursor_column = line_right - 1;
                self.wrap_pending = auto_wrap;
            } else {
                self.cursor_column = column + 2;
                self.wrap_pending = false;
            }
        }
        Ok(())
    }

    fn previous_grapheme_ends_with_joiner(
        &self,
        auto_wrap: bool,
        line_left: u32,
        line_right: u32,
    ) -> bool {
        self.previous_lead_index(auto_wrap, line_left, line_right)
            .and_then(|index| self.cells[index].combining.chars().last())
            == Some('\u{200d}')
    }

    fn previous_lead_index(
        &self,
        auto_wrap: bool,
        line_left: u32,
        line_right: u32,
    ) -> Option<usize> {
        let mut index = self.visible_cell_index(self.cursor_row, self.cursor_column);
        if !self.wrap_pending {
            if !auto_wrap
                && self.cursor_column == line_right - 1
                && self.cursor_column < self.meaningful_end[self.visible_row_index(self.cursor_row)]
            {
                // With DECAWM disabled, a printable scalar written in the last
                // column leaves the cursor on that cell without delayed wrap.
                // A following combining scalar therefore belongs to the cell
                // under the cursor rather than the geometric previous cell.
            } else if self.cursor_column > line_left {
                index -= 1;
            } else if self.cursor_row > 0
                && self.soft_wrapped[self.visible_row_index(self.cursor_row - 1)]
            {
                index = self.visible_cell_index(self.cursor_row - 1, line_right - 1);
            } else {
                return None;
            }
        }
        if self.cells.get(index)?.continuation {
            index = index.checked_sub(1)?;
        }
        let width = usize::try_from(self.columns).ok()?;
        let row = index / width;
        let column = u32::try_from(index % width).ok()?;
        (column < *self.meaningful_end.get(row)? && !self.cells.get(index)?.continuation)
            .then_some(index)
    }

    fn clear_grapheme_at(&mut self, row: u32, column: u32) {
        let mut index = self.visible_cell_index(row, column);
        if self.cells[index].continuation {
            index -= 1;
        }
        let width = self.cells[index].width;
        let lead_column =
            u32::try_from(index % usize::try_from(self.columns).expect("bounded width"))
                .expect("bounded column");
        let row_index = self.visible_row_index(row);
        if lead_column < self.meaningful_end[row_index]
            && lead_column + u32::from(width) >= self.meaningful_end[row_index]
        {
            self.meaningful_end[row_index] = lead_column;
            self.refresh_continuation_metadata(row_index);
        }
        self.cells[index] = Cell::blank(self.attributes);
        if width == 2 {
            self.cells[index + 1] = Cell::blank(self.attributes);
        }
    }

    fn erase_range(&mut self, mut start: usize, mut end: usize) {
        if start < end && self.cells[start].continuation {
            start -= 1;
        }
        if end < self.cells.len() && self.cells[end].continuation {
            end += 1;
        }
        let erase_attributes = self.standard_erase_attributes();
        self.cells[start..end].fill(Cell::blank(erase_attributes));
        let width = usize::try_from(self.columns).expect("bounded columns");
        let first_row = start / width;
        let last_row = (end.saturating_sub(1)) / width;
        for row in first_row..=last_row {
            let erased_end = if row == last_row {
                let remainder = end % width;
                if remainder == 0 { width } else { remainder }
            } else {
                width
            };
            let explicit_end = u32::try_from(erased_end).expect("bounded end");
            self.meaningful_end[row] = self.meaningful_end[row].max(explicit_end);
            self.refresh_continuation_metadata(row);
        }
    }

    fn repair_all_rows(&mut self) {
        for row in 0..self.stored_rows() {
            let mut column = 0;
            while column < self.columns {
                let index = cell_index(self.columns, row, column);
                if self.cells[index].continuation {
                    self.cells[index] = Cell::default();
                } else if self.cells[index].width == 2 {
                    if column + 1 >= self.columns {
                        self.cells[index] = Cell::default();
                        let row_index = usize::try_from(row).expect("bounded row");
                        self.meaningful_end[row_index] = self.meaningful_end[row_index].min(column);
                    } else {
                        self.cells[index + 1] = Cell::continuation_from(&self.cells[index]);
                        column += 1;
                    }
                }
                column += 1;
            }
        }
        self.normalize_row_metadata();
    }

    fn snap_cursors_from_continuations(&mut self) {
        self.snap_cursor_from_continuation();
        let index = self.visible_cell_index(self.saved.row, self.saved.column);
        if self.cells[index].continuation {
            self.saved.column -= 1;
            self.saved.wrap_pending = false;
        }
    }

    pub(super) fn canonicalize_cursor_cells(&mut self) {
        if !self.wrap_pending {
            self.snap_cursor_from_continuation();
        }
        if !self.saved.wrap_pending {
            let index = self.visible_cell_index(self.saved.row, self.saved.column);
            if self.cells[index].continuation {
                self.saved.column -= 1;
            }
        }
    }

    fn snap_cursor_from_continuation(&mut self) {
        let index = self.visible_cell_index(self.cursor_row, self.cursor_column);
        if self.cells[index].continuation {
            self.cursor_column -= 1;
            self.wrap_pending = false;
        }
    }

    fn logical_position(&self, row: u32, column: u32, wrap: bool) -> LogicalAnchor {
        let row = self.visible_stored_row(row.min(self.rows - 1));
        self.logical_position_stored(row, column, wrap)
    }

    fn logical_position_stored(&self, row: u32, column: u32, wrap: bool) -> LogicalAnchor {
        let row = row.min(self.stored_rows() - 1);
        let metadata = self.row_metadata[usize::try_from(row).expect("bounded row")];
        LogicalAnchor {
            logical_line_id: metadata.logical_line_id,
            logical_cell_offset: metadata
                .logical_cell_offset
                .checked_add(column)
                .and_then(|offset| offset.checked_add(u32::from(wrap)))
                .expect("logical cursor offset is cell-bounded"),
        }
    }

    fn logical_lines(&self, cursor: LogicalAnchor, saved: LogicalAnchor) -> Vec<LogicalLine> {
        let mut lines = Vec::new();
        let mut current = None;
        for row in 0..self.stored_rows() {
            let row_index = usize::try_from(row).expect("bounded row");
            let row_end = self.meaningful_end[row_index];
            current.get_or_insert_with(|| {
                let metadata = self.row_metadata[row_index];
                LogicalLine {
                    metadata,
                    cells: Vec::new(),
                    old_width: metadata.logical_cell_offset,
                }
            });
            let line = current.as_mut().expect("current logical line exists");
            for column in 0..row_end {
                let cell = &self.cells[cell_index(self.columns, row, column)];
                if !cell.continuation {
                    line.cells.push(LogicalCell {
                        cell: cell.clone(),
                        old_offset: line.old_width + column,
                    });
                }
            }
            line.old_width += row_end;
            let next_row_starts_new_line =
                self.row_metadata.get(row_index + 1).is_some_and(|next| {
                    next.logical_line_id != self.row_metadata[row_index].logical_line_id
                });
            if !self.soft_wrapped[row_index] || next_row_starts_new_line {
                let preserve = [cursor, saved]
                    .into_iter()
                    .filter(|position| position.logical_line_id == line.metadata.logical_line_id)
                    .map(|position| position.logical_cell_offset)
                    .max()
                    .unwrap_or(0);
                while line.old_width < preserve {
                    line.cells.push(LogicalCell {
                        cell: Cell::default(),
                        old_offset: line.old_width,
                    });
                    line.old_width += 1;
                }
                lines.push(current.take().expect("current logical line exists"));
            }
        }
        if let Some(current) = current {
            lines.push(current);
        }
        if lines.is_empty() {
            let metadata = self.row_metadata[0];
            lines.push(LogicalLine {
                metadata,
                cells: Vec::new(),
                old_width: metadata.logical_cell_offset,
            });
        }
        lines
    }
}

fn expanded_rectangle_columns(cells: &[Cell], left: u32, right: u32) -> (usize, usize) {
    let mut start = usize::try_from(left).expect("bounded rectangle column");
    let mut end = usize::try_from(right).expect("bounded rectangle column");
    if cells[start].continuation {
        start -= 1;
    }
    if end < cells.len() && cells[end].continuation {
        end += 1;
    }
    (start, end)
}

fn repair_horizontal_boundaries(
    cells: &mut [Cell],
    meaningful: &mut [bool],
    left: usize,
    right: usize,
) {
    debug_assert!(left < right && right <= cells.len());
    if cells[left].continuation {
        cells[left] = Cell::default();
        meaningful[left] = false;
    }
    if cells[right - 1].width == 2 && !cells[right - 1].continuation {
        cells[right - 1] = Cell::default();
        meaningful[right - 1] = false;
    }
}

fn normalized_count(count: u16, available: u32) -> u32 {
    u32::from(count.max(1)).min(available)
}

fn degrade_cell_to_replacement(cell: &mut Cell) {
    *cell = Cell {
        protected: cell.protected,
        hyperlink: cell.hyperlink,
        ..Cell::lead('\u{fffd}', 1, cell.attributes)
    };
}

fn repair_row(cells: &mut [Cell], meaningful: &mut [bool], blank_attributes: Attributes) {
    debug_assert_eq!(cells.len(), meaningful.len());
    let mut column = 0;
    while column < cells.len() {
        if cells[column].continuation {
            let valid_lead =
                column > 0 && !cells[column - 1].continuation && cells[column - 1].width == 2;
            if !valid_lead {
                cells[column] = Cell::blank(blank_attributes);
                meaningful[column] = false;
            }
            column += 1;
            continue;
        }
        if cells[column].width == 2 {
            if column + 1 >= cells.len() || !cells[column + 1].continuation {
                cells[column] = Cell::blank(blank_attributes);
                meaningful[column] = false;
                column += 1;
                continue;
            }
            cells[column + 1] = Cell::continuation_from(&cells[column]);
            let grapheme_is_meaningful = meaningful[column] || meaningful[column + 1];
            meaningful[column] = grapheme_is_meaningful;
            meaningful[column + 1] = grapheme_is_meaningful;
            column += 2;
            continue;
        }
        column += 1;
    }
}

struct LineLayout {
    rows: Vec<Vec<Cell>>,
    meaningful_end: Vec<u32>,
    row_metadata: Vec<RowMetadata>,
    row_count: usize,
    positions: Vec<(u32, u32, u32, bool)>,
    end: (u32, u32, bool),
}

impl LineLayout {
    fn position(&self, offset: u32, first_row: usize, columns: u32) -> (u32, u32, bool) {
        let (row, column, wrap) = self
            .positions
            .iter()
            .find(|(old_offset, _, _, _)| *old_offset == offset)
            .map(|(_, row, column, wrap)| (*row, *column, *wrap))
            .unwrap_or(self.end);
        (
            u32::try_from(first_row).expect("bounded row count") + row,
            column.min(columns - 1),
            wrap,
        )
    }
}

fn layout_line(line: &LogicalLine, columns: u32) -> Result<LineLayout, TerminalError> {
    let width = usize::try_from(columns).expect("bounded columns");
    let mut rows = vec![vec![Cell::default(); width]];
    let mut meaningful_end = vec![0];
    let mut row_metadata = vec![line.metadata];
    let mut positions = Vec::new();
    let mut row = 0_u32;
    let mut column = 0_u32;
    let mut wrap = false;
    let mut logical_cell_offset = line.metadata.logical_cell_offset;
    for entry in &line.cells {
        let mut reflow_cell = entry.cell.clone();
        if u32::from(reflow_cell.width) > columns {
            degrade_cell_to_replacement(&mut reflow_cell);
        }
        let cell_width = u32::from(reflow_cell.width);
        if wrap || column + cell_width > columns {
            row += 1;
            column = 0;
            wrap = false;
            rows.push(vec![Cell::default(); width]);
            meaningful_end.push(0);
            row_metadata.push(RowMetadata {
                logical_line_id: line.metadata.logical_line_id,
                logical_cell_offset,
                sealed_revision: line.metadata.sealed_revision,
                flags: 0,
            });
        }
        positions.push((entry.old_offset, row, column, false));
        let index = usize::try_from(column).expect("bounded column");
        rows[usize::try_from(row).expect("bounded row")][index] = reflow_cell.clone();
        if cell_width == 2 {
            rows[usize::try_from(row).expect("bounded row")][index + 1] =
                Cell::continuation_from(&reflow_cell);
        }
        column += cell_width;
        logical_cell_offset = logical_cell_offset
            .checked_add(cell_width)
            .ok_or(TerminalError::LimitExceeded)?;
        meaningful_end[usize::try_from(row).expect("bounded row")] = column;
        if column == columns {
            column = columns - 1;
            wrap = true;
        }
    }
    let row_count = rows.len();
    Ok(LineLayout {
        rows,
        meaningful_end,
        row_metadata,
        row_count,
        positions,
        end: (row, column, wrap),
    })
}

fn window_anchored_position(
    position: (u32, u32, bool),
    dropped_rows: usize,
    target_rows: usize,
) -> (u32, u32, bool) {
    let dropped_rows = u32::try_from(dropped_rows).expect("bounded row count");
    let target_rows = u32::try_from(target_rows).expect("bounded row count");
    if position.0 < dropped_rows {
        (0, 0, false)
    } else if position.0 - dropped_rows >= target_rows {
        (target_rows - 1, position.1, false)
    } else {
        (position.0 - dropped_rows, position.1, position.2)
    }
}

fn valid_combining_text(text: &str) -> bool {
    let mut joined = false;
    for character in text.chars() {
        if character.is_control() && character != '\u{200d}' {
            return false;
        }
        if scalar_width(character) == 0 {
            joined = character == '\u{200d}';
        } else if joined && scalar_width(character) == 2 {
            joined = false;
        } else {
            return false;
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    fn text_screen(columns: u32, rows: u32, text: &str) -> Screen {
        let mut screen = Screen::new(columns, rows);
        for character in text.chars() {
            screen.print(character).expect("test text is supported");
        }
        screen
    }

    fn lettered_screen() -> Screen {
        let mut screen = Screen::new(3, 5);
        for (row, character) in ['A', 'B', 'C', 'D', 'E'].into_iter().enumerate() {
            let row = u32::try_from(row).expect("bounded test row");
            let index = cell_index(screen.columns, row, 0);
            screen.cells[index] = Cell::lead(character, 1, Attributes::default());
            screen.meaningful_end[usize::try_from(row).expect("bounded test row")] = 1;
        }
        screen
    }

    fn row_signature(screen: &Screen, row: u32) -> Vec<String> {
        (0..screen.columns)
            .map(|column| {
                let cell = &screen.cells[cell_index(screen.columns, row, column)];
                if cell.continuation {
                    "~".to_owned()
                } else {
                    cell.text()
                }
            })
            .collect()
    }

    fn first_columns(screen: &Screen) -> Vec<char> {
        (0..screen.rows)
            .map(|row| screen.cells[cell_index(screen.columns, row, 0)].character)
            .collect()
    }

    fn set_row_text(screen: &mut Screen, row: u32, text: &str) {
        screen.position_cursor(row, 0);
        for character in text.chars() {
            screen
                .print_modes(character, false, false, 0, screen.rows)
                .expect("fixture text is supported");
        }
    }

    fn print_in_rectangle(
        screen: &mut Screen,
        character: char,
        top: u32,
        bottom: u32,
        left: u32,
        right: u32,
    ) -> Result<(), TerminalError> {
        screen.print_in_rectangle_with_mode_and_scroll_cause(
            character,
            false,
            true,
            top,
            bottom,
            left,
            right,
            None,
            ScrollCause::Ordinary,
        )
    }

    fn bce_attributes() -> Attributes {
        Attributes {
            bold: true,
            faint: true,
            conceal: true,
            strikethrough: true,
            overline: true,
            underline_style: UnderlineStyle::Dotted,
            underline_color: Color::Rgb(1, 2, 3),
            foreground: Color::Indexed(5),
            background: Color::Indexed(6),
            ..Attributes::default()
        }
    }

    fn bce_erase_attributes() -> Attributes {
        let attributes = bce_attributes();
        Attributes {
            foreground: attributes.foreground,
            background: attributes.background,
            ..Attributes::default()
        }
    }

    fn linked_wide_screen(columns: u32, rows: u32) -> (Screen, Attributes, HyperlinkRef) {
        let attributes = bce_attributes();
        let hyperlink = HyperlinkRef::new(41);
        let mut screen = Screen::new(columns, rows);
        screen.attributes = attributes;
        screen.protected = true;
        screen
            .print_modes_with_hyperlink('界', false, true, 0, rows, Some(hyperlink))
            .expect("linked wide fixture is supported");
        screen.validate().expect("linked wide fixture is canonical");
        (screen, attributes, hyperlink)
    }

    fn assert_wide_presentation(
        screen: &Screen,
        row: u32,
        column: u32,
        attributes: Attributes,
        hyperlink: HyperlinkRef,
    ) {
        let index = cell_index(screen.columns, row, column);
        let lead = &screen.cells[index];
        let continuation = &screen.cells[index + 1];
        assert_eq!(lead.width, 2);
        assert!(!lead.continuation);
        assert_eq!(lead.attributes, attributes);
        assert!(lead.protected);
        assert_eq!(lead.hyperlink, Some(hyperlink));
        assert_eq!(continuation, &Cell::continuation_from(lead));
    }

    #[test]
    fn visible_row_layout_starts_bounded_with_deterministic_anchors() {
        let screen = Screen::new(4, 3);

        assert_eq!(screen.stored_rows(), screen.rows);
        assert_eq!(screen.viewport_top(), 0);
        assert_eq!(screen.next_logical_line_id, 3);
        assert_eq!(
            screen.row_metadata,
            [
                RowMetadata::line_head(0),
                RowMetadata::line_head(1),
                RowMetadata::line_head(2),
            ]
        );
        assert_eq!(screen.clone().row_metadata, screen.row_metadata);
        screen.validate().expect("initial row layout is canonical");
    }

    #[test]
    fn reflow_preserves_logical_ids_offsets_revisions_and_cursor_anchor() {
        let mut screen = text_screen(4, 3, "ABCDEF");
        screen.row_metadata[0].sealed_revision = 9;
        let line_id = screen.row_metadata[0].logical_line_id;
        let cursor_anchor =
            screen.logical_position(screen.cursor_row, screen.cursor_column, screen.wrap_pending);

        screen.resize_reflow(3, 3).expect("bounded reflow");

        assert_eq!(
            screen.logical_position(screen.cursor_row, screen.cursor_column, screen.wrap_pending,),
            cursor_anchor
        );
        assert_eq!(screen.row_metadata[0].logical_line_id, line_id);
        assert_eq!(screen.row_metadata[0].logical_cell_offset, 0);
        assert_eq!(screen.row_metadata[1].logical_line_id, line_id);
        assert_eq!(screen.row_metadata[1].logical_cell_offset, 3);
        assert_eq!(screen.row_metadata[0].sealed_revision, 9);
        assert_eq!(screen.row_metadata[1].sealed_revision, 9);
        assert_eq!(screen.row_metadata[0].flags, 0);
        assert_eq!(screen.row_metadata[1].flags, 0);
        screen.validate().expect("reflowed row layout is canonical");
    }

    #[test]
    fn reflow_marks_a_partially_evicted_logical_head() {
        let mut screen = text_screen(6, 2, "ABCDEFGHIJKL");
        let line_id = screen.row_metadata[0].logical_line_id;

        screen.resize_reflow(2, 2).expect("cursor-anchored reflow");

        assert_eq!(screen.row_metadata[0].logical_line_id, line_id);
        assert_eq!(screen.row_metadata[0].logical_cell_offset, 8);
        assert!(screen.row_metadata[0].is_head_truncated());
        assert_eq!(screen.row_metadata[1].logical_line_id, line_id);
        assert_eq!(screen.row_metadata[1].logical_cell_offset, 10);
        assert_eq!(screen.row_metadata[1].flags, 0);
        screen
            .validate()
            .expect("truncated reflow head is canonical");
    }

    #[test]
    fn render_window_canonicalizes_logical_lines_cut_at_both_boundaries() {
        let mut screen = text_screen(3, 3, "ABCDEFGHI");
        screen.rows = 2;
        screen.storage = StoragePolicy::new(4, MAX_CELLS);
        screen.cursor_row = 1;
        screen.saved.row = 1;
        assert_eq!(screen.viewport_top(), 1);
        screen.validate().expect("source scrollback is canonical");

        let live = screen.render_window(0);
        assert_eq!(live.row_metadata[0].logical_cell_offset, 3);
        assert!(live.row_metadata[0].is_head_truncated());
        live.validate()
            .expect("live projection marks its discarded logical head");

        let history = screen.render_window(1);
        assert!(screen.soft_wrapped[1]);
        assert!(!history.soft_wrapped[1]);
        history
            .validate()
            .expect("history projection terminates its discarded logical tail");
    }

    #[test]
    fn partial_rectangle_hard_line_feed_detaches_a_wrapped_suffix() {
        for bottom in [1, 3] {
            let mut screen = text_screen(4, 3, "ABCDE");
            let original_line_id = screen.row_metadata[0].logical_line_id;
            assert!(screen.soft_wrapped[0]);
            assert_eq!(screen.row_metadata[1].logical_line_id, original_line_id);
            assert_eq!(screen.row_metadata[1].logical_cell_offset, 4);

            screen.position_cursor(0, 1);
            assert!(screen.line_feed_in_rectangle_with_wrap(
                false,
                0,
                bottom,
                1,
                3,
                ScrollCause::Output(1),
            ));

            assert!(!screen.soft_wrapped[0]);
            assert_ne!(screen.row_metadata[1].logical_line_id, original_line_id);
            assert_eq!(screen.row_metadata[1].logical_cell_offset, 0);
            screen
                .validate()
                .expect("partial rectangle hard LF leaves canonical logical rows");
        }
    }

    #[test]
    fn full_width_partial_scroll_hard_line_feed_detaches_the_outside_wrapped_suffix() {
        let mut screen = text_screen(4, 3, "ABCDE");
        let original_line_id = screen.row_metadata[0].logical_line_id;
        assert!(screen.soft_wrapped[0]);
        assert_eq!(screen.row_metadata[1].logical_line_id, original_line_id);
        assert_eq!(screen.row_metadata[1].logical_cell_offset, 4);

        screen.position_cursor(0, 0);
        assert!(
            screen.line_feed_in_rectangle_with_wrap(false, 0, 1, 0, 4, ScrollCause::Output(1),)
        );

        assert_ne!(screen.row_metadata[1].logical_line_id, original_line_id);
        assert_eq!(screen.row_metadata[1].logical_cell_offset, 0);
        screen
            .validate()
            .expect("partial scroll boundary remains canonical");
    }

    #[test]
    fn crop_detaches_wrapped_rows_and_padding_gets_fresh_ids() {
        let mut screen = text_screen(4, 3, "ABCDEF");
        let first_line_id = screen.row_metadata[0].logical_line_id;
        let trailing_line_id = screen.row_metadata[2].logical_line_id;

        screen.resize_crop(3, 4);

        assert_eq!(screen.stored_rows(), 4);
        assert_eq!(screen.viewport_top(), 0);
        assert_eq!(screen.soft_wrapped, [false; 4]);
        assert_eq!(screen.row_metadata[0].logical_line_id, first_line_id);
        assert_ne!(screen.row_metadata[1].logical_line_id, first_line_id);
        assert_eq!(screen.row_metadata[2].logical_line_id, trailing_line_id);
        assert!(
            screen
                .row_metadata
                .iter()
                .all(|metadata| metadata.logical_cell_offset == 0 && metadata.flags == 0)
        );
        let mut line_ids = screen
            .row_metadata
            .iter()
            .map(|metadata| metadata.logical_line_id)
            .collect::<Vec<_>>();
        line_ids.sort_unstable();
        line_ids.dedup();
        assert_eq!(line_ids.len(), 4);
        screen.validate().expect("cropped row layout is canonical");
    }

    #[test]
    fn row_shifts_preserve_evicted_heads_and_reidentify_visible_splits() {
        let mut evicted = text_screen(3, 3, "ABCDEF");
        let line_id = evicted.row_metadata[0].logical_line_id;
        evicted.scroll_up_region(1, 0, 3);
        assert_eq!(evicted.row_metadata[0].logical_line_id, line_id);
        assert_eq!(evicted.row_metadata[0].logical_cell_offset, 3);
        assert!(evicted.row_metadata[0].is_head_truncated());
        assert_eq!(evicted.row_metadata[1].logical_cell_offset, 0);
        evicted
            .validate()
            .expect("top eviction keeps a stable truncated anchor");

        let mut split = text_screen(3, 3, "ABCDEF");
        let prefix_id = split.row_metadata[0].logical_line_id;
        split.scroll_down_region(1, 1, 3);
        assert_eq!(split.row_metadata[0].logical_line_id, prefix_id);
        assert_ne!(split.row_metadata[2].logical_line_id, prefix_id);
        assert_eq!(split.row_metadata[2].logical_cell_offset, 0);
        assert_eq!(split.row_metadata[2].flags, 0);
        split
            .validate()
            .expect("a visible line split receives a fresh anchor");
    }

    #[test]
    fn wide_degradation_preserves_presentation_metadata() {
        let (mut reflow, attributes, hyperlink) = linked_wide_screen(2, 1);
        reflow
            .resize_reflow(1, 1)
            .expect("wide cell can reflow to one column");
        assert_eq!(reflow.cells[0].character, '\u{fffd}');
        assert_eq!(reflow.cells[0].width, 1);
        assert_eq!(reflow.cells[0].attributes, attributes);
        assert!(reflow.cells[0].protected);
        assert_eq!(reflow.cells[0].hyperlink, Some(hyperlink));
        reflow.validate().expect("degraded reflow is canonical");

        let mut variation = Screen::new(1, 1);
        variation.attributes = attributes;
        variation.protected = true;
        variation
            .print_modes_with_hyperlink('❤', false, true, 0, 1, Some(hyperlink))
            .expect("narrow heart is supported");
        variation
            .print_modes_with_hyperlink('\u{fe0f}', false, true, 0, 1, None)
            .expect("variation selector is supported");
        assert_eq!(variation.cells[0].character, '\u{fffd}');
        assert_eq!(variation.cells[0].attributes, attributes);
        assert!(variation.cells[0].protected);
        assert_eq!(variation.cells[0].hyperlink, Some(hyperlink));
        variation
            .validate()
            .expect("in-place degradation is canonical");
    }

    #[test]
    fn repair_row_copies_lead_presentation_and_clears_orphan_links() {
        let attributes = bce_attributes();
        let hyperlink = HyperlinkRef::new(41);
        let stale_hyperlink = HyperlinkRef::new(42);
        let blank_attributes = Attributes {
            foreground: Color::Indexed(3),
            ..Attributes::default()
        };
        let lead = Cell::lead_with_presentation('界', 2, attributes, true, Some(hyperlink));
        let mut cells = vec![
            lead.clone(),
            Cell::continuation_with_presentation(
                Attributes::default(),
                false,
                Some(stale_hyperlink),
            ),
            Cell::continuation_with_presentation(attributes, true, Some(hyperlink)),
            lead,
        ];
        let mut meaningful = vec![true; cells.len()];

        repair_row(&mut cells, &mut meaningful, blank_attributes);

        assert_eq!(cells[1], Cell::continuation_from(&cells[0]));
        assert_eq!(cells[2], Cell::blank(blank_attributes));
        assert_eq!(cells[3], Cell::blank(blank_attributes));
        assert_eq!(meaningful, [true, true, false, false]);

        let (mut screen, _, _) = linked_wide_screen(3, 1);
        screen.cells[1] = Cell::continuation_with_presentation(
            Attributes::default(),
            false,
            Some(stale_hyperlink),
        );
        screen.repair_all_rows();
        assert_wide_presentation(&screen, 0, 0, attributes, hyperlink);
        screen
            .validate()
            .expect("full-row presentation repair is canonical");
    }

    #[test]
    fn wide_shifts_preserve_presentation_and_new_blanks_are_unlinked() {
        let (mut screen, attributes, hyperlink) = linked_wide_screen(4, 2);
        screen.position_cursor(0, 0);
        screen.insert_characters(1);
        assert_eq!(screen.cells[0], Cell::blank(bce_erase_attributes()));
        assert_wide_presentation(&screen, 0, 1, attributes, hyperlink);

        screen.scroll_down_region(1, 0, 2);
        assert!(screen.cells[..4].iter().all(|cell| {
            cell.character == ' '
                && !cell.protected
                && cell.hyperlink.is_none()
                && !cell.continuation
        }));
        assert_wide_presentation(&screen, 1, 1, attributes, hyperlink);
        screen.validate().expect("shifted wide cell is canonical");
    }

    #[test]
    fn wide_removal_and_fill_repairs_do_not_leave_hyperlinks_on_blanks() {
        let (mut deleted, _, _) = linked_wide_screen(4, 1);
        deleted.position_cursor(0, 0);
        deleted.delete_characters(1);
        assert!(deleted.cells.iter().all(|cell| cell.hyperlink.is_none()));
        deleted.validate().expect("DCH repair is canonical");

        let (mut erased, _, _) = linked_wide_screen(4, 1);
        erased.erase_rectangle(0, 1, 1, 2);
        assert!(
            erased.cells[..2]
                .iter()
                .all(|cell| cell.hyperlink.is_none())
        );
        erased.validate().expect("rectangle erase is canonical");

        let (mut filled, attributes, hyperlink) = linked_wide_screen(4, 1);
        filled
            .fill_rectangle_with_hyperlink('X', 0, 0, 1, 1, Some(hyperlink))
            .expect("rectangle fill is supported");
        assert_eq!(filled.cells[0].hyperlink, Some(hyperlink));
        assert_eq!(filled.cells[1], Cell::blank(attributes));
        filled
            .validate()
            .expect("rectangle fill repair is canonical");

        let (mut cropped, _, _) = linked_wide_screen(2, 1);
        cropped.resize_crop(1, 1);
        assert_eq!(cropped.cells[0], Cell::default());
        cropped.validate().expect("crop repair is canonical");
    }

    #[test]
    fn ich_repairs_wide_graphemes_from_lead_and_continuation() {
        let mut from_lead = text_screen(8, 1, "AB界CD");
        from_lead.position_cursor(0, 2);
        from_lead.insert_characters(0);

        let mut from_continuation = text_screen(8, 1, "AB界CD");
        from_continuation.position_cursor(0, 3);
        assert_eq!(from_continuation.cursor_column, 2);
        from_continuation.insert_characters(1);

        assert_eq!(from_lead, from_continuation);
        assert_eq!(
            row_signature(&from_lead, 0),
            ["A", "B", " ", "界", "~", "C", "D", " "]
        );
        assert_eq!(from_lead.meaningful_end, vec![7]);
        from_lead.validate().expect("ICH result is canonical");
    }

    #[test]
    fn dch_repairs_wide_graphemes_from_lead_and_continuation() {
        let mut from_lead = text_screen(8, 1, "AB界CD");
        from_lead.position_cursor(0, 2);
        from_lead.delete_characters(0);

        let mut from_continuation = text_screen(8, 1, "AB界CD");
        from_continuation.position_cursor(0, 3);
        assert_eq!(from_continuation.cursor_column, 2);
        from_continuation.delete_characters(1);

        assert_eq!(from_lead, from_continuation);
        assert_eq!(
            row_signature(&from_lead, 0),
            ["A", "B", " ", "C", "D", " ", " ", " "]
        );
        assert_eq!(from_lead.meaningful_end, vec![8]);
        from_lead.validate().expect("DCH result is canonical");
    }

    #[test]
    fn ech_clears_whole_wide_graphemes_from_lead_and_continuation() {
        let mut from_lead = text_screen(8, 1, "AB界CD");
        from_lead.position_cursor(0, 2);
        from_lead.erase_characters(0);

        let mut from_continuation = text_screen(8, 1, "AB界CD");
        from_continuation.position_cursor(0, 3);
        assert_eq!(from_continuation.cursor_column, 2);
        from_continuation.erase_characters(1);

        assert_eq!(from_lead, from_continuation);
        assert_eq!(
            row_signature(&from_lead, 0),
            ["A", "B", " ", " ", "C", "D", " ", " "]
        );
        assert_eq!(from_lead.meaningful_end, vec![6]);
        from_lead.validate().expect("ECH result is canonical");
    }

    #[test]
    fn character_edits_use_bce_attributes_and_clamp_counts() {
        let attributes = bce_attributes();
        let erase_attributes = bce_erase_attributes();

        let mut ich = text_screen(4, 1, "AB");
        ich.attributes = attributes;
        ich.position_cursor(0, 1);
        ich.insert_characters(u16::MAX);
        assert!(
            ich.cells[1..]
                .iter()
                .all(|cell| cell.attributes == erase_attributes)
        );
        assert_eq!(ich.meaningful_end, vec![4]);
        ich.validate().expect("clamped ICH is canonical");

        let mut dch = text_screen(4, 1, "AB界");
        dch.attributes = attributes;
        dch.position_cursor(0, 1);
        dch.delete_characters(u16::MAX);
        assert!(
            dch.cells[1..]
                .iter()
                .all(|cell| cell.attributes == erase_attributes)
        );
        assert_eq!(dch.meaningful_end, vec![4]);
        dch.validate().expect("clamped DCH is canonical");

        let mut ech = text_screen(4, 1, "AB界");
        ech.attributes = attributes;
        ech.position_cursor(0, 1);
        ech.erase_characters(u16::MAX);
        assert!(
            ech.cells[1..]
                .iter()
                .all(|cell| cell.attributes == erase_attributes)
        );
        assert_eq!(ech.meaningful_end, vec![4]);
        ech.validate().expect("clamped ECH is canonical");
    }

    #[test]
    fn explicit_bce_blanks_survive_primary_reflow_without_promoting_padding() {
        let attributes = bce_attributes();
        let erase_attributes = bce_erase_attributes();

        let mut ich = text_screen(4, 2, "AB");
        ich.attributes = attributes;
        ich.position_cursor(0, 1);
        ich.insert_characters(3);
        ich.resize_reflow(5, 2).expect("ICH blank reflow");
        assert!(
            ich.cells[1..4]
                .iter()
                .all(|cell| cell.attributes == erase_attributes)
        );
        assert_eq!(ich.cells[4], Cell::default());

        let mut dch = text_screen(4, 2, "AB");
        dch.attributes = attributes;
        dch.position_cursor(0, 1);
        dch.delete_characters(3);
        dch.resize_reflow(5, 2).expect("DCH blank reflow");
        assert!(
            dch.cells[1..4]
                .iter()
                .all(|cell| cell.attributes == erase_attributes)
        );
        assert_eq!(dch.cells[4], Cell::default());

        let mut ech = text_screen(4, 2, "AB");
        ech.attributes = attributes;
        ech.position_cursor(0, 1);
        ech.erase_characters(3);
        ech.resize_reflow(5, 2).expect("ECH blank reflow");
        assert!(
            ech.cells[1..4]
                .iter()
                .all(|cell| cell.attributes == erase_attributes)
        );
        assert_eq!(ech.cells[4], Cell::default());
    }

    #[test]
    fn insert_and_delete_lines_respect_half_open_margins() {
        let mut outside = lettered_screen();
        outside.position_cursor(0, 0);
        let unchanged = outside.clone();
        outside.insert_lines(1, 1, 4);
        outside.delete_lines(1, 1, 4);
        assert_eq!(outside, unchanged);

        let mut inserted = lettered_screen();
        inserted.attributes = bce_attributes();
        inserted.position_cursor(2, 0);
        inserted.soft_wrapped[2] = true;
        inserted.insert_lines(0, 1, 4);
        assert_eq!(first_columns(&inserted), ['A', 'B', ' ', 'C', 'E']);
        assert_eq!(inserted.meaningful_end, [1, 1, 3, 1, 1]);
        assert!(!inserted.soft_wrapped[2]);
        let inserted_row = cell_index(inserted.columns, 2, 0);
        assert!(
            inserted.cells[inserted_row..inserted_row + 3]
                .iter()
                .all(|cell| cell.attributes == bce_erase_attributes())
        );
        inserted.validate().expect("IL result is canonical");

        let mut deleted = lettered_screen();
        deleted.attributes = bce_attributes();
        deleted.position_cursor(2, 0);
        deleted.delete_lines(0, 1, 4);
        assert_eq!(first_columns(&deleted), ['A', 'B', 'D', ' ', 'E']);
        assert_eq!(deleted.meaningful_end, [1, 1, 1, 3, 1]);
        let deleted_row = cell_index(deleted.columns, 3, 0);
        assert!(
            deleted.cells[deleted_row..deleted_row + 3]
                .iter()
                .all(|cell| cell.attributes == bce_erase_attributes())
        );
        deleted.validate().expect("DL result is canonical");
    }

    #[test]
    fn region_scrolls_leave_cursor_unchanged_and_clear_new_metadata() {
        let mut up = lettered_screen();
        up.position_cursor(4, 2);
        up.wrap_pending = true;
        let cursor = (up.cursor_row, up.cursor_column, up.wrap_pending);
        up.scroll_up_region(0, 1, 4);
        assert_eq!((up.cursor_row, up.cursor_column, up.wrap_pending), cursor);
        assert_eq!(first_columns(&up), ['A', 'C', 'D', ' ', 'E']);
        assert_eq!(up.meaningful_end, [1, 1, 1, 0, 1]);
        assert!(!up.soft_wrapped[3]);
        up.validate().expect("SU result is canonical");

        let mut down = lettered_screen();
        down.position_cursor(4, 2);
        down.wrap_pending = true;
        let cursor = (down.cursor_row, down.cursor_column, down.wrap_pending);
        down.scroll_down_region(0, 1, 4);
        assert_eq!(
            (down.cursor_row, down.cursor_column, down.wrap_pending),
            cursor
        );
        assert_eq!(first_columns(&down), ['A', ' ', 'B', 'C', 'E']);
        assert_eq!(down.meaningful_end, [1, 0, 1, 1, 1]);
        assert!(!down.soft_wrapped[1]);
        down.validate().expect("SD result is canonical");

        let mut clamped = lettered_screen();
        clamped.scroll_up_region(u16::MAX, 1, 4);
        assert_eq!(first_columns(&clamped), ['A', ' ', ' ', ' ', 'E']);
        assert_eq!(clamped.meaningful_end, [1, 0, 0, 0, 1]);
        clamped.validate().expect("clamped SU is canonical");
    }

    #[test]
    fn rectangle_scrolls_preserve_outside_cells_and_use_bce_blanks() {
        let fixture = || {
            let mut screen = Screen::new(6, 4);
            for (row, text) in ["abcdef", "ghijkl", "mnopqr", "stuvwx"]
                .into_iter()
                .enumerate()
            {
                set_row_text(
                    &mut screen,
                    u32::try_from(row).expect("bounded fixture row"),
                    text,
                );
            }
            screen.attributes = bce_attributes();
            screen
        };

        let mut up = fixture();
        let before_up = up.clone();
        let cursor = (up.cursor_row, up.cursor_column, up.wrap_pending);
        up.scroll_up_rectangle(1, 0, 3, 1, 5);
        assert_eq!(row_signature(&up, 0), ["a", "h", "i", "j", "k", "f"]);
        assert_eq!(row_signature(&up, 1), ["g", "n", "o", "p", "q", "l"]);
        assert_eq!(row_signature(&up, 2), ["m", " ", " ", " ", " ", "r"]);
        assert_eq!(row_signature(&up, 3), ["s", "t", "u", "v", "w", "x"]);
        assert!((1..5).all(|column| {
            up.cells[cell_index(up.columns, 2, column)] == Cell::blank(bce_erase_attributes())
        }));
        assert!((0..up.rows).all(|row| (0..up.columns).all(|column| {
            (row < 3 && (1..5).contains(&column))
                || up.cells[cell_index(up.columns, row, column)]
                    == before_up.cells[cell_index(before_up.columns, row, column)]
        })));
        assert_eq!((up.cursor_row, up.cursor_column, up.wrap_pending), cursor);
        up.validate().expect("rectangle scroll up is canonical");

        let mut down = fixture();
        let before_down = down.clone();
        down.scroll_down_rectangle(1, 1, 4, 1, 5);
        assert_eq!(row_signature(&down, 0), ["a", "b", "c", "d", "e", "f"]);
        assert_eq!(row_signature(&down, 1), ["g", " ", " ", " ", " ", "l"]);
        assert_eq!(row_signature(&down, 2), ["m", "h", "i", "j", "k", "r"]);
        assert_eq!(row_signature(&down, 3), ["s", "n", "o", "p", "q", "x"]);
        assert!((1..5).all(|column| {
            down.cells[cell_index(down.columns, 1, column)] == Cell::blank(bce_erase_attributes())
        }));
        assert!((0..down.rows).all(|row| (0..down.columns).all(|column| {
            (row >= 1 && (1..5).contains(&column))
                || down.cells[cell_index(down.columns, row, column)]
                    == before_down.cells[cell_index(before_down.columns, row, column)]
        })));
        down.validate().expect("rectangle scroll down is canonical");
    }

    #[test]
    fn rectangle_scroll_count_clamps_to_the_rectangle_height() {
        let mut screen = Screen::new(5, 3);
        for (row, text) in ["ABCDE", "FGHIJ", "KLMNO"].into_iter().enumerate() {
            set_row_text(
                &mut screen,
                u32::try_from(row).expect("bounded fixture row"),
                text,
            );
        }

        screen.scroll_down_rectangle(u16::MAX, 0, 3, 1, 4);

        assert_eq!(row_signature(&screen, 0), ["A", " ", " ", " ", "E"]);
        assert_eq!(row_signature(&screen, 1), ["F", " ", " ", " ", "J"]);
        assert_eq!(row_signature(&screen, 2), ["K", " ", " ", " ", "O"]);
        screen
            .validate()
            .expect("clamped rectangle scroll is canonical");
    }

    #[test]
    fn rectangle_scroll_repairs_wide_graphemes_split_by_horizontal_margins() {
        let mut screen = Screen::new(6, 2);
        set_row_text(&mut screen, 0, "好Y好Q");
        set_row_text(&mut screen, 1, "界X界Z");

        screen.scroll_up_rectangle(1, 0, 2, 1, 4);

        assert_eq!(row_signature(&screen, 0), [" ", " ", "X", " ", " ", "Q"]);
        assert_eq!(row_signature(&screen, 1), [" ", " ", " ", " ", " ", "Z"]);
        assert!(screen.cells.iter().all(|cell| !cell.continuation));
        for column in [0, 1, 3, 4] {
            assert_eq!(
                screen.cells[cell_index(screen.columns, 0, column)],
                Cell::default()
            );
        }
        screen
            .validate()
            .expect("rectangle wide-boundary repair is canonical");
    }

    #[test]
    fn full_width_rectangle_scrolls_reuse_row_and_history_semantics() {
        let mut fixture = Screen::new_with_history_capacity(3, 3, 2, 15);
        for (row, marker) in ['A', 'B', 'C'].into_iter().enumerate() {
            put_row_marker(
                &mut fixture,
                u32::try_from(row).expect("bounded fixture row"),
                marker,
            );
        }
        fixture.soft_wrapped[0] = true;
        fixture.attributes = bce_attributes();

        let mut legacy_up = fixture.clone();
        legacy_up.scroll_up_region_with_cause(1, 0, 3, ScrollCause::Output(7));
        let mut rectangle_up = fixture.clone();
        rectangle_up.scroll_up_rectangle_with_cause(
            1,
            0,
            u32::MAX,
            0,
            u32::MAX,
            ScrollCause::Output(7),
        );
        assert_eq!(rectangle_up, legacy_up);
        assert_eq!(rectangle_up.cells, legacy_up.cells);
        assert_eq!(rectangle_up.soft_wrapped, legacy_up.soft_wrapped);
        assert_eq!(rectangle_up.meaningful_end, legacy_up.meaningful_end);
        assert_eq!(rectangle_up.row_metadata, legacy_up.row_metadata);
        assert_eq!(
            rectangle_up.next_logical_line_id,
            legacy_up.next_logical_line_id
        );
        assert_eq!(rectangle_up.viewport_top(), 1);
        let up_blank_start = rectangle_up.visible_cell_index(2, 0);
        assert!(
            rectangle_up.cells[up_blank_start..up_blank_start + 3]
                .iter()
                .all(|cell| *cell == Cell::blank(bce_erase_attributes()))
        );

        let history_cells = rectangle_up.cells[..3].to_vec();
        let history_soft_wrapped = rectangle_up.soft_wrapped[0];
        let history_meaningful_end = rectangle_up.meaningful_end[0];
        let history_metadata = rectangle_up.row_metadata[0];
        rectangle_up.scroll_up_rectangle_with_cause(1, 0, 3, 1, 3, ScrollCause::Output(8));
        assert_eq!(rectangle_up.viewport_top(), 1);
        assert_eq!(&rectangle_up.cells[..3], history_cells);
        assert_eq!(rectangle_up.soft_wrapped[0], history_soft_wrapped);
        assert_eq!(rectangle_up.meaningful_end[0], history_meaningful_end);
        assert_eq!(rectangle_up.row_metadata[0], history_metadata);

        let mut legacy_down = fixture.clone();
        legacy_down.scroll_down_region(1, 0, 3);
        let mut rectangle_down = fixture;
        rectangle_down.scroll_down_rectangle(1, 0, u32::MAX, 0, u32::MAX);
        assert_eq!(rectangle_down, legacy_down);
        assert_eq!(rectangle_down.row_metadata, legacy_down.row_metadata);
        let down_blank_start = rectangle_down.visible_cell_index(0, 0);
        assert!(
            rectangle_down.cells[down_blank_start..down_blank_start + 3]
                .iter()
                .all(|cell| *cell == Cell::blank(bce_erase_attributes()))
        );
        rectangle_up
            .validate()
            .expect("partial-width output scroll is history-isolated");
        rectangle_down
            .validate()
            .expect("full-width rectangle scroll down is canonical");
    }

    #[test]
    fn rectangle_line_feed_and_reverse_index_scroll_only_inside_horizontal_margins() {
        let fixture = || {
            let mut screen = Screen::new(5, 3);
            for (row, text) in ["ABCDE", "FGHIJ", "KLMNO"].into_iter().enumerate() {
                set_row_text(
                    &mut screen,
                    u32::try_from(row).expect("bounded fixture row"),
                    text,
                );
            }
            screen
        };

        let mut outside_lf = fixture();
        outside_lf.position_cursor(1, 0);
        outside_lf.wrap_pending = true;
        let cells = outside_lf.cells.clone();
        outside_lf.line_feed_in_rectangle_with_scroll_cause(0, 2, 1, 4, ScrollCause::Output(1));
        assert_eq!(outside_lf.cells, cells);
        assert_eq!((outside_lf.cursor_row, outside_lf.cursor_column), (2, 0));
        assert!(!outside_lf.wrap_pending);

        let mut inside_lf = fixture();
        inside_lf.position_cursor(1, 2);
        inside_lf.line_feed_in_rectangle_with_scroll_cause(0, 2, 1, 4, ScrollCause::Output(1));
        assert_eq!(row_signature(&inside_lf, 0), ["A", "G", "H", "I", "E"]);
        assert_eq!(row_signature(&inside_lf, 1), ["F", " ", " ", " ", "J"]);
        assert_eq!(row_signature(&inside_lf, 2), ["K", "L", "M", "N", "O"]);
        assert_eq!((inside_lf.cursor_row, inside_lf.cursor_column), (1, 2));

        let mut outside_ri = fixture();
        outside_ri.position_cursor(1, 0);
        let cells = outside_ri.cells.clone();
        outside_ri.reverse_index_in_rectangle(1, 3, 1, 4);
        assert_eq!(outside_ri.cells, cells);
        assert_eq!((outside_ri.cursor_row, outside_ri.cursor_column), (0, 0));

        let mut inside_ri = fixture();
        inside_ri.position_cursor(1, 2);
        inside_ri.reverse_index_in_rectangle(1, 3, 1, 4);
        assert_eq!(row_signature(&inside_ri, 0), ["A", "B", "C", "D", "E"]);
        assert_eq!(row_signature(&inside_ri, 1), ["F", " ", " ", " ", "J"]);
        assert_eq!(row_signature(&inside_ri, 2), ["K", "G", "H", "I", "O"]);
        assert_eq!((inside_ri.cursor_row, inside_ri.cursor_column), (1, 2));
        inside_lf
            .validate()
            .expect("rectangle LF remains canonical");
        inside_ri
            .validate()
            .expect("rectangle RI remains canonical");
    }

    #[test]
    fn rectangle_print_delays_wrap_to_left_without_full_row_soft_wrap() {
        let mut screen = Screen::new_with_history_capacity(6, 2, 2, 24);
        screen.position_cursor(0, 1);
        for character in "ABCDEFGHI".chars() {
            print_in_rectangle(&mut screen, character, 0, 2, 1, 5)
                .expect("rectangle text is supported");
        }

        assert_eq!(row_signature(&screen, 0), [" ", "E", "F", "G", "H", " "]);
        assert_eq!(row_signature(&screen, 1), [" ", "I", " ", " ", " ", " "]);
        assert_eq!((screen.cursor_row, screen.cursor_column), (1, 2));
        assert!(!screen.wrap_pending);
        assert_eq!(screen.viewport_top(), 0);
        assert_eq!(screen.soft_wrapped, [false, false]);

        let mut exact_edge = Screen::new(6, 2);
        exact_edge.position_cursor(0, 3);
        print_in_rectangle(&mut exact_edge, '界', 0, 2, 1, 5).expect("wide edge text is supported");
        assert_eq!(
            row_signature(&exact_edge, 0),
            [" ", " ", " ", "界", "~", " "]
        );
        assert_eq!((exact_edge.cursor_row, exact_edge.cursor_column), (0, 4));
        assert!(exact_edge.wrap_pending);
        exact_edge
            .validate()
            .expect("custom delayed wrap is screen-canonical");

        let mut prewrapped = Screen::new(6, 2);
        prewrapped.position_cursor(0, 4);
        print_in_rectangle(&mut prewrapped, '界', 0, 2, 1, 5)
            .expect("wide text wraps before the right margin");
        assert_eq!(
            row_signature(&prewrapped, 0),
            [" ", " ", " ", " ", " ", " "]
        );
        assert_eq!(
            row_signature(&prewrapped, 1),
            [" ", "界", "~", " ", " ", " "]
        );
        assert_eq!((prewrapped.cursor_row, prewrapped.cursor_column), (1, 3));
        assert!(!prewrapped.soft_wrapped[0]);
        prewrapped
            .validate()
            .expect("prewrapped wide text is canonical");

        let mut vertically_outside = Screen::new(6, 3);
        vertically_outside.position_cursor(0, 4);
        for character in "XYZ".chars() {
            print_in_rectangle(&mut vertically_outside, character, 1, 3, 1, 5)
                .expect("vertically outside text uses the physical line");
        }
        assert_eq!(
            row_signature(&vertically_outside, 0),
            [" ", " ", " ", " ", "X", "Y"]
        );
        assert_eq!(
            row_signature(&vertically_outside, 1),
            ["Z", " ", " ", " ", " ", " "]
        );
        assert_eq!(
            (
                vertically_outside.cursor_row,
                vertically_outside.cursor_column
            ),
            (1, 1)
        );
        assert!(vertically_outside.soft_wrapped[0]);
        vertically_outside
            .validate()
            .expect("vertically outside text uses the physical line");
    }

    #[test]
    fn rectangle_character_edits_stop_at_right_and_ignore_outside_cursors() {
        let fixture = || {
            let mut screen = Screen::new(8, 1);
            set_row_text(&mut screen, 0, "ABCDEFGH");
            screen.position_cursor(0, 2);
            screen
        };

        let mut inserted = fixture();
        inserted.insert_characters_in_rectangle(2, 0, 1, 1, 6);
        assert_eq!(
            row_signature(&inserted, 0),
            ["A", "B", " ", " ", "C", "D", "G", "H"]
        );

        let mut deleted = fixture();
        deleted.delete_characters_in_rectangle(2, 0, 1, 1, 6);
        assert_eq!(
            row_signature(&deleted, 0),
            ["A", "B", "E", "F", " ", " ", "G", "H"]
        );

        let mut erased = fixture();
        erased.erase_characters_in_rectangle(2, 0, 1, 1, 6);
        assert_eq!(
            row_signature(&erased, 0),
            ["A", "B", " ", " ", "E", "F", "G", "H"]
        );

        let mut outside = fixture();
        outside.position_cursor(0, 0);
        let unchanged = outside.clone();
        outside.insert_characters_in_rectangle(1, 0, 1, 1, 6);
        outside.delete_characters_in_rectangle(1, 0, 1, 1, 6);
        outside.erase_characters_in_rectangle(1, 0, 1, 1, 6);
        assert_eq!(outside, unchanged);

        let mut wide = Screen::new(7, 1);
        set_row_text(&mut wide, 0, "AB界CD");
        wide.position_cursor(0, 1);
        wide.delete_characters_in_rectangle(1, 0, 1, 1, 4);
        assert_eq!(
            row_signature(&wide, 0),
            ["A", "界", "~", " ", "C", "D", " "]
        );
        wide.validate()
            .expect("range character edits remain wide-safe");
    }

    #[test]
    fn rectangle_line_edits_preserve_cells_outside_the_rectangle() {
        let fixture = || {
            let mut screen = Screen::new(6, 4);
            for (row, text) in ["abcdef", "ghijkl", "mnopqr", "stuvwx"]
                .into_iter()
                .enumerate()
            {
                set_row_text(
                    &mut screen,
                    u32::try_from(row).expect("bounded fixture row"),
                    text,
                );
            }
            screen.position_cursor(1, 2);
            screen
        };

        let mut inserted = fixture();
        inserted.insert_lines_in_rectangle(1, 0, 4, 1, 5);
        assert_eq!(row_signature(&inserted, 0), ["a", "b", "c", "d", "e", "f"]);
        assert_eq!(row_signature(&inserted, 1), ["g", " ", " ", " ", " ", "l"]);
        assert_eq!(row_signature(&inserted, 2), ["m", "h", "i", "j", "k", "r"]);
        assert_eq!(row_signature(&inserted, 3), ["s", "n", "o", "p", "q", "x"]);

        let mut deleted = fixture();
        deleted.delete_lines_in_rectangle(1, 0, 4, 1, 5);
        assert_eq!(row_signature(&deleted, 0), ["a", "b", "c", "d", "e", "f"]);
        assert_eq!(row_signature(&deleted, 1), ["g", "n", "o", "p", "q", "l"]);
        assert_eq!(row_signature(&deleted, 2), ["m", "t", "u", "v", "w", "r"]);
        assert_eq!(row_signature(&deleted, 3), ["s", " ", " ", " ", " ", "x"]);

        let mut outside = fixture();
        outside.position_cursor(1, 0);
        let unchanged = outside.clone();
        outside.insert_lines_in_rectangle(1, 0, 4, 1, 5);
        outside.delete_lines_in_rectangle(1, 0, 4, 1, 5);
        assert_eq!(outside, unchanged);
        inserted.validate().expect("rectangle IL is canonical");
        deleted.validate().expect("rectangle DL is canonical");
    }

    #[test]
    fn row_shifts_break_soft_wrap_at_their_top_boundary() {
        let boundary_screen = || {
            let mut screen = lettered_screen();
            screen.soft_wrapped[0] = true;
            screen
        };

        let mut inserted = boundary_screen();
        inserted.position_cursor(1, 0);
        inserted.insert_lines(1, 0, 5);
        assert!(!inserted.soft_wrapped[0]);

        let mut deleted = boundary_screen();
        deleted.position_cursor(1, 0);
        deleted.delete_lines(1, 0, 5);
        assert!(!deleted.soft_wrapped[0]);

        let mut scrolled_up = boundary_screen();
        scrolled_up.scroll_up_region(1, 1, 5);
        assert!(!scrolled_up.soft_wrapped[0]);

        let mut scrolled_down = boundary_screen();
        scrolled_down.scroll_down_region(1, 1, 5);
        assert!(!scrolled_down.soft_wrapped[0]);

        let mut reflow = Screen::new(4, 4);
        for character in "ABCDEF".chars() {
            reflow.print(character).expect("wrapped fixture text");
        }
        reflow.insert_lines(1, 0, 4);
        reflow.position_cursor(3, 0);
        reflow.resize_reflow(5, 4).expect("boundary-safe reflow");
        assert_eq!(row_signature(&reflow, 0), ["A", "B", "C", "D", " "]);
        assert_eq!(row_signature(&reflow, 1), [" ", " ", " ", " ", " "]);
        assert_eq!(row_signature(&reflow, 2), ["E", "F", " ", " ", " "]);
    }

    #[test]
    fn row_shifts_detach_soft_wrapped_suffixes_below_partial_regions() {
        let wrapped = || text_screen(4, 4, "ABCDEFGHIJKLM");

        let mut up = wrapped();
        let outside_id_before_up = up.row_metadata[2].logical_line_id;
        assert!(up.soft_wrapped[1]);
        up.scroll_up_region(1, 0, 2);
        assert_ne!(up.row_metadata[2].logical_line_id, outside_id_before_up);
        assert_eq!(up.row_metadata[2].logical_cell_offset, 0);
        assert_eq!(
            up.row_metadata[3].logical_line_id,
            up.row_metadata[2].logical_line_id
        );
        up.validate()
            .expect("scroll-up detaches the outside logical suffix");

        let mut down = wrapped();
        let outside_id_before_down = down.row_metadata[3].logical_line_id;
        assert!(down.soft_wrapped[2]);
        down.scroll_down_region(1, 1, 3);
        assert_ne!(down.row_metadata[3].logical_line_id, outside_id_before_down);
        assert_eq!(down.row_metadata[3].logical_cell_offset, 0);
        down.validate()
            .expect("scroll-down detaches the outside logical suffix");
    }

    #[test]
    fn insert_mode_only_shifts_for_new_narrow_or_wide_leads() {
        let mut narrow = text_screen(8, 1, "AB界CD");
        narrow.position_cursor(0, 2);
        narrow
            .print_insert('Z')
            .expect("narrow insert is supported");
        assert_eq!(
            row_signature(&narrow, 0),
            ["A", "B", "Z", "界", "~", "C", "D", " "]
        );
        narrow.validate().expect("narrow insert is canonical");

        let mut wide = text_screen(8, 1, "AB界CD");
        wide.position_cursor(0, 2);
        wide.print_insert('好').expect("wide insert is supported");
        assert_eq!(
            row_signature(&wide, 0),
            ["A", "B", "好", "~", "界", "~", "C", "D"]
        );
        wide.validate().expect("wide insert is canonical");

        let mut combining = text_screen(4, 1, "AB");
        combining.position_cursor(0, 1);
        combining
            .print_insert('\u{0301}')
            .expect("combining insert is supported");
        assert_eq!(row_signature(&combining, 0), ["A\u{0301}", "B", " ", " "]);
        assert_eq!(combining.meaningful_end, vec![2]);
        combining.validate().expect("combining append is canonical");
    }

    #[test]
    fn variation_selector_widening_clears_target_graphemes_without_hiding_tail() {
        let mut screen = Screen::new(4, 2);
        screen.position_cursor(1, 0);
        for _ in 0..4 {
            screen.print('B').expect("test text is supported");
        }
        screen.position_cursor(0, 3);
        screen.print('\u{2764}').expect("heart is supported");
        screen
            .print('\u{fe0f}')
            .expect("variation selector is supported");

        assert_eq!(row_signature(&screen, 1), ["❤\u{fe0f}", "~", "B", "B"]);
        assert_eq!(screen.meaningful_end[1], 4);
        screen
            .validate()
            .expect("widened grapheme does not hide target row tail");
    }

    #[test]
    fn combining_after_no_autowrap_last_column_uses_the_current_cell() {
        let mut no_autowrap = Screen::new(3, 1);
        for character in "ABC".chars() {
            no_autowrap
                .print_modes(character, false, false, 0, 1)
                .expect("DECAWM-off text");
        }
        no_autowrap
            .print_modes('\u{0301}', false, false, 0, 1)
            .expect("DECAWM-off combining mark");
        assert_eq!(row_signature(&no_autowrap, 0), ["A", "B", "C\u{0301}"]);

        let mut no_autowrap_wide = Screen::new(3, 1);
        for character in "A❤\u{fe0f}\u{0301}".chars() {
            no_autowrap_wide
                .print_modes(character, false, false, 0, 1)
                .expect("DECAWM-off wide grapheme");
        }
        assert_eq!(
            row_signature(&no_autowrap_wide, 0),
            ["A", "❤\u{fe0f}\u{0301}", "~"]
        );
        assert!(!no_autowrap_wide.wrap_pending);

        let mut delayed_wrap = text_screen(3, 1, "ABC");
        delayed_wrap
            .print('\u{0301}')
            .expect("delayed-wrap combining mark");
        assert_eq!(row_signature(&delayed_wrap, 0), ["A", "B", "C\u{0301}"]);

        let mut moved_to_padding = Screen::new(3, 1);
        moved_to_padding
            .print_modes('A', false, false, 0, 1)
            .expect("fixture text");
        moved_to_padding.position_cursor(0, 2);
        moved_to_padding
            .print_modes('\u{0301}', false, false, 0, 1)
            .expect("orphan combining mark");
        assert_eq!(row_signature(&moved_to_padding, 0), ["A", " ", " "]);
    }

    #[test]
    fn empty_soft_wrapped_rows_and_erase_wrap_cancellation_are_canonical() {
        let mut tab_wrap = Screen::new(5, 2);
        tab_wrap.horizontal_tab();
        tab_wrap.print('界').expect("wide text is supported");
        assert!(tab_wrap.soft_wrapped[0]);
        assert_eq!(tab_wrap.meaningful_end[0], 0);
        tab_wrap
            .validate()
            .expect("empty soft-wrapped tab row is canonical");

        let mut erased_wrap = Screen::new(1, 2);
        erased_wrap.print('A').expect("test text is supported");
        erased_wrap.erase_line(0).expect("EL is supported");
        erased_wrap.print('B').expect("test text is supported");
        assert!(!erased_wrap.soft_wrapped[0]);
        assert_eq!(erased_wrap.meaningful_end[0], 1);
        assert_eq!(row_signature(&erased_wrap, 0), ["B"]);
        erased_wrap
            .validate()
            .expect("erase-cancelled delayed wrap is canonical");
    }

    #[test]
    fn orphan_combining_scalar_does_not_attach_to_implicit_blank() {
        let mut screen = Screen::new(4, 2);
        screen.position_cursor(0, 1);
        screen
            .print('\u{0301}')
            .expect("orphan combining scalar is safely ignored");

        assert!(screen.cells[0].combining.is_empty());
        assert_eq!(screen.meaningful_end[0], 0);
        screen.validate().expect("implicit blank remains canonical");
    }

    #[test]
    fn rectangle_bounds_clip_safely_and_invalid_rectangles_do_nothing() {
        let mut screen = Screen::new(4, 3);
        screen.position_cursor(2, 3);
        screen.wrap_pending = true;
        screen.save_cursor(true, false);
        screen.position_cursor(1, 1);
        let cursor = (screen.cursor_row, screen.cursor_column, screen.wrap_pending);
        let saved = screen.saved;

        screen
            .fill_rectangle('Z', 1, 3, u32::MAX, u32::MAX)
            .expect("clipped rectangle fill");
        assert_eq!(row_signature(&screen, 0), [" ", " ", " ", " "]);
        assert_eq!(row_signature(&screen, 1), [" ", " ", " ", "Z"]);
        assert_eq!(row_signature(&screen, 2), [" ", " ", " ", "Z"]);
        assert_eq!(screen.meaningful_end, [0, 4, 4]);
        assert_eq!(
            (screen.cursor_row, screen.cursor_column, screen.wrap_pending),
            cursor
        );
        assert_eq!(screen.saved, saved);

        let clipped = screen.clone();
        screen.erase_rectangle(2, 3, 1, 4);
        screen.erase_rectangle(3, 0, 4, 4);
        screen.change_rectangle_attributes(
            0,
            4,
            3,
            5,
            RectangleAttributeChange {
                bold: Some(true),
                ..RectangleAttributeChange::default()
            },
        );
        assert_eq!(screen, clipped);

        assert_eq!(
            screen.fill_rectangle('\u{7f}', 0, 0, 1, 1),
            Err(TerminalError::Unsupported(
                "rectangle fill requires a narrow graphic character"
            ))
        );
        assert_eq!(screen, clipped);
        screen.validate().expect("clipped rectangles are canonical");
    }

    #[test]
    fn rectangle_copy_is_overlap_safe_clips_the_destination_and_preserves_state() {
        let mut screen = Screen::new(3, 3);
        set_row_text(&mut screen, 0, "ABC");
        set_row_text(&mut screen, 1, "DEF");
        set_row_text(&mut screen, 2, "GHI");

        let attributes = bce_attributes();
        let hyperlink = HyperlinkRef::new(73);
        let source_index = cell_index(screen.columns, 0, 0);
        screen.cells[source_index] =
            Cell::lead_with_presentation('A', 1, attributes, true, Some(hyperlink));
        screen.cells[source_index].combining.push('\u{301}');
        let expected_cell = screen.cells[source_index].clone();
        screen.cursor_row = 2;
        screen.cursor_column = 2;
        screen.wrap_pending = true;
        screen.attributes = attributes;
        screen.protected = true;
        screen.saved = SavedCursor {
            row: 1,
            column: 1,
            wrap_pending: false,
            attributes,
            protected: true,
            origin_mode: true,
            auto_wrap: false,
        };
        let current_presentation = (
            screen.cursor_row,
            screen.cursor_column,
            screen.wrap_pending,
            screen.attributes,
            screen.protected,
            screen.saved,
        );
        let soft_wrapped = screen.soft_wrapped.clone();
        let row_metadata = screen.row_metadata.clone();
        let next_logical_line_id = screen.next_logical_line_id;
        let stored_rows = screen.stored_rows();

        screen.copy_rectangle(0, 0, 2, 2, 1, 2);

        assert_eq!(row_signature(&screen, 0), ["A\u{301}", "B", "C"]);
        assert_eq!(row_signature(&screen, 1), ["D", "E", "A\u{301}"]);
        assert_eq!(row_signature(&screen, 2), ["G", "H", "D"]);
        assert_eq!(
            screen.cells[cell_index(screen.columns, 1, 2)],
            expected_cell
        );
        assert_eq!(
            (
                screen.cursor_row,
                screen.cursor_column,
                screen.wrap_pending,
                screen.attributes,
                screen.protected,
                screen.saved,
            ),
            current_presentation
        );
        assert_eq!(screen.soft_wrapped, soft_wrapped);
        assert_eq!(screen.row_metadata, row_metadata);
        assert_eq!(screen.next_logical_line_id, next_logical_line_id);
        assert_eq!(screen.stored_rows(), stored_rows);
        screen.validate().expect("overlapping DECCRA is canonical");

        let before = screen.clone();
        screen.copy_rectangle(2, 2, 1, 1, 0, 0);
        screen.copy_rectangle(0, 0, 1, 1, screen.rows, 0);
        screen.copy_rectangle(0, 0, 1, 1, 0, screen.columns);
        assert_eq!(screen, before);
    }

    #[test]
    fn rectangle_copy_repairs_cut_wide_source_and_destination_boundaries() {
        let attributes = bce_attributes();
        let hyperlink = HyperlinkRef::new(74);
        let mut screen = Screen::new(8, 2);
        screen.attributes = attributes;
        screen.protected = true;
        screen.position_cursor(0, 1);
        screen
            .print_modes_with_hyperlink('界', false, true, 0, 2, Some(hyperlink))
            .expect("wide source is supported");
        screen.position_cursor(1, 0);
        screen
            .print_modes_with_hyperlink('界', false, true, 0, 2, Some(hyperlink))
            .expect("wide destination boundary is supported");

        screen.copy_rectangle(0, 2, 1, 3, 1, 1);
        assert_eq!(screen.cells[cell_index(screen.columns, 1, 0)].width, 1);
        assert_eq!(screen.cells[cell_index(screen.columns, 1, 1)].width, 1);
        assert!(
            screen.cells[cell_index(screen.columns, 1, 0)..=cell_index(screen.columns, 1, 1)]
                .iter()
                .all(|cell| cell.character == ' '
                    && !cell.continuation
                    && cell.hyperlink.is_none())
        );

        screen.copy_rectangle(0, 1, 1, 2, 1, 3);
        let cut_lead = &screen.cells[cell_index(screen.columns, 1, 3)];
        assert_eq!(cut_lead.character, ' ');
        assert_eq!(cut_lead.width, 1);
        assert!(!cut_lead.continuation);
        assert_eq!(cut_lead.hyperlink, None);

        screen.copy_rectangle(0, 1, 1, 3, 1, 5);
        assert_wide_presentation(&screen, 1, 5, attributes, hyperlink);
        screen.validate().expect("wide DECCRA repair is canonical");
    }

    #[test]
    fn rectangle_erase_expands_across_wide_graphemes_and_preserves_soft_wrap() {
        let mut screen = text_screen(8, 2, "AB界CD");
        screen.soft_wrapped[0] = true;
        screen.attributes = bce_attributes();
        let cursor = (screen.cursor_row, screen.cursor_column, screen.wrap_pending);
        let saved = screen.saved;

        screen.erase_rectangle(0, 3, 1, 4);

        assert_eq!(
            row_signature(&screen, 0),
            ["A", "B", " ", " ", "C", "D", " ", " "]
        );
        assert!(screen.cells[2..4].iter().all(|cell| {
            !cell.attributes.bold
                && !cell.attributes.conceal
                && !cell.attributes.strikethrough
                && cell.attributes.background == bce_attributes().background
        }));
        assert_eq!(screen.meaningful_end[0], 6);
        assert!(screen.soft_wrapped[0]);
        assert_eq!(
            (screen.cursor_row, screen.cursor_column, screen.wrap_pending),
            cursor
        );
        assert_eq!(screen.saved, saved);
        screen
            .validate()
            .expect("wide rectangle erase is canonical");
    }

    #[test]
    fn rectangle_fill_repairs_each_wide_boundary_with_bce_blanks() {
        let mut from_lead = text_screen(8, 1, "AB界CD");
        from_lead.attributes = bce_attributes();
        from_lead
            .fill_rectangle('X', 0, 2, 1, 3)
            .expect("lead rectangle fill");
        assert_eq!(
            row_signature(&from_lead, 0),
            ["A", "B", "X", " ", "C", "D", " ", " "]
        );
        assert_eq!(from_lead.cells[3].attributes, bce_attributes());
        from_lead
            .validate()
            .expect("lead-boundary fill is canonical");

        let mut from_continuation = text_screen(8, 1, "AB界CD");
        from_continuation.attributes = bce_attributes();
        from_continuation
            .fill_rectangle('Y', 0, 3, 1, 4)
            .expect("continuation rectangle fill");
        assert_eq!(
            row_signature(&from_continuation, 0),
            ["A", "B", " ", "Y", "C", "D", " ", " "]
        );
        assert_eq!(from_continuation.cells[2].attributes, bce_attributes());
        from_continuation
            .validate()
            .expect("continuation-boundary fill is canonical");
    }

    #[test]
    fn rectangle_attribute_change_only_changes_attributes_including_implicit_blanks() {
        let mut screen = text_screen(6, 2, "A界B");
        screen.soft_wrapped[0] = true;
        screen.row_metadata[1] =
            RowMetadata::continuation_of(screen.row_metadata[0], screen.meaningful_end[0]);
        let cursor = (screen.cursor_row, screen.cursor_column, screen.wrap_pending);
        let saved = screen.saved;
        let meaningful_end = screen.meaningful_end.clone();
        let row_metadata = screen.row_metadata.clone();

        screen.change_rectangle_attributes(
            0,
            2,
            1,
            3,
            RectangleAttributeChange {
                bold: Some(true),
                foreground: Some(Color::Indexed(9)),
                ..RectangleAttributeChange::default()
            },
        );
        for column in 1..=2 {
            let attributes = screen.cells[cell_index(screen.columns, 0, column)].attributes;
            assert!(attributes.bold);
            assert_eq!(attributes.foreground, Color::Indexed(9));
        }
        assert!(!screen.cells[0].attributes.bold);
        assert!(!screen.cells[3].attributes.bold);

        screen.change_rectangle_attributes(
            1,
            4,
            2,
            u32::MAX,
            RectangleAttributeChange {
                background: Some(Color::Indexed(4)),
                ..RectangleAttributeChange::default()
            },
        );
        assert_eq!(screen.meaningful_end, meaningful_end);
        assert_eq!(screen.row_metadata, row_metadata);
        assert_eq!(
            screen.cells[cell_index(screen.columns, 1, 4)]
                .attributes
                .background,
            Color::Indexed(4)
        );
        assert_eq!(
            screen.cells[cell_index(screen.columns, 1, 5)]
                .attributes
                .background,
            Color::Indexed(4)
        );
        assert!(screen.soft_wrapped[0]);
        assert_eq!(
            (screen.cursor_row, screen.cursor_column, screen.wrap_pending),
            cursor
        );
        assert_eq!(screen.saved, saved);
        screen
            .validate()
            .expect("rectangle attribute change is canonical");
    }

    #[test]
    fn rectangle_attribute_reverse_toggles_supported_boolean_renditions() {
        let mut screen = text_screen(6, 1, "A界B");
        screen.change_rectangle_attributes(
            0,
            1,
            1,
            3,
            RectangleAttributeChange {
                bold: Some(true),
                italic: Some(true),
                ..RectangleAttributeChange::default()
            },
        );
        let cursor = (screen.cursor_row, screen.cursor_column, screen.wrap_pending);
        let saved = screen.saved;
        screen.reverse_rectangle_attributes(
            0,
            2,
            1,
            3,
            RectangleAttributeToggle {
                bold: true,
                underline_style: Some(UnderlineStyle::Single),
                inverse: true,
                ..RectangleAttributeToggle::default()
            },
        );

        for column in 1..=2 {
            let attributes = screen.cells[cell_index(screen.columns, 0, column)].attributes;
            assert!(!attributes.bold);
            assert!(attributes.italic);
            assert!(attributes.is_underlined());
            assert!(attributes.inverse);
        }
        assert_eq!(
            (screen.cursor_row, screen.cursor_column, screen.wrap_pending),
            cursor
        );
        assert_eq!(screen.saved, saved);
        screen
            .validate()
            .expect("rectangle attribute reverse is canonical");
    }

    #[test]
    fn selective_rectangle_erase_preserves_protected_cells_and_erases_others() {
        let mut screen = text_screen(4, 1, "AB");
        screen.cells[1].protected = true;
        screen
            .selective_erase_rectangle(0, 0, 1, 2)
            .expect("selective erase");
        assert_eq!(row_signature(&screen, 0), [" ", "B", " ", " "]);
        assert!(screen.cells[1].protected);
        assert!(!screen.cells[0].protected);
        screen.validate().expect("selective erase is canonical");
    }

    fn put_row_marker(screen: &mut Screen, row: u32, character: char) {
        let index = screen.visible_cell_index(row, 0);
        screen.cells[index] = Cell::lead(character, 1, Attributes::default());
        let row = screen.visible_row_index(row);
        screen.meaningful_end[row] = 1;
    }

    fn stored_row_markers(screen: &Screen) -> Vec<char> {
        (0..screen.stored_rows())
            .map(|row| screen.cells[cell_index(screen.columns, row, 0)].character)
            .collect()
    }

    #[test]
    fn zero_history_is_legacy_equivalent_and_capacity_is_deterministically_bounded() {
        let legacy = Screen::new(4, 3);
        let configured_zero = Screen::new_with_history_capacity(4, 3, 0, MAX_CELLS);
        assert_eq!(legacy, configured_zero);
        assert_eq!(legacy.cells, configured_zero.cells);
        assert_eq!(legacy.soft_wrapped, configured_zero.soft_wrapped);
        assert_eq!(legacy.meaningful_end, configured_zero.meaningful_end);
        assert_eq!(legacy.row_metadata, configured_zero.row_metadata);
        assert_eq!(configured_zero.history_capacity(), 0);
        assert_eq!(configured_zero.viewport_top(), 0);

        let cell_limited = Screen::new_with_history_capacity(4, 3, 100, 20);
        assert_eq!(cell_limited.history_capacity(), 2);
        let row_limited = Screen::new_with_history_capacity(1, 1, u32::MAX, MAX_CELLS);
        assert_eq!(row_limited.history_capacity(), MAX_HISTORY_ROWS);
    }

    #[test]
    fn full_output_scroll_captures_and_evicts_before_appending() {
        let mut screen = Screen::new_with_history_capacity(3, 3, 2, 15);
        for (row, marker) in ['A', 'B', 'C'].into_iter().enumerate() {
            put_row_marker(
                &mut screen,
                u32::try_from(row).expect("bounded row"),
                marker,
            );
        }

        screen.scroll_up_region_with_cause(1, 0, 3, ScrollCause::Output(1));
        assert_eq!(screen.viewport_top(), 1);
        assert_eq!(stored_row_markers(&screen), ['A', 'B', 'C', ' ']);
        put_row_marker(&mut screen, 2, 'D');
        screen.scroll_up_region_with_cause(1, 0, 3, ScrollCause::Output(2));
        assert_eq!(screen.viewport_top(), 2);
        assert_eq!(stored_row_markers(&screen), ['A', 'B', 'C', 'D', ' ']);
        put_row_marker(&mut screen, 2, 'E');
        screen.scroll_up_region_with_cause(1, 0, 3, ScrollCause::Output(3));
        assert_eq!(screen.viewport_top(), 2);
        assert_eq!(stored_row_markers(&screen), ['B', 'C', 'D', 'E', ' ']);
        assert_eq!(screen.cells.len(), 15);
        screen.validate().expect("captured storage is canonical");
    }

    #[test]
    fn partial_and_ordinary_scrolls_do_not_capture_or_edit_history() {
        let mut screen = Screen::new_with_history_capacity(3, 3, 2, 15);
        for (row, marker) in ['A', 'B', 'C'].into_iter().enumerate() {
            put_row_marker(
                &mut screen,
                u32::try_from(row).expect("bounded row"),
                marker,
            );
        }
        screen.soft_wrapped[0] = true;
        screen.normalize_row_metadata();
        screen.scroll_up_region_with_cause(1, 0, 3, ScrollCause::Output(1));
        let history_cells = screen.cells[..3].to_vec();
        let history_metadata = screen.row_metadata[0];
        let history_soft_wrap = screen.soft_wrapped[0];
        let history_meaningful_end = screen.meaningful_end[0];

        screen.scroll_up_region_with_cause(1, 1, 3, ScrollCause::Output(2));
        screen.scroll_up_region(1, 0, 3);
        screen.position_cursor(1, 0);
        screen.insert_lines(1, 0, 3);
        screen.delete_lines(1, 0, 3);
        screen.reverse_index(0, 3);
        screen.erase_display(2).expect("visible erase");
        screen
            .fill_rectangle('Z', 0, 0, 3, 3)
            .expect("visible rectangle fill");

        assert_eq!(screen.viewport_top(), 1);
        assert_eq!(&screen.cells[..3], history_cells);
        assert_eq!(screen.row_metadata[0], history_metadata);
        assert_eq!(screen.soft_wrapped[0], history_soft_wrap);
        assert_eq!(screen.meaningful_end[0], history_meaningful_end);
        screen
            .validate()
            .expect("history-isolated edits are canonical");
    }

    #[test]
    fn captured_soft_wrap_and_wide_metadata_survive_eviction() {
        let mut screen = Screen::new_with_history_capacity(4, 2, 1, 12);
        for character in "ABCD界XYZ".chars() {
            screen
                .print_with_mode_and_scroll_cause(
                    character,
                    false,
                    true,
                    0,
                    2,
                    None,
                    ScrollCause::Output(1),
                )
                .expect("wrapped output");
        }
        assert_eq!(screen.viewport_top(), 1);
        assert!(screen.soft_wrapped[0]);
        assert_eq!(
            screen.row_metadata[1].logical_line_id,
            screen.row_metadata[0].logical_line_id
        );
        assert_eq!(screen.row_metadata[1].logical_cell_offset, 4);
        assert_eq!(screen.visible_cells()[0].character, '界');
        assert!(screen.visible_cells()[1].continuation);

        for character in "Z1234".chars() {
            screen
                .print_with_mode_and_scroll_cause(
                    character,
                    false,
                    true,
                    0,
                    2,
                    None,
                    ScrollCause::Output(2),
                )
                .expect("evicting wrapped output");
        }
        assert_eq!(screen.viewport_top(), 1);
        assert!(screen.row_metadata[0].is_head_truncated());
        assert!(screen.row_metadata[0].logical_cell_offset > 0);
        screen.validate().expect("wide history remains canonical");
    }

    #[test]
    fn reflow_preserves_history_boundary_and_growth_only_adds_visible_rows() {
        let mut screen = Screen::new_with_history_capacity(4, 2, 4, 32);
        for character in "ABCDEFGH12".chars() {
            screen
                .print_with_mode_and_scroll_cause(
                    character,
                    false,
                    true,
                    0,
                    2,
                    None,
                    ScrollCause::Output(1),
                )
                .expect("history fixture output");
        }
        let history_before = screen.viewport_top();
        assert!(history_before > 0);
        screen.resize_reflow(4, 4).expect("row growth reflow");
        assert_eq!(screen.viewport_top(), history_before);
        assert_eq!(screen.visible_cells().len(), 16);
        screen
            .validate()
            .expect("grown history screen is canonical");

        screen
            .resize_reflow(2, 3)
            .expect("stored logical-line reflow");
        assert!(screen.viewport_top() <= screen.history_capacity());
        assert_eq!(screen.visible_cells().len(), 6);
        assert!(screen.cursor_row < screen.rows);
        assert!(screen.saved.row < screen.rows);
        screen
            .validate()
            .expect("reflowed history screen is canonical");

        screen.resize_crop(3, 4);
        assert!(screen.viewport_top() <= screen.history_capacity());
        assert_eq!(screen.visible_cells().len(), 12);
        screen
            .validate()
            .expect("cropped history screen is canonical");
    }

    #[test]
    fn logical_line_id_exhaustion_is_checked_without_reuse_or_panic() {
        let mut screen = Screen::new(2, 2);
        screen.next_logical_line_id = u64::MAX - 1;
        assert_eq!(screen.allocate_logical_line_id(), Some(u64::MAX - 1));
        assert_eq!(screen.next_logical_line_id, u64::MAX);
        assert_eq!(screen.allocate_logical_line_id(), None);
        assert_eq!(screen.allocate_logical_line_id(), None);
        assert_eq!(screen.next_logical_line_id, u64::MAX);

        let before = screen.clone();
        screen.scroll_up_region(1, 0, 2);
        assert_eq!(screen, before);
        assert_eq!(screen.next_logical_line_id, u64::MAX);
    }
}
