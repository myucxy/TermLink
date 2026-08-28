//! Canonical, length-bounded replies to terminal state queries.

use std::fmt::{Display, Formatter};

use crate::modes::{AttributeChangeExtent, Margins, ModifyOtherKeys};
use crate::{Attributes, Color, CursorShape, DefaultColorSlot, OscColor, UnderlineStyle};

pub(crate) const MAX_QUERY_REPLY_BYTES: usize = 64 * 1024;
pub(crate) const MAX_XTGETTCAP_REQUEST_BYTES: usize = 4 * 1024;
pub(crate) const MAX_XTGETTCAP_NAMES: usize = 256;
pub(crate) const MAX_XTGETTCAP_NAME_BYTES: usize = 32;

const ESC: u8 = 0x1b;
const ST: &[u8] = b"\x1b\\";

/// A protocol reply minted by one of this module's canonical builders.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct QueryReply(Vec<u8>);

impl QueryReply {
    pub(crate) fn as_bytes(&self) -> &[u8] {
        &self.0
    }

    #[cfg(test)]
    fn into_bytes(self) -> Vec<u8> {
        self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum QueryReplyLimit {
    ReplyBytes,
    XtgettcapRequestBytes,
    XtgettcapNames,
    XtgettcapNameBytes,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum QueryReplyError {
    Unsupported(&'static str),
    LimitExceeded {
        limit: QueryReplyLimit,
        maximum: usize,
    },
    AllocationFailed,
}

impl Display for QueryReplyError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Unsupported(reason) => write!(formatter, "unsupported terminal query: {reason}"),
            Self::LimitExceeded { limit, maximum } => {
                write!(
                    formatter,
                    "terminal query limit {limit:?} exceeded (maximum {maximum})"
                )
            }
            Self::AllocationFailed => formatter.write_str("terminal query reply allocation failed"),
        }
    }
}

impl std::error::Error for QueryReplyError {}

/// The `Pm` value in a DECRPM response.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) enum ModeReportStatus {
    NotRecognized = 0,
    Set = 1,
    Reset = 2,
    PermanentlySet = 3,
    PermanentlyReset = 4,
}

impl ModeReportStatus {
    pub(crate) const fn from_setting(setting: Option<bool>) -> Self {
        match setting {
            None => Self::NotRecognized,
            Some(true) => Self::Set,
            Some(false) => Self::Reset,
        }
    }
}

/// Builds `CSI Ps;Pm$y`, with `?` before `Ps` for a DEC private mode.
pub(crate) fn build_decrpm_reply(
    parameter: u16,
    status: ModeReportStatus,
    private: bool,
) -> Result<QueryReply, QueryReplyError> {
    let mut writer = ReplyWriter::with_capacity(16)?;
    writer.bytes(&[ESC, b'['])?;
    if private {
        writer.byte(b'?')?;
    }
    writer.decimal(u32::from(parameter))?;
    writer.byte(b';')?;
    writer.decimal(u32::from(status as u8))?;
    writer.bytes(b"$y")?;
    Ok(writer.finish())
}

/// All terminal state required by the supported DECRQSS selectors.
#[derive(Clone, Copy, Debug)]
pub(crate) struct DecrqssState {
    attributes: Attributes,
    margins: Margins,
    cursor_shape: CursorShape,
    cursor_blinking: bool,
    protected: bool,
    attribute_change_extent: AttributeChangeExtent,
}

impl DecrqssState {
    pub(crate) const fn new(
        attributes: Attributes,
        margins: Margins,
        cursor_shape: CursorShape,
        cursor_blinking: bool,
        protected: bool,
        attribute_change_extent: AttributeChangeExtent,
    ) -> Self {
        Self {
            attributes,
            margins,
            cursor_shape,
            cursor_blinking,
            protected,
            attribute_change_extent,
        }
    }
}

/// Builds a DECRQSS response for an exact `Pt` selector (without `$q`).
///
/// Supported selectors are SGR (`m`), vertical margins (`r`), horizontal
/// margins (`s`), cursor style (` SP q`), DECSCA (`"q`), and DECSACE (`*x`).
/// Unknown selectors receive the canonical failure response `DCS 0$r ST`.
pub(crate) fn build_decrqss_reply(
    selector: &[u8],
    state: DecrqssState,
) -> Result<QueryReply, QueryReplyError> {
    let mut writer = ReplyWriter::with_capacity(96)?;
    match selector {
        b"m" => {
            writer.bytes(b"\x1bP1$r")?;
            write_sgr_status(&mut writer, state.attributes)?;
        }
        b"r" => {
            writer.bytes(b"\x1bP1$r")?;
            writer.decimal(one_based(state.margins.top)?)?;
            writer.byte(b';')?;
            writer.decimal(state.margins.bottom)?;
            writer.byte(b'r')?;
        }
        b"s" => {
            writer.bytes(b"\x1bP1$r")?;
            writer.decimal(one_based(state.margins.left)?)?;
            writer.byte(b';')?;
            writer.decimal(state.margins.right)?;
            writer.byte(b's')?;
        }
        b" q" => {
            writer.bytes(b"\x1bP1$r")?;
            writer.decimal(cursor_style_parameter(
                state.cursor_shape,
                state.cursor_blinking,
            ))?;
            writer.bytes(b" q")?;
        }
        b"\"q" => {
            writer.bytes(b"\x1bP1$r")?;
            writer.byte(if state.protected { b'1' } else { b'2' })?;
            writer.bytes(b"\"q")?;
        }
        b"*x" => {
            writer.bytes(b"\x1bP1$r")?;
            writer.byte(match state.attribute_change_extent {
                AttributeChangeExtent::Stream => b'1',
                AttributeChangeExtent::Rectangle => b'2',
            })?;
            writer.bytes(b"*x")?;
        }
        _ => writer.bytes(b"\x1bP0$r")?,
    }
    writer.bytes(ST)?;
    Ok(writer.finish())
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct XtgettcapModes {
    pub(crate) newline_mode: bool,
    pub(crate) cursor_keys_application: bool,
    pub(crate) keypad_application: bool,
    pub(crate) modify_other_keys: ModifyOtherKeys,
}

/// Parses an XTGETTCAP `Pt` list and builds one transactional DCS reply.
///
/// `request` is the data after `+q` and before ST. Names must be non-empty,
/// hexadecimal byte strings separated by semicolons. Hex input is accepted in
/// either case; replies always use lowercase hex. If the list is malformed or
/// any name is unknown, the entire list receives `DCS 0+r ST` without partial
/// results or an echoed name. Resource-limit and allocation failures remain
/// errors because no protocol reply can be committed safely in those cases.
pub(crate) fn build_xtgettcap_replies(
    request: &[u8],
    modes: XtgettcapModes,
) -> Result<QueryReply, QueryReplyError> {
    let names = match parse_xtgettcap_names(request) {
        Ok(names) => names,
        Err(QueryReplyError::Unsupported(_)) => return xtgettcap_failure_reply(),
        Err(error) => return Err(error),
    };
    if names
        .iter()
        .any(|name| xtgettcap_value(name, modes).is_none())
    {
        return xtgettcap_failure_reply();
    }

    let mut writer = ReplyWriter::with_capacity(96)?;
    writer.bytes(b"\x1bP1+r")?;
    for (index, name) in names.iter().enumerate() {
        if index != 0 {
            writer.byte(b';')?;
        }
        writer.lower_hex_bytes(name)?;
        writer.byte(b'=')?;
        writer.lower_hex_bytes(
            xtgettcap_value(name, modes).expect("all XTGETTCAP names were resolved above"),
        )?;
    }
    writer.bytes(ST)?;
    Ok(writer.finish())
}

fn xtgettcap_failure_reply() -> Result<QueryReply, QueryReplyError> {
    let mut writer = ReplyWriter::with_capacity(7)?;
    writer.bytes(b"\x1bP0+r")?;
    writer.bytes(ST)?;
    Ok(writer.finish())
}

/// Builds `OSC 4 ; index ; rgb:hhhh/hhhh/hhhh ST` using a 7-bit ST.
pub(crate) fn build_osc_palette_color_reply(
    index: u8,
    color: OscColor,
) -> Result<QueryReply, QueryReplyError> {
    let mut writer = ReplyWriter::with_capacity(40)?;
    writer.bytes(b"\x1b]4;")?;
    writer.decimal(u32::from(index))?;
    writer.byte(b';')?;
    write_osc_color(&mut writer, color)?;
    writer.bytes(ST)?;
    Ok(writer.finish())
}

/// Builds `OSC Ps ; rgb:hhhh/hhhh/hhhh ST` for OSC 10, 11, or 12.
pub(crate) fn build_osc_default_color_reply(
    slot: DefaultColorSlot,
    color: OscColor,
) -> Result<QueryReply, QueryReplyError> {
    let selector = match slot {
        DefaultColorSlot::Foreground => 10,
        DefaultColorSlot::Background => 11,
        DefaultColorSlot::Cursor => 12,
    };
    let mut writer = ReplyWriter::with_capacity(40)?;
    writer.bytes(b"\x1b]")?;
    writer.decimal(selector)?;
    writer.byte(b';')?;
    write_osc_color(&mut writer, color)?;
    writer.bytes(ST)?;
    Ok(writer.finish())
}

fn one_based(value: u32) -> Result<u32, QueryReplyError> {
    value
        .checked_add(1)
        .ok_or(QueryReplyError::Unsupported("invalid DECRQSS margins"))
}

fn cursor_style_parameter(shape: CursorShape, blinking: bool) -> u32 {
    match (shape, blinking) {
        (CursorShape::Block, true) => 1,
        (CursorShape::Block, false) => 2,
        (CursorShape::Underline, true) => 3,
        (CursorShape::Underline, false) => 4,
        (CursorShape::Bar, true) => 5,
        (CursorShape::Bar, false) => 6,
    }
}

fn write_sgr_status(
    writer: &mut ReplyWriter,
    attributes: Attributes,
) -> Result<(), QueryReplyError> {
    writer.byte(b'0')?;
    for (enabled, parameter) in [
        (attributes.bold, 1),
        (attributes.faint, 2),
        (attributes.italic, 3),
    ] {
        if enabled {
            writer.byte(b';')?;
            writer.decimal(parameter)?;
        }
    }
    if attributes.underline_style != UnderlineStyle::None {
        writer.bytes(b";4:")?;
        writer.decimal(u32::from(attributes.underline_style as u8))?;
    }
    for (enabled, parameter) in [
        (attributes.blink, 5),
        (attributes.inverse, 7),
        (attributes.conceal, 8),
        (attributes.strikethrough, 9),
        (attributes.overline, 53),
    ] {
        if enabled {
            writer.byte(b';')?;
            writer.decimal(parameter)?;
        }
    }
    write_sgr_color(writer, 38, attributes.foreground)?;
    write_sgr_color(writer, 48, attributes.background)?;
    write_sgr_color(writer, 58, attributes.underline_color)?;
    writer.byte(b'm')
}

fn write_sgr_color(
    writer: &mut ReplyWriter,
    selector: u32,
    color: Color,
) -> Result<(), QueryReplyError> {
    match color {
        Color::Default => Ok(()),
        Color::Indexed(index) => {
            writer.byte(b';')?;
            writer.decimal(selector)?;
            writer.bytes(b";5;")?;
            writer.decimal(u32::from(index))
        }
        Color::Rgb(red, green, blue) => {
            writer.byte(b';')?;
            writer.decimal(selector)?;
            writer.bytes(b";2;")?;
            writer.decimal(u32::from(red))?;
            writer.byte(b';')?;
            writer.decimal(u32::from(green))?;
            writer.byte(b';')?;
            writer.decimal(u32::from(blue))
        }
    }
}

fn parse_xtgettcap_names(request: &[u8]) -> Result<Vec<Vec<u8>>, QueryReplyError> {
    if request.len() > MAX_XTGETTCAP_REQUEST_BYTES {
        return Err(QueryReplyError::LimitExceeded {
            limit: QueryReplyLimit::XtgettcapRequestBytes,
            maximum: MAX_XTGETTCAP_REQUEST_BYTES,
        });
    }
    if request.is_empty() {
        return Err(QueryReplyError::Unsupported("empty XTGETTCAP request"));
    }

    let count = request.iter().filter(|&&byte| byte == b';').count() + 1;
    if count > MAX_XTGETTCAP_NAMES {
        return Err(QueryReplyError::LimitExceeded {
            limit: QueryReplyLimit::XtgettcapNames,
            maximum: MAX_XTGETTCAP_NAMES,
        });
    }
    let mut names = Vec::new();
    names
        .try_reserve_exact(count)
        .map_err(|_| QueryReplyError::AllocationFailed)?;
    for encoded in request.split(|&byte| byte == b';') {
        if encoded.is_empty() || encoded.len() % 2 != 0 {
            return Err(QueryReplyError::Unsupported("malformed XTGETTCAP hex name"));
        }
        let decoded_len = encoded.len() / 2;
        if decoded_len > MAX_XTGETTCAP_NAME_BYTES {
            return Err(QueryReplyError::LimitExceeded {
                limit: QueryReplyLimit::XtgettcapNameBytes,
                maximum: MAX_XTGETTCAP_NAME_BYTES,
            });
        }
        let mut decoded = Vec::new();
        decoded
            .try_reserve_exact(decoded_len)
            .map_err(|_| QueryReplyError::AllocationFailed)?;
        for pair in encoded.chunks_exact(2) {
            let high = hex_value(pair[0])
                .ok_or(QueryReplyError::Unsupported("malformed XTGETTCAP hex name"))?;
            let low = hex_value(pair[1])
                .ok_or(QueryReplyError::Unsupported("malformed XTGETTCAP hex name"))?;
            decoded.push(high << 4 | low);
        }
        names.push(decoded);
    }
    Ok(names)
}

fn xtgettcap_value(name: &[u8], modes: XtgettcapModes) -> Option<&'static [u8]> {
    let cursor = |normal: &'static [u8], application: &'static [u8]| {
        if modes.cursor_keys_application {
            application
        } else {
            normal
        }
    };
    let keypad = |normal: &'static [u8], application: &'static [u8]| {
        if modes.keypad_application {
            application
        } else {
            normal
        }
    };
    match name {
        b"Co" | b"colors" => Some(b"256"),
        b"RGB" => Some(b"8/8/8"),
        b"kb" | b"kbs" => Some(if modes.modify_other_keys == ModifyOtherKeys::Level3 {
            b"\x1b[27;1;127~"
        } else {
            b"\x7f"
        }),
        b"kB" | b"kcbt" => Some(
            if matches!(
                modes.modify_other_keys,
                ModifyOtherKeys::Level2 | ModifyOtherKeys::Level3
            ) {
                b"\x1b[27;2;9~"
            } else {
                b"\x1b[Z"
            },
        ),
        b"kI" | b"kich1" => Some(b"\x1b[2~"),
        b"kD" | b"kdch1" => Some(b"\x1b[3~"),
        b"kP" | b"kpp" => Some(b"\x1b[5~"),
        b"kN" | b"knp" => Some(b"\x1b[6~"),
        b"ku" | b"kcuu1" => Some(cursor(b"\x1b[A", b"\x1bOA")),
        b"kd" | b"kcud1" => Some(cursor(b"\x1b[B", b"\x1bOB")),
        b"kr" | b"kcuf1" => Some(cursor(b"\x1b[C", b"\x1bOC")),
        b"kl" | b"kcub1" => Some(cursor(b"\x1b[D", b"\x1bOD")),
        b"kh" | b"khome" => Some(cursor(b"\x1b[H", b"\x1bOH")),
        b"@7" | b"kend" => Some(cursor(b"\x1b[F", b"\x1bOF")),
        b"k1" | b"kf1" => Some(b"\x1bOP"),
        b"k2" | b"kf2" => Some(b"\x1bOQ"),
        b"k3" | b"kf3" => Some(b"\x1bOR"),
        b"k4" | b"kf4" => Some(b"\x1bOS"),
        b"k5" | b"kf5" => Some(b"\x1b[15~"),
        b"k6" | b"kf6" => Some(b"\x1b[17~"),
        b"k7" | b"kf7" => Some(b"\x1b[18~"),
        b"k8" | b"kf8" => Some(b"\x1b[19~"),
        b"k9" | b"kf9" => Some(b"\x1b[20~"),
        b"k;" | b"kf10" => Some(b"\x1b[21~"),
        b"F1" | b"kf11" => Some(b"\x1b[23~"),
        b"F2" | b"kf12" => Some(b"\x1b[24~"),
        b"kp0" => Some(keypad(b"0", b"\x1bOp")),
        b"kp1" | b"kc1" | b"K4" => Some(keypad(b"1", b"\x1bOq")),
        b"kp2" => Some(keypad(b"2", b"\x1bOr")),
        b"kp3" | b"kc3" | b"K5" => Some(keypad(b"3", b"\x1bOs")),
        b"kp4" => Some(keypad(b"4", b"\x1bOt")),
        b"kp5" | b"kb2" | b"K2" => Some(keypad(b"5", b"\x1bOu")),
        b"kp6" => Some(keypad(b"6", b"\x1bOv")),
        b"kp7" | b"ka1" | b"K1" => Some(keypad(b"7", b"\x1bOw")),
        b"kp8" => Some(keypad(b"8", b"\x1bOx")),
        b"kp9" | b"ka3" | b"K3" => Some(keypad(b"9", b"\x1bOy")),
        b"kdot" => Some(keypad(b".", b"\x1bOn")),
        b"ksep" => Some(keypad(b",", b"\x1bOl")),
        b"kadd" => Some(keypad(b"+", b"\x1bOk")),
        b"ksub" => Some(keypad(b"-", b"\x1bOm")),
        b"kmul" => Some(keypad(b"*", b"\x1bOj")),
        b"kdiv" => Some(keypad(b"/", b"\x1bOo")),
        b"keql" => Some(keypad(b"=", b"\x1bOX")),
        b"kent" | b"@8" => Some(if modes.keypad_application {
            b"\x1bOM"
        } else if modes.newline_mode {
            b"\r\n"
        } else {
            b"\r"
        }),
        _ => modified_navigation_value(name).or_else(|| modified_function_key_value(name)),
    }
}

fn modified_navigation_value(name: &[u8]) -> Option<&'static [u8]> {
    match name {
        b"kDC" | b"*4" => Some(b"\x1b[3;2~"),
        b"kDC3" => Some(b"\x1b[3;3~"),
        b"kDC4" => Some(b"\x1b[3;4~"),
        b"kDC5" => Some(b"\x1b[3;5~"),
        b"kDC6" => Some(b"\x1b[3;6~"),
        b"kDC7" => Some(b"\x1b[3;7~"),
        b"kDN" | b"kind" | b"kF" => Some(b"\x1b[1;2B"),
        b"kDN3" => Some(b"\x1b[1;3B"),
        b"kDN4" => Some(b"\x1b[1;4B"),
        b"kDN5" => Some(b"\x1b[1;5B"),
        b"kDN6" => Some(b"\x1b[1;6B"),
        b"kDN7" => Some(b"\x1b[1;7B"),
        b"kEND" | b"*7" => Some(b"\x1b[1;2F"),
        b"kEND3" => Some(b"\x1b[1;3F"),
        b"kEND4" => Some(b"\x1b[1;4F"),
        b"kEND5" => Some(b"\x1b[1;5F"),
        b"kEND6" => Some(b"\x1b[1;6F"),
        b"kEND7" => Some(b"\x1b[1;7F"),
        b"kHOM" | b"#2" => Some(b"\x1b[1;2H"),
        b"kHOM3" => Some(b"\x1b[1;3H"),
        b"kHOM4" => Some(b"\x1b[1;4H"),
        b"kHOM5" => Some(b"\x1b[1;5H"),
        b"kHOM6" => Some(b"\x1b[1;6H"),
        b"kHOM7" => Some(b"\x1b[1;7H"),
        b"kIC" | b"#3" => Some(b"\x1b[2;2~"),
        b"kIC3" => Some(b"\x1b[2;3~"),
        b"kIC4" => Some(b"\x1b[2;4~"),
        b"kIC5" => Some(b"\x1b[2;5~"),
        b"kIC6" => Some(b"\x1b[2;6~"),
        b"kIC7" => Some(b"\x1b[2;7~"),
        b"kLFT" | b"#4" => Some(b"\x1b[1;2D"),
        b"kLFT3" => Some(b"\x1b[1;3D"),
        b"kLFT4" => Some(b"\x1b[1;4D"),
        b"kLFT5" => Some(b"\x1b[1;5D"),
        b"kLFT6" => Some(b"\x1b[1;6D"),
        b"kLFT7" => Some(b"\x1b[1;7D"),
        b"kNXT" | b"%c" => Some(b"\x1b[6;2~"),
        b"kNXT3" => Some(b"\x1b[6;3~"),
        b"kNXT4" => Some(b"\x1b[6;4~"),
        b"kNXT5" => Some(b"\x1b[6;5~"),
        b"kNXT6" => Some(b"\x1b[6;6~"),
        b"kNXT7" => Some(b"\x1b[6;7~"),
        b"kPRV" | b"%e" => Some(b"\x1b[5;2~"),
        b"kPRV3" => Some(b"\x1b[5;3~"),
        b"kPRV4" => Some(b"\x1b[5;4~"),
        b"kPRV5" => Some(b"\x1b[5;5~"),
        b"kPRV6" => Some(b"\x1b[5;6~"),
        b"kPRV7" => Some(b"\x1b[5;7~"),
        b"kRIT" | b"%i" => Some(b"\x1b[1;2C"),
        b"kRIT3" => Some(b"\x1b[1;3C"),
        b"kRIT4" => Some(b"\x1b[1;4C"),
        b"kRIT5" => Some(b"\x1b[1;5C"),
        b"kRIT6" => Some(b"\x1b[1;6C"),
        b"kRIT7" => Some(b"\x1b[1;7C"),
        b"kUP" | b"kri" | b"kR" => Some(b"\x1b[1;2A"),
        b"kUP3" => Some(b"\x1b[1;3A"),
        b"kUP4" => Some(b"\x1b[1;4A"),
        b"kUP5" => Some(b"\x1b[1;5A"),
        b"kUP6" => Some(b"\x1b[1;6A"),
        b"kUP7" => Some(b"\x1b[1;7A"),
        _ => None,
    }
}

fn modified_function_key_value(name: &[u8]) -> Option<&'static [u8]> {
    match name {
        b"kf13" | b"F3" => Some(b"\x1b[1;2P"),
        b"kf14" | b"F4" => Some(b"\x1b[1;2Q"),
        b"kf15" | b"F5" => Some(b"\x1b[1;2R"),
        b"kf16" | b"F6" => Some(b"\x1b[1;2S"),
        b"kf17" | b"F7" => Some(b"\x1b[15;2~"),
        b"kf18" | b"F8" => Some(b"\x1b[17;2~"),
        b"kf19" | b"F9" => Some(b"\x1b[18;2~"),
        b"kf20" | b"FA" => Some(b"\x1b[19;2~"),
        b"kf21" | b"FB" => Some(b"\x1b[20;2~"),
        b"kf22" | b"FC" => Some(b"\x1b[21;2~"),
        b"kf23" | b"FD" => Some(b"\x1b[23;2~"),
        b"kf24" | b"FE" => Some(b"\x1b[24;2~"),
        b"kf25" | b"FF" => Some(b"\x1b[1;5P"),
        b"kf26" | b"FG" => Some(b"\x1b[1;5Q"),
        b"kf27" | b"FH" => Some(b"\x1b[1;5R"),
        b"kf28" | b"FI" => Some(b"\x1b[1;5S"),
        b"kf29" | b"FJ" => Some(b"\x1b[15;5~"),
        b"kf30" | b"FK" => Some(b"\x1b[17;5~"),
        b"kf31" | b"FL" => Some(b"\x1b[18;5~"),
        b"kf32" | b"FM" => Some(b"\x1b[19;5~"),
        b"kf33" | b"FN" => Some(b"\x1b[20;5~"),
        b"kf34" | b"FO" => Some(b"\x1b[21;5~"),
        b"kf35" | b"FP" => Some(b"\x1b[23;5~"),
        b"kf36" | b"FQ" => Some(b"\x1b[24;5~"),
        b"kf37" | b"FR" => Some(b"\x1b[1;6P"),
        b"kf38" | b"FS" => Some(b"\x1b[1;6Q"),
        b"kf39" | b"FT" => Some(b"\x1b[1;6R"),
        b"kf40" | b"FU" => Some(b"\x1b[1;6S"),
        b"kf41" | b"FV" => Some(b"\x1b[15;6~"),
        b"kf42" | b"FW" => Some(b"\x1b[17;6~"),
        b"kf43" | b"FX" => Some(b"\x1b[18;6~"),
        b"kf44" | b"FY" => Some(b"\x1b[19;6~"),
        b"kf45" | b"FZ" => Some(b"\x1b[20;6~"),
        b"kf46" | b"Fa" => Some(b"\x1b[21;6~"),
        b"kf47" | b"Fb" => Some(b"\x1b[23;6~"),
        b"kf48" | b"Fc" => Some(b"\x1b[24;6~"),
        b"kf49" | b"Fd" => Some(b"\x1b[1;3P"),
        b"kf50" | b"Fe" => Some(b"\x1b[1;3Q"),
        b"kf51" | b"Ff" => Some(b"\x1b[1;3R"),
        b"kf52" | b"Fg" => Some(b"\x1b[1;3S"),
        b"kf53" | b"Fh" => Some(b"\x1b[15;3~"),
        b"kf54" | b"Fi" => Some(b"\x1b[17;3~"),
        b"kf55" | b"Fj" => Some(b"\x1b[18;3~"),
        b"kf56" | b"Fk" => Some(b"\x1b[19;3~"),
        b"kf57" | b"Fl" => Some(b"\x1b[20;3~"),
        b"kf58" | b"Fm" => Some(b"\x1b[21;3~"),
        b"kf59" | b"Fn" => Some(b"\x1b[23;3~"),
        b"kf60" | b"Fo" => Some(b"\x1b[24;3~"),
        b"kf61" | b"Fp" => Some(b"\x1b[1;4P"),
        b"kf62" | b"Fq" => Some(b"\x1b[1;4Q"),
        b"kf63" | b"Fr" => Some(b"\x1b[1;4R"),
        _ => None,
    }
}

fn write_osc_color(writer: &mut ReplyWriter, color: OscColor) -> Result<(), QueryReplyError> {
    writer.bytes(b"rgb:")?;
    writer.lower_hex_u16(u16::from(color.red) * 0x0101)?;
    writer.byte(b'/')?;
    writer.lower_hex_u16(u16::from(color.green) * 0x0101)?;
    writer.byte(b'/')?;
    writer.lower_hex_u16(u16::from(color.blue) * 0x0101)
}

const fn hex_value(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

struct ReplyWriter {
    bytes: Vec<u8>,
}

impl ReplyWriter {
    fn with_capacity(capacity: usize) -> Result<Self, QueryReplyError> {
        if capacity > MAX_QUERY_REPLY_BYTES {
            return Err(reply_limit());
        }
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(capacity)
            .map_err(|_| QueryReplyError::AllocationFailed)?;
        Ok(Self { bytes })
    }

    fn byte(&mut self, byte: u8) -> Result<(), QueryReplyError> {
        self.bytes(&[byte])
    }

    fn bytes(&mut self, bytes: &[u8]) -> Result<(), QueryReplyError> {
        let new_len = self
            .bytes
            .len()
            .checked_add(bytes.len())
            .ok_or_else(reply_limit)?;
        if new_len > MAX_QUERY_REPLY_BYTES {
            return Err(reply_limit());
        }
        self.bytes
            .try_reserve(bytes.len())
            .map_err(|_| QueryReplyError::AllocationFailed)?;
        self.bytes.extend_from_slice(bytes);
        Ok(())
    }

    fn decimal(&mut self, mut value: u32) -> Result<(), QueryReplyError> {
        let mut scratch = [0_u8; 10];
        let mut index = scratch.len();
        loop {
            index -= 1;
            scratch[index] = b'0' + u8::try_from(value % 10).expect("decimal digit fits u8");
            value /= 10;
            if value == 0 {
                break;
            }
        }
        self.bytes(&scratch[index..])
    }

    fn lower_hex_bytes(&mut self, bytes: &[u8]) -> Result<(), QueryReplyError> {
        const DIGITS: &[u8; 16] = b"0123456789abcdef";
        for &byte in bytes {
            self.bytes(&[
                DIGITS[usize::from(byte >> 4)],
                DIGITS[usize::from(byte & 0x0f)],
            ])?;
        }
        Ok(())
    }

    fn lower_hex_u16(&mut self, value: u16) -> Result<(), QueryReplyError> {
        const DIGITS: &[u8; 16] = b"0123456789abcdef";
        self.bytes(&[
            DIGITS[usize::from(value >> 12)],
            DIGITS[usize::from(value >> 8 & 0x0f)],
            DIGITS[usize::from(value >> 4 & 0x0f)],
            DIGITS[usize::from(value & 0x0f)],
        ])
    }

    fn finish(self) -> QueryReply {
        QueryReply(self.bytes)
    }
}

const fn reply_limit() -> QueryReplyError {
    QueryReplyError::LimitExceeded {
        limit: QueryReplyLimit::ReplyBytes,
        maximum: MAX_QUERY_REPLY_BYTES,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::{InputEvent, Key, KeyEvent, KeypadKey, Modifiers, encode_input};
    use crate::modes::ModeState;

    fn state(attributes: Attributes) -> DecrqssState {
        DecrqssState::new(
            attributes,
            Margins::full(80, 24),
            CursorShape::Block,
            true,
            false,
            AttributeChangeExtent::Stream,
        )
    }

    fn bytes(result: Result<QueryReply, QueryReplyError>) -> Vec<u8> {
        result.expect("build query reply").into_bytes()
    }

    fn key(key: Key, modifiers: Modifiers) -> InputEvent<'static> {
        InputEvent::Key(KeyEvent { key, modifiers })
    }

    fn xtgettcap_modes(modes: &ModeState) -> XtgettcapModes {
        XtgettcapModes {
            newline_mode: modes.newline_mode,
            cursor_keys_application: modes.cursor_keys_application,
            keypad_application: modes.keypad_application,
            modify_other_keys: modes.modify_other_keys,
        }
    }

    fn assert_cap_matches_encoder(name: &[u8], event: InputEvent<'_>, modes: &ModeState) {
        let encoded = encode_input(event, modes, 0).expect("encode semantic key");
        assert_eq!(
            xtgettcap_value(name, xtgettcap_modes(modes)),
            Some(encoded.as_slice()),
            "capability {}",
            String::from_utf8_lossy(name)
        );
    }

    const fn modifiers_for_parameter(parameter: u8) -> Modifiers {
        match parameter {
            2 => Modifiers {
                shift: true,
                ..Modifiers::NONE
            },
            3 => Modifiers {
                alt: true,
                ..Modifiers::NONE
            },
            4 => Modifiers {
                shift: true,
                alt: true,
                ..Modifiers::NONE
            },
            5 => Modifiers {
                control: true,
                ..Modifiers::NONE
            },
            6 => Modifiers {
                shift: true,
                control: true,
                ..Modifiers::NONE
            },
            7 => Modifiers {
                alt: true,
                control: true,
                ..Modifiers::NONE
            },
            _ => panic!("unsupported test modifier parameter"),
        }
    }

    const fn function_key(number: u8) -> Key {
        match number {
            1 => Key::F1,
            2 => Key::F2,
            3 => Key::F3,
            4 => Key::F4,
            5 => Key::F5,
            6 => Key::F6,
            7 => Key::F7,
            8 => Key::F8,
            9 => Key::F9,
            10 => Key::F10,
            11 => Key::F11,
            12 => Key::F12,
            _ => panic!("unsupported test function key"),
        }
    }

    const fn function_termcap_alias(number: u8) -> [u8; 2] {
        let suffix = match number {
            11..=19 => b'1' + (number - 11),
            20..=45 => b'A' + (number - 20),
            46..=63 => b'a' + (number - 46),
            _ => panic!("unsupported test function capability"),
        };
        [b'F', suffix]
    }

    #[test]
    fn decrpm_canonical_statuses_and_private_marker() {
        let statuses = [
            ModeReportStatus::NotRecognized,
            ModeReportStatus::Set,
            ModeReportStatus::Reset,
            ModeReportStatus::PermanentlySet,
            ModeReportStatus::PermanentlyReset,
        ];
        for (value, status) in statuses.into_iter().enumerate() {
            assert_eq!(
                bytes(build_decrpm_reply(25, status, false)),
                format!("\x1b[25;{value}$y").into_bytes()
            );
        }
        assert_eq!(
            bytes(build_decrpm_reply(2004, ModeReportStatus::Set, true)),
            b"\x1b[?2004;1$y"
        );
        assert_eq!(
            ModeReportStatus::from_setting(None),
            ModeReportStatus::NotRecognized
        );
        assert_eq!(
            ModeReportStatus::from_setting(Some(true)),
            ModeReportStatus::Set
        );
        assert_eq!(
            ModeReportStatus::from_setting(Some(false)),
            ModeReportStatus::Reset
        );
    }

    #[test]
    fn decrqss_covers_supported_state_and_unknown_failure() {
        let custom = DecrqssState::new(
            Attributes::default(),
            Margins {
                top: 2,
                bottom: 20,
                left: 4,
                right: 70,
                vertical_follows_screen: false,
                horizontal_follows_screen: false,
            },
            CursorShape::Bar,
            false,
            true,
            AttributeChangeExtent::Rectangle,
        );
        let cases: &[(&[u8], &[u8])] = &[
            (b"r", b"\x1bP1$r3;20r\x1b\\"),
            (b"s", b"\x1bP1$r5;70s\x1b\\"),
            (b" q", b"\x1bP1$r6 q\x1b\\"),
            (b"\"q", b"\x1bP1$r1\"q\x1b\\"),
            (b"*x", b"\x1bP1$r2*x\x1b\\"),
            (b"unknown", b"\x1bP0$r\x1b\\"),
        ];
        for &(selector, expected) in cases {
            assert_eq!(bytes(build_decrqss_reply(selector, custom)), expected);
        }
        assert_eq!(
            bytes(build_decrqss_reply(b"\"q", state(Attributes::default()))),
            b"\x1bP1$r2\"q\x1b\\"
        );
    }

    #[test]
    fn decrqss_sgr_is_stable_and_preserves_every_attribute_and_color_kind() {
        let attributes = Attributes {
            bold: true,
            faint: true,
            italic: true,
            underline_style: UnderlineStyle::Curly,
            blink: true,
            inverse: true,
            conceal: true,
            strikethrough: true,
            overline: true,
            foreground: Color::Indexed(0),
            background: Color::Indexed(255),
            underline_color: Color::Rgb(1, 2, 3),
        };
        assert_eq!(
            bytes(build_decrqss_reply(b"m", state(attributes))),
            b"\x1bP1$r0;1;2;3;4:3;5;7;8;9;53;38;5;0;48;5;255;58;2;1;2;3m\x1b\\"
        );
        assert_eq!(
            bytes(build_decrqss_reply(b"m", state(Attributes::default()))),
            b"\x1bP1$r0m\x1b\\"
        );

        for (value, style) in [
            UnderlineStyle::Single,
            UnderlineStyle::Double,
            UnderlineStyle::Curly,
            UnderlineStyle::Dotted,
            UnderlineStyle::Dashed,
        ]
        .into_iter()
        .enumerate()
        {
            let attributes = Attributes {
                underline_style: style,
                ..Attributes::default()
            };
            let expected = format!("\x1bP1$r0;4:{}m\x1b\\", value + 1);
            assert_eq!(
                bytes(build_decrqss_reply(b"m", state(attributes))),
                expected.as_bytes()
            );
        }
    }

    #[test]
    fn decrqss_reports_all_cursor_style_pairs() {
        for (shape, blinking, parameter) in [
            (CursorShape::Block, true, 1),
            (CursorShape::Block, false, 2),
            (CursorShape::Underline, true, 3),
            (CursorShape::Underline, false, 4),
            (CursorShape::Bar, true, 5),
            (CursorShape::Bar, false, 6),
        ] {
            let custom = DecrqssState::new(
                Attributes::default(),
                Margins::full(80, 24),
                shape,
                blinking,
                false,
                AttributeChangeExtent::Stream,
            );
            assert_eq!(
                bytes(build_decrqss_reply(b" q", custom)),
                format!("\x1bP1$r{parameter} q\x1b\\").as_bytes()
            );
        }
    }

    #[test]
    fn xtgettcap_registry_is_canonical_mode_aware_and_transactional() {
        let request = b"436f;524742;6b63757531;6b7031";
        assert_eq!(
            bytes(build_xtgettcap_replies(request, XtgettcapModes::default())),
            b"\x1bP1+r436f=323536;524742=382f382f38;6b63757531=1b5b41;6b7031=31\x1b\\"
        );
        assert_eq!(
            bytes(build_xtgettcap_replies(
                b"436f;6e6f7065",
                XtgettcapModes::default(),
            )),
            b"\x1bP0+r\x1b\\"
        );

        assert_eq!(
            bytes(build_xtgettcap_replies(
                b"6b63757531;6b7031;6b656e74",
                XtgettcapModes {
                    cursor_keys_application: true,
                    keypad_application: true,
                    ..XtgettcapModes::default()
                },
            )),
            b"\x1bP1+r6b63757531=1b4f41;6b7031=1b4f71;6b656e74=1b4f4d\x1b\\"
        );
    }

    #[test]
    fn xtgettcap_keypad_position_aliases_match_the_existing_encoder_in_both_modes() {
        let cases = [
            (b"K1".as_slice(), KeypadKey::Digit7),
            (b"K2".as_slice(), KeypadKey::Digit5),
            (b"K3".as_slice(), KeypadKey::Digit9),
            (b"K4".as_slice(), KeypadKey::Digit1),
            (b"K5".as_slice(), KeypadKey::Digit3),
        ];
        for keypad_application in [false, true] {
            let modes = ModeState {
                keypad_application,
                ..ModeState::default()
            };
            for &(name, keypad_key) in &cases {
                assert_cap_matches_encoder(
                    name,
                    key(Key::Keypad(keypad_key), Modifiers::NONE),
                    &modes,
                );
            }
        }
    }

    #[test]
    fn xtgettcap_modified_navigation_registry_matches_every_encoder_sequence() {
        let capabilities = [
            (b"kDC".as_slice(), Key::Delete),
            (b"kDN".as_slice(), Key::ArrowDown),
            (b"kEND".as_slice(), Key::End),
            (b"kHOM".as_slice(), Key::Home),
            (b"kIC".as_slice(), Key::Insert),
            (b"kLFT".as_slice(), Key::ArrowLeft),
            (b"kNXT".as_slice(), Key::PageDown),
            (b"kPRV".as_slice(), Key::PageUp),
            (b"kRIT".as_slice(), Key::ArrowRight),
            (b"kUP".as_slice(), Key::ArrowUp),
        ];
        let modes = ModeState::default();
        for &(prefix, semantic_key) in &capabilities {
            for parameter in 2..=7 {
                let mut name = prefix.to_vec();
                if parameter != 2 {
                    name.push(b'0' + parameter);
                }
                assert_cap_matches_encoder(
                    &name,
                    key(semantic_key, modifiers_for_parameter(parameter)),
                    &modes,
                );
            }
        }

        for &(name, semantic_key) in &[
            (b"*4".as_slice(), Key::Delete),
            (b"kind".as_slice(), Key::ArrowDown),
            (b"kF".as_slice(), Key::ArrowDown),
            (b"*7".as_slice(), Key::End),
            (b"#2".as_slice(), Key::Home),
            (b"#3".as_slice(), Key::Insert),
            (b"#4".as_slice(), Key::ArrowLeft),
            (b"%c".as_slice(), Key::PageDown),
            (b"%e".as_slice(), Key::PageUp),
            (b"%i".as_slice(), Key::ArrowRight),
            (b"kri".as_slice(), Key::ArrowUp),
            (b"kR".as_slice(), Key::ArrowUp),
        ] {
            assert_cap_matches_encoder(name, key(semantic_key, modifiers_for_parameter(2)), &modes);
        }
    }

    #[test]
    fn xtgettcap_modified_function_registry_matches_every_encoder_sequence() {
        let modes = ModeState::default();
        for capability_number in 13..=63 {
            let (key_number, parameter) = match capability_number {
                13..=24 => (capability_number - 12, 2),
                25..=36 => (capability_number - 24, 5),
                37..=48 => (capability_number - 36, 6),
                49..=60 => (capability_number - 48, 3),
                61..=63 => (capability_number - 60, 4),
                _ => unreachable!(),
            };
            let event = key(function_key(key_number), modifiers_for_parameter(parameter));
            let terminfo_name = format!("kf{capability_number}");
            assert_cap_matches_encoder(terminfo_name.as_bytes(), event, &modes);
            assert_cap_matches_encoder(&function_termcap_alias(capability_number), event, &modes);
        }
    }

    #[test]
    fn xtgettcap_enter_backspace_and_backtab_follow_relevant_encoder_modes() {
        for modify_other_keys in [
            ModifyOtherKeys::Disabled,
            ModifyOtherKeys::Level1,
            ModifyOtherKeys::Level2,
            ModifyOtherKeys::Level3,
        ] {
            let mut modes = ModeState {
                modify_other_keys,
                ..ModeState::default()
            };
            assert_cap_matches_encoder(b"kbs", key(Key::Backspace, Modifiers::NONE), &modes);
            assert_cap_matches_encoder(b"kcbt", key(Key::Tab, modifiers_for_parameter(2)), &modes);

            modes.newline_mode = true;
            assert_cap_matches_encoder(
                b"kent",
                key(Key::Keypad(KeypadKey::Enter), Modifiers::NONE),
                &modes,
            );
            modes.keypad_application = true;
            assert_cap_matches_encoder(
                b"kent",
                key(Key::Keypad(KeypadKey::Enter), Modifiers::NONE),
                &modes,
            );
        }

        let modes = ModeState::default();
        assert_cap_matches_encoder(
            b"kent",
            key(Key::Keypad(KeypadKey::Enter), Modifiers::NONE),
            &modes,
        );
    }

    #[test]
    fn xtgettcap_preserves_duplicate_order_and_uses_one_lowercase_hex_reply() {
        assert_eq!(
            bytes(build_xtgettcap_replies(
                b"6B5550;6B663133;6B5550",
                XtgettcapModes::default(),
            )),
            b"\x1bP1+r6b5550=1b5b313b3241;6b663133=1b5b313b3250;6b5550=1b5b313b3241\x1b\\"
        );
    }

    #[test]
    fn xtgettcap_rejects_malformed_and_distinguishes_every_limit() {
        for malformed in [b"".as_slice(), b"0", b"00;", b";00", b"0g"] {
            assert_eq!(
                bytes(build_xtgettcap_replies(
                    malformed,
                    XtgettcapModes::default(),
                )),
                b"\x1bP0+r\x1b\\"
            );
        }

        assert_eq!(
            bytes(build_xtgettcap_replies(
                b"6b5550;6e6f7065;6b663133",
                XtgettcapModes::default(),
            )),
            b"\x1bP0+r\x1b\\"
        );

        let long_name = vec![b'0'; 2 * (MAX_XTGETTCAP_NAME_BYTES + 1)];
        assert_eq!(
            build_xtgettcap_replies(&long_name, XtgettcapModes::default()),
            Err(QueryReplyError::LimitExceeded {
                limit: QueryReplyLimit::XtgettcapNameBytes,
                maximum: MAX_XTGETTCAP_NAME_BYTES,
            })
        );

        let many = vec!["00"; MAX_XTGETTCAP_NAMES + 1].join(";");
        assert_eq!(
            build_xtgettcap_replies(many.as_bytes(), XtgettcapModes::default()),
            Err(QueryReplyError::LimitExceeded {
                limit: QueryReplyLimit::XtgettcapNames,
                maximum: MAX_XTGETTCAP_NAMES,
            })
        );

        let oversized = vec![b'0'; MAX_XTGETTCAP_REQUEST_BYTES + 1];
        assert_eq!(
            build_xtgettcap_replies(&oversized, XtgettcapModes::default()),
            Err(QueryReplyError::LimitExceeded {
                limit: QueryReplyLimit::XtgettcapRequestBytes,
                maximum: MAX_XTGETTCAP_REQUEST_BYTES,
            })
        );
    }

    #[test]
    fn xtgettcap_accepts_count_name_and_near_payload_boundaries() {
        let maximum_count = vec!["436f"; MAX_XTGETTCAP_NAMES].join(";");
        assert_eq!(
            parse_xtgettcap_names(maximum_count.as_bytes())
                .expect("maximum name count")
                .len(),
            MAX_XTGETTCAP_NAMES
        );
        assert!(
            build_xtgettcap_replies(maximum_count.as_bytes(), XtgettcapModes::default()).is_ok()
        );

        let maximum_dynamic_count = vec!["6b6273"; MAX_XTGETTCAP_NAMES].join(";");
        let maximum_dynamic_reply = build_xtgettcap_replies(
            maximum_dynamic_count.as_bytes(),
            XtgettcapModes {
                modify_other_keys: ModifyOtherKeys::Level3,
                ..XtgettcapModes::default()
            },
        )
        .expect("maximum dynamic capability count");
        let encoded_pair_bytes = b"6b6273".len() + 1 + b"\x1b[27;1;127~".len() * 2;
        assert_eq!(
            maximum_dynamic_reply.as_bytes().len(),
            b"\x1bP1+r".len()
                + MAX_XTGETTCAP_NAMES * encoded_pair_bytes
                + (MAX_XTGETTCAP_NAMES - 1)
                + ST.len()
        );
        assert!(maximum_dynamic_reply.as_bytes().len() < MAX_QUERY_REPLY_BYTES);

        let maximum_name = vec![b'0'; 2 * MAX_XTGETTCAP_NAME_BYTES];
        assert_eq!(
            parse_xtgettcap_names(&maximum_name)
                .expect("maximum name bytes")
                .len(),
            1
        );

        let near_payload_cap = vec!["00".repeat(MAX_XTGETTCAP_NAME_BYTES); 63].join(";");
        assert_eq!(near_payload_cap.len(), MAX_XTGETTCAP_REQUEST_BYTES - 2);
        assert_eq!(
            parse_xtgettcap_names(near_payload_cap.as_bytes())
                .expect("near maximum request bytes")
                .len(),
            63
        );

        let mut at_payload_cap = vec!["00".repeat(MAX_XTGETTCAP_NAME_BYTES); 62];
        at_payload_cap.extend(["00".repeat(30), "00".to_owned(), "00".to_owned()]);
        let at_payload_cap = at_payload_cap.join(";");
        assert_eq!(at_payload_cap.len(), MAX_XTGETTCAP_REQUEST_BYTES);
        assert_eq!(
            parse_xtgettcap_names(at_payload_cap.as_bytes())
                .expect("exact maximum request bytes")
                .len(),
            65
        );
    }

    #[test]
    fn osc_color_replies_use_four_digit_components_and_seven_bit_st() {
        assert_eq!(
            bytes(build_osc_palette_color_reply(
                255,
                OscColor::new(0, 0x80, 0xff)
            )),
            b"\x1b]4;255;rgb:0000/8080/ffff\x1b\\"
        );
        assert_eq!(
            bytes(build_osc_default_color_reply(
                DefaultColorSlot::Foreground,
                OscColor::new(0x12, 0xab, 0xcd),
            )),
            b"\x1b]10;rgb:1212/abab/cdcd\x1b\\"
        );
        assert_eq!(
            bytes(build_osc_default_color_reply(
                DefaultColorSlot::Background,
                OscColor::new(1, 2, 3),
            )),
            b"\x1b]11;rgb:0101/0202/0303\x1b\\"
        );
        assert_eq!(
            bytes(build_osc_default_color_reply(
                DefaultColorSlot::Cursor,
                OscColor::new(0xfe, 0xdc, 0xba),
            )),
            b"\x1b]12;rgb:fefe/dcdc/baba\x1b\\"
        );
    }

    #[test]
    fn reply_writer_enforces_the_single_reply_limit() {
        let mut at_limit =
            ReplyWriter::with_capacity(MAX_QUERY_REPLY_BYTES).expect("allocate maximum reply");
        at_limit
            .bytes(&vec![0; MAX_QUERY_REPLY_BYTES])
            .expect("write maximum reply");
        assert_eq!(at_limit.finish().as_bytes().len(), MAX_QUERY_REPLY_BYTES);

        let mut over_limit = ReplyWriter::with_capacity(0).expect("allocate empty writer");
        assert_eq!(
            over_limit.bytes(&vec![0; MAX_QUERY_REPLY_BYTES + 1]),
            Err(QueryReplyError::LimitExceeded {
                limit: QueryReplyLimit::ReplyBytes,
                maximum: MAX_QUERY_REPLY_BYTES,
            })
        );
    }
}
