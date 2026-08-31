use std::cmp::Ordering;

use crate::osc::{
    HyperlinkRef, MAX_HYPERLINK_ENTRIES, MAX_HYPERLINK_TOTAL_BYTES, MAX_HYPERLINK_URI_BYTES,
    MAX_TITLE_BYTES,
};
use crate::screen::Screen;
use crate::{Attributes, Cell, Color, CursorShape, Terminal, TerminalError, UnderlineStyle};

pub const RENDER_FRAME_V1_FORMAT_VERSION: u32 = 1;
pub const RENDER_FRAME_V2_FORMAT_VERSION: u32 = 2;
pub const RENDER_FRAME_V3_FORMAT_VERSION: u32 = 3;
pub const MAX_RENDER_FRAME_V1_BYTES: usize = 16 * 1024 * 1024;
pub const MAX_RENDER_FRAME_V2_BYTES: usize = 16 * 1024 * 1024;
pub const MAX_RENDER_FRAME_V3_BYTES: usize = 16 * 1024 * 1024;

const MAGIC_V1: &[u8; 8] = b"TLRFRM1\0";
const MAGIC_V2: &[u8; 8] = b"TLRFRM2\0";
const MAGIC_V3: &[u8; 8] = b"TLRFRM3\0";
const HEADER_BYTES: usize = 96;
const DIRECTORY_ENTRY_BYTES: usize = 24;
const SECTION_COUNT: usize = 6;
const ENVELOPE_BYTES: usize = HEADER_BYTES + SECTION_COUNT * DIRECTORY_ENTRY_BYTES;
const CRITICAL_SECTION: u16 = 1;
const WIDTH_PROFILE_V1: u32 = 1;
const REQUIRED_FEATURES_V1: u64 = 0x1f;
const REQUIRED_FEATURES_V2: u64 = 0x3f;
const REQUIRED_FEATURES_V3: u64 = 0x7f;

const GRAPHEME_TAG: u32 = 1;
const RENDITION_TAG: u32 = 2;
const PALETTE_TAG: u32 = 3;
const HYPERLINK_TAG: u32 = 4;
const ROWS_CELLS_TAG: u32 = 5;
const TITLE_TAG: u32 = 6;

const GRAPHEME_RECORD_BYTES: usize = 8;
const RENDITION_RECORD_BYTES: usize = 16;
const PALETTE_RECORD_BYTES: usize = 4;
const PALETTE_RECORDS: usize = 259;
const HYPERLINK_RECORD_BYTES: usize = 16;
const ROWS_CELLS_PREFIX_BYTES: usize = 8;
const ROW_RECORD_BYTES: usize = 8;
const CELL_RECORD_BYTES: usize = 12;
const RENDITION_V3_ALLOWED_FLAGS: u32 = 0x0fff;
const RENDITION_UNDERLINE_PRESENT: u32 = 1 << 4;
const RENDITION_UNDERLINE_STYLE_SHIFT: u32 = 9;
const RENDITION_UNDERLINE_STYLE_MASK: u32 = 7 << RENDITION_UNDERLINE_STYLE_SHIFT;

const MAX_CANONICAL_RENDER_FRAME_BYTES: usize = ENVELOPE_BYTES
    + 22 * crate::MAX_CELLS
    + RENDITION_RECORD_BYTES * crate::MAX_CELLS
    + PALETTE_RECORD_BYTES * PALETTE_RECORDS
    + HYPERLINK_RECORD_BYTES * MAX_HYPERLINK_ENTRIES
    + MAX_HYPERLINK_TOTAL_BYTES
    + ROWS_CELLS_PREFIX_BYTES
    + ROW_RECORD_BYTES * crate::MAX_ROWS as usize
    + CELL_RECORD_BYTES * crate::MAX_CELLS
    + MAX_TITLE_BYTES
    + 4; // The palette section is the only worst-case section needing padding.
const _: () = assert!(MAX_CANONICAL_RENDER_FRAME_BYTES == 13_395_208);
const _: () = assert!(MAX_CANONICAL_RENDER_FRAME_BYTES <= MAX_RENDER_FRAME_V1_BYTES);
const _: () = assert!(MAX_CANONICAL_RENDER_FRAME_BYTES <= MAX_RENDER_FRAME_V2_BYTES);
const _: () = assert!(MAX_CANONICAL_RENDER_FRAME_BYTES <= MAX_RENDER_FRAME_V3_BYTES);

#[derive(Clone, Copy)]
enum FrameVersion {
    V1,
    V2,
    V3,
}

#[derive(Clone, Copy, Debug, Default)]
struct SectionDesc {
    tag: u32,
    offset: u32,
    length: u32,
    count: u32,
    record_size: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct GraphemeRef<'a> {
    character: char,
    combining: &'a str,
    width: u8,
}

impl GraphemeRef<'_> {
    fn compare(self, other: Self) -> Ordering {
        // UTF-8 preserves scalar-value order. Once the first scalar matches,
        // comparing the remaining raw UTF-8 bytes gives the required ordering
        // for the complete grapheme byte strings.
        self.character
            .cmp(&other.character)
            .then_with(|| self.combining.as_bytes().cmp(other.combining.as_bytes()))
    }

    fn byte_len(self) -> usize {
        self.character.len_utf8() + self.combining.len()
    }
}

pub(crate) fn export(terminal: &Terminal) -> Result<Vec<u8>, TerminalError> {
    let bytes = export_version(terminal, FrameVersion::V1)?;
    validate_v1_header(&bytes)?;
    Ok(bytes)
}

pub(crate) fn export_v2(terminal: &Terminal) -> Result<Vec<u8>, TerminalError> {
    let bytes = export_version(terminal, FrameVersion::V2)?;
    validate_v2_header(&bytes)?;
    Ok(bytes)
}

pub(crate) fn export_v3(terminal: &Terminal) -> Result<Vec<u8>, TerminalError> {
    let bytes = export_version(terminal, FrameVersion::V3)?;
    validate_v3_header(&bytes)?;
    Ok(bytes)
}

pub(crate) fn export_v3_at_scrollback(
    terminal: &Terminal,
    offset_rows: u32,
) -> Result<Vec<u8>, TerminalError> {
    terminal.ensure_presentation_eligible()?;
    crate::validate_dimensions(terminal.columns, terminal.rows)?;

    let screen = terminal.active_screen();
    if screen.columns != terminal.columns || screen.rows != terminal.rows {
        return Err(TerminalError::MalformedSnapshot);
    }

    let window = screen.render_window(offset_rows);
    window.validate()?;
    let bytes = export_version_with_screen(terminal, &window, false, FrameVersion::V3)?;
    validate_v3_header(&bytes)?;
    Ok(bytes)
}

fn export_version(terminal: &Terminal, version: FrameVersion) -> Result<Vec<u8>, TerminalError> {
    terminal.ensure_presentation_eligible()?;
    crate::validate_dimensions(terminal.columns, terminal.rows)?;

    let screen = terminal.active_screen();
    if screen.columns != terminal.columns || screen.rows != terminal.rows {
        return Err(TerminalError::MalformedSnapshot);
    }
    screen.validate()?;

    export_version_with_screen(terminal, screen, terminal.cursor_visible, version)
}

fn export_version_with_screen(
    terminal: &Terminal,
    screen: &Screen,
    cursor_visible: bool,
    version: FrameVersion,
) -> Result<Vec<u8>, TerminalError> {
    let graphemes = collect_graphemes(screen)?;
    let renditions = collect_renditions(screen, version)?;
    let hyperlinks = collect_visible_hyperlinks(terminal, screen)?;
    encode_frame(
        terminal,
        screen,
        &graphemes,
        &renditions,
        &hyperlinks,
        cursor_visible,
        version,
    )
}

fn collect_graphemes(screen: &Screen) -> Result<Vec<GraphemeRef<'_>>, TerminalError> {
    let visible_cells = screen.visible_cells();
    let mut graphemes = Vec::new();
    graphemes
        .try_reserve_exact(visible_cells.len())
        .map_err(|_| TerminalError::LimitExceeded)?;
    for cell in visible_cells {
        if !cell.continuation {
            if !(1..=crate::MAX_GRAPHEME_BYTES).contains(&cell.grapheme_bytes())
                || !matches!(cell.width, 1 | 2)
            {
                return Err(TerminalError::MalformedSnapshot);
            }
            graphemes.push(grapheme_ref(cell));
        }
    }
    graphemes.sort_unstable_by(|left, right| left.compare(*right));
    for pair in graphemes.windows(2) {
        if pair[0].compare(pair[1]) == Ordering::Equal && pair[0].width != pair[1].width {
            return Err(TerminalError::MalformedSnapshot);
        }
    }
    graphemes.dedup_by(|left, right| left.compare(*right) == Ordering::Equal);
    Ok(graphemes)
}

fn collect_renditions(
    screen: &Screen,
    version: FrameVersion,
) -> Result<Vec<[u8; RENDITION_RECORD_BYTES]>, TerminalError> {
    let visible_cells = screen.visible_cells();
    let mut renditions = Vec::new();
    renditions
        .try_reserve_exact(visible_cells.len())
        .map_err(|_| TerminalError::LimitExceeded)?;
    for cell in visible_cells {
        renditions.push(rendition_bytes(cell.attributes, version));
    }
    renditions.sort_unstable();
    renditions.dedup();
    Ok(renditions)
}

fn collect_visible_hyperlinks(
    terminal: &Terminal,
    screen: &Screen,
) -> Result<Vec<u32>, TerminalError> {
    let visible_cells = screen.visible_cells();
    let mut references = Vec::new();
    references
        .try_reserve_exact(visible_cells.len())
        .map_err(|_| TerminalError::LimitExceeded)?;
    for cell in visible_cells {
        if let Some(reference) = cell.hyperlink {
            let value = reference.get();
            if value > MAX_HYPERLINK_ENTRIES as u32 || terminal.osc.hyperlink(reference).is_none() {
                return Err(TerminalError::MalformedSnapshot);
            }
            references.push(value);
        }
    }
    references.sort_unstable();
    references.dedup();
    if references.len() > MAX_HYPERLINK_ENTRIES {
        return Err(TerminalError::MalformedSnapshot);
    }
    Ok(references)
}

fn encode_frame(
    terminal: &Terminal,
    screen: &Screen,
    graphemes: &[GraphemeRef<'_>],
    renditions: &[[u8; RENDITION_RECORD_BYTES]],
    hyperlinks: &[u32],
    cursor_visible: bool,
    version: FrameVersion,
) -> Result<Vec<u8>, TerminalError> {
    let grapheme_blob_bytes = graphemes.iter().try_fold(0_usize, |length, grapheme| {
        checked_add(length, grapheme.byte_len())
    })?;
    let grapheme_bytes = checked_add(
        checked_mul(graphemes.len(), GRAPHEME_RECORD_BYTES)?,
        grapheme_blob_bytes,
    )?;
    let rendition_bytes = checked_mul(renditions.len(), RENDITION_RECORD_BYTES)?;
    let palette_bytes = checked_mul(PALETTE_RECORDS, PALETTE_RECORD_BYTES)?;
    let hyperlink_blob_bytes = hyperlinks.iter().try_fold(0_usize, |length, &value| {
        let link = terminal
            .osc
            .hyperlink(HyperlinkRef::new(value))
            .ok_or(TerminalError::MalformedSnapshot)?;
        if link.uri().is_empty() || link.uri().len() > MAX_HYPERLINK_URI_BYTES {
            return Err(TerminalError::MalformedSnapshot);
        }
        checked_add(length, link.uri().len())
    })?;
    if hyperlink_blob_bytes > MAX_HYPERLINK_TOTAL_BYTES {
        return Err(TerminalError::MalformedSnapshot);
    }
    let hyperlink_bytes = checked_add(
        checked_mul(hyperlinks.len(), HYPERLINK_RECORD_BYTES)?,
        hyperlink_blob_bytes,
    )?;
    let visible_rows = usize::try_from(screen.rows).map_err(|_| TerminalError::LimitExceeded)?;
    let visible_cells = screen.visible_cells();
    let rows_bytes = checked_mul(visible_rows, ROW_RECORD_BYTES)?;
    let cells_bytes = checked_mul(visible_cells.len(), CELL_RECORD_BYTES)?;
    let rows_cells_bytes = checked_add(
        ROWS_CELLS_PREFIX_BYTES,
        checked_add(rows_bytes, cells_bytes)?,
    )?;
    let title = terminal.osc.window_title().as_bytes();
    if title.len() > MAX_TITLE_BYTES {
        return Err(TerminalError::MalformedSnapshot);
    }

    let lengths = [
        grapheme_bytes,
        rendition_bytes,
        palette_bytes,
        hyperlink_bytes,
        rows_cells_bytes,
        title.len(),
    ];
    let counts = [
        checked_u32(graphemes.len())?,
        checked_u32(renditions.len())?,
        PALETTE_RECORDS as u32,
        checked_u32(hyperlinks.len())?,
        checked_u32(visible_cells.len())?,
        checked_u32(title.len())?,
    ];
    let record_sizes = [
        GRAPHEME_RECORD_BYTES as u32,
        RENDITION_RECORD_BYTES as u32,
        PALETTE_RECORD_BYTES as u32,
        HYPERLINK_RECORD_BYTES as u32,
        0,
        0,
    ];
    let tags = [
        GRAPHEME_TAG,
        RENDITION_TAG,
        PALETTE_TAG,
        HYPERLINK_TAG,
        ROWS_CELLS_TAG,
        TITLE_TAG,
    ];

    let mut descs = [SectionDesc::default(); SECTION_COUNT];
    let mut end = ENVELOPE_BYTES;
    for index in 0..SECTION_COUNT {
        let offset = align8(end)?;
        end = checked_add(offset, lengths[index])?;
        descs[index] = SectionDesc {
            tag: tags[index],
            offset: checked_u32(offset)?,
            length: checked_u32(lengths[index])?,
            count: counts[index],
            record_size: record_sizes[index],
        };
    }
    let total = align8(end)?;
    let maximum = match version {
        FrameVersion::V1 => MAX_RENDER_FRAME_V1_BYTES,
        FrameVersion::V2 => MAX_RENDER_FRAME_V2_BYTES,
        FrameVersion::V3 => MAX_RENDER_FRAME_V3_BYTES,
    };
    if total > maximum {
        return Err(TerminalError::LimitExceeded);
    }

    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(total)
        .map_err(|_| TerminalError::LimitExceeded)?;
    bytes.resize(total, 0);

    encode_graphemes(&mut bytes, descs[0], graphemes)?;
    encode_renditions(&mut bytes, descs[1], renditions);
    encode_palette(&mut bytes, descs[2], terminal);
    encode_hyperlinks(&mut bytes, descs[3], terminal, hyperlinks)?;
    encode_rows_cells(&mut bytes, descs[4], screen, graphemes, renditions, version)?;
    patch_bytes(&mut bytes, descs[5].offset as usize, title);

    encode_directory(&mut bytes, &descs, version);
    encode_header(&mut bytes, terminal, screen, total, cursor_visible, version)?;
    Ok(bytes)
}

fn encode_graphemes(
    bytes: &mut [u8],
    desc: SectionDesc,
    graphemes: &[GraphemeRef<'_>],
) -> Result<(), TerminalError> {
    let section = desc.offset as usize;
    let mut blob_offset = checked_mul(graphemes.len(), GRAPHEME_RECORD_BYTES)?;
    for (index, grapheme) in graphemes.iter().copied().enumerate() {
        let record = section + index * GRAPHEME_RECORD_BYTES;
        patch_u32(bytes, record, checked_u32(blob_offset)?);
        patch_u16(
            bytes,
            record + 4,
            u16::try_from(grapheme.byte_len()).map_err(|_| TerminalError::LimitExceeded)?,
        );
        bytes[record + 6] = grapheme.width;

        let blob = section + blob_offset;
        let mut scalar = [0_u8; 4];
        let character = grapheme.character.encode_utf8(&mut scalar).as_bytes();
        patch_bytes(bytes, blob, character);
        patch_bytes(bytes, blob + character.len(), grapheme.combining.as_bytes());
        blob_offset = checked_add(blob_offset, grapheme.byte_len())?;
    }
    debug_assert_eq!(blob_offset, desc.length as usize);
    Ok(())
}

fn encode_renditions(
    bytes: &mut [u8],
    desc: SectionDesc,
    renditions: &[[u8; RENDITION_RECORD_BYTES]],
) {
    let mut offset = desc.offset as usize;
    for rendition in renditions {
        patch_bytes(bytes, offset, rendition);
        offset += RENDITION_RECORD_BYTES;
    }
    debug_assert_eq!(offset, desc.offset as usize + desc.length as usize);
}

fn encode_palette(bytes: &mut [u8], desc: SectionDesc, terminal: &Terminal) {
    let mut offset = desc.offset as usize;
    for index in 0_u8..=u8::MAX {
        patch_u32(bytes, offset, osc_color(terminal.osc.palette(index)));
        offset += PALETTE_RECORD_BYTES;
    }
    for color in [
        terminal.osc.foreground(),
        terminal.osc.background(),
        terminal.osc.cursor_color(),
    ] {
        patch_u32(bytes, offset, osc_color(color));
        offset += PALETTE_RECORD_BYTES;
    }
    debug_assert_eq!(offset, desc.offset as usize + desc.length as usize);
}

fn encode_hyperlinks(
    bytes: &mut [u8],
    desc: SectionDesc,
    terminal: &Terminal,
    hyperlinks: &[u32],
) -> Result<(), TerminalError> {
    let section = desc.offset as usize;
    let mut blob_offset = checked_mul(hyperlinks.len(), HYPERLINK_RECORD_BYTES)?;
    for (index, &value) in hyperlinks.iter().enumerate() {
        let link = terminal
            .osc
            .hyperlink(HyperlinkRef::new(value))
            .ok_or(TerminalError::MalformedSnapshot)?;
        let record = section + index * HYPERLINK_RECORD_BYTES;
        patch_u32(bytes, record, value);
        patch_u32(bytes, record + 4, checked_u32(blob_offset)?);
        patch_u32(bytes, record + 8, checked_u32(link.uri().len())?);
        patch_bytes(bytes, section + blob_offset, link.uri().as_bytes());
        blob_offset = checked_add(blob_offset, link.uri().len())?;
    }
    debug_assert_eq!(blob_offset, desc.length as usize);
    Ok(())
}

fn encode_rows_cells(
    bytes: &mut [u8],
    desc: SectionDesc,
    screen: &Screen,
    graphemes: &[GraphemeRef<'_>],
    renditions: &[[u8; RENDITION_RECORD_BYTES]],
    version: FrameVersion,
) -> Result<(), TerminalError> {
    let section = desc.offset as usize;
    patch_u32(bytes, section, ROW_RECORD_BYTES as u32);
    patch_u32(bytes, section + 4, CELL_RECORD_BYTES as u32);

    let rows = section + ROWS_CELLS_PREFIX_BYTES;
    let visible_rows = usize::try_from(screen.rows).map_err(|_| TerminalError::LimitExceeded)?;
    for visible_row in 0..visible_rows {
        let stored_row = screen.visible_stored_row(
            u32::try_from(visible_row).map_err(|_| TerminalError::LimitExceeded)?,
        );
        let stored_row = usize::try_from(stored_row).map_err(|_| TerminalError::LimitExceeded)?;
        let record = rows + visible_row * ROW_RECORD_BYTES;
        patch_u32(bytes, record, screen.meaningful_end[stored_row]);
        patch_u32(
            bytes,
            record + 4,
            u32::from(screen.soft_wrapped[stored_row]),
        );
    }

    let cells = rows + visible_rows * ROW_RECORD_BYTES;
    for (index, cell) in screen.visible_cells().iter().enumerate() {
        let record = cells + index * CELL_RECORD_BYTES;
        let grapheme_id = if cell.continuation {
            0
        } else {
            let key = grapheme_ref(cell);
            let id = graphemes
                .binary_search_by(|candidate| candidate.compare(key))
                .map_err(|_| TerminalError::MalformedSnapshot)?;
            checked_u32(id + 1)?
        };
        let rendition = rendition_bytes(cell.attributes, version);
        let rendition_id = renditions
            .binary_search(&rendition)
            .map_err(|_| TerminalError::MalformedSnapshot)?;
        let hyperlink = cell.hyperlink.map_or(0, HyperlinkRef::get);
        if hyperlink > 0x7ff {
            return Err(TerminalError::MalformedSnapshot);
        }
        let metadata =
            u32::from(cell.continuation) | (u32::from(cell.protected) << 1) | (hyperlink << 2);
        patch_u32(bytes, record, grapheme_id);
        patch_u32(bytes, record + 4, checked_u32(rendition_id)?);
        patch_u32(bytes, record + 8, metadata);
    }
    Ok(())
}

fn encode_directory(bytes: &mut [u8], descs: &[SectionDesc; SECTION_COUNT], version: FrameVersion) {
    for (index, desc) in descs.iter().copied().enumerate() {
        let base = HEADER_BYTES + index * DIRECTORY_ENTRY_BYTES;
        patch_u32(bytes, base, desc.tag);
        let section_version = if matches!(version, FrameVersion::V3) && desc.tag == RENDITION_TAG {
            2
        } else {
            1
        };
        patch_u16(bytes, base + 4, section_version);
        patch_u16(bytes, base + 6, CRITICAL_SECTION);
        patch_u32(bytes, base + 8, desc.offset);
        patch_u32(bytes, base + 12, desc.length);
        patch_u32(bytes, base + 16, desc.count);
        patch_u32(bytes, base + 20, desc.record_size);
    }
}

fn encode_header(
    bytes: &mut [u8],
    terminal: &Terminal,
    screen: &Screen,
    total: usize,
    cursor_visible: bool,
    version: FrameVersion,
) -> Result<(), TerminalError> {
    let (magic, major, required_features) = match version {
        FrameVersion::V1 => (MAGIC_V1, 1, REQUIRED_FEATURES_V1),
        FrameVersion::V2 => (MAGIC_V2, 2, REQUIRED_FEATURES_V2),
        FrameVersion::V3 => (MAGIC_V3, 3, REQUIRED_FEATURES_V3),
    };
    patch_bytes(bytes, 0, magic);
    patch_u16(bytes, 8, major);
    patch_u16(bytes, 10, 0);
    patch_u16(bytes, 12, HEADER_BYTES as u16);
    patch_u16(bytes, 14, DIRECTORY_ENTRY_BYTES as u16);
    patch_u16(bytes, 16, SECTION_COUNT as u16);
    let frame_flags =
        u16::from(terminal.active_alternate) | (u16::from(terminal.modes.reverse_video) << 1);
    patch_u16(bytes, 18, frame_flags);
    patch_u32(bytes, 20, checked_u32(total)?);
    patch_u64(bytes, 24, terminal.next_source_sequence);
    patch_u64(bytes, 32, terminal.next_source_sequence);
    patch_u32(bytes, 40, terminal.columns);
    patch_u32(bytes, 44, terminal.rows);
    patch_u32(bytes, 48, screen.cursor_row);
    patch_u32(bytes, 52, screen.cursor_column);
    patch_u32(
        bytes,
        56,
        u32::from(cursor_visible) | (u32::from(screen.wrap_pending) << 1),
    );
    patch_u32(bytes, 60, WIDTH_PROFILE_V1);
    patch_u32(bytes, 64, HEADER_BYTES as u32);
    if matches!(version, FrameVersion::V2 | FrameVersion::V3) {
        bytes[68] = terminal.cursor_shape as u8;
        bytes[69] = u8::from(terminal.cursor_blinking);
    }
    patch_u64(bytes, 72, required_features);

    let payload_crc = crate::crc32c::checksum(&bytes[HEADER_BYTES..]);
    patch_u32(bytes, 88, payload_crc);
    let header_crc = crate::crc32c::checksum(&bytes[..92]);
    patch_u32(bytes, 92, header_crc);
    Ok(())
}

fn validate_v2_header(bytes: &[u8]) -> Result<(), TerminalError> {
    if bytes.len() < HEADER_BYTES {
        return Err(TerminalError::MalformedSnapshot);
    }
    if &bytes[..8] != MAGIC_V2
        || read_u16(bytes, 8)? != RENDER_FRAME_V2_FORMAT_VERSION as u16
        || read_u16(bytes, 10)? != 0
        || read_u16(bytes, 12)? != HEADER_BYTES as u16
        || read_u16(bytes, 14)? != DIRECTORY_ENTRY_BYTES as u16
        || read_u16(bytes, 16)? != SECTION_COUNT as u16
        || read_u32(bytes, 60)? != WIDTH_PROFILE_V1
        || read_u32(bytes, 64)? != HEADER_BYTES as u32
        || read_u64(bytes, 72)? != REQUIRED_FEATURES_V2
    {
        return Err(TerminalError::IncompatibleSnapshot);
    }
    if read_u32(bytes, 20)? as usize != bytes.len()
        || read_u16(bytes, 18)? & !3 != 0
        || read_u32(bytes, 56)? & !3 != 0
        || bytes[68] > CursorShape::Bar as u8
        || bytes[69] > 1
        || read_u16(bytes, 70)? != 0
        || read_u64(bytes, 80)? != 0
    {
        return Err(TerminalError::MalformedSnapshot);
    }
    let columns = read_u32(bytes, 40)?;
    let rows = read_u32(bytes, 44)?;
    crate::validate_dimensions(columns, rows)?;
    let cursor_row = read_u32(bytes, 48)?;
    let cursor_column = read_u32(bytes, 52)?;
    let cursor_flags = read_u32(bytes, 56)?;
    if cursor_row >= rows
        || cursor_column >= columns
        || (cursor_flags & 2 != 0 && cursor_column != columns - 1)
        || crate::crc32c::checksum(&bytes[HEADER_BYTES..]) != read_u32(bytes, 88)?
        || crate::crc32c::checksum(&bytes[..92]) != read_u32(bytes, 92)?
    {
        return Err(TerminalError::MalformedSnapshot);
    }
    Ok(())
}

pub(crate) fn validate_v3_header(bytes: &[u8]) -> Result<(), TerminalError> {
    if bytes.len() < HEADER_BYTES {
        return Err(TerminalError::MalformedSnapshot);
    }
    if &bytes[..8] != MAGIC_V3
        || read_u16(bytes, 8)? != RENDER_FRAME_V3_FORMAT_VERSION as u16
        || read_u16(bytes, 10)? != 0
        || read_u16(bytes, 12)? != HEADER_BYTES as u16
        || read_u16(bytes, 14)? != DIRECTORY_ENTRY_BYTES as u16
        || read_u16(bytes, 16)? != SECTION_COUNT as u16
        || read_u32(bytes, 60)? != WIDTH_PROFILE_V1
        || read_u32(bytes, 64)? != HEADER_BYTES as u32
        || read_u64(bytes, 72)? != REQUIRED_FEATURES_V3
    {
        return Err(TerminalError::IncompatibleSnapshot);
    }
    if read_u32(bytes, 20)? as usize != bytes.len()
        || read_u16(bytes, 18)? & !3 != 0
        || read_u32(bytes, 56)? & !3 != 0
        || bytes[68] > CursorShape::Bar as u8
        || bytes[69] > 1
        || read_u16(bytes, 70)? != 0
        || read_u64(bytes, 80)? != 0
    {
        return Err(TerminalError::MalformedSnapshot);
    }
    let columns = read_u32(bytes, 40)?;
    let rows = read_u32(bytes, 44)?;
    crate::validate_dimensions(columns, rows)?;
    let cursor_row = read_u32(bytes, 48)?;
    let cursor_column = read_u32(bytes, 52)?;
    let cursor_flags = read_u32(bytes, 56)?;
    if cursor_row >= rows
        || cursor_column >= columns
        || (cursor_flags & 2 != 0 && cursor_column != columns - 1)
        || crate::crc32c::checksum(&bytes[HEADER_BYTES..]) != read_u32(bytes, 88)?
        || crate::crc32c::checksum(&bytes[..92]) != read_u32(bytes, 92)?
    {
        return Err(TerminalError::MalformedSnapshot);
    }
    validate_v3_renditions(bytes)
}

fn validate_v3_renditions(bytes: &[u8]) -> Result<(), TerminalError> {
    if bytes.len() < ENVELOPE_BYTES {
        return Err(TerminalError::MalformedSnapshot);
    }
    for (index, expected_tag) in [
        GRAPHEME_TAG,
        RENDITION_TAG,
        PALETTE_TAG,
        HYPERLINK_TAG,
        ROWS_CELLS_TAG,
        TITLE_TAG,
    ]
    .into_iter()
    .enumerate()
    {
        let entry = HEADER_BYTES + index * DIRECTORY_ENTRY_BYTES;
        let expected_version = if expected_tag == RENDITION_TAG { 2 } else { 1 };
        if read_u32(bytes, entry)? != expected_tag
            || read_u16(bytes, entry + 4)? != expected_version
        {
            return Err(TerminalError::IncompatibleSnapshot);
        }
        if read_u16(bytes, entry + 6)? != CRITICAL_SECTION {
            return Err(TerminalError::MalformedSnapshot);
        }
    }
    let directory = HEADER_BYTES + (RENDITION_TAG as usize - 1) * DIRECTORY_ENTRY_BYTES;
    if read_u32(bytes, directory + 20)? != RENDITION_RECORD_BYTES as u32 {
        return Err(TerminalError::MalformedSnapshot);
    }
    let offset = read_u32(bytes, directory + 8)? as usize;
    let length = read_u32(bytes, directory + 12)? as usize;
    let count = read_u32(bytes, directory + 16)? as usize;
    let expected_length = count
        .checked_mul(RENDITION_RECORD_BYTES)
        .ok_or(TerminalError::MalformedSnapshot)?;
    let end = offset
        .checked_add(length)
        .ok_or(TerminalError::MalformedSnapshot)?;
    if offset < ENVELOPE_BYTES
        || !offset.is_multiple_of(8)
        || length != expected_length
        || end > bytes.len()
    {
        return Err(TerminalError::MalformedSnapshot);
    }

    for record in bytes[offset..end].chunks_exact(RENDITION_RECORD_BYTES) {
        let flags = u32::from_le_bytes(record[..4].try_into().expect("four-byte rendition flags"));
        let style = (flags & RENDITION_UNDERLINE_STYLE_MASK) >> RENDITION_UNDERLINE_STYLE_SHIFT;
        let underline_present = flags & RENDITION_UNDERLINE_PRESENT != 0;
        if flags & !RENDITION_V3_ALLOWED_FLAGS != 0
            || style > 5
            || underline_present != (style != 0)
            || !valid_packed_color(u32::from_le_bytes(
                record[4..8].try_into().expect("four-byte foreground color"),
            ))
            || !valid_packed_color(u32::from_le_bytes(
                record[8..12]
                    .try_into()
                    .expect("four-byte background color"),
            ))
            || !valid_packed_color(u32::from_le_bytes(
                record[12..16]
                    .try_into()
                    .expect("four-byte underline color"),
            ))
        {
            return Err(TerminalError::MalformedSnapshot);
        }
    }
    Ok(())
}

fn validate_v1_header(bytes: &[u8]) -> Result<(), TerminalError> {
    if bytes.len() < HEADER_BYTES {
        return Err(TerminalError::MalformedSnapshot);
    }
    if &bytes[..8] != MAGIC_V1
        || read_u16(bytes, 8)? != RENDER_FRAME_V1_FORMAT_VERSION as u16
        || read_u16(bytes, 10)? != 0
        || read_u16(bytes, 12)? != HEADER_BYTES as u16
        || read_u16(bytes, 14)? != DIRECTORY_ENTRY_BYTES as u16
        || read_u16(bytes, 16)? != SECTION_COUNT as u16
        || read_u32(bytes, 60)? != WIDTH_PROFILE_V1
        || read_u32(bytes, 64)? != HEADER_BYTES as u32
        || read_u64(bytes, 72)? != REQUIRED_FEATURES_V1
    {
        return Err(TerminalError::IncompatibleSnapshot);
    }
    if read_u32(bytes, 20)? as usize != bytes.len()
        || read_u16(bytes, 18)? & !3 != 0
        || read_u32(bytes, 56)? & !3 != 0
        || read_u32(bytes, 68)? != 0
        || read_u64(bytes, 80)? != 0
    {
        return Err(TerminalError::MalformedSnapshot);
    }
    let columns = read_u32(bytes, 40)?;
    let rows = read_u32(bytes, 44)?;
    crate::validate_dimensions(columns, rows)?;
    let cursor_row = read_u32(bytes, 48)?;
    let cursor_column = read_u32(bytes, 52)?;
    let cursor_flags = read_u32(bytes, 56)?;
    if cursor_row >= rows
        || cursor_column >= columns
        || (cursor_flags & 2 != 0 && cursor_column != columns - 1)
        || crate::crc32c::checksum(&bytes[HEADER_BYTES..]) != read_u32(bytes, 88)?
        || crate::crc32c::checksum(&bytes[..92]) != read_u32(bytes, 92)?
    {
        return Err(TerminalError::MalformedSnapshot);
    }
    Ok(())
}

fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, TerminalError> {
    let value = bytes
        .get(offset..offset + 2)
        .ok_or(TerminalError::MalformedSnapshot)?;
    Ok(u16::from_le_bytes(
        value
            .try_into()
            .map_err(|_| TerminalError::MalformedSnapshot)?,
    ))
}

fn read_u32(bytes: &[u8], offset: usize) -> Result<u32, TerminalError> {
    let value = bytes
        .get(offset..offset + 4)
        .ok_or(TerminalError::MalformedSnapshot)?;
    Ok(u32::from_le_bytes(
        value
            .try_into()
            .map_err(|_| TerminalError::MalformedSnapshot)?,
    ))
}

fn read_u64(bytes: &[u8], offset: usize) -> Result<u64, TerminalError> {
    let value = bytes
        .get(offset..offset + 8)
        .ok_or(TerminalError::MalformedSnapshot)?;
    Ok(u64::from_le_bytes(
        value
            .try_into()
            .map_err(|_| TerminalError::MalformedSnapshot)?,
    ))
}

fn rendition_bytes(attributes: Attributes, version: FrameVersion) -> [u8; RENDITION_RECORD_BYTES] {
    let mut bytes = [0_u8; RENDITION_RECORD_BYTES];
    let underline_present = attributes.underline_style != UnderlineStyle::None;
    let flags = u32::from(attributes.bold)
        | (u32::from(attributes.blink) << 1)
        | (u32::from(attributes.italic) << 2)
        | (u32::from(attributes.inverse) << 3)
        | (u32::from(underline_present) << 4)
        | (u32::from(attributes.conceal) << 5)
        | (u32::from(attributes.strikethrough) << 6)
        | if matches!(version, FrameVersion::V3) {
            (u32::from(attributes.faint) << 7)
                | (u32::from(attributes.overline) << 8)
                | (u32::from(attributes.underline_style as u8) << RENDITION_UNDERLINE_STYLE_SHIFT)
        } else {
            0
        };
    bytes[..4].copy_from_slice(&flags.to_le_bytes());
    bytes[4..8].copy_from_slice(&packed_color(attributes.foreground).to_le_bytes());
    bytes[8..12].copy_from_slice(&packed_color(attributes.background).to_le_bytes());
    if matches!(version, FrameVersion::V3) {
        bytes[12..16].copy_from_slice(&packed_color(attributes.underline_color).to_le_bytes());
    }
    bytes
}

fn packed_color(color: Color) -> u32 {
    match color {
        Color::Default => 0,
        Color::Indexed(index) => 0x0100_0000 | u32::from(index),
        Color::Rgb(red, green, blue) => {
            0x0200_0000 | (u32::from(red) << 16) | (u32::from(green) << 8) | u32::from(blue)
        }
    }
}

fn valid_packed_color(color: u32) -> bool {
    match color >> 24 {
        0 => color == 0,
        1 => color & 0x00ff_ff00 == 0,
        2 => true,
        _ => false,
    }
}

fn osc_color(color: Option<crate::OscColor>) -> u32 {
    color.map_or(u32::MAX, |value| {
        (u32::from(value.red) << 16) | (u32::from(value.green) << 8) | u32::from(value.blue)
    })
}

fn grapheme_ref(cell: &Cell) -> GraphemeRef<'_> {
    GraphemeRef {
        character: cell.character,
        combining: &cell.combining,
        width: cell.width,
    }
}

fn checked_add(left: usize, right: usize) -> Result<usize, TerminalError> {
    left.checked_add(right).ok_or(TerminalError::LimitExceeded)
}

fn checked_mul(left: usize, right: usize) -> Result<usize, TerminalError> {
    left.checked_mul(right).ok_or(TerminalError::LimitExceeded)
}

fn checked_u32(value: usize) -> Result<u32, TerminalError> {
    u32::try_from(value).map_err(|_| TerminalError::LimitExceeded)
}

fn align8(value: usize) -> Result<usize, TerminalError> {
    checked_add(value, 7).map(|aligned| aligned & !7)
}

fn patch_bytes(target: &mut [u8], offset: usize, value: &[u8]) {
    target[offset..offset + value.len()].copy_from_slice(value);
}

fn patch_u16(target: &mut [u8], offset: usize, value: u16) {
    patch_bytes(target, offset, &value.to_le_bytes());
}

fn patch_u32(target: &mut [u8], offset: usize, value: u32) {
    patch_bytes(target, offset, &value.to_le_bytes());
}

fn patch_u64(target: &mut [u8], offset: usize, value: u64) {
    patch_bytes(target, offset, &value.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::screen::ScrollCause;
    use crate::{Event, Terminal};

    const HISTORY_ONLY_GRAPHEME: &str = "Q\u{0301}\u{0302}\u{0303}\u{0304}";
    const HISTORY_ONLY_URI: &str = "https://history-only.invalid/render-frame-v1";

    fn u16_at(bytes: &[u8], offset: usize) -> u16 {
        u16::from_le_bytes(bytes[offset..offset + 2].try_into().expect("u16 field"))
    }

    fn u32_at(bytes: &[u8], offset: usize) -> u32 {
        u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("u32 field"))
    }

    fn u64_at(bytes: &[u8], offset: usize) -> u64 {
        u64::from_le_bytes(bytes[offset..offset + 8].try_into().expect("u64 field"))
    }

    fn refresh_crc(bytes: &mut [u8]) {
        let payload = crate::crc32c::checksum(&bytes[HEADER_BYTES..]);
        bytes[88..92].copy_from_slice(&payload.to_le_bytes());
        let header = crate::crc32c::checksum(&bytes[..92]);
        bytes[92..96].copy_from_slice(&header.to_le_bytes());
    }

    fn section(bytes: &[u8], index: usize) -> (usize, usize) {
        let directory = HEADER_BYTES + index * DIRECTORY_ENTRY_BYTES;
        (
            u32_at(bytes, directory + 8) as usize,
            u32_at(bytes, directory + 12) as usize,
        )
    }

    fn terminal_with_hidden_history() -> Terminal {
        let mut terminal = Terminal::new(3, 2).expect("terminal");
        terminal.main = Screen::new_with_history_capacity(3, 2, 1, 9);
        let output = format!(
            "\x1b]8;;{HISTORY_ONLY_URI}\x1b\\\x1b[1;3;9;38;2;7;8;9;48;5;42m{HISTORY_ONLY_GRAPHEME}\x1b[0m\x1b]8;;\x1b\\\r\nVIS"
        );
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: output.as_bytes(),
            })
            .expect("history fixture output");
        terminal
            .main
            .scroll_up_region_with_cause(1, 0, 2, ScrollCause::Output(1));
        assert_eq!(terminal.main.viewport_top(), 1);
        assert_eq!(terminal.main.visible_cells().len(), 6);
        terminal.main.validate().expect("history fixture screen");
        terminal
    }

    fn clone_without_history_prefix(terminal: &Terminal) -> Terminal {
        let mut visible = terminal.clone();
        let source = &terminal.main;
        let viewport = usize::try_from(source.viewport_top()).expect("bounded viewport");
        let mut screen = Screen::new(source.columns, source.rows);
        screen.cells.clone_from_slice(source.visible_cells());
        screen
            .soft_wrapped
            .copy_from_slice(&source.soft_wrapped[viewport..]);
        screen
            .meaningful_end
            .copy_from_slice(&source.meaningful_end[viewport..]);
        screen.cursor_row = source.cursor_row;
        screen.cursor_column = source.cursor_column;
        screen.wrap_pending = source.wrap_pending;
        screen.attributes = source.attributes;
        screen.protected = source.protected;
        screen.saved = source.saved;
        visible.main = screen;

        assert_ne!(
            source.requested_history_rows(),
            visible.main.requested_history_rows()
        );
        assert_ne!(
            source.next_logical_line_id,
            visible.main.next_logical_line_id
        );
        assert_ne!(
            &source.row_metadata[viewport..],
            visible.main.row_metadata.as_slice()
        );
        assert_eq!(visible.main.viewport_top(), 0);
        visible.main.validate().expect("visible-only clone screen");
        visible
    }

    #[test]
    fn blank_frame_has_exact_canonical_structure() {
        let terminal = Terminal::new(2, 1).expect("terminal");
        let frame = export(&terminal).expect("render frame");

        assert_eq!(&frame[..8], MAGIC_V1);
        assert_eq!(u16_at(&frame, 8), 1);
        assert_eq!(u16_at(&frame, 12), HEADER_BYTES as u16);
        assert_eq!(u16_at(&frame, 14), DIRECTORY_ENTRY_BYTES as u16);
        assert_eq!(u16_at(&frame, 16), SECTION_COUNT as u16);
        assert_eq!(u32_at(&frame, 20) as usize, frame.len());
        assert_eq!(u32_at(&frame, 40), 2);
        assert_eq!(u32_at(&frame, 44), 1);
        assert_eq!(u32_at(&frame, 56), 1);
        assert_eq!(u32_at(&frame, 60), 1);
        assert_eq!(u32_at(&frame, 64), HEADER_BYTES as u32);
        assert_eq!(u32_at(&frame, 88), crate::crc32c::checksum(&frame[96..]));
        assert_eq!(u32_at(&frame, 92), crate::crc32c::checksum(&frame[..92]));

        for index in 0..SECTION_COUNT {
            let base = HEADER_BYTES + index * DIRECTORY_ENTRY_BYTES;
            assert_eq!(u32_at(&frame, base), index as u32 + 1);
            assert_eq!(u16_at(&frame, base + 4), 1);
            assert_eq!(u16_at(&frame, base + 6), 1);
            assert_eq!(u32_at(&frame, base + 8) % 8, 0);
        }
        assert_eq!(
            u32_at(&frame, HEADER_BYTES + 4 * DIRECTORY_ENTRY_BYTES + 20),
            0
        );

        let (grapheme, grapheme_len) = section(&frame, 0);
        assert_eq!(grapheme, ENVELOPE_BYTES);
        assert_eq!(grapheme_len, 9);
        assert_eq!(u32_at(&frame, grapheme), 8);
        assert_eq!(u16_at(&frame, grapheme + 4), 1);
        assert_eq!(frame[grapheme + 6], 1);
        assert_eq!(frame[grapheme + 8], b' ');

        let (rows, rows_len) = section(&frame, 4);
        assert_eq!(rows_len, 8 + 8 + 2 * 12);
        assert_eq!(u32_at(&frame, rows), 8);
        assert_eq!(u32_at(&frame, rows + 4), 12);
        assert_eq!(u32_at(&frame, rows + 8), 0);
        assert_eq!(u32_at(&frame, rows + 12), 0);
        for cell in 0..2 {
            let record = rows + 16 + cell * 12;
            assert_eq!(u32_at(&frame, record), 1);
            assert_eq!(u32_at(&frame, record + 4), 0);
            assert_eq!(u32_at(&frame, record + 8), 0);
        }
    }

    #[test]
    fn render_frame_v1_is_byte_stable_and_v2_reuses_its_exact_payload() {
        let mut terminal = Terminal::new(4, 2).expect("terminal");
        let baseline = export(&terminal).expect("V1 baseline");
        let baseline_v2 = export_v2(&terminal).expect("V2 baseline");
        terminal.synchronized_output_deadline_micros = Some(1_000_000);
        assert_eq!(export(&terminal), Err(TerminalError::PresentationDeferred));
        assert_eq!(
            export_v2(&terminal),
            Err(TerminalError::PresentationDeferred)
        );
        terminal.synchronized_output_deadline_micros = None;
        assert_eq!(export(&terminal).expect("inactive V1"), baseline);
        assert_eq!(export_v2(&terminal).expect("inactive V2"), baseline_v2);
        let mut styled = terminal.clone();
        styled.cursor_shape = CursorShape::Bar;
        styled.cursor_blinking = false;
        let styled_v1 = export(&styled).expect("styled V1");
        assert_eq!(styled_v1, baseline);
        assert_eq!(u32_at(&styled_v1, 68), 0);
        assert_eq!(u64_at(&styled_v1, 72), REQUIRED_FEATURES_V1);

        let styled_v2 = export_v2(&styled).expect("styled V2");
        assert_eq!(&styled_v2[HEADER_BYTES..], &styled_v1[HEADER_BYTES..]);
        assert_eq!(u32_at(&styled_v2, 88), u32_at(&styled_v1, 88));
        assert_eq!(styled_v2.len(), styled_v1.len());
        assert_eq!(MAX_CANONICAL_RENDER_FRAME_BYTES, 13_395_208);
    }

    #[test]
    fn render_frame_v2_encodes_all_cursor_shape_and_blink_combinations() {
        for shape in [CursorShape::Block, CursorShape::Underline, CursorShape::Bar] {
            for blinking in [false, true] {
                let mut terminal = Terminal::new(4, 2).expect("terminal");
                terminal.cursor_shape = shape;
                terminal.cursor_blinking = blinking;
                let frame = export_v2(&terminal).expect("V2 frame");
                assert_eq!(&frame[..8], MAGIC_V2);
                assert_eq!(u16_at(&frame, 8), RENDER_FRAME_V2_FORMAT_VERSION as u16);
                assert_eq!(u16_at(&frame, 10), 0);
                assert_eq!(u16_at(&frame, 12), HEADER_BYTES as u16);
                assert_eq!(frame[68], shape as u8);
                assert_eq!(frame[69], u8::from(blinking));
                assert_eq!(u16_at(&frame, 70), 0);
                assert_eq!(u64_at(&frame, 72), REQUIRED_FEATURES_V2);
                assert_eq!(u64_at(&frame, 80), 0);
                assert_eq!(u32_at(&frame, 88), crate::crc32c::checksum(&frame[96..]));
                assert_eq!(u32_at(&frame, 92), crate::crc32c::checksum(&frame[..92]));
                validate_v2_header(&frame).expect("strict V2 header");
            }
        }
    }

    #[test]
    fn render_frame_versions_mutually_reject_and_v2_header_is_strict() {
        let mut terminal = Terminal::new(4, 2).expect("terminal");
        terminal.cursor_shape = CursorShape::Underline;
        let v1 = export(&terminal).expect("V1 frame");
        let v2 = export_v2(&terminal).expect("V2 frame");
        assert_eq!(
            validate_v2_header(&v1),
            Err(TerminalError::IncompatibleSnapshot)
        );
        assert_eq!(
            validate_v1_header(&v2),
            Err(TerminalError::IncompatibleSnapshot)
        );

        for (name, offset, value) in [
            ("shape", 68_usize, 3_u8),
            ("blinking", 69, 2),
            ("reserved", 70, 1),
        ] {
            let mut malformed = v2.clone();
            malformed[offset] = value;
            refresh_crc(&mut malformed);
            assert_eq!(
                validate_v2_header(&malformed),
                Err(TerminalError::MalformedSnapshot),
                "{name}"
            );
        }

        for (name, mutate) in [
            ("magic", (0_usize, 1_u64, 1_usize)),
            ("major", (8, 1, 2)),
            ("minor", (10, 1, 2)),
            ("features", (72, REQUIRED_FEATURES_V2 | 0x40, 8)),
        ] {
            let mut incompatible = v2.clone();
            let (offset, value, width) = mutate;
            incompatible[offset..offset + width].copy_from_slice(&value.to_le_bytes()[..width]);
            refresh_crc(&mut incompatible);
            assert_eq!(
                validate_v2_header(&incompatible),
                Err(TerminalError::IncompatibleSnapshot),
                "{name}"
            );
        }

        let mut total = v2.clone();
        total[20..24].copy_from_slice(&0_u32.to_le_bytes());
        refresh_crc(&mut total);
        assert_eq!(
            validate_v2_header(&total),
            Err(TerminalError::MalformedSnapshot)
        );

        let mut cursor_flags = v2.clone();
        cursor_flags[56..60].copy_from_slice(&4_u32.to_le_bytes());
        refresh_crc(&mut cursor_flags);
        assert_eq!(
            validate_v2_header(&cursor_flags),
            Err(TerminalError::MalformedSnapshot)
        );

        let mut payload_crc = v2.clone();
        *payload_crc.last_mut().expect("frame payload") ^= 1;
        assert_eq!(
            validate_v2_header(&payload_crc),
            Err(TerminalError::MalformedSnapshot)
        );

        let mut header_crc = v2;
        header_crc[92] ^= 1;
        assert_eq!(
            validate_v2_header(&header_crc),
            Err(TerminalError::MalformedSnapshot)
        );
    }

    #[test]
    fn render_frame_v3_header_and_section_versions_are_exact_and_strict() {
        let mut terminal = Terminal::new(4, 2).expect("terminal");
        terminal.cursor_shape = CursorShape::Bar;
        terminal.cursor_blinking = false;
        let frame = export_v3(&terminal).expect("V3 frame");

        assert_eq!(&frame[..8], MAGIC_V3);
        assert_eq!(u16_at(&frame, 8), RENDER_FRAME_V3_FORMAT_VERSION as u16);
        assert_eq!(u16_at(&frame, 10), 0);
        assert_eq!(u16_at(&frame, 12), HEADER_BYTES as u16);
        assert_eq!(u16_at(&frame, 14), DIRECTORY_ENTRY_BYTES as u16);
        assert_eq!(u16_at(&frame, 16), SECTION_COUNT as u16);
        assert_eq!(u32_at(&frame, 20) as usize, frame.len());
        assert_eq!(frame[68], CursorShape::Bar as u8);
        assert_eq!(frame[69], 0);
        assert_eq!(u16_at(&frame, 70), 0);
        assert_eq!(u64_at(&frame, 72), REQUIRED_FEATURES_V3);
        assert_eq!(u64_at(&frame, 80), 0);
        assert_eq!(u32_at(&frame, 88), crate::crc32c::checksum(&frame[96..]));
        assert_eq!(u32_at(&frame, 92), crate::crc32c::checksum(&frame[..92]));
        assert_eq!(MAX_CANONICAL_RENDER_FRAME_BYTES, 13_395_208);
        assert_eq!(MAX_RENDER_FRAME_V3_BYTES, 16 * 1024 * 1024);

        for index in 0..SECTION_COUNT {
            let entry = HEADER_BYTES + index * DIRECTORY_ENTRY_BYTES;
            assert_eq!(u16_at(&frame, entry + 4), if index == 1 { 2 } else { 1 });
        }
        validate_v3_header(&frame).expect("strict V3 header and renditions");

        for (name, offset, value) in [
            ("shape", 68_usize, 3_u8),
            ("blinking", 69, 2),
            ("reserved", 70, 1),
        ] {
            let mut malformed = frame.clone();
            malformed[offset] = value;
            refresh_crc(&mut malformed);
            assert_eq!(
                validate_v3_header(&malformed),
                Err(TerminalError::MalformedSnapshot),
                "{name}"
            );
        }

        for (name, offset, value, width) in [
            ("magic", 0_usize, 1_u64, 1_usize),
            ("major", 8, 2, 2),
            ("minor", 10, 1, 2),
            ("features", 72, REQUIRED_FEATURES_V3 | 0x80, 8),
        ] {
            let mut incompatible = frame.clone();
            incompatible[offset..offset + width].copy_from_slice(&value.to_le_bytes()[..width]);
            refresh_crc(&mut incompatible);
            assert_eq!(
                validate_v3_header(&incompatible),
                Err(TerminalError::IncompatibleSnapshot),
                "{name}"
            );
        }
    }

    #[test]
    fn legacy_frames_drop_extended_sgr_and_downgrade_every_underline_style() {
        let styles = [
            UnderlineStyle::None,
            UnderlineStyle::Single,
            UnderlineStyle::Double,
            UnderlineStyle::Curly,
            UnderlineStyle::Dotted,
            UnderlineStyle::Dashed,
        ];
        let mut terminal = Terminal::new(styles.len() as u32, 1).expect("terminal");
        for (index, style) in styles.into_iter().enumerate() {
            terminal.main.cells[index].attributes = Attributes {
                faint: index % 2 == 0,
                underline_style: style,
                overline: index % 3 == 0,
                underline_color: Color::Rgb(index as u8, 17, 29),
                ..Attributes::default()
            };
        }

        let mut downgraded = terminal.clone();
        for cell in &mut downgraded.main.cells {
            cell.attributes.faint = false;
            cell.attributes.overline = false;
            cell.attributes.underline_color = Color::Default;
            if cell.attributes.underline_style != UnderlineStyle::None {
                cell.attributes.underline_style = UnderlineStyle::Single;
            }
        }

        let v1 = export(&terminal).expect("extended source V1");
        assert_eq!(v1, export(&downgraded).expect("explicitly downgraded V1"));
        let v2 = export_v2(&terminal).expect("extended source V2");
        assert_eq!(
            v2,
            export_v2(&downgraded).expect("explicitly downgraded V2")
        );
        assert_eq!(&v1[HEADER_BYTES..], &v2[HEADER_BYTES..]);

        let rendition_entry = HEADER_BYTES + DIRECTORY_ENTRY_BYTES;
        assert_eq!(u32_at(&v1, rendition_entry + 16), 2);
        let (renditions, length) = section(&v1, 1);
        assert_eq!(length, 2 * RENDITION_RECORD_BYTES);
        assert_eq!(u32_at(&v1, renditions), 0);
        assert_eq!(u32_at(&v1, renditions + RENDITION_RECORD_BYTES), 1 << 4);
        assert!(
            v1[renditions + 12..renditions + RENDITION_RECORD_BYTES]
                .iter()
                .all(|&byte| byte == 0)
        );
        assert!(
            v1[renditions + RENDITION_RECORD_BYTES + 12..renditions + 2 * RENDITION_RECORD_BYTES]
                .iter()
                .all(|&byte| byte == 0)
        );

        let (rows, _) = section(&v1, 4);
        let cells = rows + ROWS_CELLS_PREFIX_BYTES + ROW_RECORD_BYTES;
        for index in 0..styles.len() {
            let expected_id = u32::from(index != 0);
            assert_eq!(
                u32_at(&v1, cells + index * CELL_RECORD_BYTES + 4),
                expected_id
            );
        }
    }

    #[test]
    fn render_frame_v3_encodes_all_styles_flags_and_color_kinds() {
        for (style, raw) in [
            (UnderlineStyle::None, 0_u32),
            (UnderlineStyle::Single, 1),
            (UnderlineStyle::Double, 2),
            (UnderlineStyle::Curly, 3),
            (UnderlineStyle::Dotted, 4),
            (UnderlineStyle::Dashed, 5),
        ] {
            let attributes = Attributes {
                underline_style: style,
                ..Attributes::default()
            };
            let record = rendition_bytes(attributes, FrameVersion::V3);
            assert_eq!(
                u32_at(&record, 0),
                (raw << RENDITION_UNDERLINE_STYLE_SHIFT)
                    | if raw == 0 {
                        0
                    } else {
                        RENDITION_UNDERLINE_PRESENT
                    }
            );
        }

        let rich = Attributes {
            bold: true,
            faint: true,
            italic: true,
            underline_style: UnderlineStyle::Curly,
            blink: true,
            inverse: true,
            conceal: true,
            strikethrough: true,
            overline: true,
            foreground: Color::Indexed(231),
            background: Color::Rgb(1, 2, 3),
            underline_color: Color::Rgb(4, 5, 6),
        };
        let record = rendition_bytes(rich, FrameVersion::V3);
        assert_eq!(u32_at(&record, 0), 0x07ff);
        assert_eq!(u32_at(&record, 4), 0x0100_00e7);
        assert_eq!(u32_at(&record, 8), 0x0201_0203);
        assert_eq!(u32_at(&record, 12), 0x0204_0506);

        for (color, packed) in [
            (Color::Default, 0_u32),
            (Color::Indexed(255), 0x0100_00ff),
            (Color::Rgb(7, 8, 9), 0x0207_0809),
        ] {
            let attributes = Attributes {
                foreground: color,
                background: color,
                underline_color: color,
                ..Attributes::default()
            };
            let record = rendition_bytes(attributes, FrameVersion::V3);
            assert_eq!(u32_at(&record, 4), packed);
            assert_eq!(u32_at(&record, 8), packed);
            assert_eq!(u32_at(&record, 12), packed);
        }
    }

    #[test]
    fn render_frame_v3_sorts_deduplicates_and_remaps_renditions() {
        let mut terminal = Terminal::new(4, 1).expect("terminal");
        let attributes = [
            Attributes {
                overline: true,
                underline_color: Color::Indexed(3),
                ..Attributes::default()
            },
            Attributes::default(),
            Attributes {
                faint: true,
                underline_style: UnderlineStyle::Dotted,
                ..Attributes::default()
            },
            Attributes {
                overline: true,
                underline_color: Color::Indexed(3),
                ..Attributes::default()
            },
        ];
        for (cell, attributes) in terminal.main.cells.iter_mut().zip(attributes) {
            cell.attributes = attributes;
        }

        let mut expected = attributes
            .map(|attributes| rendition_bytes(attributes, FrameVersion::V3))
            .to_vec();
        expected.sort_unstable();
        expected.dedup();
        let frame = export_v3(&terminal).expect("V3 frame");
        let rendition_entry = HEADER_BYTES + DIRECTORY_ENTRY_BYTES;
        assert_eq!(
            u32_at(&frame, rendition_entry + 16) as usize,
            expected.len()
        );
        let (renditions, length) = section(&frame, 1);
        assert_eq!(length, expected.len() * RENDITION_RECORD_BYTES);
        for (index, record) in expected.iter().enumerate() {
            assert_eq!(
                &frame[renditions + index * RENDITION_RECORD_BYTES
                    ..renditions + (index + 1) * RENDITION_RECORD_BYTES],
                record
            );
        }

        let (rows, _) = section(&frame, 4);
        let cells = rows + ROWS_CELLS_PREFIX_BYTES + ROW_RECORD_BYTES;
        for (index, attributes) in attributes.into_iter().enumerate() {
            let record = rendition_bytes(attributes, FrameVersion::V3);
            let expected_id = expected.binary_search(&record).expect("rendition ID") as u32;
            assert_eq!(
                u32_at(&frame, cells + index * CELL_RECORD_BYTES + 4),
                expected_id
            );
        }
    }

    #[test]
    fn render_frame_v3_rejects_invalid_renditions_and_section_versions() {
        let frame = export_v3(&Terminal::new(2, 1).expect("terminal")).expect("V3 frame");
        let (renditions, _) = section(&frame, 1);

        for (name, flags) in [
            ("reserved flag", 1_u32 << 12),
            ("unknown style", RENDITION_UNDERLINE_PRESENT | (6 << 9)),
            ("style without presence", 1_u32 << 9),
            ("presence without style", RENDITION_UNDERLINE_PRESENT),
        ] {
            let mut malformed = frame.clone();
            malformed[renditions..renditions + 4].copy_from_slice(&flags.to_le_bytes());
            refresh_crc(&mut malformed);
            assert_eq!(
                validate_v3_header(&malformed),
                Err(TerminalError::MalformedSnapshot),
                "{name}"
            );
        }

        for (name, field, value) in [
            ("noncanonical default", 4_usize, 0x0000_0001_u32),
            ("noncanonical indexed", 4, 0x0100_0100),
            ("unknown foreground", 4, 0x0300_0000),
            ("unknown background", 8, 0x0300_0000),
            ("unknown underline", 12, 0x0300_0000),
        ] {
            let mut malformed = frame.clone();
            malformed[renditions + field..renditions + field + 4]
                .copy_from_slice(&value.to_le_bytes());
            refresh_crc(&mut malformed);
            assert_eq!(
                validate_v3_header(&malformed),
                Err(TerminalError::MalformedSnapshot),
                "{name}"
            );
        }

        for (name, entry, version) in [
            ("rendition V1", 1_usize, 1_u16),
            ("rendition future", 1, 3),
            ("grapheme V2", 0, 2),
        ] {
            let mut incompatible = frame.clone();
            let offset = HEADER_BYTES + entry * DIRECTORY_ENTRY_BYTES + 4;
            incompatible[offset..offset + 2].copy_from_slice(&version.to_le_bytes());
            refresh_crc(&mut incompatible);
            assert_eq!(
                validate_v3_header(&incompatible),
                Err(TerminalError::IncompatibleSnapshot),
                "{name}"
            );
        }

        let mut invalid_section_flags = frame.clone();
        let flags = HEADER_BYTES + DIRECTORY_ENTRY_BYTES + 6;
        invalid_section_flags[flags..flags + 2].copy_from_slice(&2_u16.to_le_bytes());
        refresh_crc(&mut invalid_section_flags);
        assert_eq!(
            validate_v3_header(&invalid_section_flags),
            Err(TerminalError::MalformedSnapshot)
        );
    }

    #[test]
    fn render_frame_versions_are_isolated_from_v3() {
        let terminal = Terminal::new(2, 1).expect("terminal");
        let v1 = export(&terminal).expect("V1 frame");
        let v2 = export_v2(&terminal).expect("V2 frame");
        let v3 = export_v3(&terminal).expect("V3 frame");

        assert_eq!(
            validate_v3_header(&v1),
            Err(TerminalError::IncompatibleSnapshot)
        );
        assert_eq!(
            validate_v3_header(&v2),
            Err(TerminalError::IncompatibleSnapshot)
        );
        assert_eq!(
            validate_v1_header(&v3),
            Err(TerminalError::IncompatibleSnapshot)
        );
        assert_eq!(
            validate_v2_header(&v3),
            Err(TerminalError::IncompatibleSnapshot)
        );
    }

    #[test]
    fn rich_frame_is_deterministic_and_preserves_visible_references() {
        let mut terminal = Terminal::new(4, 1).expect("terminal");
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b]2;title\x07\x1b]4;3;#123456\x07\x1b]8;;https://example.test\x07\x1b[1;4;38;2;1;2;3mA\xe7\x95\x8c",
            })
            .expect("output");

        let first = export(&terminal).expect("first frame");
        let second = export(&terminal).expect("second frame");
        assert_eq!(first, second);
        assert_eq!(u32_at(&first, 20) as usize % 8, 0);
        assert_eq!(u32_at(&first, 24), 1);
        assert_eq!(u32_at(&first, 32), 1);

        let (palette, _) = section(&first, 2);
        assert_eq!(u32_at(&first, palette + 3 * 4), 0x0012_3456);

        let (links, links_len) = section(&first, 3);
        assert_eq!(u32_at(&first, links), 1);
        assert_eq!(u32_at(&first, links + 4), 16);
        let uri_len = u32_at(&first, links + 8) as usize;
        assert_eq!(links_len, 16 + uri_len);
        assert_eq!(
            &first[links + 16..links + 16 + uri_len],
            b"https://example.test"
        );

        let (title, title_len) = section(&first, 5);
        assert_eq!(&first[title..title + title_len], b"title");

        let (rows, _) = section(&first, 4);
        let first_cell = rows + ROWS_CELLS_PREFIX_BYTES + ROW_RECORD_BYTES;
        assert_eq!(u32_at(&first, first_cell + 8) >> 2, 1);
        assert_eq!(u32_at(&first, first_cell + CELL_RECORD_BYTES + 8) >> 2, 1);
        assert_eq!(u32_at(&first, first_cell + 2 * CELL_RECORD_BYTES), 0);
    }

    #[test]
    fn frame_flags_and_rendition_flags_preserve_global_reverse_and_new_sgr_attributes() {
        let mut terminal = Terminal::new(2, 1).expect("terminal");
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[?5h\x1b[?1049h\x1b[8;9mX",
            })
            .expect("global reverse, alternate, and styled cell");
        let frame = export(&terminal).expect("render frame");

        assert_eq!(u16_at(&frame, 18), 0x0003);
        let (renditions, length) = section(&frame, 1);
        assert_eq!(length, 2 * RENDITION_RECORD_BYTES);
        assert_eq!(u32_at(&frame, renditions), 0);
        assert_eq!(u32_at(&frame, renditions + RENDITION_RECORD_BYTES), 0x60);
        assert!(
            frame
                [renditions + RENDITION_RECORD_BYTES + 12..renditions + 2 * RENDITION_RECORD_BYTES]
                .iter()
                .all(|&byte| byte == 0)
        );

        terminal
            .apply_event(Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[?5l",
            })
            .expect("disable global reverse");
        let reset = export(&terminal).expect("reset render frame");
        assert_eq!(u16_at(&reset, 18), 0x0001);
    }

    #[test]
    fn hidden_history_tables_rows_and_cells_do_not_change_visible_frame_bytes() {
        let terminal = terminal_with_hidden_history();
        let visible_only = clone_without_history_prefix(&terminal);
        let hidden_cell = &terminal.main.cells[0];
        let hidden_rendition = rendition_bytes(hidden_cell.attributes, FrameVersion::V1);
        assert_eq!(
            (hidden_cell.character, hidden_cell.combining.as_str()),
            ('Q', &HISTORY_ONLY_GRAPHEME['Q'.len_utf8()..])
        );
        assert!(hidden_cell.hyperlink.is_some());

        let graphemes = collect_graphemes(&terminal.main).expect("visible grapheme table");
        assert!(graphemes.iter().all(|grapheme| {
            grapheme.character != hidden_cell.character
                || grapheme.combining != hidden_cell.combining
        }));
        let renditions =
            collect_renditions(&terminal.main, FrameVersion::V1).expect("visible rendition table");
        assert_eq!(
            renditions,
            [rendition_bytes(Attributes::default(), FrameVersion::V1)]
        );
        assert!(!renditions.contains(&hidden_rendition));
        assert!(
            collect_visible_hyperlinks(&terminal, &terminal.main)
                .expect("visible hyperlink table")
                .is_empty()
        );

        let frame = export(&terminal).expect("history-backed visible frame");
        let visible_frame = export(&visible_only).expect("visible-only frame");
        assert_eq!(frame, visible_frame);
        assert!(
            !frame
                .windows(HISTORY_ONLY_GRAPHEME.len())
                .any(|window| window == HISTORY_ONLY_GRAPHEME.as_bytes())
        );
        assert!(
            !frame
                .windows(HISTORY_ONLY_URI.len())
                .any(|window| window == HISTORY_ONLY_URI.as_bytes())
        );

        let rows_directory = HEADER_BYTES + 4 * DIRECTORY_ENTRY_BYTES;
        assert_eq!(u32_at(&frame, rows_directory + 16), 6);
        let (_, rows_length) = section(&frame, 4);
        assert_eq!(
            rows_length,
            ROWS_CELLS_PREFIX_BYTES + 2 * ROW_RECORD_BYTES + 6 * CELL_RECORD_BYTES
        );
        let (_, links_length) = section(&frame, 3);
        assert_eq!(links_length, 0);
        let links_directory = HEADER_BYTES + 3 * DIRECTORY_ENTRY_BYTES;
        assert_eq!(u32_at(&frame, links_directory + 16), 0);
    }

    #[test]
    fn hidden_history_hyperlink_reference_is_not_validated_as_visible_state() {
        let mut terminal = terminal_with_hidden_history();
        terminal.main.cells[0].hyperlink = Some(HyperlinkRef::new(
            u32::try_from(MAX_HYPERLINK_ENTRIES + 1).expect("bounded hyperlink limit"),
        ));
        terminal
            .main
            .validate()
            .expect("hidden reference is structurally valid screen state");
        let visible_only = clone_without_history_prefix(&terminal);

        assert_eq!(
            export(&terminal).expect("hidden reference is ignored"),
            export(&visible_only).expect("visible-only frame")
        );
    }

    #[test]
    fn partial_parser_and_utf8_boundaries_are_not_eligible() {
        let mut parser = Terminal::new(2, 1).expect("terminal");
        parser
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[",
            })
            .expect("partial CSI");
        assert_eq!(export(&parser), Err(TerminalError::SnapshotNotEligible));

        let mut utf8 = Terminal::new(2, 1).expect("terminal");
        utf8.apply_event(Event::Output {
            source_sequence: 0,
            bytes: &[0xe7],
        })
        .expect("partial UTF-8");
        assert_eq!(export(&utf8), Err(TerminalError::SnapshotNotEligible));
    }
}
