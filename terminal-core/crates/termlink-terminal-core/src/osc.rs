//! Deterministic, bounded persistent state for the supported OSC subset.
//!
//! This module records only terminal state. Queries are classified in
//! [`OscEffect`] and deliberately do not synthesize replies or replay any
//! external side effect.

use std::fmt::{Display, Formatter};

pub const MAX_OSC_PAYLOAD_BYTES: usize = 64 * 1024;
pub const MAX_TITLE_BYTES: usize = 4 * 1024;
pub const MAX_TITLE_STACK_DEPTH: usize = 10;
pub const MAX_HYPERLINK_URI_BYTES: usize = 4 * 1024;
pub const MAX_HYPERLINK_PARAMS_BYTES: usize = 1024;
pub const MAX_HYPERLINK_ID_BYTES: usize = 256;
pub const MAX_HYPERLINK_PARAMS: usize = 16;
pub const MAX_HYPERLINK_ENTRIES: usize = 1024;
pub const MAX_HYPERLINK_TOTAL_BYTES: usize = 256 * 1024;

const MAX_PALETTE_OPERATIONS: usize = 256;
const PALETTE_SIZE: usize = 256;

const OSC_CODEC_MAGIC: &[u8; 8] = b"TLOSCV1\0";
const OSC_CODEC_VERSION: u16 = 2;
const OSC_CODEC_LEGACY_VERSION: u16 = 1;
const LEGACY_MAX_TITLE_STACK_DEPTH: usize = 32;
const OSC_CODEC_HEADER_BYTES: usize = 12;
const OSC_CODEC_COLOR_BYTES: usize = 4;
const OSC_CODEC_LENGTH_BYTES: usize = 4;
const OSC_CODEC_STACK_HEADER_BYTES: usize = 4;
const OSC_CODEC_STACK_SLOT_BYTES: usize = 4;
const OSC_CODEC_HYPERLINK_HEADER_BYTES: usize = 16;
const OSC_CODEC_HYPERLINK_RECORD_BYTES: usize = 16;
const OSC_CODEC_PARAM_RECORD_BYTES: usize = 8;

/// Exact maximum length of a canonical encoded [`OscState`].
///
/// The bound combines all independently capped title fields and stacks with
/// the hyperlink table's entry, parameter, and stored-byte maxima.
pub(crate) const MAX_OSC_SNAPSHOT_V2_BYTES: usize = OSC_CODEC_HEADER_BYTES
    + (2 * PALETTE_SIZE * OSC_CODEC_COLOR_BYTES)
    + (6 * OSC_CODEC_COLOR_BYTES)
    + (2 * (OSC_CODEC_LENGTH_BYTES + MAX_TITLE_BYTES))
    + OSC_CODEC_STACK_HEADER_BYTES
    + (MAX_TITLE_STACK_DEPTH
        * (OSC_CODEC_STACK_SLOT_BYTES + 2 * (OSC_CODEC_LENGTH_BYTES + MAX_TITLE_BYTES)))
    + OSC_CODEC_HYPERLINK_HEADER_BYTES
    + (MAX_HYPERLINK_ENTRIES * OSC_CODEC_HYPERLINK_RECORD_BYTES)
    + (MAX_HYPERLINK_ENTRIES * MAX_HYPERLINK_PARAMS * OSC_CODEC_PARAM_RECORD_BYTES)
    + MAX_HYPERLINK_TOTAL_BYTES;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct OscColor {
    pub red: u8,
    pub green: u8,
    pub blue: u8,
}

impl OscColor {
    #[must_use]
    pub const fn new(red: u8, green: u8, blue: u8) -> Self {
        Self { red, green, blue }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct OscBaseline {
    pub palette: [Option<OscColor>; PALETTE_SIZE],
    pub foreground: Option<OscColor>,
    pub background: Option<OscColor>,
    pub cursor: Option<OscColor>,
}

impl Default for OscBaseline {
    fn default() -> Self {
        Self {
            palette: [None; PALETTE_SIZE],
            foreground: None,
            background: None,
            cursor: None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OscLimit {
    PayloadBytes,
    TitleBytes,
    TitleStackDepth,
    PaletteOperations,
    HyperlinkUriBytes,
    HyperlinkParamsBytes,
    HyperlinkIdBytes,
    HyperlinkParams,
    HyperlinkEntries,
    HyperlinkTotalBytes,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum OscError {
    LimitExceeded { limit: OscLimit, maximum: usize },
    InvalidUtf8,
    InvalidText,
    InvalidColor,
    InvalidPaletteIndex,
    InvalidTitleStackSelector,
    InvalidTitleStackSlot,
    MalformedPayload(&'static str),
    AllocationFailed,
}

impl Display for OscError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::LimitExceeded { limit, maximum } => {
                write!(
                    formatter,
                    "OSC limit {limit:?} exceeded (maximum {maximum})"
                )
            }
            Self::InvalidUtf8 => formatter.write_str("OSC payload is not valid UTF-8"),
            Self::InvalidText => formatter.write_str("OSC text contains a control character"),
            Self::InvalidColor => formatter.write_str("OSC color specification is invalid"),
            Self::InvalidPaletteIndex => formatter.write_str("OSC palette index is invalid"),
            Self::InvalidTitleStackSelector => {
                formatter.write_str("title-stack target must be 0, 1, or 2")
            }
            Self::InvalidTitleStackSlot => {
                formatter.write_str("title-stack slot must be in the range 0..=10")
            }
            Self::MalformedPayload(reason) => write!(formatter, "malformed OSC payload: {reason}"),
            Self::AllocationFailed => formatter.write_str("OSC state allocation failed"),
        }
    }
}

impl std::error::Error for OscError {}

/// Failure from the crate-internal OSC snapshot codec.
///
/// Importers can map [`Self::Malformed`] and [`Self::LimitExceeded`]
/// deterministically to their public malformed-snapshot classification while
/// retaining a distinct allocation failure for export/import plumbing.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum OscCodecError {
    Malformed,
    LimitExceeded,
    AllocationFailed,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TitleTarget {
    /// Xterm `Pt=0` (and an omitted `Pt`) addresses both stored titles.
    Both,
    /// Xterm `Pt=1` addresses only the icon title.
    Icon,
    /// Xterm `Pt=2` addresses only the window title.
    Window,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TitleOperation {
    /// OSC 0, 1, or 2 replaced a current title.
    Set,
    /// CSI 22 saved titles, either by pushing or by writing a direct slot.
    Push,
    /// CSI 23 restored titles, either by popping or by reading a direct slot.
    Pop,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DefaultColorSlot {
    Foreground,
    Background,
    Cursor,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PaletteMask([u64; 4]);

impl PaletteMask {
    #[must_use]
    pub fn contains(self, index: u8) -> bool {
        let word = usize::from(index) / 64;
        let bit = u32::from(index) % 64;
        self.0[word] & (1_u64 << bit) != 0
    }

    #[must_use]
    pub fn is_empty(self) -> bool {
        self.0 == [0; 4]
    }

    #[must_use]
    pub fn len(self) -> u32 {
        self.0.iter().map(|word| word.count_ones()).sum()
    }

    fn insert(&mut self, index: u8) {
        let word = usize::from(index) / 64;
        let bit = u32::from(index) % 64;
        self.0[word] |= 1_u64 << bit;
    }
}

/// A one-based table reference that is never reassigned during an
/// [`OscState`]'s lifetime, including after the link is closed.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct HyperlinkRef(u32);

impl HyperlinkRef {
    #[must_use]
    pub const fn get(self) -> u32 {
        self.0
    }

    pub(crate) const fn new(value: u32) -> Self {
        debug_assert!(value != 0);
        Self(value)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HyperlinkParam {
    key: String,
    value: String,
}

impl HyperlinkParam {
    #[must_use]
    pub fn key(&self) -> &str {
        &self.key
    }

    #[must_use]
    pub fn value(&self) -> &str {
        &self.value
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Hyperlink {
    reference: HyperlinkRef,
    uri: String,
    params: Vec<HyperlinkParam>,
    stored_bytes: usize,
}

impl Hyperlink {
    #[must_use]
    pub const fn reference(&self) -> HyperlinkRef {
        self.reference
    }

    #[must_use]
    pub fn uri(&self) -> &str {
        &self.uri
    }

    #[must_use]
    pub fn params(&self) -> &[HyperlinkParam] {
        &self.params
    }

    #[must_use]
    pub fn id(&self) -> Option<&str> {
        self.params
            .iter()
            .find(|parameter| parameter.key == "id")
            .map(|parameter| parameter.value.as_str())
    }

    #[must_use]
    pub const fn stored_bytes(&self) -> usize {
        self.stored_bytes
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OscQuery {
    Palette(PaletteMask),
    DefaultColor(DefaultColorSlot),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OscEffect {
    Title {
        operation: TitleOperation,
        target: TitleTarget,
    },
    PaletteSet {
        updated: PaletteMask,
        queried: PaletteMask,
    },
    PaletteReset {
        reset: PaletteMask,
    },
    DefaultColorSet {
        slot: DefaultColorSlot,
    },
    DefaultColorReset {
        slot: DefaultColorSlot,
    },
    Query(OscQuery),
    HyperlinkOpened {
        link: HyperlinkRef,
        reused: bool,
    },
    HyperlinkClosed {
        previous: Option<HyperlinkRef>,
    },
    UnknownSelector {
        selector: u16,
    },
}

/// One color query observed while applying an OSC payload in wire order.
///
/// A missing color is retained explicitly so semantic dispatch can consume a
/// valid query without inventing a renderer- or platform-dependent fallback.
#[allow(dead_code)] // Consumed by the host-effect dispatch layer added separately.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum OscColorQueryResult {
    Palette {
        index: u8,
        color: Option<OscColor>,
    },
    DefaultColor {
        slot: DefaultColorSlot,
        color: Option<OscColor>,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PaletteOperation {
    Set { index: u8, color: OscColor },
    Query { index: u8 },
}

struct PalettePlan {
    operations: [Option<PaletteOperation>; MAX_PALETTE_OPERATIONS],
    operation_count: usize,
    query_count: usize,
    updated: PaletteMask,
    queried: PaletteMask,
}

impl PalettePlan {
    fn new() -> Self {
        Self {
            operations: [None; MAX_PALETTE_OPERATIONS],
            operation_count: 0,
            query_count: 0,
            updated: PaletteMask::default(),
            queried: PaletteMask::default(),
        }
    }

    fn push(&mut self, operation: PaletteOperation) -> Result<(), OscError> {
        check_limit(
            self.operation_count + 1,
            MAX_PALETTE_OPERATIONS,
            OscLimit::PaletteOperations,
        )?;
        match operation {
            PaletteOperation::Set { index, .. } => self.updated.insert(index),
            PaletteOperation::Query { index } => {
                self.queried.insert(index);
                self.query_count += 1;
            }
        }
        self.operations[self.operation_count] = Some(operation);
        self.operation_count += 1;
        Ok(())
    }

    fn operations(&self) -> impl Iterator<Item = PaletteOperation> + '_ {
        self.operations[..self.operation_count]
            .iter()
            .map(|operation| operation.expect("palette plan slots are initialized"))
    }

    fn effect(&self) -> OscEffect {
        if self.updated.is_empty() {
            OscEffect::Query(OscQuery::Palette(self.queried))
        } else {
            OscEffect::PaletteSet {
                updated: self.updated,
                queried: self.queried,
            }
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct TitleStackSlot {
    icon: Option<String>,
    window: Option<String>,
}

/// Persistent OSC state.
///
/// CSI 22/23 title saves use ten records and one shared LIFO position. A
/// record can hold an icon title, a window title, or both. Direct slots 1
/// through 10 do not move the LIFO position; slot zero performs the ordinary
/// push/pop operation, and popping an empty stack is a no-op. Hyperlinks are
/// interned by the full canonical tuple of sorted parameters and URI. An `id`
/// parameter participates in that tuple but is not treated as a process-global
/// identity by itself.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OscState {
    baseline: OscBaseline,
    palette: [Option<OscColor>; PALETTE_SIZE],
    foreground: Option<OscColor>,
    background: Option<OscColor>,
    cursor: Option<OscColor>,
    icon_title: String,
    window_title: String,
    title_stack: [TitleStackSlot; MAX_TITLE_STACK_DEPTH],
    title_stack_position: usize,
    hyperlinks: Vec<Hyperlink>,
    current_hyperlink: Option<HyperlinkRef>,
    hyperlink_total_bytes: usize,
}

impl Default for OscState {
    fn default() -> Self {
        Self::new(OscBaseline::default())
    }
}

impl OscState {
    #[must_use]
    pub fn new(baseline: OscBaseline) -> Self {
        Self {
            baseline,
            palette: baseline.palette,
            foreground: baseline.foreground,
            background: baseline.background,
            cursor: baseline.cursor,
            icon_title: String::new(),
            window_title: String::new(),
            title_stack: std::array::from_fn(|_| TitleStackSlot::default()),
            title_stack_position: 0,
            hyperlinks: Vec::new(),
            current_hyperlink: None,
            hyperlink_total_bytes: 0,
        }
    }

    #[must_use]
    pub const fn baseline(&self) -> &OscBaseline {
        &self.baseline
    }

    #[must_use]
    pub fn palette(&self, index: u8) -> Option<OscColor> {
        self.palette[usize::from(index)]
    }

    #[must_use]
    pub const fn foreground(&self) -> Option<OscColor> {
        self.foreground
    }

    #[must_use]
    pub const fn background(&self) -> Option<OscColor> {
        self.background
    }

    #[must_use]
    pub const fn cursor_color(&self) -> Option<OscColor> {
        self.cursor
    }

    #[must_use]
    pub fn icon_title(&self) -> &str {
        &self.icon_title
    }

    #[must_use]
    pub fn window_title(&self) -> &str {
        &self.window_title
    }

    #[must_use]
    pub const fn current_hyperlink(&self) -> Option<HyperlinkRef> {
        self.current_hyperlink
    }

    #[must_use]
    pub fn hyperlink(&self, reference: HyperlinkRef) -> Option<&Hyperlink> {
        let index = usize::try_from(reference.0).ok()?.checked_sub(1)?;
        self.hyperlinks.get(index)
    }

    #[must_use]
    pub fn hyperlink_count(&self) -> usize {
        self.hyperlinks.len()
    }

    #[must_use]
    pub const fn hyperlink_total_bytes(&self) -> usize {
        self.hyperlink_total_bytes
    }

    /// Encodes this state as the canonical, length-bounded OSC section used by
    /// Snapshot V2.
    pub(crate) fn encode_snapshot_v2(&self) -> Result<Vec<u8>, OscCodecError> {
        let encoded_len = self.snapshot_v2_encoded_len()?;
        let legacy_title_stack = self.has_legacy_codec_title_stack();
        let mut writer = OscCodecWriter::with_capacity(encoded_len)?;
        writer.bytes(OSC_CODEC_MAGIC);
        writer.u16(if legacy_title_stack {
            OSC_CODEC_LEGACY_VERSION
        } else {
            OSC_CODEC_VERSION
        });
        writer.u16(0);
        encode_codec_palette(&mut writer, &self.baseline.palette);
        encode_codec_palette(&mut writer, &self.palette);
        for color in [
            self.baseline.foreground,
            self.baseline.background,
            self.baseline.cursor,
            self.foreground,
            self.background,
            self.cursor,
        ] {
            encode_codec_color(&mut writer, color);
        }
        encode_codec_text(&mut writer, &self.icon_title);
        encode_codec_text(&mut writer, &self.window_title);
        if legacy_title_stack {
            writer.bytes(&[0; 2 * OSC_CODEC_STACK_HEADER_BYTES]);
        } else {
            encode_codec_title_stack(&mut writer, &self.title_stack, self.title_stack_position);
        }
        writer.u32(codec_u32(self.hyperlinks.len())?);
        writer.u32(self.current_hyperlink.map_or(0, HyperlinkRef::get));
        writer.u32(codec_u32(self.hyperlink_total_bytes)?);
        writer.u32(0);
        for link in &self.hyperlinks {
            writer.u32(link.reference.get());
            writer.u32(codec_u32(link.stored_bytes)?);
            writer.u32(codec_u32(link.uri.len())?);
            writer.u16(codec_u16(link.params.len())?);
            writer.u16(0);
            writer.bytes(link.uri.as_bytes());
            for parameter in &link.params {
                writer.u32(codec_u32(parameter.key.len())?);
                writer.u32(codec_u32(parameter.value.len())?);
                writer.bytes(parameter.key.as_bytes());
                writer.bytes(parameter.value.as_bytes());
            }
        }
        debug_assert_eq!(writer.len(), encoded_len);
        Ok(writer.finish())
    }

    /// Decodes one complete canonical OSC section.
    ///
    /// The first pass is allocation-free. It validates the full byte region,
    /// including canonical ordering and cross-record references, before the
    /// second pass performs any persistent allocation.
    pub(crate) fn decode_snapshot_v2(bytes: &[u8]) -> Result<Self, OscCodecError> {
        preflight_osc_snapshot_v2(bytes)?;
        decode_osc_snapshot_v2(bytes)
    }

    pub(crate) fn snapshot_v2_hyperlink_count(bytes: &[u8]) -> Result<usize, OscCodecError> {
        preflight_osc_snapshot_v2(bytes)
    }

    fn snapshot_v2_encoded_len(&self) -> Result<usize, OscCodecError> {
        validate_codec_text(self.icon_title.as_bytes(), MAX_TITLE_BYTES)?;
        validate_codec_text(self.window_title.as_bytes(), MAX_TITLE_BYTES)?;
        validate_codec_title_stack(&self.title_stack, self.title_stack_position)?;
        if self.hyperlinks.len() > MAX_HYPERLINK_ENTRIES {
            return Err(OscCodecError::LimitExceeded);
        }
        validate_codec_reference(self.current_hyperlink, self.hyperlinks.len())?;

        let mut hyperlink_total_bytes = 0_usize;
        let mut encoded_len = OSC_CODEC_HEADER_BYTES
            + (2 * PALETTE_SIZE * OSC_CODEC_COLOR_BYTES)
            + (6 * OSC_CODEC_COLOR_BYTES)
            + (2 * OSC_CODEC_LENGTH_BYTES)
            + self.icon_title.len()
            + self.window_title.len();
        if self.has_legacy_codec_title_stack() {
            encoded_len = codec_add(encoded_len, 2 * OSC_CODEC_STACK_HEADER_BYTES)?;
        } else {
            encoded_len = codec_add(
                encoded_len,
                OSC_CODEC_STACK_HEADER_BYTES + (MAX_TITLE_STACK_DEPTH * OSC_CODEC_STACK_SLOT_BYTES),
            )?;
            for slot in &self.title_stack {
                for title in [&slot.icon, &slot.window].into_iter().flatten() {
                    encoded_len = codec_add(encoded_len, OSC_CODEC_LENGTH_BYTES + title.len())?;
                }
            }
        }
        encoded_len = codec_add(encoded_len, OSC_CODEC_HYPERLINK_HEADER_BYTES)?;

        for (index, link) in self.hyperlinks.iter().enumerate() {
            let expected_reference = u32::try_from(index + 1).expect("hyperlink table is bounded");
            if link.reference.get() != expected_reference {
                return Err(OscCodecError::Malformed);
            }
            validate_codec_link(link)?;
            if self.hyperlinks[..index]
                .iter()
                .any(|previous| previous.uri == link.uri && previous.params == link.params)
            {
                return Err(OscCodecError::Malformed);
            }
            hyperlink_total_bytes = codec_add(hyperlink_total_bytes, link.stored_bytes)?;
            encoded_len = codec_add(
                encoded_len,
                OSC_CODEC_HYPERLINK_RECORD_BYTES
                    + link.uri.len()
                    + (link.params.len() * OSC_CODEC_PARAM_RECORD_BYTES),
            )?;
            for parameter in &link.params {
                encoded_len = codec_add(encoded_len, parameter.key.len())?;
                encoded_len = codec_add(encoded_len, parameter.value.len())?;
            }
        }
        if hyperlink_total_bytes != self.hyperlink_total_bytes {
            return Err(OscCodecError::Malformed);
        }
        if hyperlink_total_bytes > MAX_HYPERLINK_TOTAL_BYTES
            || encoded_len > MAX_OSC_SNAPSHOT_V2_BYTES
        {
            return Err(OscCodecError::LimitExceeded);
        }
        Ok(encoded_len)
    }

    fn has_legacy_codec_title_stack(&self) -> bool {
        self.title_stack_position == 0
            && self
                .title_stack
                .iter()
                .all(|slot| slot.icon.is_none() && slot.window.is_none())
    }

    pub fn apply(&mut self, selector: u16, payload: &[u8]) -> Result<OscEffect, OscError> {
        check_limit(payload.len(), MAX_OSC_PAYLOAD_BYTES, OscLimit::PayloadBytes)?;
        self.apply_after_payload_limit(selector, payload)
    }

    /// Applies one OSC payload and returns its color queries in their original
    /// textual order. Palette mutations and observations are evaluated in that
    /// order, but the complete payload is parsed and all result capacity is
    /// reserved before state changes, preserving operation atomicity.
    #[allow(dead_code)] // Consumed by the host-effect dispatch layer added separately.
    pub(crate) fn apply_with_color_queries(
        &mut self,
        selector: u16,
        payload: &[u8],
    ) -> Result<(OscEffect, Vec<OscColorQueryResult>), OscError> {
        check_limit(payload.len(), MAX_OSC_PAYLOAD_BYTES, OscLimit::PayloadBytes)?;
        match selector {
            4 => self.apply_palette_with_color_queries(payload),
            10 => self.apply_default_color_with_query(DefaultColorSlot::Foreground, payload),
            11 => self.apply_default_color_with_query(DefaultColorSlot::Background, payload),
            12 => self.apply_default_color_with_query(DefaultColorSlot::Cursor, payload),
            _ => Ok((
                self.apply_after_payload_limit(selector, payload)?,
                Vec::new(),
            )),
        }
    }

    fn apply_after_payload_limit(
        &mut self,
        selector: u16,
        payload: &[u8],
    ) -> Result<OscEffect, OscError> {
        match selector {
            0 => self.set_title(TitleTarget::Both, payload),
            1 => self.set_title(TitleTarget::Icon, payload),
            2 => self.set_title(TitleTarget::Window, payload),
            4 => self.apply_palette(payload),
            8 => self.apply_hyperlink(payload),
            10 => self.apply_default_color(DefaultColorSlot::Foreground, payload),
            11 => self.apply_default_color(DefaultColorSlot::Background, payload),
            12 => self.apply_default_color(DefaultColorSlot::Cursor, payload),
            104 => self.reset_palette(payload),
            110 => self.reset_default_color(DefaultColorSlot::Foreground, payload),
            111 => self.reset_default_color(DefaultColorSlot::Background, payload),
            112 => self.reset_default_color(DefaultColorSlot::Cursor, payload),
            _ => Ok(OscEffect::UnknownSelector { selector }),
        }
    }

    fn set_title(&mut self, target: TitleTarget, payload: &[u8]) -> Result<OscEffect, OscError> {
        let title = bounded_text(payload, MAX_TITLE_BYTES, OscLimit::TitleBytes)?;
        match target {
            TitleTarget::Both => {
                let icon = try_owned(title)?;
                let window = try_owned(title)?;
                self.icon_title = icon;
                self.window_title = window;
            }
            TitleTarget::Icon => self.icon_title = try_owned(title)?,
            TitleTarget::Window => self.window_title = try_owned(title)?,
        }
        Ok(OscEffect::Title {
            operation: TitleOperation::Set,
            target,
        })
    }

    /// Implements xterm's CSI 22 title save. Slot zero pushes one shared
    /// record; slots 1 through 10 replace that record without moving the LIFO
    /// position.
    pub(crate) fn save_titles(
        &mut self,
        target: TitleTarget,
        slot: u16,
    ) -> Result<OscEffect, OscError> {
        let direct_index = direct_title_slot(slot)?;
        let index = if let Some(index) = direct_index {
            index
        } else {
            check_limit(
                self.title_stack_position + 1,
                MAX_TITLE_STACK_DEPTH,
                OscLimit::TitleStackDepth,
            )?;
            self.title_stack_position
        };

        let record = TitleStackSlot {
            icon: if matches!(target, TitleTarget::Both | TitleTarget::Icon) {
                Some(try_owned(&self.icon_title)?)
            } else {
                None
            },
            window: if matches!(target, TitleTarget::Both | TitleTarget::Window) {
                Some(try_owned(&self.window_title)?)
            } else {
                None
            },
        };
        self.title_stack[index] = record;
        if direct_index.is_none() {
            self.title_stack_position += 1;
        }
        Ok(OscEffect::Title {
            operation: TitleOperation::Push,
            target,
        })
    }

    /// Implements xterm's CSI 23 title restore. Slot zero pops one shared
    /// record; slots 1 through 10 read that record without moving the LIFO
    /// position.
    pub(crate) fn restore_titles(
        &mut self,
        target: TitleTarget,
        slot: u16,
    ) -> Result<OscEffect, OscError> {
        let direct_index = direct_title_slot(slot)?;
        let (index, popped) = if let Some(index) = direct_index {
            (index, false)
        } else if self.title_stack_position == 0 {
            return Ok(OscEffect::Title {
                operation: TitleOperation::Pop,
                target,
            });
        } else {
            (self.title_stack_position - 1, true)
        };

        // Xterm fills a title missing from the selected record by scanning
        // older records. Clone every requested value before changing the
        // pointer or current titles so allocation failure is atomic.
        let icon = if matches!(target, TitleTarget::Both | TitleTarget::Icon) {
            self.saved_title_at_or_before(index, true)
                .map(try_owned)
                .transpose()?
        } else {
            None
        };
        let window = if matches!(target, TitleTarget::Both | TitleTarget::Window) {
            self.saved_title_at_or_before(index, false)
                .map(try_owned)
                .transpose()?
        } else {
            None
        };

        if popped {
            self.title_stack[index] = TitleStackSlot::default();
            self.title_stack_position -= 1;
        }
        if let Some(icon) = icon {
            self.icon_title = icon;
        }
        if let Some(window) = window {
            self.window_title = window;
        }
        Ok(OscEffect::Title {
            operation: TitleOperation::Pop,
            target,
        })
    }

    fn saved_title_at_or_before(&self, index: usize, icon: bool) -> Option<&str> {
        (0..MAX_TITLE_STACK_DEPTH).find_map(|distance| {
            let index = (index + MAX_TITLE_STACK_DEPTH - distance) % MAX_TITLE_STACK_DEPTH;
            let title = if icon {
                &self.title_stack[index].icon
            } else {
                &self.title_stack[index].window
            };
            title.as_deref()
        })
    }

    fn apply_palette(&mut self, payload: &[u8]) -> Result<OscEffect, OscError> {
        let plan = parse_palette_plan(payload)?;
        for operation in plan.operations() {
            if let PaletteOperation::Set { index, color } = operation {
                self.palette[usize::from(index)] = Some(color);
            }
        }
        Ok(plan.effect())
    }

    fn apply_palette_with_color_queries(
        &mut self,
        payload: &[u8],
    ) -> Result<(OscEffect, Vec<OscColorQueryResult>), OscError> {
        let plan = parse_palette_plan(payload)?;
        let mut queries = Vec::new();
        queries
            .try_reserve_exact(plan.query_count)
            .map_err(|_| OscError::AllocationFailed)?;

        for operation in plan.operations() {
            match operation {
                PaletteOperation::Set { index, color } => {
                    self.palette[usize::from(index)] = Some(color);
                }
                PaletteOperation::Query { index } => {
                    queries.push(OscColorQueryResult::Palette {
                        index,
                        color: self.palette[usize::from(index)],
                    });
                }
            }
        }
        Ok((plan.effect(), queries))
    }

    fn reset_palette(&mut self, payload: &[u8]) -> Result<OscEffect, OscError> {
        let mut reset = PaletteMask::default();
        if payload.is_empty() {
            for index in u8::MIN..=u8::MAX {
                reset.insert(index);
            }
        } else {
            let fields = payload.split(|byte| *byte == b';');
            let mut operation_count = 0;
            for field in fields {
                operation_count += 1;
                check_limit(
                    operation_count,
                    MAX_PALETTE_OPERATIONS,
                    OscLimit::PaletteOperations,
                )?;
                reset.insert(parse_palette_index(field)?);
            }
        }
        for index in u8::MIN..=u8::MAX {
            if reset.contains(index) {
                self.palette[usize::from(index)] = self.baseline.palette[usize::from(index)];
            }
        }
        Ok(OscEffect::PaletteReset { reset })
    }

    fn apply_default_color(
        &mut self,
        slot: DefaultColorSlot,
        payload: &[u8],
    ) -> Result<OscEffect, OscError> {
        if payload == b"?" {
            return Ok(OscEffect::Query(OscQuery::DefaultColor(slot)));
        }
        // OpenTUI restores xterm's default foreground/background/cursor
        // colors through OSC 10/11/12 with the exact `default` keyword.
        // Treat that spelling as the baseline-reset alias while keeping all
        // other non-color payloads fail-closed.
        if payload == b"default" {
            let baseline = match slot {
                DefaultColorSlot::Foreground => self.baseline.foreground,
                DefaultColorSlot::Background => self.baseline.background,
                DefaultColorSlot::Cursor => self.baseline.cursor,
            };
            *self.default_color_mut(slot) = baseline;
            return Ok(OscEffect::DefaultColorReset { slot });
        }
        let color = parse_color(payload)?;
        *self.default_color_mut(slot) = Some(color);
        Ok(OscEffect::DefaultColorSet { slot })
    }

    fn apply_default_color_with_query(
        &mut self,
        slot: DefaultColorSlot,
        payload: &[u8],
    ) -> Result<(OscEffect, Vec<OscColorQueryResult>), OscError> {
        let mut queries = Vec::new();
        if payload == b"?" {
            queries
                .try_reserve_exact(1)
                .map_err(|_| OscError::AllocationFailed)?;
        }
        let effect = self.apply_default_color(slot, payload)?;
        if payload == b"?" {
            let color = match slot {
                DefaultColorSlot::Foreground => self.foreground,
                DefaultColorSlot::Background => self.background,
                DefaultColorSlot::Cursor => self.cursor,
            };
            queries.push(OscColorQueryResult::DefaultColor { slot, color });
        }
        Ok((effect, queries))
    }

    fn reset_default_color(
        &mut self,
        slot: DefaultColorSlot,
        payload: &[u8],
    ) -> Result<OscEffect, OscError> {
        if !payload.is_empty() {
            return Err(OscError::MalformedPayload(
                "default-color reset requires an empty payload",
            ));
        }
        let baseline = match slot {
            DefaultColorSlot::Foreground => self.baseline.foreground,
            DefaultColorSlot::Background => self.baseline.background,
            DefaultColorSlot::Cursor => self.baseline.cursor,
        };
        *self.default_color_mut(slot) = baseline;
        Ok(OscEffect::DefaultColorReset { slot })
    }

    fn default_color_mut(&mut self, slot: DefaultColorSlot) -> &mut Option<OscColor> {
        match slot {
            DefaultColorSlot::Foreground => &mut self.foreground,
            DefaultColorSlot::Background => &mut self.background,
            DefaultColorSlot::Cursor => &mut self.cursor,
        }
    }

    fn apply_hyperlink(&mut self, payload: &[u8]) -> Result<OscEffect, OscError> {
        let (raw_params, raw_uri) = split_once(payload, b';')
            .ok_or(OscError::MalformedPayload("OSC 8 requires params;URI"))?;
        check_limit(
            raw_params.len(),
            MAX_HYPERLINK_PARAMS_BYTES,
            OscLimit::HyperlinkParamsBytes,
        )?;
        check_limit(
            raw_uri.len(),
            MAX_HYPERLINK_URI_BYTES,
            OscLimit::HyperlinkUriBytes,
        )?;

        if raw_uri.is_empty() {
            if !raw_params.is_empty() {
                return Err(OscError::MalformedPayload(
                    "OSC 8 close must use an empty params field",
                ));
            }
            let previous = self.current_hyperlink.take();
            return Ok(OscEffect::HyperlinkClosed { previous });
        }

        let uri_text = canonical_text(raw_uri)?;
        let uri = try_owned(uri_text)?;
        let params = parse_hyperlink_params(raw_params)?;
        let stored_bytes = hyperlink_stored_bytes(&uri, &params)?;

        if let Some(existing) = self
            .hyperlinks
            .iter()
            .find(|link| link.uri == uri && link.params == params)
        {
            self.current_hyperlink = Some(existing.reference);
            return Ok(OscEffect::HyperlinkOpened {
                link: existing.reference,
                reused: true,
            });
        }

        check_limit(
            self.hyperlinks.len() + 1,
            MAX_HYPERLINK_ENTRIES,
            OscLimit::HyperlinkEntries,
        )?;
        let new_total = self.hyperlink_total_bytes.checked_add(stored_bytes).ok_or(
            OscError::LimitExceeded {
                limit: OscLimit::HyperlinkTotalBytes,
                maximum: MAX_HYPERLINK_TOTAL_BYTES,
            },
        )?;
        check_limit(
            new_total,
            MAX_HYPERLINK_TOTAL_BYTES,
            OscLimit::HyperlinkTotalBytes,
        )?;
        self.hyperlinks
            .try_reserve(1)
            .map_err(|_| OscError::AllocationFailed)?;
        let reference = HyperlinkRef(
            u32::try_from(self.hyperlinks.len() + 1).expect("hyperlink table is bounded"),
        );
        self.hyperlinks.push(Hyperlink {
            reference,
            uri,
            params,
            stored_bytes,
        });
        self.current_hyperlink = Some(reference);
        self.hyperlink_total_bytes = new_total;
        Ok(OscEffect::HyperlinkOpened {
            link: reference,
            reused: false,
        })
    }
}

fn validate_codec_text(bytes: &[u8], maximum: usize) -> Result<&str, OscCodecError> {
    if bytes.len() > maximum {
        return Err(OscCodecError::LimitExceeded);
    }
    let text = std::str::from_utf8(bytes).map_err(|_| OscCodecError::Malformed)?;
    if text.chars().any(char::is_control) {
        return Err(OscCodecError::Malformed);
    }
    Ok(text)
}

fn validate_codec_title_stack(
    stack: &[TitleStackSlot; MAX_TITLE_STACK_DEPTH],
    position: usize,
) -> Result<(), OscCodecError> {
    if position > MAX_TITLE_STACK_DEPTH {
        return Err(OscCodecError::Malformed);
    }
    for (index, slot) in stack.iter().enumerate() {
        if index < position && slot.icon.is_none() && slot.window.is_none() {
            return Err(OscCodecError::Malformed);
        }
        for title in [&slot.icon, &slot.window].into_iter().flatten() {
            validate_codec_text(title.as_bytes(), MAX_TITLE_BYTES)?;
        }
    }
    Ok(())
}

fn validate_codec_reference(
    reference: Option<HyperlinkRef>,
    hyperlink_count: usize,
) -> Result<(), OscCodecError> {
    let Some(reference) = reference else {
        return Ok(());
    };
    let index = usize::try_from(reference.get()).map_err(|_| OscCodecError::Malformed)?;
    if index == 0 || index > hyperlink_count {
        return Err(OscCodecError::Malformed);
    }
    Ok(())
}

fn validate_codec_link(link: &Hyperlink) -> Result<(), OscCodecError> {
    if link.uri.is_empty() {
        return Err(OscCodecError::Malformed);
    }
    validate_codec_text(link.uri.as_bytes(), MAX_HYPERLINK_URI_BYTES)?;
    if link.params.len() > MAX_HYPERLINK_PARAMS {
        return Err(OscCodecError::LimitExceeded);
    }

    let mut previous_key: Option<&str> = None;
    let mut raw_params_bytes = 0_usize;
    let mut stored_bytes = link.uri.len();
    for (index, parameter) in link.params.iter().enumerate() {
        validate_codec_param_key(parameter.key.as_bytes())?;
        validate_codec_text(parameter.value.as_bytes(), MAX_HYPERLINK_PARAMS_BYTES)?;
        if previous_key.is_some_and(|previous| previous >= parameter.key.as_str()) {
            return Err(OscCodecError::Malformed);
        }
        if parameter.key == "id" {
            if parameter.value.is_empty() {
                return Err(OscCodecError::Malformed);
            }
            if parameter.value.len() > MAX_HYPERLINK_ID_BYTES {
                return Err(OscCodecError::LimitExceeded);
            }
        }
        if index != 0 {
            raw_params_bytes = codec_add(raw_params_bytes, 1)?;
        }
        raw_params_bytes = codec_add(raw_params_bytes, parameter.key.len())?;
        raw_params_bytes = codec_add(raw_params_bytes, 1)?;
        raw_params_bytes = codec_add(raw_params_bytes, parameter.value.len())?;
        stored_bytes = codec_add(stored_bytes, parameter.key.len())?;
        stored_bytes = codec_add(stored_bytes, parameter.value.len())?;
        previous_key = Some(&parameter.key);
    }
    if raw_params_bytes > MAX_HYPERLINK_PARAMS_BYTES || stored_bytes > MAX_HYPERLINK_TOTAL_BYTES {
        return Err(OscCodecError::LimitExceeded);
    }
    if link.stored_bytes != stored_bytes {
        return Err(OscCodecError::Malformed);
    }
    Ok(())
}

fn validate_codec_param_key(key: &[u8]) -> Result<(), OscCodecError> {
    if key.is_empty()
        || !key
            .iter()
            .all(|byte| byte.is_ascii_alphanumeric() || b"_.-".contains(byte))
    {
        return Err(OscCodecError::Malformed);
    }
    Ok(())
}

fn codec_add(left: usize, right: usize) -> Result<usize, OscCodecError> {
    left.checked_add(right).ok_or(OscCodecError::LimitExceeded)
}

fn codec_u16(value: usize) -> Result<u16, OscCodecError> {
    u16::try_from(value).map_err(|_| OscCodecError::LimitExceeded)
}

fn codec_u32(value: usize) -> Result<u32, OscCodecError> {
    u32::try_from(value).map_err(|_| OscCodecError::LimitExceeded)
}

fn codec_usize(value: u32) -> Result<usize, OscCodecError> {
    usize::try_from(value).map_err(|_| OscCodecError::LimitExceeded)
}

fn encode_codec_color(writer: &mut OscCodecWriter, color: Option<OscColor>) {
    match color {
        None => writer.bytes(&[0, 0, 0, 0]),
        Some(color) => writer.bytes(&[1, color.red, color.green, color.blue]),
    }
}

fn encode_codec_palette(writer: &mut OscCodecWriter, palette: &[Option<OscColor>; PALETTE_SIZE]) {
    for color in palette {
        encode_codec_color(writer, *color);
    }
}

fn encode_codec_text(writer: &mut OscCodecWriter, text: &str) {
    writer.u32(u32::try_from(text.len()).expect("validated OSC text length fits u32"));
    writer.bytes(text.as_bytes());
}

fn encode_codec_title_stack(
    writer: &mut OscCodecWriter,
    stack: &[TitleStackSlot; MAX_TITLE_STACK_DEPTH],
    position: usize,
) {
    writer.u16(u16::try_from(position).expect("validated OSC title stack position fits u16"));
    writer.u16(0);
    for slot in stack {
        let flags = u8::from(slot.icon.is_some()) | (u8::from(slot.window.is_some()) << 1);
        writer.bytes(&[flags, 0, 0, 0]);
        if let Some(title) = &slot.icon {
            encode_codec_text(writer, title);
        }
        if let Some(title) = &slot.window {
            encode_codec_text(writer, title);
        }
    }
}

fn preflight_osc_snapshot_v2(bytes: &[u8]) -> Result<usize, OscCodecError> {
    if bytes.len() > MAX_OSC_SNAPSHOT_V2_BYTES {
        return Err(OscCodecError::LimitExceeded);
    }
    let mut reader = OscCodecReader::new(bytes);
    if reader.take(OSC_CODEC_MAGIC.len())? != OSC_CODEC_MAGIC {
        return Err(OscCodecError::Malformed);
    }
    let version = reader.u16()?;
    if version != OSC_CODEC_VERSION && version != OSC_CODEC_LEGACY_VERSION {
        return Err(OscCodecError::Malformed);
    }
    require_codec_zero(reader.u16()?)?;
    preflight_codec_palette(&mut reader)?;
    preflight_codec_palette(&mut reader)?;
    for _ in 0..6 {
        preflight_codec_color(&mut reader)?;
    }
    preflight_codec_text(&mut reader, MAX_TITLE_BYTES)?;
    preflight_codec_text(&mut reader, MAX_TITLE_BYTES)?;
    if version == OSC_CODEC_VERSION {
        if !preflight_codec_title_stack(&mut reader)? {
            return Err(OscCodecError::Malformed);
        }
    } else {
        let icon_count = preflight_legacy_codec_title_stack(&mut reader)?;
        let window_count = preflight_legacy_codec_title_stack(&mut reader)?;
        // V1 represented two independent stacks and did not preserve their
        // operation order. Only an empty legacy stack has an exact shared-
        // pointer representation; reject non-empty V1 state rather than
        // silently assigning it different future semantics.
        if icon_count != 0 || window_count != 0 {
            return Err(OscCodecError::Malformed);
        }
    }

    let hyperlink_count = codec_usize(reader.u32()?)?;
    if hyperlink_count > MAX_HYPERLINK_ENTRIES {
        return Err(OscCodecError::LimitExceeded);
    }
    let current_hyperlink = reader.u32()?;
    if current_hyperlink != 0
        && codec_usize(current_hyperlink)?
            .checked_sub(1)
            .is_none_or(|index| index >= hyperlink_count)
    {
        return Err(OscCodecError::Malformed);
    }
    let encoded_total_bytes = codec_usize(reader.u32()?)?;
    if encoded_total_bytes > MAX_HYPERLINK_TOTAL_BYTES {
        return Err(OscCodecError::LimitExceeded);
    }
    require_codec_zero(reader.u32()?)?;

    let mut canonical_tuples: [&[u8]; MAX_HYPERLINK_ENTRIES] = [&[]; MAX_HYPERLINK_ENTRIES];
    let mut computed_total_bytes = 0_usize;
    for index in 0..hyperlink_count {
        let expected_reference = u32::try_from(index + 1).expect("hyperlink table is bounded");
        let record = preflight_codec_hyperlink(&mut reader, expected_reference)?;
        computed_total_bytes = codec_add(computed_total_bytes, record.stored_bytes)?;
        if computed_total_bytes > MAX_HYPERLINK_TOTAL_BYTES {
            return Err(OscCodecError::LimitExceeded);
        }

        if canonical_tuples[..index].contains(&record.canonical_tuple) {
            return Err(OscCodecError::Malformed);
        }
        canonical_tuples[index] = record.canonical_tuple;
    }
    if computed_total_bytes != encoded_total_bytes || !reader.is_empty() {
        return Err(OscCodecError::Malformed);
    }
    Ok(hyperlink_count)
}

fn preflight_codec_palette(reader: &mut OscCodecReader<'_>) -> Result<(), OscCodecError> {
    for _ in 0..PALETTE_SIZE {
        preflight_codec_color(reader)?;
    }
    Ok(())
}

fn preflight_codec_color(reader: &mut OscCodecReader<'_>) -> Result<(), OscCodecError> {
    let tag = reader.u8()?;
    let components = reader.take(3)?;
    match tag {
        0 if components == [0, 0, 0] => Ok(()),
        1 => Ok(()),
        _ => Err(OscCodecError::Malformed),
    }
}

fn preflight_codec_text(
    reader: &mut OscCodecReader<'_>,
    maximum: usize,
) -> Result<(), OscCodecError> {
    let length = codec_usize(reader.u32()?)?;
    if length > maximum {
        return Err(OscCodecError::LimitExceeded);
    }
    validate_codec_text(reader.take(length)?, maximum)?;
    Ok(())
}

fn preflight_codec_title_stack(reader: &mut OscCodecReader<'_>) -> Result<bool, OscCodecError> {
    let position = usize::from(reader.u16()?);
    if position > MAX_TITLE_STACK_DEPTH {
        return Err(OscCodecError::Malformed);
    }
    require_codec_zero(reader.u16()?)?;
    let mut has_data = false;
    for index in 0..MAX_TITLE_STACK_DEPTH {
        let flags = reader.u8()?;
        if flags & !0b11 != 0 || reader.take(3)? != [0, 0, 0] {
            return Err(OscCodecError::Malformed);
        }
        if index < position && flags == 0 {
            return Err(OscCodecError::Malformed);
        }
        has_data |= flags != 0;
        if flags & 0b01 != 0 {
            preflight_codec_text(reader, MAX_TITLE_BYTES)?;
        }
        if flags & 0b10 != 0 {
            preflight_codec_text(reader, MAX_TITLE_BYTES)?;
        }
    }
    Ok(has_data)
}

fn preflight_legacy_codec_title_stack(
    reader: &mut OscCodecReader<'_>,
) -> Result<usize, OscCodecError> {
    let count = usize::from(reader.u16()?);
    if count > LEGACY_MAX_TITLE_STACK_DEPTH {
        return Err(OscCodecError::LimitExceeded);
    }
    require_codec_zero(reader.u16()?)?;
    for _ in 0..count {
        preflight_codec_text(reader, MAX_TITLE_BYTES)?;
    }
    Ok(count)
}

#[derive(Clone, Copy)]
struct PreflightHyperlink<'a> {
    canonical_tuple: &'a [u8],
    stored_bytes: usize,
}

fn preflight_codec_hyperlink<'a>(
    reader: &mut OscCodecReader<'a>,
    expected_reference: u32,
) -> Result<PreflightHyperlink<'a>, OscCodecError> {
    if reader.u32()? != expected_reference {
        return Err(OscCodecError::Malformed);
    }
    let canonical_start = reader.position();
    let stored_bytes = codec_usize(reader.u32()?)?;
    if stored_bytes > MAX_HYPERLINK_TOTAL_BYTES {
        return Err(OscCodecError::LimitExceeded);
    }
    let uri_len = codec_usize(reader.u32()?)?;
    if uri_len == 0 {
        return Err(OscCodecError::Malformed);
    }
    if uri_len > MAX_HYPERLINK_URI_BYTES {
        return Err(OscCodecError::LimitExceeded);
    }
    let param_count = usize::from(reader.u16()?);
    if param_count > MAX_HYPERLINK_PARAMS {
        return Err(OscCodecError::LimitExceeded);
    }
    require_codec_zero(reader.u16()?)?;
    validate_codec_text(reader.take(uri_len)?, MAX_HYPERLINK_URI_BYTES)?;

    let mut previous_key: Option<&[u8]> = None;
    let mut raw_params_bytes = 0_usize;
    let mut computed_stored_bytes = uri_len;
    for index in 0..param_count {
        let key_len = codec_usize(reader.u32()?)?;
        let value_len = codec_usize(reader.u32()?)?;
        let key = reader.take(key_len)?;
        let value = reader.take(value_len)?;
        validate_codec_param_key(key)?;
        validate_codec_text(value, MAX_HYPERLINK_PARAMS_BYTES)?;
        if previous_key.is_some_and(|previous| previous >= key) {
            return Err(OscCodecError::Malformed);
        }
        if key == b"id" {
            if value.is_empty() {
                return Err(OscCodecError::Malformed);
            }
            if value.len() > MAX_HYPERLINK_ID_BYTES {
                return Err(OscCodecError::LimitExceeded);
            }
        }
        if index != 0 {
            raw_params_bytes = codec_add(raw_params_bytes, 1)?;
        }
        raw_params_bytes = codec_add(raw_params_bytes, key.len())?;
        raw_params_bytes = codec_add(raw_params_bytes, 1)?;
        raw_params_bytes = codec_add(raw_params_bytes, value.len())?;
        computed_stored_bytes = codec_add(computed_stored_bytes, key.len())?;
        computed_stored_bytes = codec_add(computed_stored_bytes, value.len())?;
        if raw_params_bytes > MAX_HYPERLINK_PARAMS_BYTES
            || computed_stored_bytes > MAX_HYPERLINK_TOTAL_BYTES
        {
            return Err(OscCodecError::LimitExceeded);
        }
        previous_key = Some(key);
    }
    if computed_stored_bytes != stored_bytes {
        return Err(OscCodecError::Malformed);
    }
    let canonical_tuple = reader.region(canonical_start, reader.position())?;
    Ok(PreflightHyperlink {
        canonical_tuple,
        stored_bytes,
    })
}

fn decode_osc_snapshot_v2(bytes: &[u8]) -> Result<OscState, OscCodecError> {
    let mut reader = OscCodecReader::new(bytes);
    reader.take(OSC_CODEC_MAGIC.len())?;
    let version = reader.u16()?;
    reader.u16()?;
    let baseline_palette = decode_codec_palette(&mut reader)?;
    let palette = decode_codec_palette(&mut reader)?;
    let baseline_foreground = decode_codec_color(&mut reader)?;
    let baseline_background = decode_codec_color(&mut reader)?;
    let baseline_cursor = decode_codec_color(&mut reader)?;
    let foreground = decode_codec_color(&mut reader)?;
    let background = decode_codec_color(&mut reader)?;
    let cursor = decode_codec_color(&mut reader)?;
    let icon_title = decode_codec_text(&mut reader, MAX_TITLE_BYTES)?;
    let window_title = decode_codec_text(&mut reader, MAX_TITLE_BYTES)?;
    let (title_stack, title_stack_position) = if version == OSC_CODEC_VERSION {
        decode_codec_title_stack(&mut reader)?
    } else {
        // Preflight accepts legacy V1 only when both independent stacks are
        // empty, which is the sole lossless shared-stack conversion.
        reader.u16()?;
        reader.u16()?;
        reader.u16()?;
        reader.u16()?;
        (std::array::from_fn(|_| TitleStackSlot::default()), 0)
    };
    let hyperlink_count = codec_usize(reader.u32()?)?;
    let current_reference = reader.u32()?;
    let hyperlink_total_bytes = codec_usize(reader.u32()?)?;
    reader.u32()?;

    let mut hyperlinks = Vec::new();
    hyperlinks
        .try_reserve_exact(hyperlink_count)
        .map_err(|_| OscCodecError::AllocationFailed)?;
    for _ in 0..hyperlink_count {
        let reference = HyperlinkRef(reader.u32()?);
        let stored_bytes = codec_usize(reader.u32()?)?;
        let uri_len = codec_usize(reader.u32()?)?;
        let param_count = usize::from(reader.u16()?);
        reader.u16()?;
        let uri_bytes = reader.take(uri_len)?;
        let uri = codec_try_owned(validate_codec_text(uri_bytes, MAX_HYPERLINK_URI_BYTES)?)?;
        let mut params = Vec::new();
        params
            .try_reserve_exact(param_count)
            .map_err(|_| OscCodecError::AllocationFailed)?;
        for _ in 0..param_count {
            let key_len = codec_usize(reader.u32()?)?;
            let value_len = codec_usize(reader.u32()?)?;
            let key_bytes = reader.take(key_len)?;
            let value_bytes = reader.take(value_len)?;
            let key = codec_try_owned(
                std::str::from_utf8(key_bytes).map_err(|_| OscCodecError::Malformed)?,
            )?;
            let value = codec_try_owned(validate_codec_text(
                value_bytes,
                MAX_HYPERLINK_PARAMS_BYTES,
            )?)?;
            params.push(HyperlinkParam { key, value });
        }
        hyperlinks.push(Hyperlink {
            reference,
            uri,
            params,
            stored_bytes,
        });
    }
    debug_assert!(reader.is_empty());
    Ok(OscState {
        baseline: OscBaseline {
            palette: baseline_palette,
            foreground: baseline_foreground,
            background: baseline_background,
            cursor: baseline_cursor,
        },
        palette,
        foreground,
        background,
        cursor,
        icon_title,
        window_title,
        title_stack,
        title_stack_position,
        hyperlinks,
        current_hyperlink: (current_reference != 0).then_some(HyperlinkRef(current_reference)),
        hyperlink_total_bytes,
    })
}

fn decode_codec_palette(
    reader: &mut OscCodecReader<'_>,
) -> Result<[Option<OscColor>; PALETTE_SIZE], OscCodecError> {
    let mut palette = [None; PALETTE_SIZE];
    for color in &mut palette {
        *color = decode_codec_color(reader)?;
    }
    Ok(palette)
}

fn decode_codec_color(reader: &mut OscCodecReader<'_>) -> Result<Option<OscColor>, OscCodecError> {
    let tag = reader.u8()?;
    let components = reader.take(3)?;
    match tag {
        0 => Ok(None),
        1 => Ok(Some(OscColor::new(
            components[0],
            components[1],
            components[2],
        ))),
        _ => Err(OscCodecError::Malformed),
    }
}

fn decode_codec_text(
    reader: &mut OscCodecReader<'_>,
    maximum: usize,
) -> Result<String, OscCodecError> {
    let length = codec_usize(reader.u32()?)?;
    let text = validate_codec_text(reader.take(length)?, maximum)?;
    codec_try_owned(text)
}

fn decode_codec_title_stack(
    reader: &mut OscCodecReader<'_>,
) -> Result<([TitleStackSlot; MAX_TITLE_STACK_DEPTH], usize), OscCodecError> {
    let position = usize::from(reader.u16()?);
    reader.u16()?;
    let mut stack = std::array::from_fn(|_| TitleStackSlot::default());
    for slot in &mut stack {
        let flags = reader.u8()?;
        reader.take(3)?;
        if flags & 0b01 != 0 {
            slot.icon = Some(decode_codec_text(reader, MAX_TITLE_BYTES)?);
        }
        if flags & 0b10 != 0 {
            slot.window = Some(decode_codec_text(reader, MAX_TITLE_BYTES)?);
        }
    }
    Ok((stack, position))
}

fn codec_try_owned(text: &str) -> Result<String, OscCodecError> {
    let mut owned = String::new();
    owned
        .try_reserve_exact(text.len())
        .map_err(|_| OscCodecError::AllocationFailed)?;
    owned.push_str(text);
    Ok(owned)
}

fn require_codec_zero<T>(value: T) -> Result<(), OscCodecError>
where
    T: Default + PartialEq,
{
    if value == T::default() {
        Ok(())
    } else {
        Err(OscCodecError::Malformed)
    }
}

struct OscCodecWriter {
    bytes: Vec<u8>,
}

impl OscCodecWriter {
    fn with_capacity(capacity: usize) -> Result<Self, OscCodecError> {
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(capacity)
            .map_err(|_| OscCodecError::AllocationFailed)?;
        Ok(Self { bytes })
    }

    fn u16(&mut self, value: u16) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn u32(&mut self, value: u32) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn bytes(&mut self, values: &[u8]) {
        self.bytes.extend_from_slice(values);
    }

    fn len(&self) -> usize {
        self.bytes.len()
    }

    fn finish(self) -> Vec<u8> {
        self.bytes
    }
}

struct OscCodecReader<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> OscCodecReader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    fn take(&mut self, count: usize) -> Result<&'a [u8], OscCodecError> {
        let end = self
            .offset
            .checked_add(count)
            .ok_or(OscCodecError::Malformed)?;
        let value = self
            .bytes
            .get(self.offset..end)
            .ok_or(OscCodecError::Malformed)?;
        self.offset = end;
        Ok(value)
    }

    fn u8(&mut self) -> Result<u8, OscCodecError> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, OscCodecError> {
        let bytes = self.take(2)?;
        Ok(u16::from_le_bytes([bytes[0], bytes[1]]))
    }

    fn u32(&mut self) -> Result<u32, OscCodecError> {
        let bytes = self.take(4)?;
        Ok(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    }

    fn position(&self) -> usize {
        self.offset
    }

    fn region(&self, start: usize, end: usize) -> Result<&'a [u8], OscCodecError> {
        self.bytes.get(start..end).ok_or(OscCodecError::Malformed)
    }

    fn is_empty(&self) -> bool {
        self.offset == self.bytes.len()
    }
}

fn check_limit(value: usize, maximum: usize, limit: OscLimit) -> Result<(), OscError> {
    if value > maximum {
        Err(OscError::LimitExceeded { limit, maximum })
    } else {
        Ok(())
    }
}

fn canonical_text(bytes: &[u8]) -> Result<&str, OscError> {
    let text = std::str::from_utf8(bytes).map_err(|_| OscError::InvalidUtf8)?;
    if text.chars().any(char::is_control) {
        return Err(OscError::InvalidText);
    }
    Ok(text)
}

fn bounded_text(bytes: &[u8], maximum: usize, limit: OscLimit) -> Result<&str, OscError> {
    check_limit(bytes.len(), maximum, limit)?;
    canonical_text(bytes)
}

fn try_owned(text: &str) -> Result<String, OscError> {
    let mut owned = String::new();
    owned
        .try_reserve_exact(text.len())
        .map_err(|_| OscError::AllocationFailed)?;
    owned.push_str(text);
    Ok(owned)
}

fn direct_title_slot(slot: u16) -> Result<Option<usize>, OscError> {
    match slot {
        0 => Ok(None),
        1..=10 => Ok(Some(usize::from(slot - 1))),
        _ => Err(OscError::InvalidTitleStackSlot),
    }
}

fn parse_palette_plan(payload: &[u8]) -> Result<PalettePlan, OscError> {
    if payload.is_empty() {
        return Err(OscError::MalformedPayload(
            "OSC 4 requires index/spec pairs",
        ));
    }
    let mut plan = PalettePlan::new();
    let mut fields = payload.split(|byte| *byte == b';');
    while let Some(raw_index) = fields.next() {
        let raw_spec = fields.next().ok_or(OscError::MalformedPayload(
            "OSC 4 requires index/spec pairs",
        ))?;
        let index = parse_palette_index(raw_index)?;
        let operation = if raw_spec == b"?" {
            PaletteOperation::Query { index }
        } else {
            PaletteOperation::Set {
                index,
                color: parse_color(raw_spec)?,
            }
        };
        plan.push(operation)?;
    }
    Ok(plan)
}

fn parse_palette_index(bytes: &[u8]) -> Result<u8, OscError> {
    if bytes.is_empty() {
        return Err(OscError::InvalidPaletteIndex);
    }
    let mut value = 0_u16;
    for byte in bytes {
        if !byte.is_ascii_digit() {
            return Err(OscError::InvalidPaletteIndex);
        }
        value = value
            .checked_mul(10)
            .and_then(|current| current.checked_add(u16::from(*byte - b'0')))
            .ok_or(OscError::InvalidPaletteIndex)?;
        if value > u16::from(u8::MAX) {
            return Err(OscError::InvalidPaletteIndex);
        }
    }
    u8::try_from(value).map_err(|_| OscError::InvalidPaletteIndex)
}

fn parse_color(bytes: &[u8]) -> Result<OscColor, OscError> {
    match bytes {
        [b'#', red, green, blue] => Ok(OscColor::new(
            expand_nibble(hex_nibble(*red)?),
            expand_nibble(hex_nibble(*green)?),
            expand_nibble(hex_nibble(*blue)?),
        )),
        [
            b'#',
            red_high,
            red_low,
            green_high,
            green_low,
            blue_high,
            blue_low,
        ] => Ok(OscColor::new(
            hex_byte(*red_high, *red_low)?,
            hex_byte(*green_high, *green_low)?,
            hex_byte(*blue_high, *blue_low)?,
        )),
        [
            b'r',
            b'g',
            b'b',
            b':',
            red_high,
            red_low,
            b'/',
            green_high,
            green_low,
            b'/',
            blue_high,
            blue_low,
        ] => Ok(OscColor::new(
            hex_byte(*red_high, *red_low)?,
            hex_byte(*green_high, *green_low)?,
            hex_byte(*blue_high, *blue_low)?,
        )),
        _ => Err(OscError::InvalidColor),
    }
}

fn hex_nibble(value: u8) -> Result<u8, OscError> {
    match value {
        b'0'..=b'9' => Ok(value - b'0'),
        b'a'..=b'f' => Ok(value - b'a' + 10),
        b'A'..=b'F' => Ok(value - b'A' + 10),
        _ => Err(OscError::InvalidColor),
    }
}

fn hex_byte(high: u8, low: u8) -> Result<u8, OscError> {
    Ok((hex_nibble(high)? << 4) | hex_nibble(low)?)
}

const fn expand_nibble(value: u8) -> u8 {
    (value << 4) | value
}

fn split_once(bytes: &[u8], delimiter: u8) -> Option<(&[u8], &[u8])> {
    let index = bytes.iter().position(|byte| *byte == delimiter)?;
    Some((&bytes[..index], &bytes[index + 1..]))
}

fn parse_hyperlink_params(raw: &[u8]) -> Result<Vec<HyperlinkParam>, OscError> {
    if raw.is_empty() {
        return Ok(Vec::new());
    }
    let count = raw
        .split(|byte| *byte == b':')
        .filter(|parameter| *parameter != b"id=")
        .count();
    check_limit(count, MAX_HYPERLINK_PARAMS, OscLimit::HyperlinkParams)?;
    let mut params = Vec::new();
    params
        .try_reserve_exact(count)
        .map_err(|_| OscError::AllocationFailed)?;
    for raw_param in raw.split(|byte| *byte == b':') {
        let (raw_key, raw_value) = split_once(raw_param, b'=').ok_or(
            OscError::MalformedPayload("hyperlink parameter requires key=value"),
        )?;
        if raw_key.is_empty()
            || !raw_key
                .iter()
                .all(|byte| byte.is_ascii_alphanumeric() || b"_.-".contains(byte))
        {
            return Err(OscError::MalformedPayload(
                "invalid hyperlink parameter key",
            ));
        }
        let key_text = std::str::from_utf8(raw_key).map_err(|_| OscError::InvalidUtf8)?;
        let value_text = canonical_text(raw_value)?;
        if key_text == "id" {
            if value_text.is_empty() {
                continue;
            }
            check_limit(
                value_text.len(),
                MAX_HYPERLINK_ID_BYTES,
                OscLimit::HyperlinkIdBytes,
            )?;
        }
        params.push(HyperlinkParam {
            key: try_owned(key_text)?,
            value: try_owned(value_text)?,
        });
    }
    params.sort_by(|left, right| {
        left.key
            .cmp(&right.key)
            .then_with(|| left.value.cmp(&right.value))
    });
    if params.windows(2).any(|pair| pair[0].key == pair[1].key) {
        return Err(OscError::MalformedPayload(
            "duplicate hyperlink parameter key",
        ));
    }
    Ok(params)
}

fn hyperlink_stored_bytes(uri: &str, params: &[HyperlinkParam]) -> Result<usize, OscError> {
    params.iter().try_fold(uri.len(), |total, parameter| {
        total
            .checked_add(parameter.key.len())
            .and_then(|value| value.checked_add(parameter.value.len()))
            .ok_or(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkTotalBytes,
                maximum: MAX_HYPERLINK_TOTAL_BYTES,
            })
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ops::Range;

    const RED: OscColor = OscColor::new(0xff, 0, 0);
    const GREEN: OscColor = OscColor::new(0, 0xff, 0);
    const BLUE: OscColor = OscColor::new(0, 0, 0xff);

    #[derive(Clone, Debug)]
    struct CodecParamLayout {
        value_length: usize,
        key: Range<usize>,
        value: Range<usize>,
    }

    #[derive(Clone, Debug)]
    struct CodecLinkLayout {
        reference: usize,
        stored_bytes: usize,
        uri_length: usize,
        param_count: usize,
        reserved: usize,
        uri: Range<usize>,
        params: Vec<CodecParamLayout>,
        end: usize,
    }

    #[derive(Clone, Debug)]
    struct CodecLayout {
        icon_title_length: usize,
        icon_title: Range<usize>,
        title_stack_position: usize,
        title_stack_reserved: usize,
        title_stack_slots: Vec<usize>,
        hyperlink_count: usize,
        current_reference: usize,
        total_bytes: usize,
        hyperlink_reserved: usize,
        links: Vec<CodecLinkLayout>,
    }

    fn codec_u16_at(bytes: &[u8], offset: usize) -> u16 {
        u16::from_le_bytes([bytes[offset], bytes[offset + 1]])
    }

    fn codec_u32_at(bytes: &[u8], offset: usize) -> u32 {
        u32::from_le_bytes([
            bytes[offset],
            bytes[offset + 1],
            bytes[offset + 2],
            bytes[offset + 3],
        ])
    }

    fn set_codec_u16(bytes: &mut [u8], offset: usize, value: u16) {
        bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
    }

    fn set_codec_u32(bytes: &mut [u8], offset: usize, value: u32) {
        bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
    }

    fn skip_codec_text(bytes: &[u8], offset: &mut usize) -> Range<usize> {
        let length = usize::try_from(codec_u32_at(bytes, *offset)).expect("test length fits usize");
        *offset += OSC_CODEC_LENGTH_BYTES;
        let range = *offset..*offset + length;
        *offset = range.end;
        range
    }

    fn skip_codec_stack(bytes: &[u8], offset: &mut usize) {
        let count = usize::from(codec_u16_at(bytes, *offset));
        *offset += OSC_CODEC_STACK_HEADER_BYTES;
        for _ in 0..count {
            skip_codec_text(bytes, offset);
        }
    }

    fn codec_layout(bytes: &[u8]) -> CodecLayout {
        let mut offset = OSC_CODEC_HEADER_BYTES
            + (2 * PALETTE_SIZE * OSC_CODEC_COLOR_BYTES)
            + (6 * OSC_CODEC_COLOR_BYTES);
        let icon_title_length = offset;
        let icon_title = skip_codec_text(bytes, &mut offset);
        skip_codec_text(bytes, &mut offset);
        let title_stack_position = offset;
        let title_stack_reserved = offset + 2;
        let mut title_stack_slots = Vec::new();
        if codec_u16_at(bytes, 8) == OSC_CODEC_LEGACY_VERSION {
            skip_codec_stack(bytes, &mut offset);
            skip_codec_stack(bytes, &mut offset);
        } else {
            offset += OSC_CODEC_STACK_HEADER_BYTES;
            for _ in 0..MAX_TITLE_STACK_DEPTH {
                title_stack_slots.push(offset);
                let flags = bytes[offset];
                offset += OSC_CODEC_STACK_SLOT_BYTES;
                if flags & 0b01 != 0 {
                    skip_codec_text(bytes, &mut offset);
                }
                if flags & 0b10 != 0 {
                    skip_codec_text(bytes, &mut offset);
                }
            }
        }
        let hyperlink_count = offset;
        let count = usize::try_from(codec_u32_at(bytes, offset)).expect("test count fits usize");
        offset += 4;
        let current_reference = offset;
        offset += 4;
        let total_bytes = offset;
        offset += 4;
        let hyperlink_reserved = offset;
        offset += 4;
        let mut links = Vec::new();
        for _ in 0..count {
            let reference = offset;
            offset += 4;
            let stored_bytes = offset;
            offset += 4;
            let uri_length = offset;
            let uri_len =
                usize::try_from(codec_u32_at(bytes, offset)).expect("test URI length fits usize");
            offset += 4;
            let param_count = offset;
            let count = usize::from(codec_u16_at(bytes, offset));
            offset += 2;
            let reserved = offset;
            offset += 2;
            let uri = offset..offset + uri_len;
            offset = uri.end;
            let mut params = Vec::new();
            for _ in 0..count {
                let key_len = usize::try_from(codec_u32_at(bytes, offset))
                    .expect("test key length fits usize");
                offset += 4;
                let value_length = offset;
                let value_len = usize::try_from(codec_u32_at(bytes, offset))
                    .expect("test value length fits usize");
                offset += 4;
                let key = offset..offset + key_len;
                offset = key.end;
                let value = offset..offset + value_len;
                offset = value.end;
                params.push(CodecParamLayout {
                    value_length,
                    key,
                    value,
                });
            }
            links.push(CodecLinkLayout {
                reference,
                stored_bytes,
                uri_length,
                param_count,
                reserved,
                uri,
                params,
                end: offset,
            });
        }
        assert_eq!(offset, bytes.len());
        CodecLayout {
            icon_title_length,
            icon_title,
            title_stack_position,
            title_stack_reserved,
            title_stack_slots,
            hyperlink_count,
            current_reference,
            total_bytes,
            hyperlink_reserved,
            links,
        }
    }

    fn populated_codec_state() -> OscState {
        let mut baseline = OscBaseline {
            foreground: Some(GREEN),
            background: Some(BLUE),
            cursor: Some(RED),
            ..OscBaseline::default()
        };
        baseline.palette[1] = Some(BLUE);
        baseline.palette[200] = Some(GREEN);
        let mut state = OscState::new(baseline);
        state.apply(4, b"1;#f00;2;#0f0").expect("palette state");
        state.apply(10, b"#123456").expect("foreground state");
        state.apply(11, b"#654321").expect("background state");
        state.apply(12, b"#abcdef").expect("cursor state");
        state.apply(1, b"icon-base").expect("base icon title");
        state
            .save_titles(TitleTarget::Icon, 0)
            .expect("push icon title");
        state.apply(1, b"icon-current").expect("current icon title");
        state.apply(2, b"window-base").expect("base window title");
        state
            .save_titles(TitleTarget::Window, 0)
            .expect("push window title");
        state
            .apply(2, b"window-current")
            .expect("current window title");
        state
            .apply(8, b"id=first:a=1;https://one.test")
            .expect("first hyperlink");
        state
            .apply(8, b"z=2:id=second;https://two.test")
            .expect("second hyperlink");
        state
    }

    #[test]
    fn title_save_and_restore_use_one_shared_lifo_position() {
        let mut state = OscState::default();
        assert_eq!(
            state.apply(0, b"both"),
            Ok(OscEffect::Title {
                operation: TitleOperation::Set,
                target: TitleTarget::Both,
            })
        );
        assert_eq!((state.icon_title(), state.window_title()), ("both", "both"));

        state.save_titles(TitleTarget::Both, 0).expect("push both");
        state.apply(1, b"icon-next").expect("set icon");
        state.apply(2, b"window-next").expect("set window");
        state
            .restore_titles(TitleTarget::Icon, 0)
            .expect("pop icon");
        assert_eq!(
            (state.icon_title(), state.window_title()),
            ("both", "window-next")
        );
        assert_eq!(state.title_stack_position, 0);
        let before = state.clone();
        assert_eq!(
            state.restore_titles(TitleTarget::Window, 0),
            Ok(OscEffect::Title {
                operation: TitleOperation::Pop,
                target: TitleTarget::Window,
            })
        );
        assert_eq!(state, before);

        state.apply(0, b"base").expect("reset both titles");
        state
            .save_titles(TitleTarget::Icon, 0)
            .expect("push icon record");
        state.apply(1, b"icon-new").expect("change icon");
        state
            .save_titles(TitleTarget::Window, 0)
            .expect("push window record");
        state.apply(0, b"latest").expect("change both titles");
        state
            .restore_titles(TitleTarget::Both, 0)
            .expect("pop window record");
        assert_eq!((state.icon_title(), state.window_title()), ("base", "base"));
        assert_eq!(state.title_stack_position, 1);
    }

    #[test]
    fn title_bounds_and_stack_failure_are_atomic() {
        let mut state = OscState::default();
        let maximum = vec![b'x'; MAX_TITLE_BYTES];
        state.apply(2, &maximum).expect("title at cap");
        let before = state.clone();
        let too_long = vec![b'x'; MAX_TITLE_BYTES + 1];
        assert_eq!(
            state.apply(2, &too_long),
            Err(OscError::LimitExceeded {
                limit: OscLimit::TitleBytes,
                maximum: MAX_TITLE_BYTES,
            })
        );
        assert_eq!(state, before);

        for _ in 0..MAX_TITLE_STACK_DEPTH {
            state
                .save_titles(TitleTarget::Window, 0)
                .expect("bounded push");
        }
        let before = state.clone();
        assert_eq!(
            state.save_titles(TitleTarget::Window, 0),
            Err(OscError::LimitExceeded {
                limit: OscLimit::TitleStackDepth,
                maximum: MAX_TITLE_STACK_DEPTH,
            })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn direct_title_slots_one_and_ten_do_not_move_the_pointer() {
        let mut state = OscState::default();
        state.apply(0, b"slot-one").expect("set slot one titles");
        state
            .save_titles(TitleTarget::Both, 1)
            .expect("save slot one");
        state.apply(0, b"slot-ten").expect("set slot ten titles");
        state
            .save_titles(TitleTarget::Both, 10)
            .expect("save slot ten");
        assert_eq!(state.title_stack_position, 0);

        state.apply(0, b"changed").expect("change both titles");
        state
            .restore_titles(TitleTarget::Both, 1)
            .expect("restore slot one");
        assert_eq!(
            (state.icon_title(), state.window_title()),
            ("slot-one", "slot-one")
        );
        state
            .restore_titles(TitleTarget::Both, 10)
            .expect("restore slot ten");
        assert_eq!(
            (state.icon_title(), state.window_title()),
            ("slot-ten", "slot-ten")
        );
        assert_eq!(state.title_stack_position, 0);

        let before = state.clone();
        assert_eq!(
            state.save_titles(TitleTarget::Both, 11),
            Err(OscError::InvalidTitleStackSlot)
        );
        assert_eq!(state, before);
        assert_eq!(
            state.restore_titles(TitleTarget::Both, u16::MAX),
            Err(OscError::InvalidTitleStackSlot)
        );
        assert_eq!(state, before);
    }

    #[test]
    fn osc_22_and_23_do_not_mutate_title_stack_state() {
        let mut state = OscState::default();
        state.apply(0, b"title").expect("set titles");
        let before = state.clone();
        assert_eq!(
            state.apply(22, b"pointer-shape"),
            Ok(OscEffect::UnknownSelector { selector: 22 })
        );
        assert_eq!(state, before);
        assert_eq!(
            state.apply(23, b"opaque"),
            Ok(OscEffect::UnknownSelector { selector: 23 })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn palette_set_query_and_reset_use_the_baseline() {
        let mut baseline = OscBaseline::default();
        baseline.palette[1] = Some(BLUE);
        let mut state = OscState::new(baseline);
        let effect = state.apply(4, b"1;#f00;2;#0f0;3;?").expect("palette batch");
        let OscEffect::PaletteSet { updated, queried } = effect else {
            panic!("expected palette batch");
        };
        assert!(updated.contains(1));
        assert!(updated.contains(2));
        assert!(queried.contains(3));
        assert_eq!(state.palette(1), Some(RED));
        assert_eq!(state.palette(2), Some(GREEN));

        let before = state.clone();
        assert_eq!(
            state.apply(4, b"1;?;2;?"),
            Ok(OscEffect::Query(OscQuery::Palette({
                let mut mask = PaletteMask::default();
                mask.insert(1);
                mask.insert(2);
                mask
            })))
        );
        assert_eq!(state, before);

        state.apply(104, b"1").expect("single reset");
        assert_eq!(state.palette(1), Some(BLUE));
        assert_eq!(state.palette(2), Some(GREEN));
        state.apply(104, b"").expect("reset all");
        assert_eq!(state.palette(2), None);
    }

    #[test]
    fn ordered_palette_queries_preserve_duplicates_and_text_order() {
        let mut baseline = OscBaseline::default();
        baseline.palette[1] = Some(RED);
        baseline.palette[2] = Some(GREEN);
        let mut state = OscState::new(baseline);
        let before = state.clone();

        let (effect, queries) = state
            .apply_with_color_queries(4, b"2;?;1;?;2;?")
            .expect("ordered repeated palette queries");
        assert_eq!(
            queries,
            [
                OscColorQueryResult::Palette {
                    index: 2,
                    color: Some(GREEN),
                },
                OscColorQueryResult::Palette {
                    index: 1,
                    color: Some(RED),
                },
                OscColorQueryResult::Palette {
                    index: 2,
                    color: Some(GREEN),
                },
            ]
        );
        let OscEffect::Query(OscQuery::Palette(queried)) = effect else {
            panic!("expected the existing palette-query effect");
        };
        assert_eq!(queried.len(), 2);
        assert!(queried.contains(1));
        assert!(queried.contains(2));
        assert_eq!(state, before);
    }

    #[test]
    fn mixed_palette_sets_and_queries_observe_textual_state_transitions() {
        let mut baseline = OscBaseline::default();
        baseline.palette[1] = Some(BLUE);
        let mut state = OscState::new(baseline);

        let (effect, queries) = state
            .apply_with_color_queries(4, b"1;?;1;#f00;1;?;1;#0f0;1;?")
            .expect("interleaved palette operations");
        assert_eq!(
            queries,
            [
                OscColorQueryResult::Palette {
                    index: 1,
                    color: Some(BLUE),
                },
                OscColorQueryResult::Palette {
                    index: 1,
                    color: Some(RED),
                },
                OscColorQueryResult::Palette {
                    index: 1,
                    color: Some(GREEN),
                },
            ]
        );
        let OscEffect::PaletteSet { updated, queried } = effect else {
            panic!("expected the existing palette-set effect");
        };
        assert!(updated.contains(1));
        assert!(queried.contains(1));
        assert_eq!(state.palette(1), Some(GREEN));

        let before = state.clone();
        assert_eq!(
            state.apply_with_color_queries(4, b"1;#fff;2;bad"),
            Err(OscError::InvalidColor)
        );
        assert_eq!(state, before);
    }

    #[test]
    fn absent_palette_and_default_colors_are_reported_without_fallbacks() {
        let mut state = OscState::default();
        let before = state.clone();

        let (_, palette_queries) = state
            .apply_with_color_queries(4, b"7;?;7;?")
            .expect("missing repeated palette queries");
        assert_eq!(
            palette_queries,
            [
                OscColorQueryResult::Palette {
                    index: 7,
                    color: None,
                },
                OscColorQueryResult::Palette {
                    index: 7,
                    color: None,
                },
            ]
        );

        for (selector, slot) in [
            (10, DefaultColorSlot::Foreground),
            (11, DefaultColorSlot::Background),
            (12, DefaultColorSlot::Cursor),
        ] {
            let (effect, queries) = state
                .apply_with_color_queries(selector, b"?")
                .expect("missing default-color query");
            assert_eq!(effect, OscEffect::Query(OscQuery::DefaultColor(slot)));
            assert_eq!(
                queries,
                [OscColorQueryResult::DefaultColor { slot, color: None }]
            );
        }
        assert_eq!(state, before);
    }

    #[test]
    fn default_color_queries_return_current_values_and_sets_return_no_queries() {
        let baseline = OscBaseline {
            foreground: Some(BLUE),
            background: Some(GREEN),
            cursor: Some(RED),
            ..OscBaseline::default()
        };
        let mut state = OscState::new(baseline);

        for (selector, slot, color) in [
            (10, DefaultColorSlot::Foreground, BLUE),
            (11, DefaultColorSlot::Background, GREEN),
            (12, DefaultColorSlot::Cursor, RED),
        ] {
            let (_, queries) = state
                .apply_with_color_queries(selector, b"?")
                .expect("current default-color query");
            assert_eq!(
                queries,
                [OscColorQueryResult::DefaultColor {
                    slot,
                    color: Some(color),
                }]
            );
        }

        let (effect, queries) = state
            .apply_with_color_queries(10, b"#123456")
            .expect("default-color set");
        assert_eq!(
            effect,
            OscEffect::DefaultColorSet {
                slot: DefaultColorSlot::Foreground,
            }
        );
        assert!(queries.is_empty());
        assert_eq!(state.foreground(), Some(OscColor::new(0x12, 0x34, 0x56)));
    }

    #[test]
    fn palette_formats_and_atomic_parse_failure() {
        let mut state = OscState::default();
        state.apply(4, b"1;#abc").expect("short RGB");
        assert_eq!(state.palette(1), Some(OscColor::new(0xaa, 0xbb, 0xcc)));
        state
            .apply(4, b"2;#102030;3;rgb:40/50/60")
            .expect("long formats");
        assert_eq!(state.palette(2), Some(OscColor::new(0x10, 0x20, 0x30)));
        assert_eq!(state.palette(3), Some(OscColor::new(0x40, 0x50, 0x60)));

        let before = state.clone();
        assert_eq!(state.apply(4, b"1;#fff;2;bad"), Err(OscError::InvalidColor));
        assert_eq!(state, before);
        assert_eq!(
            state.apply(4, b"256;#fff"),
            Err(OscError::InvalidPaletteIndex)
        );

        let at_cap = (0..MAX_PALETTE_OPERATIONS)
            .map(|index| format!("{};#000", index % PALETTE_SIZE))
            .collect::<Vec<_>>()
            .join(";");
        state
            .apply(4, at_cap.as_bytes())
            .expect("palette operation count at cap");
        let before = state.clone();
        let over_cap = format!("{at_cap};0;#000");
        assert_eq!(
            state.apply(4, over_cap.as_bytes()),
            Err(OscError::LimitExceeded {
                limit: OscLimit::PaletteOperations,
                maximum: MAX_PALETTE_OPERATIONS,
            })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn default_colors_set_query_and_reset() {
        let baseline = OscBaseline {
            foreground: Some(BLUE),
            background: Some(GREEN),
            cursor: Some(RED),
            ..OscBaseline::default()
        };
        let mut state = OscState::new(baseline);
        state.apply(10, b"#fff").expect("foreground");
        state.apply(11, b"#010203").expect("background");
        state.apply(12, b"rgb:10/20/30").expect("cursor");
        assert_eq!(state.foreground(), Some(OscColor::new(0xff, 0xff, 0xff)));
        assert_eq!(state.background(), Some(OscColor::new(1, 2, 3)));
        assert_eq!(state.cursor_color(), Some(OscColor::new(0x10, 0x20, 0x30)));

        let before = state.clone();
        assert_eq!(
            state.apply(10, b"?"),
            Ok(OscEffect::Query(OscQuery::DefaultColor(
                DefaultColorSlot::Foreground
            )))
        );
        assert_eq!(state, before);
        assert_eq!(
            state.apply(110, b"ignored"),
            Err(OscError::MalformedPayload(
                "default-color reset requires an empty payload"
            ))
        );
        state.apply(110, b"").expect("foreground reset");
        state.apply(111, b"").expect("background reset");
        state.apply(112, b"").expect("cursor reset");
        assert_eq!(state.foreground(), Some(BLUE));
        assert_eq!(state.background(), Some(GREEN));
        assert_eq!(state.cursor_color(), Some(RED));
    }

    #[test]
    fn exact_default_color_keyword_restores_each_baseline() {
        let baseline = OscBaseline {
            foreground: Some(BLUE),
            background: Some(GREEN),
            cursor: Some(RED),
            ..OscBaseline::default()
        };
        let mut state = OscState::new(baseline);
        state.apply(10, b"#fff").expect("foreground override");
        state.apply(11, b"#fff").expect("background override");
        state.apply(12, b"#fff").expect("cursor override");

        for (selector, slot) in [
            (10, DefaultColorSlot::Foreground),
            (11, DefaultColorSlot::Background),
            (12, DefaultColorSlot::Cursor),
        ] {
            assert_eq!(
                state.apply(selector, b"default"),
                Ok(OscEffect::DefaultColorReset { slot })
            );
        }
        assert_eq!(state.foreground(), Some(BLUE));
        assert_eq!(state.background(), Some(GREEN));
        assert_eq!(state.cursor_color(), Some(RED));
    }

    #[test]
    fn default_color_keyword_compatibility_remains_strict() {
        for payload in [
            b"Default".as_slice(),
            b"DEFAULT".as_slice(),
            b"default ".as_slice(),
            b" default".as_slice(),
            b"defaults".as_slice(),
        ] {
            let mut state = OscState::default();
            assert_eq!(state.apply(12, payload), Err(OscError::InvalidColor));
            assert_eq!(state, OscState::default());
        }
    }

    #[test]
    fn hyperlink_is_canonical_reused_stable_and_closeable() {
        let mut state = OscState::default();
        let first = state
            .apply(8, b"id=group:foo=bar;https://example.test/path")
            .expect("open link");
        let OscEffect::HyperlinkOpened {
            link: first_ref,
            reused: false,
        } = first
        else {
            panic!("expected new hyperlink");
        };
        assert_eq!(state.current_hyperlink(), Some(first_ref));
        let link = state.hyperlink(first_ref).expect("stable link reference");
        assert_eq!(link.id(), Some("group"));
        assert_eq!(link.uri(), "https://example.test/path");
        assert_eq!(link.params()[0].key(), "foo");
        assert_eq!(link.params()[1].key(), "id");

        assert_eq!(
            state.apply(8, b"foo=bar:id=group;https://example.test/path"),
            Ok(OscEffect::HyperlinkOpened {
                link: first_ref,
                reused: true,
            })
        );
        assert_eq!(state.hyperlink_count(), 1);
        assert_eq!(
            state.apply(8, b";"),
            Ok(OscEffect::HyperlinkClosed {
                previous: Some(first_ref),
            })
        );
        assert_eq!(state.current_hyperlink(), None);
        assert_eq!(
            state.hyperlink(first_ref).expect("retained record").id(),
            Some("group")
        );

        let anonymous = state
            .apply(8, b";https://anonymous.test")
            .expect("open anonymous link");
        let OscEffect::HyperlinkOpened {
            link: anonymous_ref,
            reused: false,
        } = anonymous
        else {
            panic!("expected new anonymous hyperlink");
        };
        state.apply(8, b";").expect("close anonymous link");
        assert_eq!(
            state.apply(8, b"id=;https://anonymous.test"),
            Ok(OscEffect::HyperlinkOpened {
                link: anonymous_ref,
                reused: true,
            })
        );
        assert_eq!(
            state.hyperlink(anonymous_ref).expect("anonymous link").id(),
            None
        );
    }

    #[test]
    fn hyperlink_bounds_and_failure_are_atomic() {
        let mut state = OscState::default();
        let uri_at_cap = "u".repeat(MAX_HYPERLINK_URI_BYTES);
        let payload = format!(";{uri_at_cap}");
        state.apply(8, payload.as_bytes()).expect("URI at cap");
        let before = state.clone();
        let uri_over_cap = "u".repeat(MAX_HYPERLINK_URI_BYTES + 1);
        let payload = format!(";{uri_over_cap}");
        assert_eq!(
            state.apply(8, payload.as_bytes()),
            Err(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkUriBytes,
                maximum: MAX_HYPERLINK_URI_BYTES,
            })
        );
        assert_eq!(state, before);

        let id_at_cap = "i".repeat(MAX_HYPERLINK_ID_BYTES);
        let payload = format!("id={id_at_cap};id-cap");
        state.apply(8, payload.as_bytes()).expect("id at cap");
        let before = state.clone();
        let id_over_cap = "i".repeat(MAX_HYPERLINK_ID_BYTES + 1);
        let payload = format!("id={id_over_cap};id-over-cap");
        assert_eq!(
            state.apply(8, payload.as_bytes()),
            Err(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkIdBytes,
                maximum: MAX_HYPERLINK_ID_BYTES,
            })
        );
        assert_eq!(state, before);

        let params_at_cap = format!("x={}", "p".repeat(MAX_HYPERLINK_PARAMS_BYTES - 2));
        let payload = format!("{params_at_cap};params-cap");
        state.apply(8, payload.as_bytes()).expect("params at cap");
        let before = state.clone();
        let params_over_cap = format!("x={}", "p".repeat(MAX_HYPERLINK_PARAMS_BYTES - 1));
        let payload = format!("{params_over_cap};params-over-cap");
        assert_eq!(
            state.apply(8, payload.as_bytes()),
            Err(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkParamsBytes,
                maximum: MAX_HYPERLINK_PARAMS_BYTES,
            })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn hyperlink_table_entry_cap_is_enforced() {
        let mut state = OscState::default();
        for index in 0..MAX_HYPERLINK_ENTRIES {
            let payload = format!(";u{index}");
            state
                .apply(8, payload.as_bytes())
                .expect("bounded table entry");
        }
        let before = state.clone();
        assert_eq!(
            state.apply(8, b";one-too-many"),
            Err(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkEntries,
                maximum: MAX_HYPERLINK_ENTRIES,
            })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn hyperlink_total_byte_cap_is_enforced() {
        let mut state = OscState::default();
        let entry_count = MAX_HYPERLINK_TOTAL_BYTES / MAX_HYPERLINK_URI_BYTES;
        assert_eq!(
            entry_count * MAX_HYPERLINK_URI_BYTES,
            MAX_HYPERLINK_TOTAL_BYTES
        );
        for index in 0..entry_count {
            let prefix = format!("{index:02}");
            let uri = format!(
                "{prefix}{}",
                "u".repeat(MAX_HYPERLINK_URI_BYTES - prefix.len())
            );
            let payload = format!(";{uri}");
            state
                .apply(8, payload.as_bytes())
                .expect("entry within total byte cap");
        }
        assert_eq!(state.hyperlink_total_bytes(), MAX_HYPERLINK_TOTAL_BYTES);
        let before = state.clone();
        assert_eq!(
            state.apply(8, b";x"),
            Err(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkTotalBytes,
                maximum: MAX_HYPERLINK_TOTAL_BYTES,
            })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn malformed_hyperlinks_and_unknown_selectors_are_classified() {
        let mut state = OscState::default();
        assert_eq!(
            state.apply(8, b"id=x:"),
            Err(OscError::MalformedPayload("OSC 8 requires params;URI"))
        );
        assert_eq!(
            state.apply(8, b"id=x;"),
            Err(OscError::MalformedPayload(
                "OSC 8 close must use an empty params field"
            ))
        );
        assert_eq!(
            state.apply(999, b"opaque"),
            Ok(OscEffect::UnknownSelector { selector: 999 })
        );

        let at_cap = (0..MAX_HYPERLINK_PARAMS)
            .map(|index| format!("k{index}=v"))
            .collect::<Vec<_>>()
            .join(":");
        let payload = format!("{at_cap};param-count-cap");
        state
            .apply(8, payload.as_bytes())
            .expect("parameter count at cap");
        let before = state.clone();
        let over_cap = format!("{at_cap}:extra=v;param-count-over-cap");
        assert_eq!(
            state.apply(8, over_cap.as_bytes()),
            Err(OscError::LimitExceeded {
                limit: OscLimit::HyperlinkParams,
                maximum: MAX_HYPERLINK_PARAMS,
            })
        );
        assert_eq!(state, before);
    }

    #[test]
    fn payload_and_utf8_bounds_are_strict() {
        let mut state = OscState::default();
        assert_eq!(state.apply(2, &[0xff]), Err(OscError::InvalidUtf8));
        assert_eq!(state.apply(2, b"bad\ntext"), Err(OscError::InvalidText));
        let maximum = vec![b'x'; MAX_OSC_PAYLOAD_BYTES];
        assert_eq!(
            state.apply(999, &maximum),
            Ok(OscEffect::UnknownSelector { selector: 999 })
        );
        let oversized = vec![b'x'; MAX_OSC_PAYLOAD_BYTES + 1];
        assert_eq!(
            state.apply(999, &oversized),
            Err(OscError::LimitExceeded {
                limit: OscLimit::PayloadBytes,
                maximum: MAX_OSC_PAYLOAD_BYTES,
            })
        );
    }

    #[test]
    fn snapshot_v2_codec_roundtrips_every_persistent_osc_field_canonically() {
        let state = populated_codec_state();
        let first = state.encode_snapshot_v2().expect("encode OSC state");
        assert_eq!(codec_u16_at(&first, 8), OSC_CODEC_VERSION);
        let second = state.encode_snapshot_v2().expect("repeat OSC encoding");
        assert_eq!(first, second);

        let restored = OscState::decode_snapshot_v2(&first).expect("decode OSC state");
        assert_eq!(restored, state);
        assert_eq!(restored.baseline(), state.baseline());
        assert_eq!(restored.hyperlink_count(), 2);
        assert_eq!(restored.current_hyperlink(), Some(HyperlinkRef(2)));
        assert_eq!(
            restored.encode_snapshot_v2().expect("canonical re-encode"),
            first
        );
    }

    #[test]
    fn snapshot_v2_codec_preserves_empty_legacy_stack_encoding() {
        let mut state = OscState::default();
        state.apply(0, b"titles").expect("set current titles");
        state.apply(8, b";uri").expect("set hyperlink state");
        let encoded = state.encode_snapshot_v2().expect("encode legacy form");
        assert_eq!(codec_u16_at(&encoded, 8), OSC_CODEC_LEGACY_VERSION);
        let restored = OscState::decode_snapshot_v2(&encoded).expect("decode legacy form");
        assert_eq!(restored, state);
        assert_eq!(
            restored.encode_snapshot_v2().expect("canonical re-encode"),
            encoded
        );

        let layout = codec_layout(&encoded);
        let mut nonempty_legacy = encoded;
        set_codec_u16(&mut nonempty_legacy, layout.title_stack_position, 1);
        nonempty_legacy.splice(
            layout.title_stack_position + OSC_CODEC_STACK_HEADER_BYTES
                ..layout.title_stack_position + OSC_CODEC_STACK_HEADER_BYTES,
            [1, 0, 0, 0, b'x'],
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&nonempty_legacy),
            Err(OscCodecError::Malformed)
        );

        let mut empty_v2 = state.encode_snapshot_v2().expect("encode legacy form");
        empty_v2.splice(
            layout.title_stack_position
                ..layout.title_stack_position + 2 * OSC_CODEC_STACK_HEADER_BYTES,
            vec![
                0;
                OSC_CODEC_STACK_HEADER_BYTES + MAX_TITLE_STACK_DEPTH * OSC_CODEC_STACK_SLOT_BYTES
            ],
        );
        set_codec_u16(&mut empty_v2, 8, OSC_CODEC_VERSION);
        assert_eq!(
            OscState::decode_snapshot_v2(&empty_v2),
            Err(OscCodecError::Malformed)
        );
    }

    #[test]
    fn snapshot_v2_codec_accepts_exact_total_cap_and_rejects_cap_plus_one() {
        let title = vec![b't'; MAX_TITLE_BYTES];
        let mut state = OscState::default();
        state.apply(0, &title).expect("maximum current titles");
        for _ in 0..MAX_TITLE_STACK_DEPTH {
            state
                .save_titles(TitleTarget::Both, 0)
                .expect("maximum title stacks");
        }

        let params = (0..MAX_HYPERLINK_PARAMS)
            .map(|index| format!("a{index:02}="))
            .collect::<Vec<_>>()
            .join(":");
        for index in 0..MAX_HYPERLINK_ENTRIES {
            let uri = format!("{index:04}{}", "u".repeat(204));
            let payload = format!("{params};{uri}");
            state
                .apply(8, payload.as_bytes())
                .expect("maximum hyperlink table");
        }
        assert_eq!(state.hyperlink_total_bytes(), MAX_HYPERLINK_TOTAL_BYTES);

        let encoded = state.encode_snapshot_v2().expect("encode at exact cap");
        assert_eq!(encoded.len(), MAX_OSC_SNAPSHOT_V2_BYTES);
        assert_eq!(
            OscState::decode_snapshot_v2(&encoded).expect("decode at exact cap"),
            state
        );

        let mut oversized = encoded;
        oversized.push(0);
        assert_eq!(
            OscState::decode_snapshot_v2(&oversized),
            Err(OscCodecError::LimitExceeded)
        );
    }

    #[test]
    fn snapshot_v2_codec_rejects_every_truncation_and_trailing_bytes() {
        let encoded = OscState::default()
            .encode_snapshot_v2()
            .expect("encode default state");
        for length in 0..encoded.len() {
            assert_eq!(
                OscState::decode_snapshot_v2(&encoded[..length]),
                Err(OscCodecError::Malformed),
                "accepted truncated length {length}"
            );
        }

        let mut trailing = encoded;
        trailing.push(0);
        assert_eq!(
            OscState::decode_snapshot_v2(&trailing),
            Err(OscCodecError::Malformed)
        );
    }

    #[test]
    fn snapshot_v2_codec_rejects_headers_reserved_bytes_and_colors() {
        let default = OscState::default()
            .encode_snapshot_v2()
            .expect("encode default state");
        let default_layout = codec_layout(&default);
        let populated = populated_codec_state()
            .encode_snapshot_v2()
            .expect("encode populated state");
        let populated_layout = codec_layout(&populated);

        let mut bad_magic = default.clone();
        bad_magic[0] ^= 1;
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_magic),
            Err(OscCodecError::Malformed)
        );

        let mut bad_version = default.clone();
        set_codec_u16(&mut bad_version, 8, OSC_CODEC_VERSION + 1);
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_version),
            Err(OscCodecError::Malformed)
        );

        for reserved_offset in [
            10,
            default_layout.title_stack_reserved,
            default_layout.hyperlink_reserved,
            populated_layout.links[0].reserved,
        ] {
            let source = if reserved_offset == populated_layout.links[0].reserved {
                &populated
            } else {
                &default
            };
            let mut corrupted = source.clone();
            corrupted[reserved_offset] = 1;
            assert_eq!(
                OscState::decode_snapshot_v2(&corrupted),
                Err(OscCodecError::Malformed)
            );
        }

        let mut bad_tag = default.clone();
        bad_tag[OSC_CODEC_HEADER_BYTES] = 2;
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_tag),
            Err(OscCodecError::Malformed)
        );

        let mut noncanonical_none = default;
        noncanonical_none[OSC_CODEC_HEADER_BYTES + 1] = 1;
        assert_eq!(
            OscState::decode_snapshot_v2(&noncanonical_none),
            Err(OscCodecError::Malformed)
        );
    }

    #[test]
    fn snapshot_v2_codec_rejects_invalid_and_overlimit_titles() {
        let encoded = populated_codec_state()
            .encode_snapshot_v2()
            .expect("encode populated state");
        let layout = codec_layout(&encoded);

        let mut invalid_utf8 = encoded.clone();
        invalid_utf8[layout.icon_title.start] = 0xff;
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_utf8),
            Err(OscCodecError::Malformed)
        );

        let mut invalid_text = encoded.clone();
        invalid_text[layout.icon_title.start] = b'\n';
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_text),
            Err(OscCodecError::Malformed)
        );

        let mut title_over_cap = encoded.clone();
        set_codec_u32(
            &mut title_over_cap,
            layout.icon_title_length,
            u32::try_from(MAX_TITLE_BYTES + 1).expect("title cap fits u32"),
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&title_over_cap),
            Err(OscCodecError::LimitExceeded)
        );

        let mut stack_over_cap = encoded.clone();
        set_codec_u16(
            &mut stack_over_cap,
            layout.title_stack_position,
            u16::try_from(MAX_TITLE_STACK_DEPTH + 1).expect("stack cap fits u16"),
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&stack_over_cap),
            Err(OscCodecError::Malformed)
        );

        let mut bad_slot_flags = encoded.clone();
        bad_slot_flags[layout.title_stack_slots[0]] = 4;
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_slot_flags),
            Err(OscCodecError::Malformed)
        );

        let mut bad_slot_reserved = encoded;
        bad_slot_reserved[layout.title_stack_slots[0] + 1] = 1;
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_slot_reserved),
            Err(OscCodecError::Malformed)
        );

        let mut direct_only = OscState::default();
        direct_only
            .save_titles(TitleTarget::Both, 10)
            .expect("direct slot state");
        let mut bad_used_slot = direct_only
            .encode_snapshot_v2()
            .expect("encode direct slot state");
        let direct_layout = codec_layout(&bad_used_slot);
        set_codec_u16(&mut bad_used_slot, direct_layout.title_stack_position, 1);
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_used_slot),
            Err(OscCodecError::Malformed)
        );
    }

    #[test]
    fn snapshot_v2_codec_rejects_invalid_hyperlink_refs_totals_and_duplicates() {
        let mut state = OscState::default();
        state.apply(8, b"a=x;one").expect("first link");
        state.apply(8, b"a=x;two").expect("second link");
        let encoded = state.encode_snapshot_v2().expect("encode links");
        let layout = codec_layout(&encoded);

        let mut bad_current = encoded.clone();
        set_codec_u32(&mut bad_current, layout.current_reference, 3);
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_current),
            Err(OscCodecError::Malformed)
        );

        let mut bad_record_reference = encoded.clone();
        set_codec_u32(&mut bad_record_reference, layout.links[0].reference, 2);
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_record_reference),
            Err(OscCodecError::Malformed)
        );

        let mut bad_record_total = encoded.clone();
        let stored = codec_u32_at(&bad_record_total, layout.links[0].stored_bytes);
        set_codec_u32(
            &mut bad_record_total,
            layout.links[0].stored_bytes,
            stored + 1,
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_record_total),
            Err(OscCodecError::Malformed)
        );

        let mut bad_table_total = encoded.clone();
        let total = codec_u32_at(&bad_table_total, layout.total_bytes);
        set_codec_u32(&mut bad_table_total, layout.total_bytes, total + 1);
        assert_eq!(
            OscState::decode_snapshot_v2(&bad_table_total),
            Err(OscCodecError::Malformed)
        );

        let first = &layout.links[0];
        let second = &layout.links[1];
        assert_eq!(
            first.end - first.stored_bytes,
            second.end - second.stored_bytes
        );
        let mut duplicate = encoded;
        duplicate.copy_within(first.stored_bytes..first.end, second.stored_bytes);
        assert_eq!(
            OscState::decode_snapshot_v2(&duplicate),
            Err(OscCodecError::Malformed)
        );
    }

    #[test]
    fn snapshot_v2_codec_rejects_noncanonical_or_invalid_hyperlink_text() {
        let mut state = OscState::default();
        state
            .apply(8, b"a=x:b=y;uri")
            .expect("two hyperlink params");
        let encoded = state.encode_snapshot_v2().expect("encode link");
        let layout = codec_layout(&encoded);
        let link = &layout.links[0];

        let mut unsorted = encoded.clone();
        unsorted[link.params[0].key.start] = b'c';
        assert_eq!(
            OscState::decode_snapshot_v2(&unsorted),
            Err(OscCodecError::Malformed)
        );

        let mut duplicate_key = encoded.clone();
        duplicate_key[link.params[1].key.start] = b'a';
        assert_eq!(
            OscState::decode_snapshot_v2(&duplicate_key),
            Err(OscCodecError::Malformed)
        );

        let mut invalid_key = encoded.clone();
        invalid_key[link.params[0].key.start] = b'!';
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_key),
            Err(OscCodecError::Malformed)
        );

        let mut invalid_uri_utf8 = encoded.clone();
        invalid_uri_utf8[link.uri.start] = 0xff;
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_uri_utf8),
            Err(OscCodecError::Malformed)
        );

        let mut invalid_uri_text = encoded.clone();
        invalid_uri_text[link.uri.start] = b'\r';
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_uri_text),
            Err(OscCodecError::Malformed)
        );

        let mut invalid_value_utf8 = encoded.clone();
        invalid_value_utf8[link.params[0].value.start] = 0xff;
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_value_utf8),
            Err(OscCodecError::Malformed)
        );

        let mut invalid_value_text = encoded;
        invalid_value_text[link.params[0].value.start] = b'\n';
        assert_eq!(
            OscState::decode_snapshot_v2(&invalid_value_text),
            Err(OscCodecError::Malformed)
        );
    }

    #[test]
    fn snapshot_v2_codec_rejects_hyperlink_field_cap_plus_one() {
        let default = OscState::default()
            .encode_snapshot_v2()
            .expect("encode default state");
        let default_layout = codec_layout(&default);

        let mut entry_over_cap = default.clone();
        set_codec_u32(
            &mut entry_over_cap,
            default_layout.hyperlink_count,
            u32::try_from(MAX_HYPERLINK_ENTRIES + 1).expect("entry cap fits u32"),
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&entry_over_cap),
            Err(OscCodecError::LimitExceeded)
        );

        let mut total_over_cap = default;
        set_codec_u32(
            &mut total_over_cap,
            default_layout.total_bytes,
            u32::try_from(MAX_HYPERLINK_TOTAL_BYTES + 1).expect("total cap fits u32"),
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&total_over_cap),
            Err(OscCodecError::LimitExceeded)
        );

        let mut state = OscState::default();
        state.apply(8, b"a=x;uri").expect("encode one link");
        let encoded = state.encode_snapshot_v2().expect("encode one link");
        let layout = codec_layout(&encoded);
        let link = &layout.links[0];

        let mut uri_over_cap = encoded.clone();
        set_codec_u32(
            &mut uri_over_cap,
            link.uri_length,
            u32::try_from(MAX_HYPERLINK_URI_BYTES + 1).expect("URI cap fits u32"),
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&uri_over_cap),
            Err(OscCodecError::LimitExceeded)
        );

        let mut params_over_cap = encoded;
        set_codec_u16(
            &mut params_over_cap,
            link.param_count,
            u16::try_from(MAX_HYPERLINK_PARAMS + 1).expect("param cap fits u16"),
        );
        assert_eq!(
            OscState::decode_snapshot_v2(&params_over_cap),
            Err(OscCodecError::LimitExceeded)
        );
    }

    #[test]
    fn snapshot_v2_codec_recomputes_id_and_parameter_byte_caps() {
        let mut id_state = OscState::default();
        let id = "i".repeat(MAX_HYPERLINK_ID_BYTES);
        id_state
            .apply(8, format!("id={id};u").as_bytes())
            .expect("ID at cap");
        let mut id_over_cap = id_state.encode_snapshot_v2().expect("encode ID at cap");
        let id_layout = codec_layout(&id_over_cap);
        let id_link = &id_layout.links[0];
        let id_param = &id_link.params[0];
        id_over_cap.insert(id_param.value.end, b'i');
        set_codec_u32(
            &mut id_over_cap,
            id_param.value_length,
            u32::try_from(MAX_HYPERLINK_ID_BYTES + 1).expect("ID cap fits u32"),
        );
        let id_stored = codec_u32_at(&id_over_cap, id_link.stored_bytes);
        set_codec_u32(&mut id_over_cap, id_link.stored_bytes, id_stored + 1);
        let id_total = codec_u32_at(&id_over_cap, id_layout.total_bytes);
        set_codec_u32(&mut id_over_cap, id_layout.total_bytes, id_total + 1);
        assert_eq!(
            OscState::decode_snapshot_v2(&id_over_cap),
            Err(OscCodecError::LimitExceeded)
        );

        let mut params_state = OscState::default();
        let value = "v".repeat(MAX_HYPERLINK_PARAMS_BYTES - 2);
        params_state
            .apply(8, format!("x={value};u").as_bytes())
            .expect("parameter bytes at cap");
        let mut params_over_cap = params_state
            .encode_snapshot_v2()
            .expect("encode parameter bytes at cap");
        let params_layout = codec_layout(&params_over_cap);
        let params_link = &params_layout.links[0];
        let parameter = &params_link.params[0];
        params_over_cap.insert(parameter.value.end, b'v');
        set_codec_u32(
            &mut params_over_cap,
            parameter.value_length,
            u32::try_from(MAX_HYPERLINK_PARAMS_BYTES - 1).expect("parameter value length fits u32"),
        );
        let stored = codec_u32_at(&params_over_cap, params_link.stored_bytes);
        set_codec_u32(&mut params_over_cap, params_link.stored_bytes, stored + 1);
        let total = codec_u32_at(&params_over_cap, params_layout.total_bytes);
        set_codec_u32(&mut params_over_cap, params_layout.total_bytes, total + 1);
        assert_eq!(
            OscState::decode_snapshot_v2(&params_over_cap),
            Err(OscCodecError::LimitExceeded)
        );
    }
}
