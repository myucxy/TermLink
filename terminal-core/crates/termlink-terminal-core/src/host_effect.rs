use super::{TerminalError, crc32c};

pub const HOST_EFFECT_FRAME_V1_FORMAT_VERSION: u32 = 1;
pub const HOST_EFFECT_FRAME_V2_FORMAT_VERSION: u32 = 2;
pub const MAX_HOST_EFFECTS_PER_CAPTURE: usize = 1_398_102;
pub const MAX_HOST_EFFECT_FRAME_V1_BYTES: usize = 65_710_862;
pub const MAX_HOST_EFFECT_REPLY_V2_BYTES: usize = 64 * 1024;
pub const MAX_HOST_EFFECT_PAYLOAD_V2_BYTES: usize = 64 * 1024 * 1024;
pub const MAX_HOST_EFFECT_FRAME_V2_BYTES: usize = 123_033_008;

const MAGIC: &[u8; 8] = b"TLHEF1\0\0";
const MAGIC_V2: &[u8; 8] = b"TLHEF2\0\0";
const HEADER_SIZE: usize = 64;
const RECORD_SIZE: usize = 40;
const REQUIRED_FEATURES: u64 = 1;
const EFFECT_KIND_PTY_REPLY: u32 = 1;

#[derive(Clone, Copy, Debug)]
struct Record {
    source_sequence: u64,
    source_offset_end: u32,
    ordinal: u32,
    payload_offset: usize,
    payload_len: usize,
}

pub(crate) trait CaptureSink {
    fn push_reply(
        &mut self,
        source_sequence: u64,
        source_offset_end: u32,
        payload: &[u8],
        requires_v2: bool,
    ) -> Result<(), TerminalError>;
}

#[derive(Debug, Default)]
pub(crate) struct Capture {
    records: Vec<Record>,
    payload: Vec<u8>,
    last_key: Option<(u64, u32)>,
}

impl Capture {
    pub(crate) fn push(
        &mut self,
        source_sequence: u64,
        source_offset_end: u32,
        payload: &[u8],
    ) -> Result<(), TerminalError> {
        if self.records.len() >= MAX_HOST_EFFECTS_PER_CAPTURE {
            return Err(TerminalError::LimitExceeded);
        }
        let key = (source_sequence, source_offset_end);
        if self.last_key.is_some_and(|previous| previous >= key) {
            return Err(TerminalError::Unsupported("host effect ordering"));
        }
        debug_assert!(is_canonical_reply(payload));
        if payload.len() > 11 {
            return Err(TerminalError::LimitExceeded);
        }
        let payload_offset = self.payload.len();
        let new_payload_len = payload_offset
            .checked_add(payload.len())
            .ok_or(TerminalError::LimitExceeded)?;
        self.records
            .try_reserve(1)
            .map_err(|_| TerminalError::LimitExceeded)?;
        self.payload
            .try_reserve(payload.len())
            .map_err(|_| TerminalError::LimitExceeded)?;
        self.payload.extend_from_slice(payload);
        debug_assert_eq!(self.payload.len(), new_payload_len);
        self.records.push(Record {
            source_sequence,
            source_offset_end,
            ordinal: 0,
            payload_offset,
            payload_len: payload.len(),
        });
        self.last_key = Some(key);
        Ok(())
    }

    pub(crate) fn encode_v1(self) -> Result<Vec<u8>, TerminalError> {
        let records_bytes = self
            .records
            .len()
            .checked_mul(RECORD_SIZE)
            .ok_or(TerminalError::LimitExceeded)?;
        let payload_offset = HEADER_SIZE
            .checked_add(records_bytes)
            .ok_or(TerminalError::LimitExceeded)?;
        let total_len = payload_offset
            .checked_add(self.payload.len())
            .ok_or(TerminalError::LimitExceeded)?;
        if total_len > MAX_HOST_EFFECT_FRAME_V1_BYTES {
            return Err(TerminalError::LimitExceeded);
        }

        let mut frame = Vec::new();
        frame
            .try_reserve_exact(total_len)
            .map_err(|_| TerminalError::LimitExceeded)?;
        frame.resize(HEADER_SIZE, 0);
        frame[..8].copy_from_slice(MAGIC);
        put_u16(&mut frame, 8, 1);
        put_u16(&mut frame, 10, 0);
        put_u16(&mut frame, 12, HEADER_SIZE as u16);
        put_u16(&mut frame, 14, RECORD_SIZE as u16);
        put_u32(
            &mut frame,
            16,
            u32::try_from(self.records.len()).map_err(|_| TerminalError::LimitExceeded)?,
        );
        put_u32(
            &mut frame,
            20,
            u32::try_from(total_len).map_err(|_| TerminalError::LimitExceeded)?,
        );
        put_u64(&mut frame, 24, HEADER_SIZE as u64);
        put_u64(
            &mut frame,
            32,
            u64::try_from(payload_offset).map_err(|_| TerminalError::LimitExceeded)?,
        );
        put_u64(&mut frame, 40, REQUIRED_FEATURES);

        for record in self.records {
            let base = frame.len();
            frame.resize(base + RECORD_SIZE, 0);
            put_u32(&mut frame, base, EFFECT_KIND_PTY_REPLY);
            put_u64(&mut frame, base + 8, record.source_sequence);
            put_u32(&mut frame, base + 16, record.source_offset_end);
            put_u32(&mut frame, base + 20, record.ordinal);
            let absolute_payload_offset = payload_offset
                .checked_add(record.payload_offset)
                .ok_or(TerminalError::LimitExceeded)?;
            put_u64(
                &mut frame,
                base + 24,
                u64::try_from(absolute_payload_offset).map_err(|_| TerminalError::LimitExceeded)?,
            );
            put_u32(
                &mut frame,
                base + 32,
                u32::try_from(record.payload_len).map_err(|_| TerminalError::LimitExceeded)?,
            );
        }
        frame.extend_from_slice(&self.payload);
        debug_assert_eq!(frame.len(), total_len);
        let payload_crc = crc32c::checksum(&frame[HEADER_SIZE..]);
        put_u32(&mut frame, 56, payload_crc);
        let header_crc = crc32c::checksum(&frame[..60]);
        put_u32(&mut frame, 60, header_crc);
        Ok(frame)
    }
}

impl CaptureSink for Capture {
    fn push_reply(
        &mut self,
        source_sequence: u64,
        source_offset_end: u32,
        payload: &[u8],
        requires_v2: bool,
    ) -> Result<(), TerminalError> {
        if requires_v2 {
            return Err(TerminalError::HostEffectVersionRequired);
        }
        self.push(source_sequence, source_offset_end, payload)
    }
}

#[derive(Debug, Default)]
pub(crate) struct CaptureV2 {
    records: Vec<Record>,
    payload: Vec<u8>,
    last_key: Option<(u64, u32)>,
    last_ordinal: u32,
}

impl CaptureV2 {
    pub(crate) fn push(
        &mut self,
        source_sequence: u64,
        source_offset_end: u32,
        payload: &[u8],
    ) -> Result<(), TerminalError> {
        if self.records.len() >= MAX_HOST_EFFECTS_PER_CAPTURE
            || payload.len() > MAX_HOST_EFFECT_REPLY_V2_BYTES
        {
            return Err(TerminalError::LimitExceeded);
        }

        let key = (source_sequence, source_offset_end);
        let ordinal = match self.last_key {
            None => 0,
            Some(previous) if previous < key => 0,
            Some(previous) if previous == key => self
                .last_ordinal
                .checked_add(1)
                .ok_or(TerminalError::LimitExceeded)?,
            Some(_) => return Err(TerminalError::Unsupported("host effect ordering")),
        };
        if !is_canonical_reply_v2(payload) {
            return Err(TerminalError::Unsupported("host effect reply grammar"));
        }

        let payload_offset = self.payload.len();
        let new_payload_len = payload_offset
            .checked_add(payload.len())
            .ok_or(TerminalError::LimitExceeded)?;
        if new_payload_len > MAX_HOST_EFFECT_PAYLOAD_V2_BYTES {
            return Err(TerminalError::LimitExceeded);
        }
        self.records
            .try_reserve(1)
            .map_err(|_| TerminalError::LimitExceeded)?;
        self.payload
            .try_reserve(payload.len())
            .map_err(|_| TerminalError::LimitExceeded)?;
        self.payload.extend_from_slice(payload);
        debug_assert_eq!(self.payload.len(), new_payload_len);
        self.records.push(Record {
            source_sequence,
            source_offset_end,
            ordinal,
            payload_offset,
            payload_len: payload.len(),
        });
        self.last_key = Some(key);
        self.last_ordinal = ordinal;
        Ok(())
    }

    pub(crate) fn encode_v2(self) -> Result<Vec<u8>, TerminalError> {
        if self.records.len() > MAX_HOST_EFFECTS_PER_CAPTURE
            || self.payload.len() > MAX_HOST_EFFECT_PAYLOAD_V2_BYTES
        {
            return Err(TerminalError::LimitExceeded);
        }
        let records_bytes = self
            .records
            .len()
            .checked_mul(RECORD_SIZE)
            .ok_or(TerminalError::LimitExceeded)?;
        let payload_offset = HEADER_SIZE
            .checked_add(records_bytes)
            .ok_or(TerminalError::LimitExceeded)?;
        let total_len = payload_offset
            .checked_add(self.payload.len())
            .ok_or(TerminalError::LimitExceeded)?;
        if total_len > MAX_HOST_EFFECT_FRAME_V2_BYTES {
            return Err(TerminalError::LimitExceeded);
        }

        let mut frame = Vec::new();
        frame
            .try_reserve_exact(total_len)
            .map_err(|_| TerminalError::LimitExceeded)?;
        frame.resize(HEADER_SIZE, 0);
        frame[..8].copy_from_slice(MAGIC_V2);
        put_u16(&mut frame, 8, 2);
        put_u16(&mut frame, 10, 0);
        put_u16(&mut frame, 12, HEADER_SIZE as u16);
        put_u16(&mut frame, 14, RECORD_SIZE as u16);
        put_u32(
            &mut frame,
            16,
            u32::try_from(self.records.len()).map_err(|_| TerminalError::LimitExceeded)?,
        );
        put_u32(
            &mut frame,
            20,
            u32::try_from(total_len).map_err(|_| TerminalError::LimitExceeded)?,
        );
        put_u64(&mut frame, 24, HEADER_SIZE as u64);
        put_u64(
            &mut frame,
            32,
            u64::try_from(payload_offset).map_err(|_| TerminalError::LimitExceeded)?,
        );
        put_u64(&mut frame, 40, REQUIRED_FEATURES);

        for record in self.records {
            let base = frame.len();
            frame.resize(base + RECORD_SIZE, 0);
            put_u32(&mut frame, base, EFFECT_KIND_PTY_REPLY);
            put_u64(&mut frame, base + 8, record.source_sequence);
            put_u32(&mut frame, base + 16, record.source_offset_end);
            put_u32(&mut frame, base + 20, record.ordinal);
            let absolute_payload_offset = payload_offset
                .checked_add(record.payload_offset)
                .ok_or(TerminalError::LimitExceeded)?;
            put_u64(
                &mut frame,
                base + 24,
                u64::try_from(absolute_payload_offset).map_err(|_| TerminalError::LimitExceeded)?,
            );
            put_u32(
                &mut frame,
                base + 32,
                u32::try_from(record.payload_len).map_err(|_| TerminalError::LimitExceeded)?,
            );
        }
        frame.extend_from_slice(&self.payload);
        debug_assert_eq!(frame.len(), total_len);
        let payload_crc = crc32c::checksum(&frame[HEADER_SIZE..]);
        put_u32(&mut frame, 56, payload_crc);
        let header_crc = crc32c::checksum(&frame[..60]);
        put_u32(&mut frame, 60, header_crc);
        Ok(frame)
    }
}

impl CaptureSink for CaptureV2 {
    fn push_reply(
        &mut self,
        source_sequence: u64,
        source_offset_end: u32,
        payload: &[u8],
        _requires_v2: bool,
    ) -> Result<(), TerminalError> {
        self.push(source_sequence, source_offset_end, payload)
    }
}

fn is_canonical_reply(payload: &[u8]) -> bool {
    if matches!(
        payload,
        b"\x1b[?1;2c" | b"\x1b[>0;0;0c" | b"\x1b[0n" | b"\x1b[?0n"
    ) {
        return true;
    }
    let Some(body) = payload
        .strip_prefix(b"\x1b[")
        .and_then(|value| value.strip_suffix(b"R"))
    else {
        return false;
    };
    let body = body.strip_prefix(b"?").unwrap_or(body);
    let Some(separator) = body.iter().position(|&byte| byte == b';') else {
        return false;
    };
    canonical_coordinate(&body[..separator]) && canonical_coordinate(&body[separator + 1..])
}

fn is_canonical_reply_v2(payload: &[u8]) -> bool {
    is_canonical_reply(payload)
        || is_canonical_keyboard_reply(payload)
        || is_canonical_decrpm(payload)
        || is_canonical_decrqss(payload)
        || is_canonical_xtversion(payload)
        || is_canonical_xtgettcap(payload)
        || is_canonical_osc_color(payload)
}

fn is_canonical_xtversion(payload: &[u8]) -> bool {
    payload == b"\x1bP>|TermLink(1)\x1b\\"
}

fn is_canonical_keyboard_reply(payload: &[u8]) -> bool {
    payload
        .strip_prefix(b"\x1b[?")
        .and_then(|value| value.strip_suffix(b"u"))
        .is_some_and(canonical_keyboard_flags)
}

fn is_canonical_decrpm(payload: &[u8]) -> bool {
    let Some(body) = payload
        .strip_prefix(b"\x1b[")
        .and_then(|value| value.strip_suffix(b"$y"))
    else {
        return false;
    };
    let body = body.strip_prefix(b"?").unwrap_or(body);
    let Some(separator) = body.iter().position(|&byte| byte == b';') else {
        return false;
    };
    canonical_u32(&body[..separator]).is_some()
        && matches!(&body[separator + 1..], b"0" | b"1" | b"2" | b"3" | b"4")
}

fn is_canonical_decrqss(payload: &[u8]) -> bool {
    let Some(body) = payload
        .strip_prefix(b"\x1bP")
        .and_then(|value| value.strip_suffix(b"\x1b\\"))
    else {
        return false;
    };
    if body == b"0$r" {
        return true;
    }
    let Some(status) = body.strip_prefix(b"1$r") else {
        return false;
    };
    is_canonical_sgr_status(status)
        || is_canonical_margin_status(status, b'r')
        || is_canonical_margin_status(status, b's')
        || matches!(status, b"1 q" | b"2 q" | b"3 q" | b"4 q" | b"5 q" | b"6 q")
        || matches!(status, b"1\"q" | b"2\"q" | b"1*x" | b"2*x")
}

fn is_canonical_sgr_status(status: &[u8]) -> bool {
    let Some(parameters) = status.strip_suffix(b"m") else {
        return false;
    };
    if parameters == b"0" {
        return true;
    }
    let Some(parameters) = parameters.strip_prefix(b"0;") else {
        return false;
    };

    let mut fields = parameters.split(|&byte| byte == b';').peekable();
    let mut last_rank = 0_u8;
    while let Some(field) = fields.next() {
        let (rank, valid) = match field {
            b"1" => (1, true),
            b"2" => (2, true),
            b"3" => (3, true),
            b"4:1" | b"4:2" | b"4:3" | b"4:4" | b"4:5" => (4, true),
            b"5" => (5, true),
            b"7" => (6, true),
            b"8" => (7, true),
            b"9" => (8, true),
            b"53" => (9, true),
            b"38" => (10, consume_canonical_sgr_color(&mut fields)),
            b"48" => (11, consume_canonical_sgr_color(&mut fields)),
            b"58" => (12, consume_canonical_sgr_color(&mut fields)),
            _ => (0, false),
        };
        if !valid || rank <= last_rank {
            return false;
        }
        last_rank = rank;
    }
    last_rank != 0
}

fn consume_canonical_sgr_color<'a, I>(fields: &mut I) -> bool
where
    I: Iterator<Item = &'a [u8]>,
{
    match fields.next() {
        Some(b"5") => fields
            .next()
            .and_then(canonical_u32)
            .is_some_and(|value| value <= 255),
        Some(b"2") => (0..3).all(|_| {
            fields
                .next()
                .and_then(canonical_u32)
                .is_some_and(|value| value <= 255)
        }),
        _ => false,
    }
}

fn is_canonical_margin_status(status: &[u8], final_byte: u8) -> bool {
    let Some(body) = status.strip_suffix(&[final_byte]) else {
        return false;
    };
    let Some(separator) = body.iter().position(|&byte| byte == b';') else {
        return false;
    };
    canonical_coordinate(&body[..separator]) && canonical_coordinate(&body[separator + 1..])
}

fn is_canonical_xtgettcap(payload: &[u8]) -> bool {
    let Some(body) = payload
        .strip_prefix(b"\x1bP")
        .and_then(|value| value.strip_suffix(b"\x1b\\"))
    else {
        return false;
    };
    if body == b"0+r" {
        return true;
    }
    let Some(pairs) = body.strip_prefix(b"1+r") else {
        return false;
    };
    if pairs.is_empty() {
        return false;
    }
    let mut count = 0_usize;
    pairs.split(|&byte| byte == b';').all(|pair| {
        count += 1;
        count <= 256 && is_canonical_xtgettcap_pair(pair)
    })
}

fn is_canonical_xtgettcap_pair(pair: &[u8]) -> bool {
    let mut fields = pair.split(|&byte| byte == b'=');
    let (Some(name), Some(value), None) = (fields.next(), fields.next(), fields.next()) else {
        return false;
    };
    canonical_hex(name, 64) && canonical_hex(value, usize::MAX)
}

fn is_canonical_osc_color(payload: &[u8]) -> bool {
    let Some(body) = payload
        .strip_prefix(b"\x1b]")
        .and_then(|value| value.strip_suffix(b"\x1b\\"))
    else {
        return false;
    };
    let mut fields = body.split(|&byte| byte == b';');
    match (fields.next(), fields.next(), fields.next(), fields.next()) {
        (Some(b"4"), Some(index), Some(color), None) => {
            canonical_u32(index).is_some_and(|value| value <= 255) && is_canonical_rgb_color(color)
        }
        (Some(selector @ (b"10" | b"11" | b"12")), Some(color), None, None) => {
            debug_assert!(matches!(selector, b"10" | b"11" | b"12"));
            is_canonical_rgb_color(color)
        }
        _ => false,
    }
}

fn is_canonical_rgb_color(color: &[u8]) -> bool {
    let Some(components) = color.strip_prefix(b"rgb:") else {
        return false;
    };
    let mut components = components.split(|&byte| byte == b'/');
    let (Some(red), Some(green), Some(blue), None) = (
        components.next(),
        components.next(),
        components.next(),
        components.next(),
    ) else {
        return false;
    };
    [red, green, blue]
        .into_iter()
        .all(is_canonical_repeated_byte_hex)
}

fn is_canonical_repeated_byte_hex(component: &[u8]) -> bool {
    component.len() == 4
        && component[..2] == component[2..]
        && component.iter().all(|byte| byte.is_ascii_hexdigit())
        && component.iter().all(|byte| !byte.is_ascii_uppercase())
}

fn canonical_hex(bytes: &[u8], maximum_len: usize) -> bool {
    !bytes.is_empty()
        && bytes.len() <= maximum_len
        && bytes.len().is_multiple_of(2)
        && bytes.iter().all(|byte| byte.is_ascii_hexdigit())
        && bytes.iter().all(|byte| !byte.is_ascii_uppercase())
}

fn canonical_u32(bytes: &[u8]) -> Option<u32> {
    if bytes.is_empty() || (bytes.len() > 1 && bytes[0] == b'0') {
        return None;
    }
    bytes.iter().try_fold(0_u32, |value, &byte| {
        byte.is_ascii_digit()
            .then_some(())
            .and_then(|()| value.checked_mul(10))
            .and_then(|value| value.checked_add(u32::from(byte - b'0')))
    })
}

fn canonical_keyboard_flags(bytes: &[u8]) -> bool {
    if bytes.is_empty() || bytes.len() > 2 || (bytes.len() > 1 && bytes[0] == b'0') {
        return false;
    }
    let mut value = 0_u8;
    for &byte in bytes {
        if !byte.is_ascii_digit() {
            return false;
        }
        value = value * 10 + (byte - b'0');
    }
    value <= 31
}

fn canonical_coordinate(bytes: &[u8]) -> bool {
    if bytes.is_empty() || bytes.len() > 3 || (bytes.len() > 1 && bytes[0] == b'0') {
        return false;
    }
    let mut value = 0_u32;
    for &byte in bytes {
        if !byte.is_ascii_digit() {
            return false;
        }
        value = value * 10 + u32::from(byte - b'0');
    }
    (1..=512).contains(&value)
}

fn put_u16(bytes: &mut [u8], offset: usize, value: u16) {
    bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
}

fn put_u32(bytes: &mut [u8], offset: usize, value: u32) {
    bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
}

fn put_u64(bytes: &mut [u8], offset: usize, value: u64) {
    bytes[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use super::*;

    fn u32_at(bytes: &[u8], offset: usize) -> u32 {
        u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("u32 field"))
    }

    fn u64_at(bytes: &[u8], offset: usize) -> u64 {
        u64::from_le_bytes(bytes[offset..offset + 8].try_into().expect("u64 field"))
    }

    #[test]
    fn empty_frame_has_exact_header_and_crc() {
        let frame = Capture::default().encode_v1().expect("empty frame");
        assert_eq!(frame.len(), 64);
        assert_eq!(&frame[..8], MAGIC);
        assert_eq!(&frame[8..12], &[1, 0, 0, 0]);
        assert_eq!(&frame[12..16], &[64, 0, 40, 0]);
        assert_eq!(u32_at(&frame, 16), 0);
        assert_eq!(u32_at(&frame, 20), 64);
        assert_eq!(u64_at(&frame, 24), 64);
        assert_eq!(u64_at(&frame, 32), 64);
        assert_eq!(u64_at(&frame, 40), 1);
        assert_eq!(u64_at(&frame, 48), 0);
        assert_eq!(u32_at(&frame, 56), crc32c::checksum(&[]));
        assert_eq!(u32_at(&frame, 60), crc32c::checksum(&frame[..60]));
    }

    #[test]
    fn record_offsets_ordinals_and_payload_crc_are_canonical() {
        let mut capture = Capture::default();
        capture.push(7, 3, b"\x1b[?1;2c").expect("first effect");
        capture.push(7, 4, b"\x1b[0n").expect("next-offset effect");
        capture
            .push(8, 1, b"\x1b[?0n")
            .expect("next-sequence effect");
        let frame = capture.encode_v1().expect("frame");
        assert_eq!(u32_at(&frame, 16), 3);
        assert_eq!(u64_at(&frame, 32), 184);
        assert_eq!(u64_at(&frame, 64 + 24), 184);
        assert_eq!(u32_at(&frame, 64 + 20), 0);
        assert_eq!(u64_at(&frame, 104 + 24), 191);
        assert_eq!(u32_at(&frame, 104 + 20), 0);
        assert_eq!(u64_at(&frame, 144 + 24), 195);
        assert_eq!(u32_at(&frame, 144 + 20), 0);
        assert_eq!(&frame[184..], b"\x1b[?1;2c\x1b[0n\x1b[?0n");
        assert_eq!(u32_at(&frame, 56), crc32c::checksum(&frame[64..]));
        assert_eq!(u32_at(&frame, 60), crc32c::checksum(&frame[..60]));
    }

    #[test]
    fn duplicate_or_regressing_effect_keys_are_rejected() {
        let mut capture = Capture::default();
        capture.push(7, 3, b"\x1b[0n").expect("first effect");
        assert_eq!(
            capture.push(7, 3, b"\x1b[?0n"),
            Err(TerminalError::Unsupported("host effect ordering"))
        );
        assert_eq!(
            capture.push(6, 9, b"\x1b[?0n"),
            Err(TerminalError::Unsupported("host effect ordering"))
        );
    }

    #[test]
    fn kitty_keyboard_replies_are_canonical_only_for_supported_decimal_flags() {
        for flags in 0..=31 {
            let payload = format!("\x1b[?{flags}u");
            assert!(!is_canonical_reply(payload.as_bytes()), "V1 flags {flags}");
            assert!(
                is_canonical_keyboard_reply(payload.as_bytes()),
                "V2 flags {flags}"
            );
        }
        for malformed in [
            &b"\x1b[?u"[..],
            b"\x1b[?00u",
            b"\x1b[?01u",
            b"\x1b[?32u",
            b"\x1b[?-1u",
            b"\x1b[?1;2u",
            b"\x1b[31u",
            b"\x1b[?31U",
        ] {
            assert!(!is_canonical_keyboard_reply(malformed), "{malformed:?}");
        }
    }

    #[test]
    fn published_bounds_are_fixed_and_hard_limit_covers_maximal_coordinates() {
        assert_eq!(MAX_HOST_EFFECTS_PER_CAPTURE, 1 + (4 * 1024 * 1024 - 1) / 3);
        assert_eq!(MAX_HOST_EFFECT_FRAME_V1_BYTES, 65_710_862);
        assert_eq!(b"\x1b[?512;512R".len(), 11);
        assert_eq!(
            64 + MAX_HOST_EFFECTS_PER_CAPTURE * RECORD_SIZE
                + b"\x1b[?512;512R".len()
                + (MAX_HOST_EFFECTS_PER_CAPTURE - 1) * b"\x1b[?1;2c".len(),
            MAX_HOST_EFFECT_FRAME_V1_BYTES
        );
    }

    #[test]
    fn v2_empty_frame_has_exact_header_and_crc() {
        let frame = CaptureV2::default().encode_v2().expect("empty V2 frame");
        assert_eq!(frame.len(), 64);
        assert_eq!(&frame[..8], MAGIC_V2);
        assert_eq!(&frame[8..12], &[2, 0, 0, 0]);
        assert_eq!(&frame[12..16], &[64, 0, 40, 0]);
        assert_eq!(u32_at(&frame, 16), 0);
        assert_eq!(u32_at(&frame, 20), 64);
        assert_eq!(u64_at(&frame, 24), 64);
        assert_eq!(u64_at(&frame, 32), 64);
        assert_eq!(u64_at(&frame, 40), 1);
        assert_eq!(u64_at(&frame, 48), 0);
        assert_eq!(u32_at(&frame, 56), crc32c::checksum(&[]));
        assert_eq!(u32_at(&frame, 60), crc32c::checksum(&frame[..60]));
    }

    #[test]
    fn v2_repeated_source_keys_receive_contiguous_ordinals() {
        let mut capture = CaptureV2::default();
        capture.push(7, 3, b"\x1b[0n").expect("first reply");
        capture.push(7, 3, b"\x1b[?25;1$y").expect("same-key reply");
        capture
            .push(7, 4, b"\x1bP0$r\x1b\\")
            .expect("next-key reply");

        let frame = capture.encode_v2().expect("V2 frame");
        assert_eq!(u32_at(&frame, 16), 3);
        assert_eq!(u64_at(&frame, 32), 184);
        assert_eq!(u32_at(&frame, 64), EFFECT_KIND_PTY_REPLY);
        assert_eq!(u64_at(&frame, 64 + 8), 7);
        assert_eq!(u32_at(&frame, 64 + 16), 3);
        assert_eq!(u32_at(&frame, 64 + 20), 0);
        assert_eq!(u64_at(&frame, 64 + 24), 184);
        assert_eq!(u32_at(&frame, 104 + 20), 1);
        assert_eq!(u64_at(&frame, 104 + 24), 188);
        assert_eq!(u32_at(&frame, 144 + 16), 4);
        assert_eq!(u32_at(&frame, 144 + 20), 0);
        assert_eq!(u64_at(&frame, 144 + 24), 197);
        assert_eq!(&frame[184..], b"\x1b[0n\x1b[?25;1$y\x1bP0$r\x1b\\");
        assert_eq!(u32_at(&frame, 56), crc32c::checksum(&frame[64..]));
        assert_eq!(u32_at(&frame, 60), crc32c::checksum(&frame[..60]));
    }

    #[test]
    fn v2_regressing_keys_are_rejected_without_consuming_an_ordinal() {
        let mut capture = CaptureV2::default();
        capture.push(7, 3, b"\x1b[0n").expect("first reply");
        assert_eq!(
            capture.push(6, 9, b"\x1b[?0n"),
            Err(TerminalError::Unsupported("host effect ordering"))
        );
        capture.push(7, 3, b"\x1b[?0n").expect("same-key retry");
        let frame = capture.encode_v2().expect("V2 frame");
        assert_eq!(u32_at(&frame, 64 + 20), 0);
        assert_eq!(u32_at(&frame, 104 + 20), 1);
    }

    #[test]
    fn capture_sink_selects_the_wire_profile_before_mutation() {
        let mut v1 = Capture::default();
        assert_eq!(
            v1.push_reply(1, 1, b"\x1b[?25;1$y", true),
            Err(TerminalError::HostEffectVersionRequired)
        );
        assert_eq!(v1.encode_v1().expect("unchanged V1 capture").len(), 64);

        let mut v2 = CaptureV2::default();
        v2.push_reply(1, 1, b"\x1b[?25;1$y", true)
            .expect("V2-only reply");
        v2.push_reply(1, 1, b"\x1b[0n", false)
            .expect("legacy reply in V2");
        let frame = v2.encode_v2().expect("mixed V2 frame");
        assert_eq!(u32_at(&frame, 64 + 20), 0);
        assert_eq!(u32_at(&frame, 104 + 20), 1);
    }

    #[test]
    fn v2_reply_grammar_admits_only_canonical_supported_families() {
        for reply in [
            &b"\x1b[?1;2c"[..],
            b"\x1b[?31u",
            b"\x1b[512;512R",
            b"\x1b[4;2$y",
            b"\x1b[?25;1$y",
            b"\x1bP0$r\x1b\\",
            b"\x1bP1$r0m\x1b\\",
            b"\x1bP1$r0;1;2;3;4:5;5;7;8;9;53;38;5;255;48;2;1;2;3;58;2;255;0;128m\x1b\\",
            b"\x1bP1$r1;512r\x1b\\",
            b"\x1bP1$r1;512s\x1b\\",
            b"\x1bP1$r6 q\x1b\\",
            b"\x1bP1$r2\"q\x1b\\",
            b"\x1bP1$r2*x\x1b\\",
            b"\x1bP>|TermLink(1)\x1b\\",
            b"\x1bP1+r436f=323536\x1b\\",
            b"\x1bP1+r436f=323536;524742=382f382f38\x1b\\",
            b"\x1bP0+r\x1b\\",
            b"\x1b]4;255;rgb:ffff/0000/1212\x1b\\",
            b"\x1b]10;rgb:ffff/0000/1212\x1b\\",
            b"\x1b]11;rgb:ffff/0000/1212\x1b\\",
            b"\x1b]12;rgb:ffff/0000/1212\x1b\\",
        ] {
            assert!(is_canonical_reply_v2(reply), "canonical reply {reply:?}");
        }

        for reply in [
            &b"\x1b[31m"[..],
            b"\x1b[?025;1$y",
            b"\x1b[?25;5$y",
            b"\x1bP1$rarbitrary\x1b\\",
            b"\x1bP>|TermLink(01)\x1b\\",
            b"\x1bP>|Other(1)\x1b\\",
            b"\x1bP1$r0;2;1m\x1b\\",
            b"\x1bP1+r436F=323536\x1b\\",
            b"\x1bP1+r436f=323536;\x1b\\",
            b"\x1bP1+r436f=323536=30\x1b\\",
            b"\x1bP0+r544e\x1b\\",
            b"\x1b]4;01;rgb:ffff/0000/1212\x1b\\",
            b"\x1b]4;1;rgb:1234/0000/1212\x1b\\",
            b"\x1b]10;rgb:FFFF/0000/1212\x1b\\",
            b"\x1b]10;rgb:ffff/0000/1212\x07",
            b"\x1b]10;rgb:ffff/0000/1212\x9c",
        ] {
            assert!(!is_canonical_reply_v2(reply), "invalid reply {reply:?}");
        }

        let too_many_pairs = format!("\x1bP1+r{}\x1b\\", ["41=42"; 257].join(";"));
        assert!(!is_canonical_reply_v2(too_many_pairs.as_bytes()));
    }

    #[test]
    fn v2_enforces_reply_and_aggregate_payload_limits() {
        let mut maximum_reply = b"\x1bP1+r41=".to_vec();
        maximum_reply.resize(MAX_HOST_EFFECT_REPLY_V2_BYTES - 2, b'0');
        maximum_reply.extend_from_slice(b"\x1b\\");
        assert_eq!(maximum_reply.len(), MAX_HOST_EFFECT_REPLY_V2_BYTES);
        assert!(is_canonical_reply_v2(&maximum_reply));
        CaptureV2::default()
            .push(1, 1, &maximum_reply)
            .expect("reply at cap");

        maximum_reply.insert(maximum_reply.len() - 2, b'0');
        assert_eq!(maximum_reply.len(), MAX_HOST_EFFECT_REPLY_V2_BYTES + 1);
        assert_eq!(
            CaptureV2::default().push(1, 1, &maximum_reply),
            Err(TerminalError::LimitExceeded)
        );

        let mut capture = CaptureV2 {
            payload: vec![0; MAX_HOST_EFFECT_PAYLOAD_V2_BYTES],
            ..CaptureV2::default()
        };
        assert_eq!(
            capture.push(1, 1, b"\x1b[0n"),
            Err(TerminalError::LimitExceeded)
        );
    }

    #[test]
    fn v2_published_bounds_are_exact() {
        assert_eq!(HOST_EFFECT_FRAME_V1_FORMAT_VERSION, 1);
        assert_eq!(HOST_EFFECT_FRAME_V2_FORMAT_VERSION, 2);
        assert_eq!(MAX_HOST_EFFECT_REPLY_V2_BYTES, 65_536);
        assert_eq!(MAX_HOST_EFFECT_PAYLOAD_V2_BYTES, 67_108_864);
        assert_eq!(MAX_HOST_EFFECTS_PER_CAPTURE, 1_398_102);
        assert_eq!(
            HEADER_SIZE
                + MAX_HOST_EFFECTS_PER_CAPTURE * RECORD_SIZE
                + MAX_HOST_EFFECT_PAYLOAD_V2_BYTES,
            MAX_HOST_EFFECT_FRAME_V2_BYTES
        );
        assert_eq!(MAX_HOST_EFFECT_FRAME_V2_BYTES, 123_033_008);
    }
}
