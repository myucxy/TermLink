/// One of the four bounded ISO 2022 graphic-set registers.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum GraphicRegister {
    G0,
    G1,
    G2,
    G3,
}

impl GraphicRegister {
    const fn index(self) -> usize {
        match self {
            Self::G0 => 0,
            Self::G1 => 1,
            Self::G2 => 2,
            Self::G3 => 3,
        }
    }
}

/// The size class selected by a charset-designation intermediate byte.
///
/// Keeping the size in the designation API prevents a 96-character set from
/// being mistaken for a 94-character set with the same final byte.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum CharsetSize {
    Characters94,
    Characters96,
}

/// A supported Western national replacement character set.
///
/// Every NRC is a 94-character set. The variants describe the character
/// repertoire rather than a particular SCS spelling because some repertoires
/// have more than one standard designator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum NationalReplacementCharset {
    British,
    Dutch,
    Finnish,
    French,
    FrenchCanadian,
    German,
    Italian,
    NorwegianDanish,
    Portuguese,
    Spanish,
    Swedish,
    Swiss,
}

/// Graphic character sets whose mappings are implemented by the core.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum GraphicCharset {
    Ascii,
    DecSpecialGraphics,
    IsoLatin1Supplemental,
    NationalReplacement(NationalReplacementCharset),
}

/// A complete designation, including its 94/96 size class.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct CharsetDesignation {
    pub(crate) size: CharsetSize,
    pub(crate) charset: GraphicCharset,
}

impl CharsetDesignation {
    const ASCII: Self = Self {
        size: CharsetSize::Characters94,
        charset: GraphicCharset::Ascii,
    };

    #[allow(dead_code)] // Kept for the original empty-prefix designation API.
    fn decode(size: CharsetSize, final_byte: u8) -> Result<Self, CharsetError> {
        Self::decode_with_prefix(size, b"", final_byte)
    }

    /// Decodes the bytes after the G0-G3/94-96 selection intermediate.
    ///
    /// Most supported SCS spellings have no additional prefix. Portuguese is
    /// the bounded exception and uses `%6`, represented here as prefix `%`
    /// plus final byte `6`. A slice keeps this layer independent from parser
    /// storage while still rejecting truncated or surplus intermediates.
    fn decode_with_prefix(
        size: CharsetSize,
        prefix: &[u8],
        final_byte: u8,
    ) -> Result<Self, CharsetError> {
        let charset = match (size, prefix, final_byte) {
            (CharsetSize::Characters94, b"", b'B') => GraphicCharset::Ascii,
            (CharsetSize::Characters94, b"", b'0') => GraphicCharset::DecSpecialGraphics,
            (CharsetSize::Characters96, b"", b'A') => GraphicCharset::IsoLatin1Supplemental,
            (CharsetSize::Characters94, b"", b'A') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::British)
            }
            (CharsetSize::Characters94, b"", b'4') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Dutch)
            }
            (CharsetSize::Characters94, b"", b'5' | b'C') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Finnish)
            }
            (CharsetSize::Characters94, b"", b'R') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::French)
            }
            (CharsetSize::Characters94, b"", b'Q') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::FrenchCanadian)
            }
            (CharsetSize::Characters94, b"", b'K') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::German)
            }
            (CharsetSize::Characters94, b"", b'Y') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Italian)
            }
            (CharsetSize::Characters94, b"", b'E' | b'6') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::NorwegianDanish)
            }
            (CharsetSize::Characters94, b"%", b'6') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Portuguese)
            }
            (CharsetSize::Characters94, b"", b'Z') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Spanish)
            }
            (CharsetSize::Characters94, b"", b'7' | b'H') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Swedish)
            }
            (CharsetSize::Characters94, b"", b'=') => {
                GraphicCharset::NationalReplacement(NationalReplacementCharset::Swiss)
            }
            _ => return Err(CharsetError::UnknownDesignator { size, final_byte }),
        };
        let designation = Self { size, charset };
        if designation.has_valid_repertoire() {
            Ok(designation)
        } else {
            Err(CharsetError::UnknownDesignator { size, final_byte })
        }
    }

    const fn has_valid_repertoire(self) -> bool {
        matches!(
            (self.size, self.charset),
            (
                CharsetSize::Characters94,
                GraphicCharset::Ascii
                    | GraphicCharset::DecSpecialGraphics
                    | GraphicCharset::NationalReplacement(_)
            ) | (
                CharsetSize::Characters96,
                GraphicCharset::IsoLatin1Supplemental
            )
        )
    }
}

/// Explicit charset failures. Unsupported NRC, DRCS, and 96-character sets
/// are rejected instead of being silently treated as ASCII.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum CharsetError {
    UnknownDesignator { size: CharsetSize, final_byte: u8 },
    NotGraphic(u8),
}

/// Classification of a DECFRA fill-character parameter.
///
/// [`Self::InvalidPch`] means DEC specifies that the complete command is
/// ignored. [`Self::Unsupported`] instead means the parameter selected a
/// designated character set that this core cannot map losslessly, so callers
/// must fail closed rather than silently substituting a character.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RectangleFillError {
    InvalidPch(u32),
    Unsupported(CharsetDesignation),
}

/// Bounded designation, invocation, and pending single-shift state.
///
/// A pending SS2/SS3 is retained until one byte in the invoked set's graphic
/// range is mapped. SPACE does not consume a pending 94-character G-set; C0,
/// DEL, and non-ASCII bytes are rejected by [`Self::map_graphic`] without
/// consuming it, so parser chunking and snapshot boundaries cannot lose it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct CharsetState {
    designations: [CharsetDesignation; 4],
    gl: GraphicRegister,
    gr: GraphicRegister,
    single_shift: Option<GraphicRegister>,
}

impl Default for CharsetState {
    fn default() -> Self {
        let supplemental = CharsetDesignation {
            size: CharsetSize::Characters96,
            charset: GraphicCharset::IsoLatin1Supplemental,
        };
        Self {
            designations: [
                CharsetDesignation::ASCII,
                CharsetDesignation::ASCII,
                supplemental,
                supplemental,
            ],
            gl: GraphicRegister::G0,
            gr: GraphicRegister::G2,
            single_shift: None,
        }
    }
}

impl CharsetState {
    pub(crate) const fn designation(&self, register: GraphicRegister) -> CharsetDesignation {
        self.designations[register.index()]
    }

    pub(crate) const fn gl(&self) -> GraphicRegister {
        self.gl
    }

    pub(crate) const fn gr(&self) -> GraphicRegister {
        self.gr
    }

    pub(crate) const fn pending_single_shift(&self) -> Option<GraphicRegister> {
        self.single_shift
    }

    /// Designates a supported set into G0-G3.
    ///
    /// Decoding completes before mutation, so an unknown designator leaves all
    /// charset state unchanged.
    #[allow(dead_code)] // Kept as the original empty-prefix compatibility API.
    pub(crate) fn designate(
        &mut self,
        register: GraphicRegister,
        size: CharsetSize,
        final_byte: u8,
    ) -> Result<(), CharsetError> {
        let designation = CharsetDesignation::decode(size, final_byte)?;
        self.designations[register.index()] = designation;
        Ok(())
    }

    /// Designates a supported set whose SCS identifier may have a prefix.
    ///
    /// Decoding and repertoire validation complete before mutation. The old
    /// [`Self::designate`] entry point is exactly the empty-prefix form.
    pub(crate) fn designate_with_prefix(
        &mut self,
        register: GraphicRegister,
        size: CharsetSize,
        prefix: &[u8],
        final_byte: u8,
    ) -> Result<(), CharsetError> {
        let designation = CharsetDesignation::decode_with_prefix(size, prefix, final_byte)?;
        self.designations[register.index()] = designation;
        Ok(())
    }

    /// SI: invoke G0 into GL.
    pub(crate) fn shift_in(&mut self) {
        self.gl = GraphicRegister::G0;
    }

    /// SO: invoke G1 into GL.
    pub(crate) fn shift_out(&mut self) {
        self.gl = GraphicRegister::G1;
    }

    pub(crate) fn invoke_gl(&mut self, register: GraphicRegister) {
        self.gl = register;
    }

    /// Invokes one of the designated G-sets into GR.
    ///
    /// ISO 2022 locking shifts normally select G1, G2, or G3. The register is
    /// accepted directly here so escape-sequence dispatch can retain the
    /// exact invocation without duplicating charset state transitions.
    pub(crate) fn invoke_gr(&mut self, register: GraphicRegister) {
        self.gr = register;
    }

    pub(crate) fn single_shift_2(&mut self) {
        self.single_shift = Some(GraphicRegister::G2);
    }

    pub(crate) fn single_shift_3(&mut self) {
        self.single_shift = Some(GraphicRegister::G3);
    }

    /// Maps one 7-bit graphic byte using a pending single shift or the active GL.
    ///
    /// Only `0x20..=0x7e` is accepted. Invalid bytes do not consume pending SS2
    /// or SS3 state. SPACE belongs to a 96-character set but not a 94-character
    /// set, so a pending 94-character single shift survives it while SPACE is
    /// rendered through the locking GL invocation.
    #[allow(dead_code)] // Kept as the mode-off compatibility entry point.
    pub(crate) fn map_graphic(&mut self, byte: u8) -> Result<char, CharsetError> {
        self.map_graphic_with_national_replacement(byte, false)
    }

    /// Maps one GL byte while explicitly selecting whether NRC replacement is
    /// active. Designation/invocation state is independent from this mode, so
    /// a disabled NRC maps through ASCII without discarding the designation.
    pub(crate) fn map_graphic_with_national_replacement(
        &mut self,
        byte: u8,
        national_replacement_enabled: bool,
    ) -> Result<char, CharsetError> {
        if !(b' '..=b'~').contains(&byte) {
            return Err(CharsetError::NotGraphic(byte));
        }
        let register = match self.single_shift {
            Some(single_shift)
                if byte == b' '
                    && self.designations[single_shift.index()].size
                        == CharsetSize::Characters94 =>
            {
                self.gl
            }
            Some(single_shift) => {
                self.single_shift = None;
                single_shift
            }
            None => self.gl,
        };
        Ok(map_designation(
            self.designations[register.index()],
            byte,
            national_replacement_enabled,
        ))
    }

    /// Decodes a DECFRA Pch without changing charset invocation state.
    ///
    /// DEC assigns `32..=126` to the currently invoked GL register and
    /// `160..=255` to the currently invoked GR register. GL bytes are mapped
    /// unchanged. GR bytes have bit 7 cleared (`160 -> 32`, `255 -> 127`)
    /// before the selected G-set mapping is applied. Pending SS2/SS3 is not an
    /// invocation of GL or GR and is therefore neither used nor consumed.
    ///
    /// Values outside those two ranges are [`RectangleFillError::InvalidPch`]
    /// and make the complete DECFRA command ignorable. A designation whose
    /// mapping is unavailable is [`RectangleFillError::Unsupported`] so the
    /// terminal can fail closed without approximating its presentation.
    #[allow(dead_code)] // Kept as the mode-off compatibility entry point.
    pub(crate) fn decode_rectangle_fill(&self, pch: u32) -> Result<char, RectangleFillError> {
        self.decode_rectangle_fill_with_national_replacement(pch, false)
    }

    /// Mode-aware form of [`Self::decode_rectangle_fill`].
    pub(crate) fn decode_rectangle_fill_with_national_replacement(
        &self,
        pch: u32,
        national_replacement_enabled: bool,
    ) -> Result<char, RectangleFillError> {
        let (register, byte) = match pch {
            32..=126 => (self.gl, pch as u8),
            160..=255 => (self.gr, (pch as u8) & 0x7f),
            _ => return Err(RectangleFillError::InvalidPch(pch)),
        };
        let designation = self.designations[register.index()];
        if !designation.has_valid_repertoire() {
            return Err(RectangleFillError::Unsupported(designation));
        }
        Ok(map_designation(
            designation,
            byte,
            national_replacement_enabled,
        ))
    }

    pub(crate) fn hard_reset(&mut self) {
        *self = Self::default();
    }
}

fn map_designation(
    designation: CharsetDesignation,
    byte: u8,
    national_replacement_enabled: bool,
) -> char {
    match designation.charset {
        GraphicCharset::Ascii => char::from(byte),
        GraphicCharset::DecSpecialGraphics => map_dec_special_graphics(byte),
        GraphicCharset::IsoLatin1Supplemental => char::from_u32(u32::from(byte) + 128)
            .expect("Latin-1 supplemental mapping is a Unicode scalar"),
        GraphicCharset::NationalReplacement(charset) if national_replacement_enabled => {
            map_national_replacement(charset, byte)
        }
        GraphicCharset::NationalReplacement(_) => char::from(byte),
    }
}

/// Maps the ASCII replacement points defined by the twelve Western NRCs.
/// Bytes outside a set's replacement points retain their ASCII value.
fn map_national_replacement(charset: NationalReplacementCharset, byte: u8) -> char {
    match charset {
        NationalReplacementCharset::British => match byte {
            b'#' => '£',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Dutch => match byte {
            b'#' => '£',
            b'@' => '¾',
            b'[' => 'ĳ',
            b'\\' => '½',
            b']' => '|',
            b'{' => '¨',
            b'|' => 'ƒ',
            b'}' => '¼',
            b'~' => '´',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Finnish => match byte {
            b'[' => 'Ä',
            b'\\' => 'Ö',
            b']' => 'Å',
            b'^' => 'Ü',
            b'`' => 'é',
            b'{' => 'ä',
            b'|' => 'ö',
            b'}' => 'å',
            b'~' => 'ü',
            _ => char::from(byte),
        },
        NationalReplacementCharset::French => match byte {
            b'#' => '£',
            b'@' => 'à',
            b'[' => '°',
            b'\\' => 'ç',
            b']' => '§',
            b'{' => 'é',
            b'|' => 'ù',
            b'}' => 'è',
            b'~' => '¨',
            _ => char::from(byte),
        },
        NationalReplacementCharset::FrenchCanadian => match byte {
            b'@' => 'à',
            b'[' => 'â',
            b'\\' => 'ç',
            b']' => 'ê',
            b'^' => 'î',
            b'`' => 'ô',
            b'{' => 'é',
            b'|' => 'ù',
            b'}' => 'è',
            b'~' => 'û',
            _ => char::from(byte),
        },
        NationalReplacementCharset::German => match byte {
            b'@' => '§',
            b'[' => 'Ä',
            b'\\' => 'Ö',
            b']' => 'Ü',
            b'{' => 'ä',
            b'|' => 'ö',
            b'}' => 'ü',
            b'~' => 'ß',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Italian => match byte {
            b'#' => '£',
            b'@' => '§',
            b'[' => '°',
            b'\\' => 'ç',
            b']' => 'é',
            b'`' => 'ù',
            b'{' => 'à',
            b'|' => 'ò',
            b'}' => 'è',
            b'~' => 'ì',
            _ => char::from(byte),
        },
        NationalReplacementCharset::NorwegianDanish => match byte {
            b'@' => 'Ä',
            b'[' => 'Æ',
            b'\\' => 'Ø',
            b']' => 'Å',
            b'^' => 'Ü',
            b'`' => 'ä',
            b'{' => 'æ',
            b'|' => 'ø',
            b'}' => 'å',
            b'~' => 'ü',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Portuguese => match byte {
            b'[' => 'Ã',
            b'\\' => 'Ç',
            b']' => 'Õ',
            b'{' => 'ã',
            b'|' => 'ç',
            b'}' => 'õ',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Spanish => match byte {
            b'#' => '£',
            b'@' => '§',
            b'[' => '¡',
            b'\\' => 'Ñ',
            b']' => '¿',
            b'{' => '°',
            b'|' => 'ñ',
            b'}' => 'ç',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Swedish => match byte {
            b'@' => 'É',
            b'[' => 'Ä',
            b'\\' => 'Ö',
            b']' => 'Å',
            b'^' => 'Ü',
            b'`' => 'é',
            b'{' => 'ä',
            b'|' => 'ö',
            b'}' => 'å',
            b'~' => 'ü',
            _ => char::from(byte),
        },
        NationalReplacementCharset::Swiss => match byte {
            b'#' => 'ù',
            b'@' => 'à',
            b'[' => 'é',
            b'\\' => 'ç',
            b']' => 'ê',
            b'^' => 'î',
            b'_' => 'è',
            b'`' => 'ô',
            b'{' => 'ä',
            b'|' => 'ö',
            b'}' => 'ü',
            b'~' => 'û',
            _ => char::from(byte),
        },
    }
}

/// VT100 Special Graphics mapping. Bytes not assigned a distinct glyph retain
/// their ASCII value; this includes future callers mapping the rest of the
/// bounded 94-character repertoire.
fn map_dec_special_graphics(byte: u8) -> char {
    match byte {
        b'_' => ' ',
        b'`' => '◆',
        b'a' => '▒',
        b'b' => '␉',
        b'c' => '␌',
        b'd' => '␍',
        b'e' => '␊',
        b'f' => '°',
        b'g' => '±',
        b'h' => '␤',
        b'i' => '␋',
        b'j' => '┘',
        b'k' => '┐',
        b'l' => '┌',
        b'm' => '└',
        b'n' => '┼',
        b'o' => '⎺',
        b'p' => '⎻',
        b'q' => '─',
        b'r' => '⎼',
        b's' => '⎽',
        b't' => '├',
        b'u' => '┤',
        b'v' => '┴',
        b'w' => '┬',
        b'x' => '│',
        b'y' => '≤',
        b'z' => '≥',
        b'{' => 'π',
        b'|' => '≠',
        b'}' => '£',
        b'~' => '·',
        _ => char::from(byte),
    }
}

#[cfg(test)]
mod tests {
    use super::{
        CharsetDesignation, CharsetError, CharsetSize, CharsetState, GraphicCharset,
        GraphicRegister, NationalReplacementCharset, RectangleFillError,
    };

    const REGISTERS: [GraphicRegister; 4] = [
        GraphicRegister::G0,
        GraphicRegister::G1,
        GraphicRegister::G2,
        GraphicRegister::G3,
    ];

    type NrcRepertoire = (
        &'static [u8],
        u8,
        NationalReplacementCharset,
        &'static [(u8, char)],
    );

    #[test]
    fn default_invocation_uses_ascii_gl_and_latin1_supplemental_gr() {
        let state = CharsetState::default();
        assert_eq!(
            state.designation(GraphicRegister::G0),
            CharsetDesignation::ASCII
        );
        assert_eq!(
            state.designation(GraphicRegister::G1),
            CharsetDesignation::ASCII
        );
        for register in [GraphicRegister::G2, GraphicRegister::G3] {
            assert_eq!(
                state.designation(register),
                CharsetDesignation {
                    size: CharsetSize::Characters96,
                    charset: GraphicCharset::IsoLatin1Supplemental,
                }
            );
        }
        assert_eq!(state.gl(), GraphicRegister::G0);
        assert_eq!(state.gr(), GraphicRegister::G2);
        assert_eq!(state.pending_single_shift(), None);
    }

    #[test]
    fn designation_tracks_g0_through_g3_independently() {
        let mut state = CharsetState::default();
        for register in REGISTERS {
            state
                .designate(register, CharsetSize::Characters94, b'0')
                .expect("DEC Special Graphics is supported");
            assert_eq!(
                state.designation(register),
                CharsetDesignation {
                    size: CharsetSize::Characters94,
                    charset: GraphicCharset::DecSpecialGraphics,
                }
            );
            state
                .designate(register, CharsetSize::Characters94, b'B')
                .expect("ASCII is supported");
            assert_eq!(state.designation(register), CharsetDesignation::ASCII);
        }
    }

    #[test]
    fn shift_in_and_shift_out_select_g0_and_g1() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G1, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");

        state.shift_out();
        assert_eq!(state.gl(), GraphicRegister::G1);
        assert_eq!(state.map_graphic(b'q'), Ok('─'));
        state.shift_in();
        assert_eq!(state.gl(), GraphicRegister::G0);
        assert_eq!(state.map_graphic(b'q'), Ok('q'));
    }

    #[test]
    fn single_shift_persists_until_exactly_one_graphic() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G2, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");
        state.single_shift_2();
        assert_eq!(state.pending_single_shift(), Some(GraphicRegister::G2));

        assert_eq!(
            state.map_graphic(b'\n'),
            Err(CharsetError::NotGraphic(b'\n'))
        );
        assert_eq!(state.pending_single_shift(), Some(GraphicRegister::G2));
        assert_eq!(state.map_graphic(b'x'), Ok('│'));
        assert_eq!(state.pending_single_shift(), None);
        assert_eq!(state.map_graphic(b'x'), Ok('x'));
    }

    #[test]
    fn space_does_not_consume_a_pending_94_character_single_shift() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G2, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");
        state.single_shift_2();

        assert_eq!(state.map_graphic(b' '), Ok(' '));
        assert_eq!(state.pending_single_shift(), Some(GraphicRegister::G2));
        assert_eq!(state.map_graphic(b'q'), Ok('─'));
        assert_eq!(state.pending_single_shift(), None);
    }

    #[test]
    fn ss3_uses_g3_and_a_new_single_shift_replaces_the_pending_one() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G2, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");
        state.single_shift_2();
        state.single_shift_3();
        state
            .designate(GraphicRegister::G3, CharsetSize::Characters94, b'B')
            .expect("ASCII is supported");
        assert_eq!(state.pending_single_shift(), Some(GraphicRegister::G3));
        assert_eq!(state.map_graphic(b'q'), Ok('q'));

        state.single_shift_2();
        assert_eq!(state.map_graphic(b'q'), Ok('─'));
    }

    #[test]
    fn dec_special_graphics_maps_the_complete_backtick_through_tilde_range() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G0, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");

        let mapped: String = (b'`'..=b'~')
            .map(|byte| state.map_graphic(byte).expect("range is graphic"))
            .collect();
        assert_eq!(mapped, "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·");
        assert_eq!(state.map_graphic(b'_'), Ok(' '));
        assert_eq!(state.map_graphic(b'A'), Ok('A'));
    }

    #[test]
    fn unknown_and_unimplemented_designators_are_rejected_transactionally() {
        let mut state = CharsetState::default();
        let original = state;
        assert_eq!(
            state.designate(GraphicRegister::G1, CharsetSize::Characters94, b'?'),
            Err(CharsetError::UnknownDesignator {
                size: CharsetSize::Characters94,
                final_byte: b'?',
            })
        );
        assert_eq!(state, original);

        assert_eq!(
            state.designate(GraphicRegister::G2, CharsetSize::Characters96, b'B'),
            Err(CharsetError::UnknownDesignator {
                size: CharsetSize::Characters96,
                final_byte: b'B',
            })
        );
        assert_eq!(state, original);
    }

    #[test]
    fn hard_reset_clears_designations_invocation_and_pending_shift() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G0, CharsetSize::Characters94, b'0')
            .unwrap();
        state
            .designate(GraphicRegister::G1, CharsetSize::Characters94, b'0')
            .unwrap();
        state.shift_out();
        state.invoke_gr(GraphicRegister::G3);
        state.single_shift_2();
        state.hard_reset();
        assert_eq!(state, CharsetState::default());
        assert_eq!(state.gr(), GraphicRegister::G2);
    }

    #[test]
    fn rectangle_fill_classifies_all_parameter_boundaries() {
        let state = CharsetState::default();
        assert_eq!(
            state.decode_rectangle_fill(31),
            Err(RectangleFillError::InvalidPch(31))
        );
        assert_eq!(state.decode_rectangle_fill(32), Ok(' '));
        assert_eq!(state.decode_rectangle_fill(126), Ok('~'));
        assert_eq!(
            state.decode_rectangle_fill(127),
            Err(RectangleFillError::InvalidPch(127))
        );
        assert_eq!(
            state.decode_rectangle_fill(159),
            Err(RectangleFillError::InvalidPch(159))
        );
        assert_eq!(state.decode_rectangle_fill(160), Ok('\u{00a0}'));
        assert_eq!(state.decode_rectangle_fill(255), Ok('\u{00ff}'));
        assert_eq!(
            state.decode_rectangle_fill(256),
            Err(RectangleFillError::InvalidPch(256))
        );
    }

    #[test]
    fn rectangle_fill_maps_current_gl_and_gr_designations() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G1, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");
        state.shift_out();
        assert_eq!(state.decode_rectangle_fill(u32::from(b'q')), Ok('─'));

        state
            .designate(GraphicRegister::G3, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");
        state.invoke_gr(GraphicRegister::G3);
        assert_eq!(state.decode_rectangle_fill(u32::from(b'q') + 128), Ok('─'));
    }

    #[test]
    fn rectangle_fill_does_not_consume_or_use_pending_single_shift() {
        let mut state = CharsetState::default();
        state
            .designate(GraphicRegister::G0, CharsetSize::Characters94, b'0')
            .expect("DEC Special Graphics is supported");
        state
            .designate(GraphicRegister::G2, CharsetSize::Characters94, b'B')
            .expect("ASCII is supported");
        state.single_shift_2();
        let original = state;

        assert_eq!(state.decode_rectangle_fill(u32::from(b'x')), Ok('│'));
        assert_eq!(state, original);
        assert_eq!(state.pending_single_shift(), Some(GraphicRegister::G2));

        assert_eq!(state.map_graphic(b'x'), Ok('x'));
        assert_eq!(state.pending_single_shift(), None);
    }

    #[test]
    fn rectangle_fill_rejects_unimplemented_designation_size() {
        let mut state = CharsetState::default();
        let unsupported = CharsetDesignation {
            size: CharsetSize::Characters96,
            charset: GraphicCharset::Ascii,
        };
        state.designations[GraphicRegister::G2.index()] = unsupported;
        let original = state;

        assert_eq!(
            state.decode_rectangle_fill(160),
            Err(RectangleFillError::Unsupported(unsupported))
        );
        assert_eq!(state, original);
    }

    #[test]
    fn all_western_nrc_designators_and_aliases_decode_to_their_repertoire() {
        let spellings: &[(&[u8], u8, NationalReplacementCharset)] = &[
            (b"", b'A', NationalReplacementCharset::British),
            (b"", b'4', NationalReplacementCharset::Dutch),
            (b"", b'5', NationalReplacementCharset::Finnish),
            (b"", b'C', NationalReplacementCharset::Finnish),
            (b"", b'R', NationalReplacementCharset::French),
            (b"", b'Q', NationalReplacementCharset::FrenchCanadian),
            (b"", b'K', NationalReplacementCharset::German),
            (b"", b'Y', NationalReplacementCharset::Italian),
            (b"", b'E', NationalReplacementCharset::NorwegianDanish),
            (b"", b'6', NationalReplacementCharset::NorwegianDanish),
            (b"%", b'6', NationalReplacementCharset::Portuguese),
            (b"", b'Z', NationalReplacementCharset::Spanish),
            (b"", b'7', NationalReplacementCharset::Swedish),
            (b"", b'H', NationalReplacementCharset::Swedish),
            (b"", b'=', NationalReplacementCharset::Swiss),
        ];

        for &(prefix, final_byte, charset) in spellings {
            let mut state = CharsetState::default();
            state
                .designate_with_prefix(
                    GraphicRegister::G1,
                    CharsetSize::Characters94,
                    prefix,
                    final_byte,
                )
                .expect("known Western NRC spelling must decode");
            assert_eq!(
                state.designation(GraphicRegister::G1),
                CharsetDesignation {
                    size: CharsetSize::Characters94,
                    charset: GraphicCharset::NationalReplacement(charset),
                },
                "prefix {prefix:?}, final byte {final_byte:#04x}"
            );
        }
    }

    #[test]
    fn nrc_designation_rejects_wrong_repertoire_and_bad_prefix_transactionally() {
        let invalid_pairs: &[(&[u8], u8)] = &[
            (b"", b'4'),
            (b"", b'5'),
            (b"", b'R'),
            (b"", b'Q'),
            (b"", b'K'),
            (b"", b'Y'),
            (b"", b'E'),
            (b"%", b'6'),
            (b"", b'Z'),
            (b"", b'7'),
            (b"", b'='),
        ];
        for &(prefix, final_byte) in invalid_pairs {
            let mut state = CharsetState::default();
            let original = state;
            assert_eq!(
                state.designate_with_prefix(
                    GraphicRegister::G3,
                    CharsetSize::Characters96,
                    prefix,
                    final_byte,
                ),
                Err(CharsetError::UnknownDesignator {
                    size: CharsetSize::Characters96,
                    final_byte,
                })
            );
            assert_eq!(state, original);
        }

        for prefix in [&b"%"[..], &b"%%"[..], &b" "[..]] {
            let mut state = CharsetState::default();
            let original = state;
            assert_eq!(
                state.designate_with_prefix(
                    GraphicRegister::G0,
                    CharsetSize::Characters94,
                    prefix,
                    b'B',
                ),
                Err(CharsetError::UnknownDesignator {
                    size: CharsetSize::Characters94,
                    final_byte: b'B',
                })
            );
            assert_eq!(state, original);
        }

        assert!(CharsetDesignation::ASCII.has_valid_repertoire());
        assert!(
            !CharsetDesignation {
                size: CharsetSize::Characters96,
                charset: GraphicCharset::NationalReplacement(NationalReplacementCharset::British),
            }
            .has_valid_repertoire()
        );
        assert!(
            !CharsetDesignation {
                size: CharsetSize::Characters94,
                charset: GraphicCharset::IsoLatin1Supplemental,
            }
            .has_valid_repertoire()
        );
    }

    #[test]
    fn every_western_nrc_maps_its_complete_replacement_table() {
        let repertoires: &[NrcRepertoire] = &[
            (
                b"",
                b'A',
                NationalReplacementCharset::British,
                &[(b'#', '£')],
            ),
            (
                b"",
                b'4',
                NationalReplacementCharset::Dutch,
                &[
                    (b'#', '£'),
                    (b'@', '¾'),
                    (b'[', 'ĳ'),
                    (b'\\', '½'),
                    (b']', '|'),
                    (b'{', '¨'),
                    (b'|', 'ƒ'),
                    (b'}', '¼'),
                    (b'~', '´'),
                ],
            ),
            (
                b"",
                b'5',
                NationalReplacementCharset::Finnish,
                &[
                    (b'[', 'Ä'),
                    (b'\\', 'Ö'),
                    (b']', 'Å'),
                    (b'^', 'Ü'),
                    (b'`', 'é'),
                    (b'{', 'ä'),
                    (b'|', 'ö'),
                    (b'}', 'å'),
                    (b'~', 'ü'),
                ],
            ),
            (
                b"",
                b'R',
                NationalReplacementCharset::French,
                &[
                    (b'#', '£'),
                    (b'@', 'à'),
                    (b'[', '°'),
                    (b'\\', 'ç'),
                    (b']', '§'),
                    (b'{', 'é'),
                    (b'|', 'ù'),
                    (b'}', 'è'),
                    (b'~', '¨'),
                ],
            ),
            (
                b"",
                b'Q',
                NationalReplacementCharset::FrenchCanadian,
                &[
                    (b'@', 'à'),
                    (b'[', 'â'),
                    (b'\\', 'ç'),
                    (b']', 'ê'),
                    (b'^', 'î'),
                    (b'`', 'ô'),
                    (b'{', 'é'),
                    (b'|', 'ù'),
                    (b'}', 'è'),
                    (b'~', 'û'),
                ],
            ),
            (
                b"",
                b'K',
                NationalReplacementCharset::German,
                &[
                    (b'@', '§'),
                    (b'[', 'Ä'),
                    (b'\\', 'Ö'),
                    (b']', 'Ü'),
                    (b'{', 'ä'),
                    (b'|', 'ö'),
                    (b'}', 'ü'),
                    (b'~', 'ß'),
                ],
            ),
            (
                b"",
                b'Y',
                NationalReplacementCharset::Italian,
                &[
                    (b'#', '£'),
                    (b'@', '§'),
                    (b'[', '°'),
                    (b'\\', 'ç'),
                    (b']', 'é'),
                    (b'`', 'ù'),
                    (b'{', 'à'),
                    (b'|', 'ò'),
                    (b'}', 'è'),
                    (b'~', 'ì'),
                ],
            ),
            (
                b"",
                b'E',
                NationalReplacementCharset::NorwegianDanish,
                &[
                    (b'@', 'Ä'),
                    (b'[', 'Æ'),
                    (b'\\', 'Ø'),
                    (b']', 'Å'),
                    (b'^', 'Ü'),
                    (b'`', 'ä'),
                    (b'{', 'æ'),
                    (b'|', 'ø'),
                    (b'}', 'å'),
                    (b'~', 'ü'),
                ],
            ),
            (
                b"%",
                b'6',
                NationalReplacementCharset::Portuguese,
                &[
                    (b'[', 'Ã'),
                    (b'\\', 'Ç'),
                    (b']', 'Õ'),
                    (b'{', 'ã'),
                    (b'|', 'ç'),
                    (b'}', 'õ'),
                ],
            ),
            (
                b"",
                b'Z',
                NationalReplacementCharset::Spanish,
                &[
                    (b'#', '£'),
                    (b'@', '§'),
                    (b'[', '¡'),
                    (b'\\', 'Ñ'),
                    (b']', '¿'),
                    (b'{', '°'),
                    (b'|', 'ñ'),
                    (b'}', 'ç'),
                ],
            ),
            (
                b"",
                b'7',
                NationalReplacementCharset::Swedish,
                &[
                    (b'@', 'É'),
                    (b'[', 'Ä'),
                    (b'\\', 'Ö'),
                    (b']', 'Å'),
                    (b'^', 'Ü'),
                    (b'`', 'é'),
                    (b'{', 'ä'),
                    (b'|', 'ö'),
                    (b'}', 'å'),
                    (b'~', 'ü'),
                ],
            ),
            (
                b"",
                b'=',
                NationalReplacementCharset::Swiss,
                &[
                    (b'#', 'ù'),
                    (b'@', 'à'),
                    (b'[', 'é'),
                    (b'\\', 'ç'),
                    (b']', 'ê'),
                    (b'^', 'î'),
                    (b'_', 'è'),
                    (b'`', 'ô'),
                    (b'{', 'ä'),
                    (b'|', 'ö'),
                    (b'}', 'ü'),
                    (b'~', 'û'),
                ],
            ),
        ];

        for &(prefix, final_byte, charset, replacements) in repertoires {
            let mut state = CharsetState::default();
            state
                .designate_with_prefix(
                    GraphicRegister::G0,
                    CharsetSize::Characters94,
                    prefix,
                    final_byte,
                )
                .unwrap();
            assert_eq!(
                state.designation(GraphicRegister::G0).charset,
                GraphicCharset::NationalReplacement(charset)
            );

            for byte in b'!'..=b'~' {
                assert_eq!(
                    state
                        .map_graphic_with_national_replacement(byte, false)
                        .unwrap(),
                    char::from(byte),
                    "disabled {charset:?} at {byte:#04x}"
                );
                let expected = replacements
                    .iter()
                    .find_map(|&(candidate, mapped)| (candidate == byte).then_some(mapped))
                    .unwrap_or_else(|| char::from(byte));
                assert_eq!(
                    state
                        .map_graphic_with_national_replacement(byte, true)
                        .unwrap(),
                    expected,
                    "enabled {charset:?} at {byte:#04x}"
                );
            }
        }
    }

    #[test]
    fn mode_aware_nrc_mapping_preserves_shift_and_rectangle_semantics() {
        let mut state = CharsetState::default();
        state
            .designate_with_prefix(GraphicRegister::G2, CharsetSize::Characters94, b"%", b'6')
            .unwrap();
        state.single_shift_2();

        assert_eq!(
            state.map_graphic_with_national_replacement(b' ', true),
            Ok(' ')
        );
        assert_eq!(state.pending_single_shift(), Some(GraphicRegister::G2));
        assert_eq!(
            state.map_graphic_with_national_replacement(b'[', true),
            Ok('Ã')
        );
        assert_eq!(state.pending_single_shift(), None);

        state.invoke_gr(GraphicRegister::G2);
        assert_eq!(state.decode_rectangle_fill(128 + u32::from(b'[')), Ok('['));
        assert_eq!(
            state.decode_rectangle_fill_with_national_replacement(128 + u32::from(b'['), true,),
            Ok('Ã')
        );
    }
}
