//! Bounded, byte-oriented ECMA-48 control-sequence parser.

pub(super) const MAX_CSI_PARAMS: usize = 32;
// The all-ones value remains the missing-parameter sentinel, so 65_534 is the
// largest explicit value accepted by the existing wire representation.
pub(super) const PARAM_MISSING: u16 = u16::MAX;

const MAX_PARAMETER_BYTES: usize = 128;
const MAX_INTERMEDIATES: usize = 2;
const MAX_OSC_SELECTOR_BYTES: usize = 16;
const MAX_OSC_PAYLOAD_BYTES: usize = 64 * 1024;
const MAX_OSC_TITLE_BYTES: usize = 4 * 1024;
const MAX_OSC_COLOR_BYTES: usize = 4 * 1024;
const MAX_OSC_PALETTE_BYTES: usize = 16 * 1024;
const MAX_DCS_QUERY_PAYLOAD_BYTES: usize = 4 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum ParameterSeparator {
    Semicolon,
    Colon,
}

#[cfg(test)]
std::thread_local! {
    static FAIL_NEXT_OSC_PAYLOAD_ALLOCATION: std::cell::Cell<bool> =
        const { std::cell::Cell::new(false) };
    static FAIL_NEXT_DCS_QUERY_PAYLOAD_ALLOCATION: std::cell::Cell<bool> =
        const { std::cell::Cell::new(false) };
}

#[cfg(test)]
pub(super) fn fail_next_osc_payload_allocation_for_test() {
    FAIL_NEXT_OSC_PAYLOAD_ALLOCATION.with(|fail| fail.set(true));
}

#[cfg(test)]
fn take_osc_payload_allocation_failure_for_test() -> bool {
    FAIL_NEXT_OSC_PAYLOAD_ALLOCATION.with(std::cell::Cell::take)
}

#[cfg(test)]
pub(super) fn fail_next_dcs_query_payload_allocation_for_test() {
    FAIL_NEXT_DCS_QUERY_PAYLOAD_ALLOCATION.with(|fail| fail.set(true));
}

#[cfg(test)]
fn take_dcs_query_payload_allocation_failure_for_test() -> bool {
    FAIL_NEXT_DCS_QUERY_PAYLOAD_ALLOCATION.with(std::cell::Cell::take)
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) enum Action {
    None,
    Print(u8),
    Execute(u8),
    Sequence(Sequence),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) enum Sequence {
    Esc(EscSequence),
    Csi(CsiSequence),
    Osc(OscSequence),
    Dcs(DcsSequence),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct EscSequence {
    intermediates: [u8; MAX_INTERMEDIATES],
    intermediate_count: u8,
    final_byte: u8,
}

impl EscSequence {
    pub(super) fn intermediates(&self) -> &[u8] {
        &self.intermediates[..usize::from(self.intermediate_count)]
    }

    pub(super) fn final_byte(&self) -> u8 {
        self.final_byte
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct CsiSequence {
    private_prefix: Option<u8>,
    parameters: [u16; MAX_CSI_PARAMS],
    parameter_count: u8,
    separators: [ParameterSeparator; MAX_CSI_PARAMS - 1],
    intermediates: [u8; MAX_INTERMEDIATES],
    intermediate_count: u8,
    final_byte: u8,
}

impl CsiSequence {
    pub(super) fn private_prefix(&self) -> Option<u8> {
        self.private_prefix
    }

    pub(super) fn parameter(&self, index: usize, default: u16) -> u16 {
        self.parameters
            .get(index)
            .copied()
            .filter(|value| *value != PARAM_MISSING)
            .unwrap_or(default)
    }

    pub(super) fn parameters(&self) -> &[u16] {
        &self.parameters[..usize::from(self.parameter_count)]
    }

    pub(super) fn parameter_separators(&self) -> &[ParameterSeparator] {
        let separator_count = usize::from(self.parameter_count).saturating_sub(1);
        &self.separators[..separator_count]
    }

    pub(super) fn has_subparameters(&self) -> bool {
        self.parameter_separators()
            .contains(&ParameterSeparator::Colon)
    }

    pub(super) fn intermediates(&self) -> &[u8] {
        &self.intermediates[..usize::from(self.intermediate_count)]
    }

    pub(super) fn final_byte(&self) -> u8 {
        self.final_byte
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct OscSequence {
    selector: u16,
    payload: Vec<u8>,
}

impl OscSequence {
    pub(super) fn selector(&self) -> u16 {
        self.selector
    }

    #[allow(dead_code)] // Reserved for the next semantic OSC dispatch layer.
    pub(super) fn payload(&self) -> &[u8] {
        &self.payload
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct DcsSequence {
    private_prefix: Option<u8>,
    parameters: [u16; MAX_CSI_PARAMS],
    parameter_count: u8,
    separators: [ParameterSeparator; MAX_CSI_PARAMS - 1],
    discarded_subparameters: bool,
    intermediates: [u8; MAX_INTERMEDIATES],
    intermediate_count: u8,
    final_byte: u8,
    payload: Vec<u8>,
}

impl DcsSequence {
    pub(super) fn private_prefix(&self) -> Option<u8> {
        self.private_prefix
    }

    pub(super) fn parameters(&self) -> &[u16] {
        &self.parameters[..usize::from(self.parameter_count)]
    }

    pub(super) fn has_subparameters(&self) -> bool {
        let separator_count = usize::from(self.parameter_count).saturating_sub(1);
        self.discarded_subparameters
            || self.separators[..separator_count].contains(&ParameterSeparator::Colon)
    }

    pub(super) fn intermediates(&self) -> &[u8] {
        &self.intermediates[..usize::from(self.intermediate_count)]
    }

    pub(super) fn final_byte(&self) -> u8 {
        self.final_byte
    }

    #[allow(dead_code)] // Consumed by the semantic DCS query dispatch layer.
    pub(super) fn payload(&self) -> &[u8] {
        &self.payload
    }

    fn is_query(&self) -> bool {
        self.private_prefix.is_none()
            && self.parameter_count == 0
            && matches!((self.intermediates(), self.final_byte), (b"$" | b"+", b'q'))
    }

    fn is_sixel(&self) -> bool {
        self.final_byte == b'q'
            && self.private_prefix.is_none()
            && self.intermediate_count == 0
            && !self.has_subparameters()
    }

    fn is_known_stateful(&self) -> bool {
        if self.private_prefix.is_some() || self.has_subparameters() {
            return false;
        }
        matches!(
            (self.intermediates(), self.final_byte),
            (b"", b'|') // DECUDK: user-defined keys.
                | (b"", b'{') // DECDLD: downloadable character sets (DRCS).
                | (b"!", b'z') // DECDMAC: terminal macro definition.
                | (b"!", b'u') // DECAUPSS: assign user-preferred supplemental set.
                | (b"$", b'p') // DECRSTS: restore terminal state.
                | (b"$", b't') // DECRSPS: restore presentation state.
        )
    }

    fn requires_dispatch(&self) -> bool {
        self.is_sixel() || self.is_known_stateful()
    }

    fn push_query_payload(&mut self, byte: u8) -> Result<(), ParseError> {
        if self.payload.len() == MAX_DCS_QUERY_PAYLOAD_BYTES {
            return Err(ParseError::DcsQueryPayloadLimit);
        }
        if self.payload.len() == self.payload.capacity() {
            #[cfg(test)]
            if take_dcs_query_payload_allocation_failure_for_test() {
                return Err(ParseError::DcsQueryPayloadAllocation);
            }
            let remaining = MAX_DCS_QUERY_PAYLOAD_BYTES - self.payload.len();
            self.payload
                .try_reserve(remaining.min(4096))
                .map_err(|_| ParseError::DcsQueryPayloadAllocation)?;
        }
        self.payload.push(byte);
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum ParseError {
    InvalidEscapeSyntax,
    InvalidCsiSyntax,
    InvalidDcsSyntax,
    InvalidOscSelector,
    ParameterOverflow,
    ParameterScratchLimit,
    TooManyIntermediates,
    OscSelectorOverflow,
    OscSelectorScratchLimit,
    OscPayloadLimit,
    OscPayloadAllocation,
    DcsQueryPayloadLimit,
    DcsQueryPayloadAllocation,
}

impl ParseError {
    pub(super) fn description(self) -> &'static str {
        match self {
            Self::InvalidEscapeSyntax => "escape sequence syntax",
            Self::InvalidCsiSyntax => "CSI syntax",
            Self::InvalidDcsSyntax => "DCS syntax",
            Self::InvalidOscSelector => "OSC selector syntax",
            Self::ParameterOverflow => "control sequence parameter overflow",
            Self::ParameterScratchLimit => "control sequence parameter scratch limit",
            Self::TooManyIntermediates => "too many control sequence intermediates",
            Self::OscSelectorOverflow => "OSC selector overflow",
            Self::OscSelectorScratchLimit => "OSC selector scratch limit",
            Self::OscPayloadLimit => "OSC payload limit",
            Self::OscPayloadAllocation => "OSC payload allocation",
            Self::DcsQueryPayloadLimit => "DCS query payload limit",
            Self::DcsQueryPayloadAllocation => "DCS query payload allocation",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct EscapeState {
    intermediates: [u8; MAX_INTERMEDIATES],
    intermediate_count: u8,
}

impl EscapeState {
    const fn new() -> Self {
        Self {
            intermediates: [0; MAX_INTERMEDIATES],
            intermediate_count: 0,
        }
    }

    fn push_intermediate(&mut self, byte: u8) -> Result<(), ParseError> {
        let index = usize::from(self.intermediate_count);
        if index == MAX_INTERMEDIATES {
            return Err(ParseError::TooManyIntermediates);
        }
        self.intermediates[index] = byte;
        self.intermediate_count += 1;
        Ok(())
    }

    fn finish(self, final_byte: u8) -> EscSequence {
        EscSequence {
            intermediates: self.intermediates,
            intermediate_count: self.intermediate_count,
            final_byte,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct HeaderState {
    private_prefix: Option<u8>,
    parameters: [u16; MAX_CSI_PARAMS],
    parameter_count: u8,
    current: u16,
    current_set: bool,
    separators: [ParameterSeparator; MAX_CSI_PARAMS - 1],
    discarded_subparameters: bool,
    parameter_bytes: u8,
    intermediates: [u8; MAX_INTERMEDIATES],
    intermediate_count: u8,
}

impl HeaderState {
    const fn new() -> Self {
        Self {
            private_prefix: None,
            parameters: [PARAM_MISSING; MAX_CSI_PARAMS],
            parameter_count: 0,
            current: 0,
            current_set: false,
            separators: [ParameterSeparator::Semicolon; MAX_CSI_PARAMS - 1],
            discarded_subparameters: false,
            parameter_bytes: 0,
            intermediates: [0; MAX_INTERMEDIATES],
            intermediate_count: 0,
        }
    }

    fn consume_parameter_byte(&mut self, byte: u8) -> Result<(), ParseError> {
        if self.intermediate_count != 0 {
            return Err(ParseError::InvalidCsiSyntax);
        }
        if usize::from(self.parameter_bytes) == MAX_PARAMETER_BYTES {
            return Err(ParseError::ParameterScratchLimit);
        }
        self.parameter_bytes += 1;

        match byte {
            b'0'..=b'9' => {
                let digit = u16::from(byte - b'0');
                self.current = self
                    .current
                    .checked_mul(10)
                    .and_then(|value| value.checked_add(digit))
                    .filter(|value| *value != PARAM_MISSING)
                    .ok_or(ParseError::ParameterOverflow)?;
                self.current_set = true;
            }
            b';' => self.push_separator(ParameterSeparator::Semicolon)?,
            b':' => self.push_separator(ParameterSeparator::Colon)?,
            b'<' | b'=' | b'>' | b'?'
                if self.private_prefix.is_none()
                    && self.parameter_count == 0
                    && !self.current_set =>
            {
                self.private_prefix = Some(byte);
            }
            _ => return Err(ParseError::InvalidCsiSyntax),
        }
        Ok(())
    }

    fn push_current(&mut self) -> Result<(), ParseError> {
        let index = usize::from(self.parameter_count);
        if index == MAX_CSI_PARAMS {
            // Match the bounded parameter handling used by Windows terminals:
            // retain the first 32 values, but continue validating and consuming
            // the rest of the header so a long CSI does not fail closed.
            self.current = 0;
            self.current_set = false;
            return Ok(());
        }
        self.parameters[index] = if self.current_set {
            self.current
        } else {
            PARAM_MISSING
        };
        self.parameter_count += 1;
        self.current = 0;
        self.current_set = false;
        Ok(())
    }

    fn push_separator(&mut self, separator: ParameterSeparator) -> Result<(), ParseError> {
        let separator_index = usize::from(self.parameter_count);
        self.push_current()?;
        if separator_index < MAX_CSI_PARAMS - 1 {
            self.separators[separator_index] = separator;
        } else if separator == ParameterSeparator::Colon {
            self.discarded_subparameters = true;
        }
        Ok(())
    }

    fn push_intermediate(&mut self, byte: u8) -> Result<(), ParseError> {
        let index = usize::from(self.intermediate_count);
        if index == MAX_INTERMEDIATES {
            return Err(ParseError::TooManyIntermediates);
        }
        self.intermediates[index] = byte;
        self.intermediate_count += 1;
        Ok(())
    }

    fn finish_parameters(&mut self) -> Result<(), ParseError> {
        if self.current_set || self.parameter_count != 0 {
            self.push_current()?;
        }
        Ok(())
    }

    fn finish_csi(mut self, final_byte: u8) -> Result<CsiSequence, ParseError> {
        self.finish_parameters()?;
        Ok(CsiSequence {
            private_prefix: self.private_prefix,
            parameters: self.parameters,
            parameter_count: self.parameter_count,
            separators: self.separators,
            intermediates: self.intermediates,
            intermediate_count: self.intermediate_count,
            final_byte,
        })
    }

    fn finish_dcs(mut self, final_byte: u8) -> Result<DcsSequence, ParseError> {
        self.finish_parameters()?;
        Ok(DcsSequence {
            private_prefix: self.private_prefix,
            parameters: self.parameters,
            parameter_count: self.parameter_count,
            separators: self.separators,
            discarded_subparameters: self.discarded_subparameters,
            intermediates: self.intermediates,
            intermediate_count: self.intermediate_count,
            final_byte,
            payload: Vec::new(),
        })
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct OscSelectorState {
    value: u16,
    digit_count: u8,
}

impl OscSelectorState {
    const fn new() -> Self {
        Self {
            value: 0,
            digit_count: 0,
        }
    }

    fn push_digit(&mut self, byte: u8) -> Result<(), ParseError> {
        if usize::from(self.digit_count) == MAX_OSC_SELECTOR_BYTES {
            return Err(ParseError::OscSelectorScratchLimit);
        }
        self.value = self
            .value
            .checked_mul(10)
            .and_then(|value| value.checked_add(u16::from(byte - b'0')))
            .ok_or(ParseError::OscSelectorOverflow)?;
        self.digit_count += 1;
        Ok(())
    }

    fn finish(self) -> Result<OscStringState, ParseError> {
        Ok(OscStringState {
            sequence: OscSequence {
                // Xterm-compatible producers may omit Ps in `OSC ; Pt ST`.
                // The omitted selector is the canonical title target 0.
                selector: if self.digit_count == 0 { 0 } else { self.value },
                payload: Vec::new(),
            },
            payload_limit: osc_payload_limit(self.value),
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct OscStringState {
    sequence: OscSequence,
    payload_limit: Option<usize>,
}

impl OscStringState {
    fn push_payload(&mut self, byte: u8) -> Result<(), ParseError> {
        let Some(limit) = self.payload_limit else {
            return Ok(());
        };
        if self.sequence.payload.len() == limit {
            return Err(ParseError::OscPayloadLimit);
        }
        if self.sequence.payload.len() == self.sequence.payload.capacity() {
            #[cfg(test)]
            if take_osc_payload_allocation_failure_for_test() {
                return Err(ParseError::OscPayloadAllocation);
            }
            let remaining = limit - self.sequence.payload.len();
            self.sequence
                .payload
                .try_reserve(remaining.min(4096))
                .map_err(|_| ParseError::OscPayloadAllocation)?;
        }
        self.sequence.payload.push(byte);
        Ok(())
    }

    fn finish(self) -> OscSequence {
        self.sequence
    }
}

fn osc_payload_limit(selector: u16) -> Option<usize> {
    match selector {
        0..=2 => Some(MAX_OSC_TITLE_BYTES),
        4 | 104 => Some(MAX_OSC_PALETTE_BYTES),
        8 => Some(MAX_OSC_PAYLOAD_BYTES),
        10..=12 | 110..=112 => Some(MAX_OSC_COLOR_BYTES),
        _ => None,
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) enum OscEscapeState {
    Selector(OscSelectorState),
    String(OscStringState),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) enum DcsMode {
    Passthrough(DcsSequence),
    Query(DcsSequence),
    Ignore,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum IgnoredStringKind {
    Sos,
    Pm,
    Apc,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) enum ParserState {
    Ground,
    Escape(EscapeState),
    Csi(HeaderState),
    OscSelector(OscSelectorState),
    OscString(OscStringState),
    OscEscape(OscEscapeState),
    DcsEntry(HeaderState),
    DcsPassthrough(DcsSequence),
    DcsQuery(DcsSequence),
    DcsIgnore,
    DcsEscape(DcsMode),
    IgnoredString(IgnoredStringKind),
    IgnoredStringEscape(IgnoredStringKind),
}

impl ParserState {
    pub(super) fn is_ground(&self) -> bool {
        matches!(self, Self::Ground)
    }

    /// Returns whether the next ordinary byte belongs to the data portion of
    /// an ECMA-48 control string. The terminal uses this narrower predicate to
    /// keep its UTF-8 boundary tracker out of DCS headers and OSC selectors.
    pub(super) fn is_string_payload(&self) -> bool {
        matches!(
            self,
            Self::OscString(_)
                | Self::DcsPassthrough(_)
                | Self::DcsQuery(_)
                | Self::DcsIgnore
                | Self::IgnoredString(_)
        )
    }

    pub(super) fn advance(&mut self, byte: u8) -> Result<Action, ParseError> {
        if byte == 0x00 || byte == 0x7f {
            return Ok(Action::None);
        }
        if byte == 0x18 || byte == 0x1a {
            let state = std::mem::replace(self, Self::Ground);
            let interrupted = match state {
                Self::DcsEscape(DcsMode::Passthrough(sequence)) => Some(sequence),
                _ => None,
            };
            return Ok(interrupted.map_or(Action::None, |sequence| {
                Action::Sequence(Sequence::Dcs(sequence))
            }));
        }

        if byte == 0x07 {
            let state = std::mem::replace(self, Self::Ground);
            match state {
                Self::OscSelector(selector) => {
                    return Ok(Action::Sequence(Sequence::Osc(selector.finish()?.finish())));
                }
                Self::OscString(string) => {
                    return Ok(Action::Sequence(Sequence::Osc(string.finish())));
                }
                Self::OscEscape(OscEscapeState::Selector(selector)) => {
                    return Ok(Action::Sequence(Sequence::Osc(selector.finish()?.finish())));
                }
                Self::OscEscape(OscEscapeState::String(string)) => {
                    return Ok(Action::Sequence(Sequence::Osc(string.finish())));
                }
                other => *self = other,
            }
        }

        if byte == 0x1b {
            let state = std::mem::replace(self, Self::Ground);
            let (next, interrupted) = match state {
                Self::OscSelector(selector) => {
                    (Self::OscEscape(OscEscapeState::Selector(selector)), None)
                }
                Self::OscString(string) => (Self::OscEscape(OscEscapeState::String(string)), None),
                Self::OscEscape(_) => (Self::Escape(EscapeState::new()), None),
                Self::DcsPassthrough(sequence) => {
                    (Self::DcsEscape(DcsMode::Passthrough(sequence)), None)
                }
                Self::DcsQuery(sequence) => (Self::DcsEscape(DcsMode::Query(sequence)), None),
                Self::DcsEscape(DcsMode::Passthrough(sequence)) => {
                    (Self::Escape(EscapeState::new()), Some(sequence))
                }
                Self::DcsEscape(DcsMode::Query(sequence)) => {
                    (Self::Escape(EscapeState::new()), Some(sequence))
                }
                Self::DcsEntry(_) => (Self::DcsEscape(DcsMode::Ignore), None),
                Self::DcsIgnore => (Self::DcsEscape(DcsMode::Ignore), None),
                Self::DcsEscape(DcsMode::Ignore) => (Self::Escape(EscapeState::new()), None),
                Self::IgnoredString(kind) => (Self::IgnoredStringEscape(kind), None),
                Self::IgnoredStringEscape(_) => (Self::Escape(EscapeState::new()), None),
                _ => (Self::Escape(EscapeState::new()), None),
            };
            *self = next;
            return Ok(interrupted.map_or(Action::None, |sequence| {
                Action::Sequence(Sequence::Dcs(sequence))
            }));
        }

        if byte < 0x20 {
            let state = std::mem::replace(self, Self::Ground);
            match state {
                Self::OscEscape(escape) => return self.finish_osc_escape(escape, byte),
                Self::DcsEscape(mode) => return self.finish_dcs_escape(mode, byte),
                Self::IgnoredStringEscape(kind) => {
                    *self = Self::IgnoredString(kind);
                    return Ok(Action::None);
                }
                other => *self = other,
            }
            return if self.is_string_state() {
                Ok(Action::None)
            } else {
                Ok(Action::Execute(byte))
            };
        }

        let state = std::mem::replace(self, Self::Ground);
        match state {
            Self::Ground => Ok(Action::Print(byte)),
            Self::Escape(mut escape) => self.advance_escape(&mut escape, byte),
            Self::Csi(mut header) => self.advance_csi(&mut header, byte),
            Self::OscSelector(mut selector) => match byte {
                b'0'..=b'9' => {
                    selector.push_digit(byte)?;
                    *self = Self::OscSelector(selector);
                    Ok(Action::None)
                }
                b';' => {
                    *self = Self::OscString(selector.finish()?);
                    Ok(Action::None)
                }
                _ => Err(ParseError::InvalidOscSelector),
            },
            Self::OscString(mut string) => {
                string.push_payload(byte)?;
                *self = Self::OscString(string);
                Ok(Action::None)
            }
            Self::OscEscape(escape) => self.finish_osc_escape(escape, byte),
            Self::DcsEntry(mut header) => self.advance_dcs_entry(&mut header, byte),
            Self::DcsPassthrough(sequence) => {
                *self = Self::DcsPassthrough(sequence);
                Ok(Action::None)
            }
            Self::DcsQuery(mut sequence) => {
                sequence.push_query_payload(byte)?;
                *self = Self::DcsQuery(sequence);
                Ok(Action::None)
            }
            Self::DcsIgnore => {
                *self = Self::DcsIgnore;
                Ok(Action::None)
            }
            Self::DcsEscape(mode) => self.finish_dcs_escape(mode, byte),
            Self::IgnoredString(kind) => {
                *self = Self::IgnoredString(kind);
                Ok(Action::None)
            }
            Self::IgnoredStringEscape(_) if byte == b'\\' => Ok(Action::None),
            Self::IgnoredStringEscape(_) => self.advance_fresh_escape(byte),
        }
    }

    fn is_string_state(&self) -> bool {
        matches!(
            self,
            Self::OscSelector(_)
                | Self::OscString(_)
                | Self::OscEscape(_)
                | Self::DcsEntry(_)
                | Self::DcsPassthrough(_)
                | Self::DcsQuery(_)
                | Self::DcsIgnore
                | Self::DcsEscape(_)
                | Self::IgnoredString(_)
                | Self::IgnoredStringEscape(_)
        )
    }

    fn advance_escape(&mut self, escape: &mut EscapeState, byte: u8) -> Result<Action, ParseError> {
        if escape.intermediate_count == 0 {
            match byte {
                b'\\' => return Ok(Action::None),
                b'[' => {
                    *self = Self::Csi(HeaderState::new());
                    return Ok(Action::None);
                }
                b']' => {
                    *self = Self::OscSelector(OscSelectorState::new());
                    return Ok(Action::None);
                }
                b'P' => {
                    *self = Self::DcsEntry(HeaderState::new());
                    return Ok(Action::None);
                }
                b'X' => {
                    *self = Self::IgnoredString(IgnoredStringKind::Sos);
                    return Ok(Action::None);
                }
                b'^' => {
                    *self = Self::IgnoredString(IgnoredStringKind::Pm);
                    return Ok(Action::None);
                }
                b'_' => {
                    *self = Self::IgnoredString(IgnoredStringKind::Apc);
                    return Ok(Action::None);
                }
                _ => {}
            }
        }

        match byte {
            0x20..=0x2f => {
                escape.push_intermediate(byte)?;
                *self = Self::Escape(*escape);
                Ok(Action::None)
            }
            0x30..=0x7e => Ok(Action::Sequence(Sequence::Esc(escape.finish(byte)))),
            _ => Err(ParseError::InvalidEscapeSyntax),
        }
    }

    fn advance_csi(&mut self, header: &mut HeaderState, byte: u8) -> Result<Action, ParseError> {
        match byte {
            0x30..=0x3f => {
                header.consume_parameter_byte(byte)?;
                *self = Self::Csi(*header);
                Ok(Action::None)
            }
            0x20..=0x2f => {
                header.push_intermediate(byte)?;
                *self = Self::Csi(*header);
                Ok(Action::None)
            }
            0x40..=0x7e => Ok(Action::Sequence(Sequence::Csi(header.finish_csi(byte)?))),
            _ => Err(ParseError::InvalidCsiSyntax),
        }
    }

    fn finish_osc_escape(
        &mut self,
        escape: OscEscapeState,
        byte: u8,
    ) -> Result<Action, ParseError> {
        if byte == b'\\' {
            let sequence = match escape {
                OscEscapeState::Selector(selector) => selector.finish()?.finish(),
                OscEscapeState::String(string) => string.finish(),
            };
            return Ok(Action::Sequence(Sequence::Osc(sequence)));
        }
        self.advance_fresh_escape(byte)
    }

    fn advance_dcs_entry(
        &mut self,
        header: &mut HeaderState,
        byte: u8,
    ) -> Result<Action, ParseError> {
        match byte {
            0x30..=0x3f => {
                header
                    .consume_parameter_byte(byte)
                    .map_err(map_csi_to_dcs_error)?;
                *self = Self::DcsEntry(*header);
                Ok(Action::None)
            }
            0x20..=0x2f => {
                header.push_intermediate(byte)?;
                *self = Self::DcsEntry(*header);
                Ok(Action::None)
            }
            0x40..=0x7e => {
                let sequence = header.finish_dcs(byte)?;
                if sequence.has_subparameters() {
                    return Err(ParseError::InvalidDcsSyntax);
                }
                *self = if sequence.is_query() {
                    Self::DcsQuery(sequence)
                } else if sequence.requires_dispatch() {
                    Self::DcsPassthrough(sequence)
                } else {
                    Self::DcsIgnore
                };
                Ok(Action::None)
            }
            _ => Err(ParseError::InvalidDcsSyntax),
        }
    }

    fn finish_dcs_escape(&mut self, mode: DcsMode, byte: u8) -> Result<Action, ParseError> {
        if byte == b'\\' {
            return Ok(match mode {
                DcsMode::Passthrough(sequence) | DcsMode::Query(sequence) => {
                    Action::Sequence(Sequence::Dcs(sequence))
                }
                DcsMode::Ignore => Action::None,
            });
        }
        let fresh_action = self.advance_fresh_escape(byte);
        match mode {
            DcsMode::Passthrough(sequence) | DcsMode::Query(sequence) => {
                match fresh_action {
                    Ok(_) | Err(_) => {}
                }
                Ok(Action::Sequence(Sequence::Dcs(sequence)))
            }
            DcsMode::Ignore => fresh_action,
        }
    }

    fn advance_fresh_escape(&mut self, byte: u8) -> Result<Action, ParseError> {
        let mut escape = EscapeState::new();
        if byte < 0x20 {
            *self = Self::Escape(escape);
            Ok(Action::Execute(byte))
        } else {
            self.advance_escape(&mut escape, byte)
        }
    }
}

fn map_csi_to_dcs_error(error: ParseError) -> ParseError {
    match error {
        ParseError::InvalidCsiSyntax => ParseError::InvalidDcsSyntax,
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Event, Terminal, TerminalError};

    fn feed(parser: &mut ParserState, bytes: &[u8]) -> Result<Vec<Action>, ParseError> {
        bytes.iter().try_fold(Vec::new(), |mut actions, &byte| {
            let action = parser.advance(byte)?;
            if action != Action::None {
                actions.push(action);
            }
            Ok(actions)
        })
    }

    fn parse_csi_at_split(bytes: &[u8], split: usize) -> CsiSequence {
        let mut parser = ParserState::Ground;
        let mut actions = feed(&mut parser, &bytes[..split]).expect("CSI prefix");
        actions.extend(feed(&mut parser, &bytes[split..]).expect("CSI suffix"));
        assert!(parser.is_ground());
        let [Action::Sequence(Sequence::Csi(sequence))] = actions.as_slice() else {
            panic!("one CSI action expected");
        };
        *sequence
    }

    fn parse_osc_at_split(bytes: &[u8], split: usize) -> OscSequence {
        let mut parser = ParserState::Ground;
        let mut actions = feed(&mut parser, &bytes[..split]).expect("OSC prefix");
        actions.extend(feed(&mut parser, &bytes[split..]).expect("OSC suffix"));
        assert!(parser.is_ground());
        let [Action::Sequence(Sequence::Osc(sequence))] = actions.as_slice() else {
            panic!("one OSC action expected");
        };
        sequence.clone()
    }

    fn parse_dcs_at_split(bytes: &[u8], split: usize) -> DcsSequence {
        let mut parser = ParserState::Ground;
        let mut actions = feed(&mut parser, &bytes[..split]).expect("DCS prefix");
        actions.extend(feed(&mut parser, &bytes[split..]).expect("DCS suffix"));
        assert!(parser.is_ground());
        let [Action::Sequence(Sequence::Dcs(sequence))] = actions.as_slice() else {
            panic!("one DCS action expected");
        };
        sequence.clone()
    }

    fn csi_with_parameters(parameters: &[u16], final_byte: u8) -> Vec<u8> {
        let mut bytes = b"\x1b[".to_vec();
        for (index, parameter) in parameters.iter().enumerate() {
            if index != 0 {
                bytes.push(b';');
            }
            bytes.extend_from_slice(parameter.to_string().as_bytes());
        }
        bytes.push(final_byte);
        bytes
    }

    #[test]
    fn terminal_preserves_every_output_event_split_and_checkpoint_boundary() {
        for bytes in [
            &b"\x1b7"[..],
            &b"\x1b[31m"[..],
            &b"\x1b]777;discarded\x07"[..],
            &b"\x1bPzdiscarded\x1b\\"[..],
            &b"\x1b_ignored\x1b\\"[..],
        ] {
            for split in 1..bytes.len() {
                let mut terminal = Terminal::new(8, 2).expect("terminal");
                terminal
                    .apply_event(Event::Output {
                        source_sequence: 0,
                        bytes: &bytes[..split],
                    })
                    .expect("sequence prefix");
                assert_eq!(
                    terminal.export_snapshot_v2(),
                    Err(TerminalError::SnapshotNotEligible)
                );
                terminal
                    .apply_event(Event::Output {
                        source_sequence: 1,
                        bytes: &bytes[split..],
                    })
                    .expect("sequence suffix");
                terminal
                    .export_snapshot_v2()
                    .expect("complete parser boundary");
            }
        }
    }

    #[test]
    fn sos_pm_and_apc_are_ignored_until_st_at_every_split() {
        for bytes in [
            &b"A\x1bXignored\x1b\\B"[..],
            &b"A\x1b^private\x07still-private\x1b\\B"[..],
            &b"A\x1b_G=1,payload;more\x1b\\B"[..],
        ] {
            for split in 0..=bytes.len() {
                let mut terminal = Terminal::new(8, 2).expect("terminal");
                terminal
                    .apply_event(Event::Output {
                        source_sequence: 0,
                        bytes: &bytes[..split],
                    })
                    .expect("ignored string prefix");
                terminal
                    .apply_event(Event::Output {
                        source_sequence: 1,
                        bytes: &bytes[split..],
                    })
                    .expect("ignored string suffix");
                assert_eq!(terminal.cell(0, 0).expect("A").character, 'A');
                assert_eq!(terminal.cell(0, 1).expect("B").character, 'B');
                assert!(!terminal.is_failed_closed());
                terminal
                    .export_snapshot_v2()
                    .expect("complete ignored string returns to Ground");
            }
        }
    }

    #[test]
    fn ignored_strings_are_checkpoint_gated_and_can_or_sub_cancels_them() {
        for cancel in [0x18, 0x1a] {
            let mut terminal = Terminal::new(8, 2).expect("terminal");
            terminal
                .apply_event(Event::Output {
                    source_sequence: 0,
                    bytes: b"\x1b_partial",
                })
                .expect("partial APC");
            assert_eq!(
                terminal.export_snapshot_v2(),
                Err(TerminalError::SnapshotNotEligible)
            );
            terminal
                .apply_event(Event::Output {
                    source_sequence: 1,
                    bytes: &[cancel, b'X'],
                })
                .expect("cancel APC and print suffix");
            assert_eq!(terminal.cell(0, 0).expect("suffix").character, 'X');
            terminal
                .export_snapshot_v2()
                .expect("cancel returns parser to Ground");
        }
    }

    #[test]
    fn c0_controls_are_ignored_inside_osc_payload_and_dcs_header() {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b]2;a\x09b\x07\x1bP\x09zignored\x1b\\X",
            })
            .expect("string C0 controls are inert");
        assert_eq!(terminal.osc_state().window_title(), "ab");
        assert_eq!(terminal.cell(0, 0).expect("suffix").character, 'X');
    }

    #[test]
    fn terminal_ignores_unknown_strings_but_rejects_known_unimplemented_state() {
        let mut ignored = Terminal::new(8, 2).expect("terminal");
        ignored
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b]777;ignored\x07\x1bPzignored\x1b\\X",
            })
            .expect("unknown OSC and DCS are safe to ignore");
        assert_eq!(ignored.cell(0, 0).expect("printed suffix").character, 'X');

        let mut titled = Terminal::new(8, 2).expect("terminal");
        titled
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b]2;title\x07",
            })
            .expect("supported title state");
        assert_eq!(titled.osc_state().window_title(), "title");

        let mut terminal = Terminal::new(8, 2).expect("terminal");
        assert_eq!(
            terminal.apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1bP1;2qdata\x1b\\",
            }),
            Err(TerminalError::Unsupported("SIXEL"))
        );
        assert!(terminal.is_failed_closed());
    }

    #[test]
    fn escape_sequence_accepts_two_intermediates_across_byte_chunks() {
        let mut parser = ParserState::Ground;
        let actions = feed(&mut parser, b"\x1b !p").expect("valid ESC sequence");
        let [Action::Sequence(Sequence::Esc(sequence))] = actions.as_slice() else {
            panic!("one ESC action expected");
        };
        assert_eq!(sequence.intermediates(), b" !");
        assert_eq!(sequence.final_byte(), b'p');
        assert!(parser.is_ground());
    }

    #[test]
    fn csi_retains_parameter_groups_at_every_split() {
        let bytes = b"\x1b[?12;34:5  p";
        let sequence = parse_csi_at_split(bytes, 0);
        assert_eq!(sequence.private_prefix(), Some(b'?'));
        assert_eq!(sequence.parameters(), &[12, 34, 5]);
        assert_eq!(
            sequence.parameter_separators(),
            &[ParameterSeparator::Semicolon, ParameterSeparator::Colon]
        );
        assert!(sequence.has_subparameters());
        assert_eq!(sequence.intermediates(), b"  ");
        assert_eq!(sequence.final_byte(), b'p');

        for split in 0..=bytes.len() {
            assert_eq!(parse_csi_at_split(bytes, split), sequence);
        }
    }

    #[test]
    fn common_colon_sgr_headers_are_split_deterministic() {
        for bytes in [
            &b"\x1b[38:5:123m"[..],
            &b"\x1b[48:5:45m"[..],
            &b"\x1b[38:2::1:2:3;1m"[..],
            &b"\x1b[48:2:0:4:5:6;4m"[..],
        ] {
            let joined = parse_csi_at_split(bytes, 0);
            for split in 0..=bytes.len() {
                assert_eq!(parse_csi_at_split(bytes, split), joined);
            }
        }
    }

    #[test]
    fn osc_bel_is_complete_when_every_byte_is_a_separate_chunk() {
        let mut parser = ParserState::Ground;
        let actions = feed(&mut parser, b"\x1b]777;discarded\x07").expect("valid OSC");
        let [Action::Sequence(Sequence::Osc(sequence))] = actions.as_slice() else {
            panic!("one OSC action expected");
        };
        assert_eq!(sequence.selector(), 777);
        assert!(sequence.payload().is_empty());
        assert!(parser.is_ground());
    }

    #[test]
    fn osc_st_may_split_between_escape_and_backslash() {
        let mut parser = ParserState::Ground;
        assert!(
            feed(&mut parser, b"\x1b]8;payload\x1b")
                .expect("OSC prefix")
                .is_empty()
        );
        assert!(!parser.is_ground());
        let actions = feed(&mut parser, b"\\").expect("OSC terminator");
        let [Action::Sequence(Sequence::Osc(sequence))] = actions.as_slice() else {
            panic!("one OSC action expected");
        };
        assert_eq!(sequence.selector(), 8);
        assert_eq!(sequence.payload(), b"payload");
        assert!(parser.is_ground());
    }

    #[test]
    fn known_osc_payload_ignores_c0_at_every_bel_and_st_split_boundary() {
        for bytes in [
            &b"\x1b]2;exact\tbytes\x07"[..],
            &b"\x1b]2;exact\tbytes\x1b\\"[..],
        ] {
            for split in 0..=bytes.len() {
                let sequence = parse_osc_at_split(bytes, split);
                assert_eq!(sequence.selector(), 2);
                assert_eq!(sequence.payload(), b"exactbytes");
            }
        }
    }

    #[test]
    fn bel_terminates_osc_even_while_st_is_tentative() {
        let sequence = parse_osc_at_split(b"\x1b]2;exact\x1b\x07", 10);
        assert_eq!(sequence.selector(), 2);
        assert_eq!(sequence.payload(), b"exact");
    }

    #[test]
    fn non_st_escape_abandons_known_osc_and_processes_the_following_byte() {
        for (bytes, expected) in [
            (
                &b"\x1b]2;discarded\x1b[31m"[..],
                Sequence::Csi(CsiSequence {
                    private_prefix: None,
                    parameters: {
                        let mut parameters = [PARAM_MISSING; MAX_CSI_PARAMS];
                        parameters[0] = 31;
                        parameters
                    },
                    parameter_count: 1,
                    separators: [ParameterSeparator::Semicolon; MAX_CSI_PARAMS - 1],
                    intermediates: [0; MAX_INTERMEDIATES],
                    intermediate_count: 0,
                    final_byte: b'm',
                }),
            ),
            (
                &b"\x1b]2;discarded\x1b7"[..],
                Sequence::Esc(EscapeState::new().finish(b'7')),
            ),
        ] {
            for split in 1..bytes.len() {
                let mut parser = ParserState::Ground;
                let mut actions = feed(&mut parser, &bytes[..split]).expect("interrupted prefix");
                actions.extend(
                    feed(&mut parser, &bytes[split..]).expect("fresh sequence after interruption"),
                );
                assert_eq!(actions, [Action::Sequence(expected.clone())]);
                assert!(parser.is_ground());
            }
        }
    }

    #[test]
    fn known_osc_payload_accepts_cap_and_rejects_cap_plus_one() {
        let limit = osc_payload_limit(8).expect("known hyperlink selector");
        assert_eq!(limit, MAX_OSC_PAYLOAD_BYTES);

        let mut at_cap = ParserState::Ground;
        feed(&mut at_cap, b"\x1b]8;").expect("OSC entry");
        for _ in 0..limit {
            assert_eq!(at_cap.advance(b'x'), Ok(Action::None));
        }
        let actions = feed(&mut at_cap, b"\x07").expect("OSC at cap");
        let [Action::Sequence(Sequence::Osc(sequence))] = actions.as_slice() else {
            panic!("one OSC action expected");
        };
        assert_eq!(sequence.payload().len(), limit);

        let mut over_cap = ParserState::Ground;
        feed(&mut over_cap, b"\x1b]8;").expect("OSC entry");
        for _ in 0..limit {
            assert_eq!(over_cap.advance(b'x'), Ok(Action::None));
        }
        assert_eq!(over_cap.advance(b'x'), Err(ParseError::OscPayloadLimit));
    }

    #[test]
    fn unknown_osc_discards_payload_without_allocating() {
        let mut parser = ParserState::Ground;
        feed(&mut parser, b"\x1b]777;").expect("unknown OSC entry");
        for _ in 0..MAX_OSC_PAYLOAD_BYTES * 2 {
            assert_eq!(parser.advance(b'x'), Ok(Action::None));
        }
        let ParserState::OscString(string) = &parser else {
            panic!("unknown OSC remains pending");
        };
        assert!(string.sequence.payload.is_empty());
        assert_eq!(string.sequence.payload.capacity(), 0);

        let actions = feed(&mut parser, b"\x07").expect("unknown OSC completion");
        let [Action::Sequence(Sequence::Osc(sequence))] = actions.as_slice() else {
            panic!("one OSC action expected");
        };
        assert_eq!(sequence.selector(), 777);
        assert!(sequence.payload().is_empty());
    }

    #[test]
    fn invalid_osc_selector_is_classified_deterministically() {
        let mut parser = ParserState::Ground;
        assert_eq!(
            feed(&mut parser, b"\x1b]x;payload"),
            Err(ParseError::InvalidOscSelector)
        );

        let omitted = parse_osc_at_split(b"\x1b];title\x07", 3);
        assert_eq!(omitted.selector(), 0);
        assert_eq!(omitted.payload(), b"title");
    }

    #[test]
    fn can_and_sub_discard_collected_osc_payload() {
        for cancel in [0x18, 0x1a] {
            let mut parser = ParserState::Ground;
            feed(&mut parser, b"\x1b]2;discarded").expect("known OSC prefix");
            assert_eq!(parser.advance(cancel), Ok(Action::None));
            assert!(parser.is_ground());
            assert_eq!(parser.advance(b'X'), Ok(Action::Print(b'X')));
        }
    }

    #[test]
    fn stateful_osc_selectors_have_explicit_payload_limits() {
        for selector in [0, 1, 2, 4, 8, 10, 11, 12, 104, 110, 111, 112] {
            assert!(osc_payload_limit(selector).is_some());
        }
        assert_eq!(osc_payload_limit(22), None);
        assert_eq!(osc_payload_limit(23), None);
        assert_eq!(osc_payload_limit(777), None);
    }

    #[test]
    fn dcs_st_may_split_between_escape_and_backslash() {
        let mut parser = ParserState::Ground;
        assert!(
            feed(&mut parser, b"\x1bP1;2qdata\x1b")
                .expect("DCS prefix")
                .is_empty()
        );
        assert!(!parser.is_ground());
        let actions = feed(&mut parser, b"\\").expect("DCS terminator");
        let [Action::Sequence(Sequence::Dcs(sequence))] = actions.as_slice() else {
            panic!("one DCS action expected");
        };
        assert_eq!(sequence.parameters(), &[1, 2]);
        assert_eq!(sequence.final_byte(), b'q');
        assert!(sequence.payload().is_empty());
        assert_eq!(sequence.payload.capacity(), 0);
        assert!(parser.is_ground());
    }

    #[test]
    fn canonical_dcs_queries_retain_exact_payload_at_every_split() {
        for (bytes, expected_intermediate, expected_payload) in [
            (&b"\x1bP$qm\x1b\\"[..], &b"$"[..], &b"m"[..]),
            (&b"\x1bP+q544e;436f\x1b\\"[..], &b"+"[..], &b"544e;436f"[..]),
        ] {
            for split in 0..=bytes.len() {
                let sequence = parse_dcs_at_split(bytes, split);
                assert_eq!(sequence.private_prefix(), None);
                assert!(sequence.parameters().is_empty());
                assert_eq!(sequence.intermediates(), expected_intermediate);
                assert_eq!(sequence.final_byte(), b'q');
                assert_eq!(sequence.payload(), expected_payload);
            }
        }
    }

    #[test]
    fn canonical_dcs_queries_parse_one_byte_at_a_time() {
        let mut parser = ParserState::Ground;
        let mut actions = Vec::new();
        for byte in b"\x1bP$qone-byte\x1b\\" {
            let action = parser.advance(*byte).expect("one-byte DCS query chunk");
            if action != Action::None {
                actions.push(action);
            }
        }
        let [Action::Sequence(Sequence::Dcs(sequence))] = actions.as_slice() else {
            panic!("one DCS query action expected");
        };
        assert_eq!(sequence.payload(), b"one-byte");
        assert!(parser.is_ground());
    }

    #[test]
    fn dcs_query_payload_accepts_cap_and_rejects_cap_plus_one() {
        let mut at_cap = ParserState::Ground;
        feed(&mut at_cap, b"\x1bP$q").expect("DCS query entry");
        for _ in 0..MAX_DCS_QUERY_PAYLOAD_BYTES {
            assert_eq!(at_cap.advance(b'x'), Ok(Action::None));
        }
        let actions = feed(&mut at_cap, b"\x1b\\").expect("DCS query at cap");
        let [Action::Sequence(Sequence::Dcs(sequence))] = actions.as_slice() else {
            panic!("one DCS query action expected");
        };
        assert_eq!(sequence.payload().len(), MAX_DCS_QUERY_PAYLOAD_BYTES);

        let mut over_cap = ParserState::Ground;
        feed(&mut over_cap, b"\x1bP+q").expect("DCS query entry");
        for _ in 0..MAX_DCS_QUERY_PAYLOAD_BYTES {
            assert_eq!(over_cap.advance(b'x'), Ok(Action::None));
        }
        assert_eq!(
            over_cap.advance(b'x'),
            Err(ParseError::DcsQueryPayloadLimit)
        );
        assert!(over_cap.is_ground());
    }

    #[test]
    fn dcs_query_payload_allocation_failure_is_deterministic() {
        let mut parser = ParserState::Ground;
        feed(&mut parser, b"\x1bP$q").expect("DCS query entry");
        fail_next_dcs_query_payload_allocation_for_test();
        assert_eq!(
            parser.advance(b'x'),
            Err(ParseError::DcsQueryPayloadAllocation)
        );
        assert!(parser.is_ground());
    }

    #[test]
    fn can_and_sub_discard_collected_dcs_query_payload() {
        for (prefix, cancel) in [
            (&b"\x1bP$qdiscarded"[..], 0x18),
            (&b"\x1bP+qdiscarded"[..], 0x1a),
            (&b"\x1bP$qdiscarded\x1b"[..], 0x18),
            (&b"\x1bP+qdiscarded\x1b"[..], 0x1a),
        ] {
            let mut parser = ParserState::Ground;
            feed(&mut parser, prefix).expect("DCS query prefix");
            assert_eq!(parser.advance(cancel), Ok(Action::None));
            assert!(parser.is_ground());
            assert_eq!(parser.advance(b'X'), Ok(Action::Print(b'X')));
        }
    }

    #[test]
    fn dcs_query_body_ignores_bel_and_other_c0_controls() {
        let sequence = parse_dcs_at_split(b"\x1bP$qA\x00\x07\x08\x09\x0a\x0d\x1f\x7fB\x1b\\", 0);
        assert_eq!(sequence.payload(), b"AB");
    }

    #[test]
    fn dcs_query_non_st_escape_dispatches_then_starts_fresh_escape() {
        let bytes = b"\x1bP$qexact\x1b[31m";
        for split in 0..=bytes.len() {
            let mut parser = ParserState::Ground;
            let mut actions = feed(&mut parser, &bytes[..split]).expect("split prefix");
            actions.extend(feed(&mut parser, &bytes[split..]).expect("split suffix"));
            let [
                Action::Sequence(Sequence::Dcs(query)),
                Action::Sequence(Sequence::Csi(csi)),
            ] = actions.as_slice()
            else {
                panic!("DCS query and fresh CSI actions expected");
            };
            assert_eq!(query.payload(), b"exact");
            assert_eq!(csi.parameters(), &[31]);
            assert_eq!(csi.final_byte(), b'm');
            assert!(parser.is_ground());
        }
    }

    #[test]
    fn unknown_dcs_is_ignored_through_split_st() {
        let mut parser = ParserState::Ground;
        assert!(
            feed(&mut parser, b"\x1bP1zignored\x1b")
                .expect("unknown DCS prefix")
                .is_empty()
        );
        assert!(feed(&mut parser, b"\\").expect("DCS terminator").is_empty());
        assert!(parser.is_ground());
    }

    #[test]
    fn osc_and_unknown_dcs_non_st_escape_starts_a_fresh_sequence() {
        for bytes in [
            &b"A\x1b]777;discarded\x1b[2JX"[..],
            &b"A\x1bPzdiscarded\x1b[2JX"[..],
        ] {
            let mut terminal = Terminal::new(8, 2).expect("terminal");
            terminal
                .apply_event(Event::Output {
                    source_sequence: 0,
                    bytes,
                })
                .expect("interrupted string and fresh CSI");
            assert_eq!(terminal.cell(0, 0).expect("cleared cell").character, ' ');
            assert_eq!(terminal.cell(0, 1).expect("printed suffix").character, 'X');
            terminal
                .export_snapshot_v2()
                .expect("fresh CSI returns parser to ground");
        }
    }

    #[test]
    fn osc_and_unknown_dcs_non_st_escape_may_split_before_fresh_sequence() {
        for prefix in [
            &b"A\x1b]777;discarded\x1b"[..],
            &b"A\x1bPzdiscarded\x1b"[..],
        ] {
            let mut terminal = Terminal::new(8, 2).expect("terminal");
            terminal
                .apply_event(Event::Output {
                    source_sequence: 0,
                    bytes: prefix,
                })
                .expect("string prefix ending in ESC");
            assert_eq!(
                terminal.export_snapshot_v2(),
                Err(TerminalError::SnapshotNotEligible)
            );
            terminal
                .apply_event(Event::Output {
                    source_sequence: 1,
                    bytes: b"[2JX",
                })
                .expect("fresh CSI after split ESC");
            assert_eq!(terminal.cell(0, 0).expect("cleared cell").character, ' ');
            assert_eq!(terminal.cell(0, 1).expect("printed suffix").character, 'X');
            terminal
                .export_snapshot_v2()
                .expect("split fresh CSI returns parser to ground");
        }
    }

    #[test]
    fn sixel_non_st_escape_cannot_be_cancelled_before_fail_closed_dispatch() {
        for bytes in [&b"\x1bPq~\x1b\x18X"[..], &b"\x1bPq~\x1b\x1aX"[..]] {
            let mut terminal = Terminal::new(8, 2).expect("terminal");
            assert_eq!(
                terminal.apply_event(Event::Output {
                    source_sequence: 0,
                    bytes,
                }),
                Err(TerminalError::Unsupported("SIXEL"))
            );
            assert!(terminal.is_failed_closed());
        }

        let mut split = Terminal::new(8, 2).expect("terminal");
        split
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1bPq~\x1b",
            })
            .expect("SIXEL prefix ending in ESC");
        assert_eq!(
            split.export_snapshot_v2(),
            Err(TerminalError::SnapshotNotEligible)
        );
        assert_eq!(
            split.apply_event(Event::Output {
                source_sequence: 1,
                bytes: b"\x18X",
            }),
            Err(TerminalError::Unsupported("SIXEL"))
        );
        assert!(split.is_failed_closed());
    }

    #[test]
    fn known_stateful_dcs_families_are_dispatched_fail_closed() {
        for (bytes, expected_intermediates, expected_final) in [
            (&b"\x1bP1;2|data\x1b\\"[..], &b""[..], b'|'),
            (&b"\x1bP0;0{data\x1b\\"[..], &b""[..], b'{'),
            (&b"\x1bP1;0!zdata\x1b\\"[..], &b"!"[..], b'z'),
            (&b"\x1bP1!udata\x1b\\"[..], &b"!"[..], b'u'),
            (&b"\x1bP1$pdata\x1b\\"[..], &b"$"[..], b'p'),
            (&b"\x1bP1$tdata\x1b\\"[..], &b"$"[..], b't'),
        ] {
            let mut parser = ParserState::Ground;
            let actions = feed(&mut parser, bytes).expect("known stateful DCS");
            let [Action::Sequence(Sequence::Dcs(sequence))] = actions.as_slice() else {
                panic!("one fail-closed DCS action expected");
            };
            assert_eq!(sequence.intermediates(), expected_intermediates);
            assert_eq!(sequence.final_byte(), expected_final);
            assert!(sequence.payload().is_empty());
            assert_eq!(sequence.payload.capacity(), 0);
            assert!(parser.is_ground());
        }
    }

    #[test]
    fn newly_classified_stateful_dcs_families_fail_closed_explicitly() {
        for (bytes, feature) in [
            (&b"\x1bP1!udata\x1b\\"[..], "DECAUPSS"),
            (&b"\x1bP1$pdata\x1b\\"[..], "DECRSTS"),
        ] {
            let mut terminal = Terminal::new(8, 2).expect("terminal");
            assert_eq!(
                terminal.apply_event(Event::Output {
                    source_sequence: 0,
                    bytes,
                }),
                Err(TerminalError::Unsupported(feature))
            );
            assert!(terminal.is_failed_closed());
        }
    }

    #[test]
    fn dcs_subparameters_are_rejected_instead_of_becoming_ignorable() {
        for bytes in [&b"\x1bP1:2q"[..], &b"\x1bP1:2z"[..]] {
            for split in 0..=bytes.len() {
                let mut parser = ParserState::Ground;
                let result = feed(&mut parser, &bytes[..split])
                    .and_then(|_| feed(&mut parser, &bytes[split..]));
                assert_eq!(result, Err(ParseError::InvalidDcsSyntax));
                assert!(parser.is_ground());
            }
        }

        let mut colon_after_storage_cap = b"\x1bP".to_vec();
        for index in 0..MAX_CSI_PARAMS {
            if index != 0 {
                colon_after_storage_cap.push(b';');
            }
            colon_after_storage_cap.push(b'1');
        }
        colon_after_storage_cap.extend_from_slice(b":2q");
        let mut parser = ParserState::Ground;
        assert_eq!(
            feed(&mut parser, &colon_after_storage_cap),
            Err(ParseError::InvalidDcsSyntax)
        );
        assert!(parser.is_ground());
    }

    #[test]
    fn noncanonical_queries_and_truly_unknown_dcs_remain_ignorable_without_allocation() {
        fail_next_dcs_query_payload_allocation_for_test();
        for bytes in [
            &b"\x1bP12\x1b\\"[..],
            &b"\x1bP1$qignored\x1b\\"[..],
            &b"\x1bP?+q544e\x1b\\"[..],
            &b"\x1bP1zignored\x1b\\"[..],
        ] {
            let mut parser = ParserState::Ground;
            assert!(feed(&mut parser, bytes).expect("ignored DCS").is_empty());
            assert!(parser.is_ground());
        }

        let mut unknown = ParserState::Ground;
        feed(&mut unknown, b"\x1bPz").expect("unknown DCS entry");
        for _ in 0..MAX_DCS_QUERY_PAYLOAD_BYTES * 2 {
            assert_eq!(unknown.advance(b'x'), Ok(Action::None));
        }
        assert!(matches!(unknown, ParserState::DcsIgnore));
        assert!(
            feed(&mut unknown, b"\x1b\\")
                .expect("unknown DCS completion")
                .is_empty()
        );

        let mut query = ParserState::Ground;
        feed(&mut query, b"\x1bP$q").expect("DCS query entry");
        assert_eq!(
            query.advance(b'x'),
            Err(ParseError::DcsQueryPayloadAllocation),
            "ignored DCS must not consume the query allocation hook"
        );
    }

    #[test]
    fn standalone_string_terminator_is_inert() {
        let mut parser = ParserState::Ground;
        assert!(
            feed(&mut parser, b"\x1b\\X")
                .expect("standalone ST and suffix")
                .contains(&Action::Print(b'X'))
        );
        assert!(parser.is_ground());
    }

    #[test]
    fn can_and_sub_abort_each_sequence_family() {
        for (prefix, cancel) in [
            (&b"\x1b[12"[..], 0x18),
            (&b"\x1b]2;title"[..], 0x1a),
            (&b"\x1bPqdata"[..], 0x18),
            (&b"\x1b "[..], 0x1a),
        ] {
            let mut parser = ParserState::Ground;
            feed(&mut parser, prefix).expect("sequence prefix");
            assert!(!parser.is_ground());
            assert_eq!(parser.advance(cancel), Ok(Action::None));
            assert!(parser.is_ground());
            assert_eq!(parser.advance(b'X'), Ok(Action::Print(b'X')));
        }
    }

    #[test]
    fn csi_retains_exactly_thirty_two_parameters() {
        let expected = (1..=MAX_CSI_PARAMS as u16).collect::<Vec<_>>();
        let bytes = csi_with_parameters(&expected, b'm');
        let sequence = parse_csi_at_split(&bytes, 0);

        assert_eq!(sequence.parameters(), expected);
        assert_eq!(sequence.parameter_separators().len(), MAX_CSI_PARAMS - 1);
        assert_eq!(sequence.final_byte(), b'm');
    }

    #[test]
    fn csi_ignores_parameters_after_thirty_two() {
        let parameters = (1..=40).collect::<Vec<_>>();
        let bytes = csi_with_parameters(&parameters, b'm');
        let sequence = parse_csi_at_split(&bytes, bytes.len());

        assert_eq!(sequence.parameters(), &parameters[..MAX_CSI_PARAMS]);
        assert_eq!(sequence.parameter_separators().len(), MAX_CSI_PARAMS - 1);
        assert_eq!(sequence.final_byte(), b'm');
    }

    #[test]
    fn long_sgr_at_parameter_scratch_cap_does_not_fail_closed() {
        let mut bytes = csi_with_parameters(&[1; 64], b'm');
        bytes.insert(2, b'0');
        assert_eq!(bytes.len() - b"\x1b[".len() - 1, MAX_PARAMETER_BYTES);

        let mut terminal = Terminal::new(8, 2).expect("terminal");
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: &bytes,
            })
            .expect("long SGR");
        assert!(!terminal.is_failed_closed());
        terminal
            .export_snapshot_v2()
            .expect("long SGR returns parser to ground");
    }

    #[test]
    fn over_capacity_csi_is_deterministic_one_byte_at_a_time() {
        let parameters = (1..=40).collect::<Vec<_>>();
        let bytes = csi_with_parameters(&parameters, b'm');
        let mut parser = ParserState::Ground;
        let mut actions = Vec::new();

        for byte in bytes {
            actions.extend(feed(&mut parser, &[byte]).expect("one-byte CSI chunk"));
        }

        let [Action::Sequence(Sequence::Csi(sequence))] = actions.as_slice() else {
            panic!("one CSI action expected");
        };
        assert_eq!(sequence.parameters(), &parameters[..MAX_CSI_PARAMS]);
        assert!(parser.is_ground());
    }

    #[test]
    fn parameter_value_limit_is_enforced_after_storage_cap() {
        let at_limit = parse_csi_at_split(b"\x1b[65534m", 0);
        assert_eq!(at_limit.parameters(), &[65_534]);

        let mut ignored_overflow = csi_with_parameters(&[1; MAX_CSI_PARAMS], b'm');
        ignored_overflow.truncate(ignored_overflow.len() - 1);
        ignored_overflow.extend_from_slice(b";65535m");
        for bytes in [b"\x1b[65535m".to_vec(), ignored_overflow] {
            let mut parser = ParserState::Ground;
            assert_eq!(
                feed(&mut parser, &bytes),
                Err(ParseError::ParameterOverflow)
            );
            assert!(parser.is_ground());
        }

        let mut invalid_tail = csi_with_parameters(&[1; MAX_CSI_PARAMS], b'm');
        invalid_tail.truncate(invalid_tail.len() - 1);
        invalid_tail.extend_from_slice(b";<m");
        let mut parser = ParserState::Ground;
        assert_eq!(
            feed(&mut parser, &invalid_tail),
            Err(ParseError::InvalidCsiSyntax)
        );
        assert!(parser.is_ground());
    }

    #[test]
    fn osc_selector_scratch_accepts_cap_and_rejects_cap_plus_one() {
        let mut at_cap = ParserState::Ground;
        feed(&mut at_cap, b"\x1b]").expect("OSC entry");
        feed(&mut at_cap, &[b'0'; MAX_OSC_SELECTOR_BYTES]).expect("selector at cap");
        assert_eq!(at_cap.advance(b';'), Ok(Action::None));

        let mut over_cap = ParserState::Ground;
        feed(&mut over_cap, b"\x1b]").expect("OSC entry");
        feed(&mut over_cap, &[b'0'; MAX_OSC_SELECTOR_BYTES]).expect("selector at cap");
        assert_eq!(
            over_cap.advance(b'0'),
            Err(ParseError::OscSelectorScratchLimit)
        );
    }

    #[test]
    fn parameter_scratch_accepts_cap_and_rejects_cap_plus_one() {
        let mut at_cap = ParserState::Ground;
        feed(&mut at_cap, b"\x1b[").expect("CSI entry");
        feed(&mut at_cap, &[b'0'; MAX_PARAMETER_BYTES]).expect("scratch at cap");
        assert!(matches!(
            at_cap.advance(b'm'),
            Ok(Action::Sequence(Sequence::Csi(_)))
        ));

        let mut over_cap = ParserState::Ground;
        feed(&mut over_cap, b"\x1b[").expect("CSI entry");
        feed(&mut over_cap, &[b'0'; MAX_PARAMETER_BYTES]).expect("scratch at cap");
        assert_eq!(
            over_cap.advance(b'0'),
            Err(ParseError::ParameterScratchLimit)
        );
    }

    #[test]
    fn intermediate_count_accepts_cap_and_rejects_cap_plus_one() {
        let mut at_cap = ParserState::Ground;
        assert!(feed(&mut at_cap, b"\x1b  p").is_ok());
        assert!(at_cap.is_ground());

        let mut over_cap = ParserState::Ground;
        assert_eq!(
            feed(&mut over_cap, b"\x1b   "),
            Err(ParseError::TooManyIntermediates)
        );
    }

    #[test]
    fn nul_and_del_are_ignored_without_disturbing_pending_state() {
        let mut parser = ParserState::Ground;
        feed(&mut parser, b"\x1b[3").expect("CSI prefix");
        assert_eq!(parser.advance(0), Ok(Action::None));
        assert_eq!(parser.advance(0x7f), Ok(Action::None));
        assert!(matches!(
            parser.advance(b'm'),
            Ok(Action::Sequence(Sequence::Csi(_)))
        ));
    }
}
