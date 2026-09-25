use crate::modes::{ModeState, ModifyOtherKeys, MouseEncoding, MouseTracking};

/// Maximum number of payload bytes accepted by one paste event.
///
/// Bracketed-paste delimiters are not included in this limit.
pub const MAX_PASTE_BYTES: usize = 1024 * 1024;

/// Maximum UTF-8 byte length accepted for Kitty associated text.
pub const MAX_KITTY_ASSOCIATED_TEXT_BYTES: usize = 4096;

/// Maximum number of Unicode scalar values accepted for Kitty associated text.
pub const MAX_KITTY_ASSOCIATED_TEXT_SCALARS: usize = 1024;

/// Largest zero-based cell coordinate representable by the default mouse
/// encoding.
///
/// The wire byte contains the one-based coordinate plus 32, so 222 is encoded
/// as 255.
pub const MAX_DEFAULT_MOUSE_CELL_COORDINATE: u32 = 222;

/// Largest zero-based cell coordinate representable by DEC private mode 1005.
///
/// Mode 1005 encodes the one-based coordinate plus 32 as a UTF-8 scalar no
/// larger than U+07FF.
pub const MAX_UTF8_MOUSE_CELL_COORDINATE: u32 = 2014;

/// Largest zero-based pixel coordinate accepted for DEC private mode 1016.
///
/// One is added for the wire representation, so `u32::MAX` itself cannot be
/// represented by the semantic API.
pub const MAX_MOUSE_PIXEL_COORDINATE: u32 = u32::MAX - 1;

const ESC: u8 = 0x1b;
const BRACKETED_PASTE_START: &[u8] = b"\x1b[200~";
const BRACKETED_PASTE_END: &[u8] = b"\x1b[201~";
const FOCUS_GAINED: &[u8] = b"\x1b[I";
const FOCUS_LOST: &[u8] = b"\x1b[O";
const MAX_WIN32_INPUT_SEQUENCE_BYTES: usize = 39;

/// A terminal input event.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InputEvent<'a> {
    Key(KeyEvent),
    /// One semantic key event for the negotiated Kitty keyboard protocol.
    KittyKey(KittyKeyEvent<'a>),
    /// One lossless Win32 `KEY_EVENT_RECORD` keyboard event.
    ///
    /// This event is accepted only while DEC private mode 9001 is enabled.
    Win32Key(Win32KeyEvent),
    Paste(PasteEvent<'a>),
    Mouse(MouseEvent),
    /// The terminal view gained focus.
    ///
    /// This produces input only while DEC private mode 1004 is enabled.
    FocusGained,
    /// The terminal view lost focus.
    ///
    /// This produces input only while DEC private mode 1004 is enabled.
    FocusLost,
}

/// A key press after the platform adapter has resolved the keyboard layout.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct KeyEvent {
    pub key: Key,
    pub modifiers: Modifiers,
}

/// One event type reported by the Kitty keyboard protocol.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KittyKeyEventType {
    Press,
    Repeat,
    Release,
}

/// The complete Kitty keyboard modifier bitfield.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct KittyModifiers {
    pub shift: bool,
    pub alt: bool,
    pub control: bool,
    pub super_key: bool,
    pub hyper: bool,
    pub meta: bool,
    pub caps_lock: bool,
    pub num_lock: bool,
}

impl KittyModifiers {
    pub const NONE: Self = Self {
        shift: false,
        alt: false,
        control: false,
        super_key: false,
        hyper: false,
        meta: false,
        caps_lock: false,
        num_lock: false,
    };

    const fn parameter(self) -> u16 {
        1 + self.shift as u16
            + (self.alt as u16 * 2)
            + (self.control as u16 * 4)
            + (self.super_key as u16 * 8)
            + (self.hyper as u16 * 16)
            + (self.meta as u16 * 32)
            + (self.caps_lock as u16 * 64)
            + (self.num_lock as u16 * 128)
    }

    const fn has_unrepresentable_legacy_modifier(self) -> bool {
        self.super_key || self.hyper
    }

    const fn legacy(self) -> Modifiers {
        Modifiers {
            shift: self.shift,
            alt: self.alt,
            control: self.control,
            meta: self.meta,
        }
    }
}

/// A Kitty key identity. Character identities are always the unshifted key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KittyKey {
    Character(char),
    Functional(KittyFunctionalKey),
    /// Text was produced, but the platform adapter could not identify a key.
    UnidentifiedText,
}

/// Functional key identities assigned by the Kitty keyboard protocol.
///
/// The declaration order follows the protocol's codepoint table.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KittyFunctionalKey {
    Escape,
    Enter,
    Tab,
    Backspace,
    Insert,
    Delete,
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,
    PageUp,
    PageDown,
    Home,
    End,
    CapsLock,
    ScrollLock,
    NumLock,
    PrintScreen,
    Pause,
    Menu,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24,
    F25,
    F26,
    F27,
    F28,
    F29,
    F30,
    F31,
    F32,
    F33,
    F34,
    F35,
    Keypad0,
    Keypad1,
    Keypad2,
    Keypad3,
    Keypad4,
    Keypad5,
    Keypad6,
    Keypad7,
    Keypad8,
    Keypad9,
    KeypadDecimal,
    KeypadDivide,
    KeypadMultiply,
    KeypadSubtract,
    KeypadAdd,
    KeypadEnter,
    KeypadEqual,
    KeypadSeparator,
    KeypadArrowLeft,
    KeypadArrowRight,
    KeypadArrowUp,
    KeypadArrowDown,
    KeypadPageUp,
    KeypadPageDown,
    KeypadHome,
    KeypadEnd,
    KeypadInsert,
    KeypadDelete,
    KeypadBegin,
    MediaPlay,
    MediaPause,
    MediaPlayPause,
    MediaReverse,
    MediaStop,
    MediaFastForward,
    MediaRewind,
    MediaTrackNext,
    MediaTrackPrevious,
    MediaRecord,
    LowerVolume,
    RaiseVolume,
    MuteVolume,
    LeftShift,
    LeftControl,
    LeftAlt,
    LeftSuper,
    LeftHyper,
    LeftMeta,
    RightShift,
    RightControl,
    RightAlt,
    RightSuper,
    RightHyper,
    RightMeta,
    IsoLevel3Shift,
    IsoLevel5Shift,
}

/// A layout-aware key event supplied to the Kitty keyboard encoder.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct KittyKeyEvent<'a> {
    pub key: KittyKey,
    pub modifiers: KittyModifiers,
    pub event_type: KittyKeyEventType,
    pub shifted_key: Option<char>,
    pub base_layout_key: Option<char>,
    pub associated_text: &'a str,
}

/// The fields of one Win32 `KEY_EVENT_RECORD`, without semantic conversion.
///
/// `unicode_char` is one UTF-16 code unit rather than a Unicode scalar value.
/// Zero-valued code units and repeat counts are preserved on the wire.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Win32KeyEvent {
    pub virtual_key: u16,
    pub scan_code: u16,
    pub unicode_char: u16,
    pub key_down: bool,
    pub control_key_state: u32,
    pub repeat_count: u16,
}

/// UTF-8-oriented paste bytes supplied by the caller.
///
/// Encoding applies the same terminal-paste normalization in plain and
/// bracketed modes: LF/CRLF become CR, C0 controls other than HT/LF/CR are
/// removed, and DEL/C1 controls are removed. Invalid non-control UTF-8 bytes
/// are retained so this byte-oriented ABI remains deterministic.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PasteEvent<'a> {
    pub bytes: &'a [u8],
}

/// One semantic mouse event at a zero-based terminal position.
///
/// `buttons_held` describes the state after this event. A press must include
/// its button and a release must exclude its button. Pixel coordinates are
/// optional unless DEC private mode 1016 needs to emit this event.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MouseEvent {
    pub action: MouseAction,
    pub position: MousePosition,
    pub buttons_held: MouseButtons,
    pub modifiers: Modifiers,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MouseAction {
    Press(MouseButton),
    Release(MouseButton),
    Move,
    /// One logical wheel step. Platform adapters are responsible for
    /// accumulating high-resolution device deltas.
    Wheel(MouseWheelDirection),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MouseButton {
    Left,
    Middle,
    Right,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MouseWheelDirection {
    Up,
    Down,
    Left,
    Right,
}

/// Cell coordinates relative to the visible terminal, with a zero-based
/// origin at the top left.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CellPosition {
    pub column: u32,
    pub row: u32,
}

/// Pixel coordinates relative to the terminal viewport, with a zero-based
/// origin at the top left.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PixelPosition {
    pub x: u32,
    pub y: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MousePosition {
    pub cell: CellPosition,
    pub pixel: Option<PixelPosition>,
}

/// Physical primary-button state after a mouse event.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct MouseButtons {
    pub left: bool,
    pub middle: bool,
    pub right: bool,
}

impl MouseButtons {
    pub const NONE: Self = Self {
        left: false,
        middle: false,
        right: false,
    };

    #[must_use]
    pub const fn contains(self, button: MouseButton) -> bool {
        match button {
            MouseButton::Left => self.left,
            MouseButton::Middle => self.middle,
            MouseButton::Right => self.right,
        }
    }

    const fn any(self) -> bool {
        self.left || self.middle || self.right
    }

    const fn primary_code(self) -> u8 {
        if self.left {
            0
        } else if self.middle {
            1
        } else if self.right {
            2
        } else {
            3
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Key {
    Character(char),
    Enter,
    Backspace,
    Tab,
    Escape,
    Delete,
    Insert,
    Home,
    End,
    PageUp,
    PageDown,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    Keypad(KeypadKey),
}

/// Keys whose encoding is selected by DEC numeric/application keypad mode.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KeypadKey {
    Digit0,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
    Decimal,
    Separator,
    Add,
    Subtract,
    Multiply,
    Divide,
    Equal,
    Enter,
}

/// Keyboard modifiers represented by the xterm modifier parameter.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct Modifiers {
    pub shift: bool,
    pub alt: bool,
    pub control: bool,
    pub meta: bool,
}

impl Modifiers {
    pub const NONE: Self = Self {
        shift: false,
        alt: false,
        control: false,
        meta: false,
    };

    const fn is_empty(self) -> bool {
        !self.shift && !self.alt && !self.control && !self.meta
    }

    const fn xterm_parameter(self) -> u8 {
        1 + self.shift as u8
            + (self.alt as u8 * 2)
            + (self.control as u8 * 4)
            + (self.meta as u8 * 8)
    }

    const fn has_legacy_meta_prefix(self) -> bool {
        self.alt || self.meta
    }
}

/// An input rejected before any bytes were produced.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InputEncodeError {
    TerminalFailedClosed,
    Win32InputModeDisabled,
    /// Legacy semantic keys are ambiguous after Kitty enhancement is active.
    KittyKeyEventRequired,
    InvalidKittyFlags {
        flags: u8,
    },
    KittyAssociatedTextTooLarge {
        actual_bytes: usize,
        maximum_bytes: usize,
        actual_scalars: usize,
        maximum_scalars: usize,
    },
    KittyTextContainsControl(char),
    InvalidKittyKeyEvent(KittyKeyEventValidationError),
    UnsupportedKittyLegacyEncoding {
        key: KittyKey,
        modifiers: KittyModifiers,
    },
    EmptyPaste,
    PasteTooLarge {
        actual: usize,
        maximum: usize,
    },
    UnsupportedModifiers {
        key: Key,
        modifiers: Modifiers,
    },
    UnsupportedControlCharacter(char),
    InvalidMouseButtonState {
        action: MouseAction,
        buttons_held: MouseButtons,
    },
    MissingMousePixelPosition,
    MouseCellOutOfBounds {
        position: CellPosition,
        columns: u32,
        rows: u32,
    },
    MouseCoordinateOutOfRange {
        value: u32,
        maximum: u32,
    },
    AllocationFailed,
}

/// The structural reason a Kitty semantic key event was rejected.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KittyKeyEventValidationError {
    ShiftedKeyWithoutShift,
    AlternateKeyForNonCharacter,
    AssociatedTextOnRelease,
    UnidentifiedTextMustBePress,
    UnidentifiedTextRequiresAssociatedText,
}

/// Encodes a semantic input event using the terminal's current input modes.
///
/// Unsupported combinations are returned as errors rather than silently
/// discarding modifiers. In particular, keypad modifiers are unsupported;
/// callers must resolve shifted numeric-keypad input before constructing an
/// event. A `Character` is already layout- and Shift-resolved. While
/// `modifyOtherKeys` is disabled it must not carry Shift; when that xterm mode
/// is enabled, Shift remains useful as wire metadata and the resolved scalar is
/// the reported codepoint. Enter, Backspace, and Escape have no portable Shift
/// or Control encoding outside `modifyOtherKeys`. Alt and Meta share the
/// traditional ESC prefix for literal input, so requesting both on a literal
/// key is rejected unless `modifyOtherKeys` represents them independently.
pub(crate) fn encode_input(
    event: InputEvent<'_>,
    modes: &ModeState,
    kitty_flags: u8,
) -> Result<Vec<u8>, InputEncodeError> {
    if kitty_flags & !0x1f != 0 {
        return Err(InputEncodeError::InvalidKittyFlags { flags: kitty_flags });
    }
    match event {
        InputEvent::Key(_) if kitty_flags != 0 => Err(InputEncodeError::KittyKeyEventRequired),
        InputEvent::Key(event) => encode_key(event, modes),
        InputEvent::KittyKey(event) => encode_kitty_key(event, modes, kitty_flags),
        InputEvent::Win32Key(event) => encode_win32_key(event, modes),
        InputEvent::Paste(event) => encode_paste(event, modes),
        InputEvent::Mouse(event) => encode_mouse(event, modes),
        InputEvent::FocusGained => encode_focus(FOCUS_GAINED, modes),
        InputEvent::FocusLost => encode_focus(FOCUS_LOST, modes),
    }
}

fn encode_kitty_key(
    event: KittyKeyEvent<'_>,
    modes: &ModeState,
    flags: u8,
) -> Result<Vec<u8>, InputEncodeError> {
    validate_kitty_key_event(event)?;

    if event.key == KittyKey::UnidentifiedText {
        return copy_bytes(event.associated_text.as_bytes());
    }

    let report_events = flags & 0x02 != 0;
    let report_all = flags & 0x08 != 0;
    let use_csi_u = match event.key {
        KittyKey::Character(character) => {
            report_all
                || flags & 0x01 != 0
                    && character.is_ascii()
                    && (event.modifiers.alt || event.modifiers.control)
        }
        KittyKey::Functional(key) if is_keypad_function(key) => {
            report_all
                || flags & 0x01 != 0 && event.associated_text.is_empty()
                || report_events && modes.keypad_application && event.associated_text.is_empty()
        }
        KittyKey::Functional(key) if is_pua_function(key) => flags != 0 && !is_keypad_function(key),
        KittyKey::Functional(KittyFunctionalKey::Escape) => report_all || flags & 0x01 != 0,
        KittyKey::Functional(
            KittyFunctionalKey::Enter | KittyFunctionalKey::Tab | KittyFunctionalKey::Backspace,
        ) => report_all,
        KittyKey::Functional(_) | KittyKey::UnidentifiedText => false,
    };
    let has_legacy_escape_shape =
        matches!(event.key, KittyKey::Functional(key) if kitty_legacy_escape_shape(key).is_some());
    if event.event_type == KittyKeyEventType::Release
        && (!report_events || !use_csi_u && !has_legacy_escape_shape)
    {
        return Ok(Vec::new());
    }

    if use_csi_u {
        let key_code = match event.key {
            KittyKey::Character(character) => u32::from(character),
            KittyKey::Functional(key) => kitty_functional_code(key).ok_or(
                InputEncodeError::UnsupportedKittyLegacyEncoding {
                    key: event.key,
                    modifiers: event.modifiers,
                },
            )?,
            KittyKey::UnidentifiedText => unreachable!("handled before CSI-u selection"),
        };
        return encode_kitty_csi_u(event, key_code, flags);
    }

    if !event.associated_text.is_empty() {
        if event.event_type == KittyKeyEventType::Release {
            return Ok(Vec::new());
        }
        return encode_kitty_legacy_text(event);
    }

    match event.key {
        KittyKey::Character(character) => encode_kitty_legacy_character(event, character, modes),
        KittyKey::Functional(key) => encode_kitty_legacy_functional(event, key, modes, flags),
        KittyKey::UnidentifiedText => unreachable!("handled before legacy selection"),
    }
}

fn validate_kitty_key_event(event: KittyKeyEvent<'_>) -> Result<(), InputEncodeError> {
    let scalar_count = event.associated_text.chars().count();
    if event.associated_text.len() > MAX_KITTY_ASSOCIATED_TEXT_BYTES
        || scalar_count > MAX_KITTY_ASSOCIATED_TEXT_SCALARS
    {
        return Err(InputEncodeError::KittyAssociatedTextTooLarge {
            actual_bytes: event.associated_text.len(),
            maximum_bytes: MAX_KITTY_ASSOCIATED_TEXT_BYTES,
            actual_scalars: scalar_count,
            maximum_scalars: MAX_KITTY_ASSOCIATED_TEXT_SCALARS,
        });
    }

    for character in event.associated_text.chars() {
        if is_c0_or_c1(character) {
            return Err(InputEncodeError::KittyTextContainsControl(character));
        }
    }
    if let KittyKey::Character(character) = event.key
        && is_c0_or_c1(character)
    {
        return Err(InputEncodeError::KittyTextContainsControl(character));
    }
    for character in [event.shifted_key, event.base_layout_key]
        .into_iter()
        .flatten()
    {
        if is_c0_or_c1(character) {
            return Err(InputEncodeError::KittyTextContainsControl(character));
        }
    }

    if event.shifted_key.is_some() && !event.modifiers.shift {
        return Err(InputEncodeError::InvalidKittyKeyEvent(
            KittyKeyEventValidationError::ShiftedKeyWithoutShift,
        ));
    }
    if !matches!(event.key, KittyKey::Character(_))
        && (event.shifted_key.is_some() || event.base_layout_key.is_some())
    {
        return Err(InputEncodeError::InvalidKittyKeyEvent(
            KittyKeyEventValidationError::AlternateKeyForNonCharacter,
        ));
    }
    if event.event_type == KittyKeyEventType::Release && !event.associated_text.is_empty() {
        return Err(InputEncodeError::InvalidKittyKeyEvent(
            KittyKeyEventValidationError::AssociatedTextOnRelease,
        ));
    }
    if event.key == KittyKey::UnidentifiedText {
        if event.event_type != KittyKeyEventType::Press {
            return Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::UnidentifiedTextMustBePress,
            ));
        }
        if event.associated_text.is_empty() {
            return Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::UnidentifiedTextRequiresAssociatedText,
            ));
        }
    }
    Ok(())
}

const fn is_c0_or_c1(character: char) -> bool {
    matches!(character as u32, 0x00..=0x1f | 0x7f..=0x9f)
}

fn encode_kitty_csi_u(
    event: KittyKeyEvent<'_>,
    key_code: u32,
    flags: u8,
) -> Result<Vec<u8>, InputEncodeError> {
    let include_alternates = flags & 0x04 != 0;
    let include_event = flags & 0x02 != 0;
    let include_text = flags & 0x18 == 0x18 && !event.associated_text.is_empty();
    let text_scalars = event.associated_text.chars().count();
    let mut output = allocate(32 + text_scalars * 8)?;
    output.extend_from_slice(b"\x1b[");
    push_decimal_u32(&mut output, key_code);

    if include_alternates {
        match (event.shifted_key, event.base_layout_key) {
            (Some(shifted), Some(base)) => {
                output.push(b':');
                push_decimal_u32(&mut output, u32::from(shifted));
                output.push(b':');
                push_decimal_u32(&mut output, u32::from(base));
            }
            (Some(shifted), None) => {
                output.push(b':');
                push_decimal_u32(&mut output, u32::from(shifted));
            }
            (None, Some(base)) => {
                output.extend_from_slice(b"::");
                push_decimal_u32(&mut output, u32::from(base));
            }
            (None, None) => {}
        }
    }

    let modifier_parameter = event.modifiers.parameter();
    if modifier_parameter != 1 || include_event || include_text {
        output.push(b';');
        push_decimal_u32(&mut output, u32::from(modifier_parameter));
    }
    if include_event {
        output.push(b':');
        output.push(match event.event_type {
            KittyKeyEventType::Press => b'1',
            KittyKeyEventType::Repeat => b'2',
            KittyKeyEventType::Release => b'3',
        });
    }
    if include_text {
        output.push(b';');
        let mut characters = event.associated_text.chars();
        if let Some(character) = characters.next() {
            push_decimal_u32(&mut output, u32::from(character));
        }
        for character in characters {
            output.push(b':');
            push_decimal_u32(&mut output, u32::from(character));
        }
    }
    output.push(b'u');
    Ok(output)
}

fn encode_kitty_legacy_text(event: KittyKeyEvent<'_>) -> Result<Vec<u8>, InputEncodeError> {
    let modifiers = event.modifiers;
    if modifiers.control
        || modifiers.has_unrepresentable_legacy_modifier()
        || (modifiers.alt && modifiers.meta)
    {
        return Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
            key: event.key,
            modifiers,
        });
    }
    encode_prefixed_bytes(
        event.associated_text.as_bytes(),
        modifiers.alt || modifiers.meta,
    )
}

fn encode_kitty_legacy_character(
    event: KittyKeyEvent<'_>,
    character: char,
    modes: &ModeState,
) -> Result<Vec<u8>, InputEncodeError> {
    if event.modifiers.has_unrepresentable_legacy_modifier() {
        return Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
            key: event.key,
            modifiers: event.modifiers,
        });
    }
    let resolved = if event.modifiers.shift {
        event.shifted_key.unwrap_or(character)
    } else if event.modifiers.caps_lock && character.is_ascii_alphabetic() {
        character.to_ascii_uppercase()
    } else {
        character
    };
    let mut modifiers = event.modifiers.legacy();
    modifiers.shift = false;
    encode_key(
        KeyEvent {
            key: Key::Character(resolved),
            modifiers,
        },
        modes,
    )
}

fn encode_kitty_legacy_functional(
    event: KittyKeyEvent<'_>,
    key: KittyFunctionalKey,
    modes: &ModeState,
    flags: u8,
) -> Result<Vec<u8>, InputEncodeError> {
    if flags == 0 {
        if event.modifiers.has_unrepresentable_legacy_modifier() {
            return Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
                key: event.key,
                modifiers: event.modifiers,
            });
        }
        return if let Some(legacy_key) = kitty_legacy_key(key) {
            encode_key(
                KeyEvent {
                    key: legacy_key,
                    modifiers: event.modifiers.legacy(),
                },
                modes,
            )
        } else if is_silent_legacy_function(key) {
            Ok(Vec::new())
        } else {
            Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
                key: event.key,
                modifiers: event.modifiers,
            })
        };
    }

    let include_event = flags & 0x02 != 0;

    if let Some((final_byte, tilde_number)) = kitty_legacy_escape_shape(key) {
        if key == KittyFunctionalKey::F3 {
            return encode_kitty_tilde(
                13,
                event.modifiers,
                include_event.then_some(event.event_type),
            );
        }
        if include_event {
            return if let Some(number) = tilde_number {
                encode_kitty_tilde(number, event.modifiers, Some(event.event_type))
            } else {
                encode_kitty_final(final_byte, event.modifiers, event.event_type)
            };
        }
        if event.modifiers.parameter() != 1 {
            return if let Some(number) = tilde_number {
                encode_kitty_tilde(number, event.modifiers, None)
            } else {
                encode_kitty_final_without_event(final_byte, event.modifiers)
            };
        }
    }

    if event.event_type == KittyKeyEventType::Release {
        return Ok(Vec::new());
    }
    if event.modifiers.has_unrepresentable_legacy_modifier() {
        return Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
            key: event.key,
            modifiers: event.modifiers,
        });
    }
    if let Some(legacy_key) = kitty_legacy_key(key) {
        return encode_key(
            KeyEvent {
                key: legacy_key,
                modifiers: event.modifiers.legacy(),
            },
            modes,
        );
    }

    Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
        key: event.key,
        modifiers: event.modifiers,
    })
}

fn encode_kitty_final(
    final_byte: u8,
    modifiers: KittyModifiers,
    event_type: KittyKeyEventType,
) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(18)?;
    output.extend_from_slice(b"\x1b[1;");
    push_decimal_u32(&mut output, u32::from(modifiers.parameter()));
    output.push(b':');
    output.push(kitty_event_byte(event_type));
    output.push(final_byte);
    Ok(output)
}

fn encode_kitty_final_without_event(
    final_byte: u8,
    modifiers: KittyModifiers,
) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(16)?;
    output.extend_from_slice(b"\x1b[1;");
    push_decimal_u32(&mut output, u32::from(modifiers.parameter()));
    output.push(final_byte);
    Ok(output)
}

fn encode_kitty_tilde(
    number: u8,
    modifiers: KittyModifiers,
    event_type: Option<KittyKeyEventType>,
) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(19)?;
    output.extend_from_slice(b"\x1b[");
    push_decimal(&mut output, number);
    if modifiers.parameter() != 1 || event_type.is_some() {
        output.push(b';');
        push_decimal_u32(&mut output, u32::from(modifiers.parameter()));
    }
    if let Some(event_type) = event_type {
        output.push(b':');
        output.push(kitty_event_byte(event_type));
    }
    output.push(b'~');
    Ok(output)
}

const fn kitty_event_byte(event_type: KittyKeyEventType) -> u8 {
    match event_type {
        KittyKeyEventType::Press => b'1',
        KittyKeyEventType::Repeat => b'2',
        KittyKeyEventType::Release => b'3',
    }
}

const fn kitty_functional_code(key: KittyFunctionalKey) -> Option<u32> {
    Some(match key {
        KittyFunctionalKey::Escape => 27,
        KittyFunctionalKey::Enter => 13,
        KittyFunctionalKey::Tab => 9,
        KittyFunctionalKey::Backspace => 127,
        KittyFunctionalKey::CapsLock => 57_358,
        KittyFunctionalKey::ScrollLock => 57_359,
        KittyFunctionalKey::NumLock => 57_360,
        KittyFunctionalKey::PrintScreen => 57_361,
        KittyFunctionalKey::Pause => 57_362,
        KittyFunctionalKey::Menu => 57_363,
        KittyFunctionalKey::F13 => 57_376,
        KittyFunctionalKey::F14 => 57_377,
        KittyFunctionalKey::F15 => 57_378,
        KittyFunctionalKey::F16 => 57_379,
        KittyFunctionalKey::F17 => 57_380,
        KittyFunctionalKey::F18 => 57_381,
        KittyFunctionalKey::F19 => 57_382,
        KittyFunctionalKey::F20 => 57_383,
        KittyFunctionalKey::F21 => 57_384,
        KittyFunctionalKey::F22 => 57_385,
        KittyFunctionalKey::F23 => 57_386,
        KittyFunctionalKey::F24 => 57_387,
        KittyFunctionalKey::F25 => 57_388,
        KittyFunctionalKey::F26 => 57_389,
        KittyFunctionalKey::F27 => 57_390,
        KittyFunctionalKey::F28 => 57_391,
        KittyFunctionalKey::F29 => 57_392,
        KittyFunctionalKey::F30 => 57_393,
        KittyFunctionalKey::F31 => 57_394,
        KittyFunctionalKey::F32 => 57_395,
        KittyFunctionalKey::F33 => 57_396,
        KittyFunctionalKey::F34 => 57_397,
        KittyFunctionalKey::F35 => 57_398,
        KittyFunctionalKey::Keypad0 => 57_399,
        KittyFunctionalKey::Keypad1 => 57_400,
        KittyFunctionalKey::Keypad2 => 57_401,
        KittyFunctionalKey::Keypad3 => 57_402,
        KittyFunctionalKey::Keypad4 => 57_403,
        KittyFunctionalKey::Keypad5 => 57_404,
        KittyFunctionalKey::Keypad6 => 57_405,
        KittyFunctionalKey::Keypad7 => 57_406,
        KittyFunctionalKey::Keypad8 => 57_407,
        KittyFunctionalKey::Keypad9 => 57_408,
        KittyFunctionalKey::KeypadDecimal => 57_409,
        KittyFunctionalKey::KeypadDivide => 57_410,
        KittyFunctionalKey::KeypadMultiply => 57_411,
        KittyFunctionalKey::KeypadSubtract => 57_412,
        KittyFunctionalKey::KeypadAdd => 57_413,
        KittyFunctionalKey::KeypadEnter => 57_414,
        KittyFunctionalKey::KeypadEqual => 57_415,
        KittyFunctionalKey::KeypadSeparator => 57_416,
        KittyFunctionalKey::KeypadArrowLeft => 57_417,
        KittyFunctionalKey::KeypadArrowRight => 57_418,
        KittyFunctionalKey::KeypadArrowUp => 57_419,
        KittyFunctionalKey::KeypadArrowDown => 57_420,
        KittyFunctionalKey::KeypadPageUp => 57_421,
        KittyFunctionalKey::KeypadPageDown => 57_422,
        KittyFunctionalKey::KeypadHome => 57_423,
        KittyFunctionalKey::KeypadEnd => 57_424,
        KittyFunctionalKey::KeypadInsert => 57_425,
        KittyFunctionalKey::KeypadDelete => 57_426,
        KittyFunctionalKey::KeypadBegin => 57_427,
        KittyFunctionalKey::MediaPlay => 57_428,
        KittyFunctionalKey::MediaPause => 57_429,
        KittyFunctionalKey::MediaPlayPause => 57_430,
        KittyFunctionalKey::MediaReverse => 57_431,
        KittyFunctionalKey::MediaStop => 57_432,
        KittyFunctionalKey::MediaFastForward => 57_433,
        KittyFunctionalKey::MediaRewind => 57_434,
        KittyFunctionalKey::MediaTrackNext => 57_435,
        KittyFunctionalKey::MediaTrackPrevious => 57_436,
        KittyFunctionalKey::MediaRecord => 57_437,
        KittyFunctionalKey::LowerVolume => 57_438,
        KittyFunctionalKey::RaiseVolume => 57_439,
        KittyFunctionalKey::MuteVolume => 57_440,
        KittyFunctionalKey::LeftShift => 57_441,
        KittyFunctionalKey::LeftControl => 57_442,
        KittyFunctionalKey::LeftAlt => 57_443,
        KittyFunctionalKey::LeftSuper => 57_444,
        KittyFunctionalKey::LeftHyper => 57_445,
        KittyFunctionalKey::LeftMeta => 57_446,
        KittyFunctionalKey::RightShift => 57_447,
        KittyFunctionalKey::RightControl => 57_448,
        KittyFunctionalKey::RightAlt => 57_449,
        KittyFunctionalKey::RightSuper => 57_450,
        KittyFunctionalKey::RightHyper => 57_451,
        KittyFunctionalKey::RightMeta => 57_452,
        KittyFunctionalKey::IsoLevel3Shift => 57_453,
        KittyFunctionalKey::IsoLevel5Shift => 57_454,
        KittyFunctionalKey::Insert
        | KittyFunctionalKey::Delete
        | KittyFunctionalKey::ArrowLeft
        | KittyFunctionalKey::ArrowRight
        | KittyFunctionalKey::ArrowUp
        | KittyFunctionalKey::ArrowDown
        | KittyFunctionalKey::PageUp
        | KittyFunctionalKey::PageDown
        | KittyFunctionalKey::Home
        | KittyFunctionalKey::End
        | KittyFunctionalKey::F1
        | KittyFunctionalKey::F2
        | KittyFunctionalKey::F3
        | KittyFunctionalKey::F4
        | KittyFunctionalKey::F5
        | KittyFunctionalKey::F6
        | KittyFunctionalKey::F7
        | KittyFunctionalKey::F8
        | KittyFunctionalKey::F9
        | KittyFunctionalKey::F10
        | KittyFunctionalKey::F11
        | KittyFunctionalKey::F12 => return None,
    })
}

const fn is_pua_function(key: KittyFunctionalKey) -> bool {
    matches!(
        key,
        KittyFunctionalKey::CapsLock
            | KittyFunctionalKey::ScrollLock
            | KittyFunctionalKey::NumLock
            | KittyFunctionalKey::PrintScreen
            | KittyFunctionalKey::Pause
            | KittyFunctionalKey::Menu
            | KittyFunctionalKey::F13
            | KittyFunctionalKey::F14
            | KittyFunctionalKey::F15
            | KittyFunctionalKey::F16
            | KittyFunctionalKey::F17
            | KittyFunctionalKey::F18
            | KittyFunctionalKey::F19
            | KittyFunctionalKey::F20
            | KittyFunctionalKey::F21
            | KittyFunctionalKey::F22
            | KittyFunctionalKey::F23
            | KittyFunctionalKey::F24
            | KittyFunctionalKey::F25
            | KittyFunctionalKey::F26
            | KittyFunctionalKey::F27
            | KittyFunctionalKey::F28
            | KittyFunctionalKey::F29
            | KittyFunctionalKey::F30
            | KittyFunctionalKey::F31
            | KittyFunctionalKey::F32
            | KittyFunctionalKey::F33
            | KittyFunctionalKey::F34
            | KittyFunctionalKey::F35
            | KittyFunctionalKey::Keypad0
            | KittyFunctionalKey::Keypad1
            | KittyFunctionalKey::Keypad2
            | KittyFunctionalKey::Keypad3
            | KittyFunctionalKey::Keypad4
            | KittyFunctionalKey::Keypad5
            | KittyFunctionalKey::Keypad6
            | KittyFunctionalKey::Keypad7
            | KittyFunctionalKey::Keypad8
            | KittyFunctionalKey::Keypad9
            | KittyFunctionalKey::KeypadDecimal
            | KittyFunctionalKey::KeypadDivide
            | KittyFunctionalKey::KeypadMultiply
            | KittyFunctionalKey::KeypadSubtract
            | KittyFunctionalKey::KeypadAdd
            | KittyFunctionalKey::KeypadEnter
            | KittyFunctionalKey::KeypadEqual
            | KittyFunctionalKey::KeypadSeparator
            | KittyFunctionalKey::KeypadArrowLeft
            | KittyFunctionalKey::KeypadArrowRight
            | KittyFunctionalKey::KeypadArrowUp
            | KittyFunctionalKey::KeypadArrowDown
            | KittyFunctionalKey::KeypadPageUp
            | KittyFunctionalKey::KeypadPageDown
            | KittyFunctionalKey::KeypadHome
            | KittyFunctionalKey::KeypadEnd
            | KittyFunctionalKey::KeypadInsert
            | KittyFunctionalKey::KeypadDelete
            | KittyFunctionalKey::KeypadBegin
            | KittyFunctionalKey::MediaPlay
            | KittyFunctionalKey::MediaPause
            | KittyFunctionalKey::MediaPlayPause
            | KittyFunctionalKey::MediaReverse
            | KittyFunctionalKey::MediaStop
            | KittyFunctionalKey::MediaFastForward
            | KittyFunctionalKey::MediaRewind
            | KittyFunctionalKey::MediaTrackNext
            | KittyFunctionalKey::MediaTrackPrevious
            | KittyFunctionalKey::MediaRecord
            | KittyFunctionalKey::LowerVolume
            | KittyFunctionalKey::RaiseVolume
            | KittyFunctionalKey::MuteVolume
            | KittyFunctionalKey::LeftShift
            | KittyFunctionalKey::LeftControl
            | KittyFunctionalKey::LeftAlt
            | KittyFunctionalKey::LeftSuper
            | KittyFunctionalKey::LeftHyper
            | KittyFunctionalKey::LeftMeta
            | KittyFunctionalKey::RightShift
            | KittyFunctionalKey::RightControl
            | KittyFunctionalKey::RightAlt
            | KittyFunctionalKey::RightSuper
            | KittyFunctionalKey::RightHyper
            | KittyFunctionalKey::RightMeta
            | KittyFunctionalKey::IsoLevel3Shift
            | KittyFunctionalKey::IsoLevel5Shift
    )
}

const fn is_keypad_function(key: KittyFunctionalKey) -> bool {
    matches!(
        key,
        KittyFunctionalKey::Keypad0
            | KittyFunctionalKey::Keypad1
            | KittyFunctionalKey::Keypad2
            | KittyFunctionalKey::Keypad3
            | KittyFunctionalKey::Keypad4
            | KittyFunctionalKey::Keypad5
            | KittyFunctionalKey::Keypad6
            | KittyFunctionalKey::Keypad7
            | KittyFunctionalKey::Keypad8
            | KittyFunctionalKey::Keypad9
            | KittyFunctionalKey::KeypadDecimal
            | KittyFunctionalKey::KeypadDivide
            | KittyFunctionalKey::KeypadMultiply
            | KittyFunctionalKey::KeypadSubtract
            | KittyFunctionalKey::KeypadAdd
            | KittyFunctionalKey::KeypadEnter
            | KittyFunctionalKey::KeypadEqual
            | KittyFunctionalKey::KeypadSeparator
            | KittyFunctionalKey::KeypadArrowLeft
            | KittyFunctionalKey::KeypadArrowRight
            | KittyFunctionalKey::KeypadArrowUp
            | KittyFunctionalKey::KeypadArrowDown
            | KittyFunctionalKey::KeypadPageUp
            | KittyFunctionalKey::KeypadPageDown
            | KittyFunctionalKey::KeypadHome
            | KittyFunctionalKey::KeypadEnd
            | KittyFunctionalKey::KeypadInsert
            | KittyFunctionalKey::KeypadDelete
            | KittyFunctionalKey::KeypadBegin
    )
}

const fn kitty_legacy_escape_shape(key: KittyFunctionalKey) -> Option<(u8, Option<u8>)> {
    match key {
        KittyFunctionalKey::ArrowUp => Some((b'A', None)),
        KittyFunctionalKey::ArrowDown => Some((b'B', None)),
        KittyFunctionalKey::ArrowRight => Some((b'C', None)),
        KittyFunctionalKey::ArrowLeft => Some((b'D', None)),
        KittyFunctionalKey::Home => Some((b'H', None)),
        KittyFunctionalKey::End => Some((b'F', None)),
        KittyFunctionalKey::F1 => Some((b'P', None)),
        KittyFunctionalKey::F2 => Some((b'Q', None)),
        KittyFunctionalKey::F3 => Some((b'R', None)),
        KittyFunctionalKey::F4 => Some((b'S', None)),
        KittyFunctionalKey::Insert => Some((0, Some(2))),
        KittyFunctionalKey::Delete => Some((0, Some(3))),
        KittyFunctionalKey::PageUp => Some((0, Some(5))),
        KittyFunctionalKey::PageDown => Some((0, Some(6))),
        KittyFunctionalKey::F5 => Some((0, Some(15))),
        KittyFunctionalKey::F6 => Some((0, Some(17))),
        KittyFunctionalKey::F7 => Some((0, Some(18))),
        KittyFunctionalKey::F8 => Some((0, Some(19))),
        KittyFunctionalKey::F9 => Some((0, Some(20))),
        KittyFunctionalKey::F10 => Some((0, Some(21))),
        KittyFunctionalKey::F11 => Some((0, Some(23))),
        KittyFunctionalKey::F12 => Some((0, Some(24))),
        _ => None,
    }
}

const fn kitty_legacy_key(key: KittyFunctionalKey) -> Option<Key> {
    match key {
        KittyFunctionalKey::Escape => Some(Key::Escape),
        KittyFunctionalKey::Enter => Some(Key::Enter),
        KittyFunctionalKey::Tab => Some(Key::Tab),
        KittyFunctionalKey::Backspace => Some(Key::Backspace),
        KittyFunctionalKey::Insert => Some(Key::Insert),
        KittyFunctionalKey::Delete => Some(Key::Delete),
        KittyFunctionalKey::ArrowLeft => Some(Key::ArrowLeft),
        KittyFunctionalKey::ArrowRight => Some(Key::ArrowRight),
        KittyFunctionalKey::ArrowUp => Some(Key::ArrowUp),
        KittyFunctionalKey::ArrowDown => Some(Key::ArrowDown),
        KittyFunctionalKey::PageUp => Some(Key::PageUp),
        KittyFunctionalKey::PageDown => Some(Key::PageDown),
        KittyFunctionalKey::Home => Some(Key::Home),
        KittyFunctionalKey::End => Some(Key::End),
        KittyFunctionalKey::F1 => Some(Key::F1),
        KittyFunctionalKey::F2 => Some(Key::F2),
        KittyFunctionalKey::F3 => Some(Key::F3),
        KittyFunctionalKey::F4 => Some(Key::F4),
        KittyFunctionalKey::F5 => Some(Key::F5),
        KittyFunctionalKey::F6 => Some(Key::F6),
        KittyFunctionalKey::F7 => Some(Key::F7),
        KittyFunctionalKey::F8 => Some(Key::F8),
        KittyFunctionalKey::F9 => Some(Key::F9),
        KittyFunctionalKey::F10 => Some(Key::F10),
        KittyFunctionalKey::F11 => Some(Key::F11),
        KittyFunctionalKey::F12 => Some(Key::F12),
        KittyFunctionalKey::Keypad0 => Some(Key::Keypad(KeypadKey::Digit0)),
        KittyFunctionalKey::Keypad1 => Some(Key::Keypad(KeypadKey::Digit1)),
        KittyFunctionalKey::Keypad2 => Some(Key::Keypad(KeypadKey::Digit2)),
        KittyFunctionalKey::Keypad3 => Some(Key::Keypad(KeypadKey::Digit3)),
        KittyFunctionalKey::Keypad4 => Some(Key::Keypad(KeypadKey::Digit4)),
        KittyFunctionalKey::Keypad5 => Some(Key::Keypad(KeypadKey::Digit5)),
        KittyFunctionalKey::Keypad6 => Some(Key::Keypad(KeypadKey::Digit6)),
        KittyFunctionalKey::Keypad7 => Some(Key::Keypad(KeypadKey::Digit7)),
        KittyFunctionalKey::Keypad8 => Some(Key::Keypad(KeypadKey::Digit8)),
        KittyFunctionalKey::Keypad9 => Some(Key::Keypad(KeypadKey::Digit9)),
        KittyFunctionalKey::KeypadDecimal => Some(Key::Keypad(KeypadKey::Decimal)),
        KittyFunctionalKey::KeypadSeparator => Some(Key::Keypad(KeypadKey::Separator)),
        KittyFunctionalKey::KeypadAdd => Some(Key::Keypad(KeypadKey::Add)),
        KittyFunctionalKey::KeypadSubtract => Some(Key::Keypad(KeypadKey::Subtract)),
        KittyFunctionalKey::KeypadMultiply => Some(Key::Keypad(KeypadKey::Multiply)),
        KittyFunctionalKey::KeypadDivide => Some(Key::Keypad(KeypadKey::Divide)),
        KittyFunctionalKey::KeypadEqual => Some(Key::Keypad(KeypadKey::Equal)),
        KittyFunctionalKey::KeypadEnter => Some(Key::Keypad(KeypadKey::Enter)),
        KittyFunctionalKey::KeypadArrowLeft => Some(Key::ArrowLeft),
        KittyFunctionalKey::KeypadArrowRight => Some(Key::ArrowRight),
        KittyFunctionalKey::KeypadArrowUp => Some(Key::ArrowUp),
        KittyFunctionalKey::KeypadArrowDown => Some(Key::ArrowDown),
        KittyFunctionalKey::KeypadPageUp => Some(Key::PageUp),
        KittyFunctionalKey::KeypadPageDown => Some(Key::PageDown),
        KittyFunctionalKey::KeypadHome => Some(Key::Home),
        KittyFunctionalKey::KeypadEnd => Some(Key::End),
        KittyFunctionalKey::KeypadInsert => Some(Key::Insert),
        KittyFunctionalKey::KeypadDelete => Some(Key::Delete),
        KittyFunctionalKey::KeypadBegin => Some(Key::Keypad(KeypadKey::Digit5)),
        _ => None,
    }
}

const fn is_silent_legacy_function(key: KittyFunctionalKey) -> bool {
    matches!(
        key,
        KittyFunctionalKey::CapsLock
            | KittyFunctionalKey::ScrollLock
            | KittyFunctionalKey::NumLock
            | KittyFunctionalKey::PrintScreen
            | KittyFunctionalKey::Pause
            | KittyFunctionalKey::Menu
            | KittyFunctionalKey::MediaPlay
            | KittyFunctionalKey::MediaPause
            | KittyFunctionalKey::MediaPlayPause
            | KittyFunctionalKey::MediaReverse
            | KittyFunctionalKey::MediaStop
            | KittyFunctionalKey::MediaFastForward
            | KittyFunctionalKey::MediaRewind
            | KittyFunctionalKey::MediaTrackNext
            | KittyFunctionalKey::MediaTrackPrevious
            | KittyFunctionalKey::MediaRecord
            | KittyFunctionalKey::LowerVolume
            | KittyFunctionalKey::RaiseVolume
            | KittyFunctionalKey::MuteVolume
            | KittyFunctionalKey::LeftShift
            | KittyFunctionalKey::LeftControl
            | KittyFunctionalKey::LeftAlt
            | KittyFunctionalKey::LeftSuper
            | KittyFunctionalKey::LeftHyper
            | KittyFunctionalKey::LeftMeta
            | KittyFunctionalKey::RightShift
            | KittyFunctionalKey::RightControl
            | KittyFunctionalKey::RightAlt
            | KittyFunctionalKey::RightSuper
            | KittyFunctionalKey::RightHyper
            | KittyFunctionalKey::RightMeta
            | KittyFunctionalKey::IsoLevel3Shift
            | KittyFunctionalKey::IsoLevel5Shift
    )
}

fn encode_win32_key(event: Win32KeyEvent, modes: &ModeState) -> Result<Vec<u8>, InputEncodeError> {
    if !modes.win32_input_mode {
        return Err(InputEncodeError::Win32InputModeDisabled);
    }

    let mut output = allocate(MAX_WIN32_INPUT_SEQUENCE_BYTES)?;
    output.extend_from_slice(&[ESC, b'[']);
    push_decimal_u32(&mut output, u32::from(event.virtual_key));
    output.push(b';');
    push_decimal_u32(&mut output, u32::from(event.scan_code));
    output.push(b';');
    push_decimal_u32(&mut output, u32::from(event.unicode_char));
    output.push(b';');
    output.push(if event.key_down { b'1' } else { b'0' });
    output.push(b';');
    push_decimal_u32(&mut output, event.control_key_state);
    output.push(b';');
    push_decimal_u32(&mut output, u32::from(event.repeat_count));
    output.push(b'_');
    Ok(output)
}

fn encode_focus(sequence: &[u8], modes: &ModeState) -> Result<Vec<u8>, InputEncodeError> {
    if modes.focus_reporting {
        copy_bytes(sequence)
    } else {
        Ok(Vec::new())
    }
}

fn encode_mouse(event: MouseEvent, modes: &ModeState) -> Result<Vec<u8>, InputEncodeError> {
    validate_mouse_event(event)?;

    if !mouse_tracking_reports(event, modes.mouse_tracking) {
        return Ok(Vec::new());
    }

    match modes.mouse_encoding {
        MouseEncoding::Default => encode_default_mouse(event),
        MouseEncoding::Utf8 => encode_utf8_mouse(event),
        MouseEncoding::Sgr => encode_sgr_mouse(event, event.position.cell),
        MouseEncoding::Urxvt => encode_urxvt_mouse(event),
        MouseEncoding::SgrPixels => {
            let pixel = event
                .position
                .pixel
                .ok_or(InputEncodeError::MissingMousePixelPosition)?;
            encode_sgr_mouse(
                event,
                CellPosition {
                    column: pixel.x,
                    row: pixel.y,
                },
            )
        }
    }
}

fn validate_mouse_event(event: MouseEvent) -> Result<(), InputEncodeError> {
    let consistent = match event.action {
        MouseAction::Press(button) => event.buttons_held.contains(button),
        MouseAction::Release(button) => !event.buttons_held.contains(button),
        MouseAction::Move | MouseAction::Wheel(_) => true,
    };
    if !consistent {
        return Err(InputEncodeError::InvalidMouseButtonState {
            action: event.action,
            buttons_held: event.buttons_held,
        });
    }

    validate_mouse_coordinate(event.position.cell.column, u32::MAX - 1)?;
    validate_mouse_coordinate(event.position.cell.row, u32::MAX - 1)?;
    if let Some(pixel) = event.position.pixel {
        validate_mouse_coordinate(pixel.x, MAX_MOUSE_PIXEL_COORDINATE)?;
        validate_mouse_coordinate(pixel.y, MAX_MOUSE_PIXEL_COORDINATE)?;
    }
    Ok(())
}

const fn mouse_tracking_reports(event: MouseEvent, tracking: MouseTracking) -> bool {
    match tracking {
        MouseTracking::None => false,
        MouseTracking::X10 => matches!(event.action, MouseAction::Press(_) | MouseAction::Wheel(_)),
        MouseTracking::Normal => !matches!(event.action, MouseAction::Move),
        MouseTracking::ButtonEvent => {
            !matches!(event.action, MouseAction::Move) || event.buttons_held.any()
        }
        MouseTracking::AnyEvent => true,
    }
}

fn encode_default_mouse(event: MouseEvent) -> Result<Vec<u8>, InputEncodeError> {
    validate_mouse_coordinate(
        event.position.cell.column,
        MAX_DEFAULT_MOUSE_CELL_COORDINATE,
    )?;
    validate_mouse_coordinate(event.position.cell.row, MAX_DEFAULT_MOUSE_CELL_COORDINATE)?;

    let button = mouse_report_code(event, false);
    let column = u8::try_from(event.position.cell.column + 33)
        .expect("validated default mouse column fits u8");
    let row =
        u8::try_from(event.position.cell.row + 33).expect("validated default mouse row fits u8");
    let mut output = allocate(6)?;
    output.extend_from_slice(&[ESC, b'[', b'M', 32 + button, column, row]);
    Ok(output)
}

fn encode_utf8_mouse(event: MouseEvent) -> Result<Vec<u8>, InputEncodeError> {
    validate_mouse_coordinate(event.position.cell.column, MAX_UTF8_MOUSE_CELL_COORDINATE)?;
    validate_mouse_coordinate(event.position.cell.row, MAX_UTF8_MOUSE_CELL_COORDINATE)?;

    let mut output = allocate(9)?;
    output.extend_from_slice(&[ESC, b'[', b'M']);
    push_utf8_scalar(&mut output, u32::from(32 + mouse_report_code(event, false)));
    push_utf8_scalar(&mut output, event.position.cell.column + 33);
    push_utf8_scalar(&mut output, event.position.cell.row + 33);
    Ok(output)
}

fn encode_sgr_mouse(
    event: MouseEvent,
    position: CellPosition,
) -> Result<Vec<u8>, InputEncodeError> {
    let column =
        position
            .column
            .checked_add(1)
            .ok_or(InputEncodeError::MouseCoordinateOutOfRange {
                value: position.column,
                maximum: u32::MAX - 1,
            })?;
    let row = position
        .row
        .checked_add(1)
        .ok_or(InputEncodeError::MouseCoordinateOutOfRange {
            value: position.row,
            maximum: u32::MAX - 1,
        })?;

    let mut output = allocate(32)?;
    output.extend_from_slice(b"\x1b[<");
    push_decimal_u32(&mut output, u32::from(mouse_report_code(event, true)));
    output.push(b';');
    push_decimal_u32(&mut output, column);
    output.push(b';');
    push_decimal_u32(&mut output, row);
    output.push(if matches!(event.action, MouseAction::Release(_)) {
        b'm'
    } else {
        b'M'
    });
    Ok(output)
}

fn encode_urxvt_mouse(event: MouseEvent) -> Result<Vec<u8>, InputEncodeError> {
    let column = event.position.cell.column + 1;
    let row = event.position.cell.row + 1;
    let mut output = allocate(31)?;
    output.extend_from_slice(&[ESC, b'[']);
    push_decimal_u32(&mut output, u32::from(32 + mouse_report_code(event, false)));
    output.push(b';');
    push_decimal_u32(&mut output, column);
    output.push(b';');
    push_decimal_u32(&mut output, row);
    output.push(b'M');
    Ok(output)
}

fn validate_mouse_coordinate(value: u32, maximum: u32) -> Result<(), InputEncodeError> {
    if value > maximum {
        return Err(InputEncodeError::MouseCoordinateOutOfRange { value, maximum });
    }
    Ok(())
}

const fn mouse_report_code(event: MouseEvent, sgr_release: bool) -> u8 {
    let base = match event.action {
        MouseAction::Press(button) => mouse_button_code(button),
        MouseAction::Release(button) if sgr_release => mouse_button_code(button),
        MouseAction::Release(_) => 3,
        MouseAction::Move => 32 + event.buttons_held.primary_code(),
        MouseAction::Wheel(MouseWheelDirection::Up) => 64,
        MouseAction::Wheel(MouseWheelDirection::Down) => 65,
        MouseAction::Wheel(MouseWheelDirection::Left) => 66,
        MouseAction::Wheel(MouseWheelDirection::Right) => 67,
    };
    base + mouse_modifier_code(event.modifiers)
}

const fn mouse_button_code(button: MouseButton) -> u8 {
    match button {
        MouseButton::Left => 0,
        MouseButton::Middle => 1,
        MouseButton::Right => 2,
    }
}

const fn mouse_modifier_code(modifiers: Modifiers) -> u8 {
    modifiers.shift as u8 * 4
        + (modifiers.alt || modifiers.meta) as u8 * 8
        + modifiers.control as u8 * 16
}

fn push_utf8_scalar(output: &mut Vec<u8>, value: u32) {
    let scalar = char::from_u32(value).expect("validated mouse value is a Unicode scalar");
    let mut encoded = [0; 4];
    output.extend_from_slice(scalar.encode_utf8(&mut encoded).as_bytes());
}

fn push_decimal_u32(output: &mut Vec<u8>, mut value: u32) {
    let mut digits = [0; 10];
    let mut index = digits.len();
    loop {
        index -= 1;
        digits[index] = b'0' + u8::try_from(value % 10).expect("decimal digit fits u8");
        value /= 10;
        if value == 0 {
            output.extend_from_slice(&digits[index..]);
            return;
        }
    }
}

fn encode_key(event: KeyEvent, modes: &ModeState) -> Result<Vec<u8>, InputEncodeError> {
    let KeyEvent { key, modifiers } = event;
    if let Some(codepoint) = modify_other_keys_codepoint(event, modes.modify_other_keys) {
        return encode_xterm_modify_other_key(codepoint, modifiers);
    }
    match key {
        Key::Character(character) if modes.modify_other_keys == ModifyOtherKeys::Level1 => {
            encode_level1_character(character, modifiers)
        }
        Key::Character(character) => encode_character(character, modifiers, key),
        Key::Enter => {
            let bytes = if modes.newline_mode {
                b"\r\n".as_slice()
            } else {
                b"\r".as_slice()
            };
            encode_literal(bytes, modifiers, key)
        }
        Key::Backspace => encode_literal(b"\x7f", modifiers, key),
        Key::Tab => encode_tab(modifiers, key),
        Key::Escape => encode_literal(b"\x1b", modifiers, key),
        Key::Delete => encode_csi_tilde(3, modifiers),
        Key::Insert => encode_csi_tilde(2, modifiers),
        Key::Home => encode_cursor_key(b'H', modifiers, modes.cursor_keys_application),
        Key::End => encode_cursor_key(b'F', modifiers, modes.cursor_keys_application),
        Key::PageUp => encode_csi_tilde(5, modifiers),
        Key::PageDown => encode_csi_tilde(6, modifiers),
        Key::ArrowUp => encode_cursor_key(b'A', modifiers, modes.cursor_keys_application),
        Key::ArrowDown => encode_cursor_key(b'B', modifiers, modes.cursor_keys_application),
        Key::ArrowLeft => encode_cursor_key(b'D', modifiers, modes.cursor_keys_application),
        Key::ArrowRight => encode_cursor_key(b'C', modifiers, modes.cursor_keys_application),
        Key::F1 => encode_function_key(b'P', None, modifiers),
        Key::F2 => encode_function_key(b'Q', None, modifiers),
        Key::F3 => encode_function_key(b'R', None, modifiers),
        Key::F4 => encode_function_key(b'S', None, modifiers),
        Key::F5 => encode_function_key(0, Some(15), modifiers),
        Key::F6 => encode_function_key(0, Some(17), modifiers),
        Key::F7 => encode_function_key(0, Some(18), modifiers),
        Key::F8 => encode_function_key(0, Some(19), modifiers),
        Key::F9 => encode_function_key(0, Some(20), modifiers),
        Key::F10 => encode_function_key(0, Some(21), modifiers),
        Key::F11 => encode_function_key(0, Some(23), modifiers),
        Key::F12 => encode_function_key(0, Some(24), modifiers),
        Key::Keypad(keypad_key) => encode_keypad(keypad_key, modifiers, modes, key),
    }
}

fn modify_other_keys_codepoint(event: KeyEvent, mode: ModifyOtherKeys) -> Option<u32> {
    if mode == ModifyOtherKeys::Disabled {
        return None;
    }
    let KeyEvent { key, modifiers } = event;
    let codepoint = match key {
        Key::Character(character) if !character.is_control() => u32::from(character),
        Key::Enter => u32::from(b'\r'),
        Key::Backspace => u32::from(0x7f_u8),
        Key::Tab => u32::from(b'\t'),
        Key::Escape => u32::from(ESC),
        _ => return None,
    };

    let encode = match mode {
        ModifyOtherKeys::Disabled => false,
        ModifyOtherKeys::Level1 => match key {
            Key::Character(character) => {
                (modifiers.control && modifiers.shift)
                    || (modifiers.control && xterm_control_byte(character).is_none())
                    || (modifiers.alt && modifiers.meta)
            }
            Key::Tab => !modifiers.is_empty() && !only_shift(modifiers),
            Key::Enter | Key::Escape => !modifiers.is_empty(),
            Key::Backspace => false,
            _ => false,
        },
        ModifyOtherKeys::Level2 => !modifiers.is_empty(),
        ModifyOtherKeys::Level3 => true,
    };
    encode.then_some(codepoint)
}

const fn only_shift(modifiers: Modifiers) -> bool {
    modifiers.shift && !modifiers.alt && !modifiers.control && !modifiers.meta
}

fn encode_level1_character(
    character: char,
    modifiers: Modifiers,
) -> Result<Vec<u8>, InputEncodeError> {
    if character.is_control() {
        return Err(InputEncodeError::UnsupportedControlCharacter(character));
    }
    if modifiers.control {
        let control = xterm_control_byte(character)
            .ok_or(InputEncodeError::UnsupportedControlCharacter(character))?;
        return encode_prefixed_bytes(&[control], modifiers.has_legacy_meta_prefix());
    }

    let mut encoded = [0; 4];
    encode_prefixed_bytes(
        character.encode_utf8(&mut encoded).as_bytes(),
        modifiers.has_legacy_meta_prefix(),
    )
}

fn encode_xterm_modify_other_key(
    codepoint: u32,
    modifiers: Modifiers,
) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(18)?;
    output.extend_from_slice(b"\x1b[27;");
    push_decimal(&mut output, modifiers.xterm_parameter());
    output.push(b';');
    push_decimal_u32(&mut output, codepoint);
    output.push(b'~');
    Ok(output)
}

fn encode_character(
    character: char,
    modifiers: Modifiers,
    key: Key,
) -> Result<Vec<u8>, InputEncodeError> {
    if modifiers.shift || (modifiers.alt && modifiers.meta) {
        return Err(InputEncodeError::UnsupportedModifiers { key, modifiers });
    }

    if modifiers.control {
        let control = control_byte(character)
            .ok_or(InputEncodeError::UnsupportedControlCharacter(character))?;
        return encode_prefixed_bytes(&[control], modifiers.has_legacy_meta_prefix());
    }
    if character.is_control() {
        return Err(InputEncodeError::UnsupportedControlCharacter(character));
    }

    let mut encoded = [0; 4];
    encode_prefixed_bytes(
        character.encode_utf8(&mut encoded).as_bytes(),
        modifiers.has_legacy_meta_prefix(),
    )
}

fn encode_literal(
    bytes: &[u8],
    modifiers: Modifiers,
    key: Key,
) -> Result<Vec<u8>, InputEncodeError> {
    if modifiers.shift || modifiers.control || (modifiers.alt && modifiers.meta) {
        return Err(InputEncodeError::UnsupportedModifiers { key, modifiers });
    }
    encode_prefixed_bytes(bytes, modifiers.has_legacy_meta_prefix())
}

fn encode_tab(modifiers: Modifiers, key: Key) -> Result<Vec<u8>, InputEncodeError> {
    if modifiers.shift {
        if modifiers
            == (Modifiers {
                shift: true,
                ..Modifiers::NONE
            })
        {
            return copy_bytes(b"\x1b[Z");
        }
        return encode_csi_final(b'Z', modifiers);
    }
    if modifiers.control || (modifiers.alt && modifiers.meta) {
        return Err(InputEncodeError::UnsupportedModifiers { key, modifiers });
    }
    encode_prefixed_bytes(b"\t", modifiers.has_legacy_meta_prefix())
}

fn encode_cursor_key(
    final_byte: u8,
    modifiers: Modifiers,
    application: bool,
) -> Result<Vec<u8>, InputEncodeError> {
    if modifiers.is_empty() && application {
        encode_ss3(final_byte)
    } else {
        encode_csi_final(final_byte, modifiers)
    }
}

fn encode_function_key(
    ss3_final: u8,
    tilde_number: Option<u8>,
    modifiers: Modifiers,
) -> Result<Vec<u8>, InputEncodeError> {
    if let Some(number) = tilde_number {
        encode_csi_tilde(number, modifiers)
    } else if modifiers.is_empty() {
        encode_ss3(ss3_final)
    } else {
        encode_csi_final(ss3_final, modifiers)
    }
}

fn encode_keypad(
    keypad_key: KeypadKey,
    modifiers: Modifiers,
    modes: &ModeState,
    key: Key,
) -> Result<Vec<u8>, InputEncodeError> {
    if !modifiers.is_empty() {
        return Err(InputEncodeError::UnsupportedModifiers { key, modifiers });
    }

    if modes.keypad_application {
        let final_byte = match keypad_key {
            KeypadKey::Digit0 => b'p',
            KeypadKey::Digit1 => b'q',
            KeypadKey::Digit2 => b'r',
            KeypadKey::Digit3 => b's',
            KeypadKey::Digit4 => b't',
            KeypadKey::Digit5 => b'u',
            KeypadKey::Digit6 => b'v',
            KeypadKey::Digit7 => b'w',
            KeypadKey::Digit8 => b'x',
            KeypadKey::Digit9 => b'y',
            KeypadKey::Decimal => b'n',
            KeypadKey::Separator => b'l',
            KeypadKey::Add => b'k',
            KeypadKey::Subtract => b'm',
            KeypadKey::Multiply => b'j',
            KeypadKey::Divide => b'o',
            KeypadKey::Equal => b'X',
            KeypadKey::Enter => b'M',
        };
        return encode_ss3(final_byte);
    }

    let bytes = match keypad_key {
        KeypadKey::Digit0 => b"0".as_slice(),
        KeypadKey::Digit1 => b"1".as_slice(),
        KeypadKey::Digit2 => b"2".as_slice(),
        KeypadKey::Digit3 => b"3".as_slice(),
        KeypadKey::Digit4 => b"4".as_slice(),
        KeypadKey::Digit5 => b"5".as_slice(),
        KeypadKey::Digit6 => b"6".as_slice(),
        KeypadKey::Digit7 => b"7".as_slice(),
        KeypadKey::Digit8 => b"8".as_slice(),
        KeypadKey::Digit9 => b"9".as_slice(),
        KeypadKey::Decimal => b".".as_slice(),
        KeypadKey::Separator => b",".as_slice(),
        KeypadKey::Add => b"+".as_slice(),
        KeypadKey::Subtract => b"-".as_slice(),
        KeypadKey::Multiply => b"*".as_slice(),
        KeypadKey::Divide => b"/".as_slice(),
        KeypadKey::Equal => b"=".as_slice(),
        KeypadKey::Enter if modes.newline_mode => b"\r\n".as_slice(),
        KeypadKey::Enter => b"\r".as_slice(),
    };
    copy_bytes(bytes)
}

fn encode_paste(event: PasteEvent<'_>, modes: &ModeState) -> Result<Vec<u8>, InputEncodeError> {
    if event.bytes.is_empty() {
        return Err(InputEncodeError::EmptyPaste);
    }
    if event.bytes.len() > MAX_PASTE_BYTES {
        return Err(InputEncodeError::PasteTooLarge {
            actual: event.bytes.len(),
            maximum: MAX_PASTE_BYTES,
        });
    }

    let delimiter_bytes = if modes.bracketed_paste {
        BRACKETED_PASTE_START.len() + BRACKETED_PASTE_END.len()
    } else {
        0
    };
    let output_len = event
        .bytes
        .len()
        .checked_add(delimiter_bytes)
        .ok_or(InputEncodeError::AllocationFailed)?;
    let mut output = allocate(output_len)?;
    if modes.bracketed_paste {
        output.extend_from_slice(BRACKETED_PASTE_START);
    }
    append_filtered_paste(&mut output, event.bytes);
    if modes.bracketed_paste {
        output.extend_from_slice(BRACKETED_PASTE_END);
    }
    Ok(output)
}

fn append_filtered_paste(output: &mut Vec<u8>, bytes: &[u8]) {
    let mut index = 0;
    let mut previous_was_cr = false;
    while index < bytes.len() {
        let byte = bytes[index];
        if let Some(sequence_len) = valid_utf8_sequence_len(&bytes[index..]) {
            let sequence = &bytes[index..index + sequence_len];
            let is_c1 = sequence_len == 2 && byte == 0xc2 && (0x80..=0x9f).contains(&sequence[1]);
            if !is_c1 {
                output.extend_from_slice(sequence);
            }
            previous_was_cr = false;
            index += sequence_len;
            continue;
        }

        match byte {
            b'\n' => {
                if !previous_was_cr {
                    output.push(b'\r');
                }
            }
            b'\r' | b'\t' => output.push(byte),
            0x00..=0x1f | 0x7f..=0x9f => {}
            _ => output.push(byte),
        }
        previous_was_cr = byte == b'\r';
        index += 1;
    }
}

fn valid_utf8_sequence_len(bytes: &[u8]) -> Option<usize> {
    let expected = match *bytes.first()? {
        0xc2..=0xdf => 2,
        0xe0..=0xef => 3,
        0xf0..=0xf4 => 4,
        _ => return None,
    };
    let sequence = bytes.get(..expected)?;
    std::str::from_utf8(sequence).ok().map(|_| expected)
}

fn encode_ss3(final_byte: u8) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(3)?;
    output.extend_from_slice(&[ESC, b'O', final_byte]);
    Ok(output)
}

fn encode_csi_final(final_byte: u8, modifiers: Modifiers) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(7)?;
    output.extend_from_slice(&[ESC, b'[']);
    if !modifiers.is_empty() {
        output.extend_from_slice(b"1;");
        push_decimal(&mut output, modifiers.xterm_parameter());
    }
    output.push(final_byte);
    Ok(output)
}

fn encode_csi_tilde(number: u8, modifiers: Modifiers) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = allocate(8)?;
    output.extend_from_slice(&[ESC, b'[']);
    push_decimal(&mut output, number);
    if !modifiers.is_empty() {
        output.push(b';');
        push_decimal(&mut output, modifiers.xterm_parameter());
    }
    output.push(b'~');
    Ok(output)
}

fn encode_prefixed_bytes(bytes: &[u8], prefix_escape: bool) -> Result<Vec<u8>, InputEncodeError> {
    let output_len = bytes
        .len()
        .checked_add(usize::from(prefix_escape))
        .ok_or(InputEncodeError::AllocationFailed)?;
    let mut output = allocate(output_len)?;
    if prefix_escape {
        output.push(ESC);
    }
    output.extend_from_slice(bytes);
    Ok(output)
}

fn copy_bytes(bytes: &[u8]) -> Result<Vec<u8>, InputEncodeError> {
    encode_prefixed_bytes(bytes, false)
}

fn allocate(capacity: usize) -> Result<Vec<u8>, InputEncodeError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(capacity)
        .map_err(|_| InputEncodeError::AllocationFailed)?;
    Ok(output)
}

fn push_decimal(output: &mut Vec<u8>, value: u8) {
    if value >= 10 {
        output.push(b'0' + value / 10);
    }
    output.push(b'0' + value % 10);
}

fn control_byte(character: char) -> Option<u8> {
    match character {
        'a'..='z' => Some((character as u8 - b'a') + 1),
        '@'..='_' => Some(character as u8 & 0x1f),
        ' ' => Some(0),
        '?' => Some(0x7f),
        _ => None,
    }
}

fn xterm_control_byte(character: char) -> Option<u8> {
    match character {
        '2' | '`' => Some(0),
        '3' => Some(0x1b),
        '4' => Some(0x1c),
        '5' => Some(0x1d),
        '6' | '~' => Some(0x1e),
        '7' | '/' => Some(0x1f),
        '8' => Some(0x7f),
        _ => control_byte(character),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Event, Terminal, TerminalError};

    fn key(key: Key) -> InputEvent<'static> {
        modified_key(key, Modifiers::NONE)
    }

    fn modified_key(key: Key, modifiers: Modifiers) -> InputEvent<'static> {
        InputEvent::Key(KeyEvent { key, modifiers })
    }

    const fn win32_key(
        virtual_key: u16,
        scan_code: u16,
        unicode_char: u16,
        key_down: bool,
        control_key_state: u32,
        repeat_count: u16,
    ) -> InputEvent<'static> {
        InputEvent::Win32Key(Win32KeyEvent {
            virtual_key,
            scan_code,
            unicode_char,
            key_down,
            control_key_state,
            repeat_count,
        })
    }

    fn mouse(
        action: MouseAction,
        buttons_held: MouseButtons,
        modifiers: Modifiers,
        column: u32,
        row: u32,
        pixel: Option<PixelPosition>,
    ) -> InputEvent<'static> {
        InputEvent::Mouse(MouseEvent {
            action,
            position: MousePosition {
                cell: CellPosition { column, row },
                pixel,
            },
            buttons_held,
            modifiers,
        })
    }

    const fn buttons(left: bool, middle: bool, right: bool) -> MouseButtons {
        MouseButtons {
            left,
            middle,
            right,
        }
    }

    fn mouse_modes(tracking: MouseTracking, encoding: MouseEncoding) -> ModeState {
        ModeState {
            mouse_tracking: tracking,
            mouse_encoding: encoding,
            ..ModeState::default()
        }
    }

    fn encode(event: InputEvent<'_>, modes: &ModeState) -> Result<Vec<u8>, InputEncodeError> {
        encode_input(event, modes, 0)
    }

    fn kitty_encode(event: KittyKeyEvent<'_>, flags: u8) -> Result<Vec<u8>, InputEncodeError> {
        encode_input(InputEvent::KittyKey(event), &ModeState::default(), flags)
    }

    fn kitty_event<'a>(
        key: KittyKey,
        modifiers: KittyModifiers,
        event_type: KittyKeyEventType,
        associated_text: &'a str,
    ) -> KittyKeyEvent<'a> {
        KittyKeyEvent {
            key,
            modifiers,
            event_type,
            shifted_key: None,
            base_layout_key: None,
            associated_text,
        }
    }

    #[test]
    fn kitty_flags_zero_preserves_legacy_press_repeat_and_release_behavior() {
        let character = kitty_event(
            KittyKey::Character('a'),
            KittyModifiers::NONE,
            KittyKeyEventType::Press,
            "A",
        );
        assert_eq!(kitty_encode(character, 0), Ok(b"A".to_vec()));
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    event_type: KittyKeyEventType::Repeat,
                    ..character
                },
                0,
            ),
            Ok(b"A".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Character('a'),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Release,
                    "",
                ),
                0,
            ),
            Ok(Vec::new())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::UnidentifiedText,
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "你",
                ),
                0,
            ),
            Ok("你".as_bytes().to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F3),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0,
            ),
            Ok(b"\x1bOR".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::Keypad0),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0,
            ),
            Ok(b"0".to_vec())
        );
    }

    #[test]
    fn kitty_disambiguation_uses_csi_u_only_for_ambiguous_legacy_keys() {
        for (modifiers, expected) in [
            (
                KittyModifiers {
                    alt: true,
                    ..KittyModifiers::NONE
                },
                b"\x1b[97;3u".as_slice(),
            ),
            (
                KittyModifiers {
                    control: true,
                    ..KittyModifiers::NONE
                },
                b"\x1b[97;5u".as_slice(),
            ),
        ] {
            assert_eq!(
                kitty_encode(
                    kitty_event(
                        KittyKey::Character('a'),
                        modifiers,
                        KittyKeyEventType::Press,
                        "",
                    ),
                    0x01,
                ),
                Ok(expected.to_vec())
            );
        }
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::Escape),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x01,
            ),
            Ok(b"\x1b[27u".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::Enter),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x01,
            ),
            Ok(b"\r".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::Keypad0),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x01,
            ),
            Ok(b"\x1b[57399u".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F3),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x01,
            ),
            Ok(b"\x1b[13~".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::Keypad0),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x04,
            ),
            Ok(b"0".to_vec())
        );
    }

    #[test]
    fn kitty_event_reporting_enhances_escape_keys_but_not_ordinary_text() {
        for (event_type, event_digit) in [
            (KittyKeyEventType::Press, b'1'),
            (KittyKeyEventType::Repeat, b'2'),
            (KittyKeyEventType::Release, b'3'),
        ] {
            let arrow = kitty_event(
                KittyKey::Functional(KittyFunctionalKey::ArrowUp),
                KittyModifiers::NONE,
                event_type,
                "",
            );
            let expected = [b"\x1b[1;1:".as_slice(), &[event_digit], b"A"].concat();
            assert_eq!(kitty_encode(arrow, 0x02), Ok(expected));
        }
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F3),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Repeat,
                    "",
                ),
                0x02,
            ),
            Ok(b"\x1b[13;1:2~".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Character('a'),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "a",
                ),
                0x02,
            ),
            Ok(b"a".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Character('a'),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Release,
                    "",
                ),
                0x02,
            ),
            Ok(Vec::new())
        );
    }

    #[test]
    fn kitty_alternate_keys_all_keys_and_associated_text_have_exact_csi_u_fields() {
        let alternates = KittyKeyEvent {
            key: KittyKey::Character('a'),
            modifiers: KittyModifiers {
                shift: true,
                ..KittyModifiers::NONE
            },
            event_type: KittyKeyEventType::Press,
            shifted_key: Some('A'),
            base_layout_key: Some('q'),
            associated_text: "",
        };
        assert_eq!(
            kitty_encode(alternates, 0x0c),
            Ok(b"\x1b[97:65:113;2u".to_vec())
        );
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    modifiers: KittyModifiers::NONE,
                    shifted_key: None,
                    ..alternates
                },
                0x0c,
            ),
            Ok(b"\x1b[97::113u".to_vec())
        );

        let text = kitty_event(
            KittyKey::Character('a'),
            KittyModifiers::NONE,
            KittyKeyEventType::Press,
            "a你",
        );
        assert_eq!(kitty_encode(text, 0x10), Ok("a你".as_bytes().to_vec()));
        assert_eq!(
            kitty_encode(text, 0x18),
            Ok(b"\x1b[97;1;97:20320u".to_vec())
        );
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    event_type: KittyKeyEventType::Repeat,
                    associated_text: "",
                    ..text
                },
                0x0a,
            ),
            Ok(b"\x1b[97;1:2u".to_vec())
        );
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    event_type: KittyKeyEventType::Release,
                    associated_text: "",
                    ..text
                },
                0x0a,
            ),
            Ok(b"\x1b[97;1:3u".to_vec())
        );
    }

    #[test]
    fn kitty_modifiers_preserve_all_eight_protocol_bits() {
        let modifiers = KittyModifiers {
            shift: true,
            alt: true,
            control: true,
            super_key: true,
            hyper: true,
            meta: true,
            caps_lock: true,
            num_lock: true,
        };
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Character('x'),
                    modifiers,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x08,
            ),
            Ok(b"\x1b[120;256u".to_vec())
        );
    }

    #[test]
    fn kitty_legacy_character_applies_caps_lock_without_losing_shift_cancellation() {
        let caps = KittyModifiers {
            caps_lock: true,
            ..KittyModifiers::NONE
        };
        assert_eq!(
            kitty_encode(
                kitty_event(KittyKey::Character('a'), caps, KittyKeyEventType::Press, "",),
                0,
            ),
            Ok(b"A".to_vec()),
        );

        let mut shifted = kitty_event(
            KittyKey::Character('a'),
            KittyModifiers {
                shift: true,
                ..caps
            },
            KittyKeyEventType::Press,
            "",
        );
        shifted.shifted_key = Some('a');
        assert_eq!(kitty_encode(shifted, 0), Ok(b"a".to_vec()));
    }

    #[test]
    fn kitty_pua_functional_ranges_are_complete_and_contiguous() {
        let groups: &[(&[KittyFunctionalKey], u32)] = &[
            (
                &[
                    KittyFunctionalKey::CapsLock,
                    KittyFunctionalKey::ScrollLock,
                    KittyFunctionalKey::NumLock,
                    KittyFunctionalKey::PrintScreen,
                    KittyFunctionalKey::Pause,
                    KittyFunctionalKey::Menu,
                ],
                57_358,
            ),
            (
                &[
                    KittyFunctionalKey::F13,
                    KittyFunctionalKey::F14,
                    KittyFunctionalKey::F15,
                    KittyFunctionalKey::F16,
                    KittyFunctionalKey::F17,
                    KittyFunctionalKey::F18,
                    KittyFunctionalKey::F19,
                    KittyFunctionalKey::F20,
                    KittyFunctionalKey::F21,
                    KittyFunctionalKey::F22,
                    KittyFunctionalKey::F23,
                    KittyFunctionalKey::F24,
                    KittyFunctionalKey::F25,
                    KittyFunctionalKey::F26,
                    KittyFunctionalKey::F27,
                    KittyFunctionalKey::F28,
                    KittyFunctionalKey::F29,
                    KittyFunctionalKey::F30,
                    KittyFunctionalKey::F31,
                    KittyFunctionalKey::F32,
                    KittyFunctionalKey::F33,
                    KittyFunctionalKey::F34,
                    KittyFunctionalKey::F35,
                ],
                57_376,
            ),
            (
                &[
                    KittyFunctionalKey::Keypad0,
                    KittyFunctionalKey::Keypad1,
                    KittyFunctionalKey::Keypad2,
                    KittyFunctionalKey::Keypad3,
                    KittyFunctionalKey::Keypad4,
                    KittyFunctionalKey::Keypad5,
                    KittyFunctionalKey::Keypad6,
                    KittyFunctionalKey::Keypad7,
                    KittyFunctionalKey::Keypad8,
                    KittyFunctionalKey::Keypad9,
                    KittyFunctionalKey::KeypadDecimal,
                    KittyFunctionalKey::KeypadDivide,
                    KittyFunctionalKey::KeypadMultiply,
                    KittyFunctionalKey::KeypadSubtract,
                    KittyFunctionalKey::KeypadAdd,
                    KittyFunctionalKey::KeypadEnter,
                    KittyFunctionalKey::KeypadEqual,
                    KittyFunctionalKey::KeypadSeparator,
                    KittyFunctionalKey::KeypadArrowLeft,
                    KittyFunctionalKey::KeypadArrowRight,
                    KittyFunctionalKey::KeypadArrowUp,
                    KittyFunctionalKey::KeypadArrowDown,
                    KittyFunctionalKey::KeypadPageUp,
                    KittyFunctionalKey::KeypadPageDown,
                    KittyFunctionalKey::KeypadHome,
                    KittyFunctionalKey::KeypadEnd,
                    KittyFunctionalKey::KeypadInsert,
                    KittyFunctionalKey::KeypadDelete,
                    KittyFunctionalKey::KeypadBegin,
                    KittyFunctionalKey::MediaPlay,
                    KittyFunctionalKey::MediaPause,
                    KittyFunctionalKey::MediaPlayPause,
                    KittyFunctionalKey::MediaReverse,
                    KittyFunctionalKey::MediaStop,
                    KittyFunctionalKey::MediaFastForward,
                    KittyFunctionalKey::MediaRewind,
                    KittyFunctionalKey::MediaTrackNext,
                    KittyFunctionalKey::MediaTrackPrevious,
                    KittyFunctionalKey::MediaRecord,
                    KittyFunctionalKey::LowerVolume,
                    KittyFunctionalKey::RaiseVolume,
                    KittyFunctionalKey::MuteVolume,
                    KittyFunctionalKey::LeftShift,
                    KittyFunctionalKey::LeftControl,
                    KittyFunctionalKey::LeftAlt,
                    KittyFunctionalKey::LeftSuper,
                    KittyFunctionalKey::LeftHyper,
                    KittyFunctionalKey::LeftMeta,
                    KittyFunctionalKey::RightShift,
                    KittyFunctionalKey::RightControl,
                    KittyFunctionalKey::RightAlt,
                    KittyFunctionalKey::RightSuper,
                    KittyFunctionalKey::RightHyper,
                    KittyFunctionalKey::RightMeta,
                    KittyFunctionalKey::IsoLevel3Shift,
                    KittyFunctionalKey::IsoLevel5Shift,
                ],
                57_399,
            ),
        ];

        for (keys, first_code) in groups {
            for (offset, key) in keys.iter().copied().enumerate() {
                let code = first_code + u32::try_from(offset).expect("small PUA table offset");
                assert_eq!(
                    kitty_functional_code(key),
                    Some(code),
                    "wrong PUA for {key:?}"
                );
                assert_eq!(
                    kitty_encode(
                        kitty_event(
                            KittyKey::Functional(key),
                            KittyModifiers::NONE,
                            KittyKeyEventType::Press,
                            "",
                        ),
                        0x08,
                    ),
                    Ok(format!("\x1b[{code}u").into_bytes())
                );
            }
        }
    }

    #[test]
    fn kitty_standard_functional_forms_and_f3_collision_avoidance_are_exact() {
        for (key, expected) in [
            (KittyFunctionalKey::Escape, "\x1b[27u"),
            (KittyFunctionalKey::Enter, "\x1b[13u"),
            (KittyFunctionalKey::Tab, "\x1b[9u"),
            (KittyFunctionalKey::Backspace, "\x1b[127u"),
        ] {
            assert_eq!(
                kitty_encode(
                    kitty_event(
                        KittyKey::Functional(key),
                        KittyModifiers::NONE,
                        KittyKeyEventType::Press,
                        "",
                    ),
                    0x08,
                ),
                Ok(expected.as_bytes().to_vec())
            );
        }

        let control = KittyModifiers {
            control: true,
            ..KittyModifiers::NONE
        };
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F2),
                    control,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x02,
            ),
            Ok(b"\x1b[1;5:1Q".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F3),
                    control,
                    KittyKeyEventType::Press,
                    "",
                ),
                0x02,
            ),
            Ok(b"\x1b[13;5:1~".to_vec())
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F5),
                    control,
                    KittyKeyEventType::Release,
                    "",
                ),
                0x02,
            ),
            Ok(b"\x1b[15;5:3~".to_vec())
        );
    }

    #[test]
    fn kitty_rejects_ambiguous_legacy_events_and_invalid_semantic_dtos() {
        assert_eq!(
            encode_input(key(Key::Character('a')), &ModeState::default(), 1),
            Err(InputEncodeError::KittyKeyEventRequired)
        );
        assert_eq!(
            encode_input(InputEvent::FocusGained, &ModeState::default(), 0x20),
            Err(InputEncodeError::InvalidKittyFlags { flags: 0x20 })
        );

        let base = kitty_event(
            KittyKey::Character('a'),
            KittyModifiers::NONE,
            KittyKeyEventType::Press,
            "",
        );
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    shifted_key: Some('A'),
                    ..base
                },
                0x08,
            ),
            Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::ShiftedKeyWithoutShift
            ))
        );
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    key: KittyKey::Functional(KittyFunctionalKey::F1),
                    base_layout_key: Some('q'),
                    ..base
                },
                0x08,
            ),
            Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::AlternateKeyForNonCharacter
            ))
        );
        assert_eq!(
            kitty_encode(
                KittyKeyEvent {
                    event_type: KittyKeyEventType::Release,
                    associated_text: "x",
                    ..base
                },
                0x1f,
            ),
            Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::AssociatedTextOnRelease
            ))
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::UnidentifiedText,
                    KittyModifiers::NONE,
                    KittyKeyEventType::Repeat,
                    "x",
                ),
                0,
            ),
            Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::UnidentifiedTextMustBePress
            ))
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::UnidentifiedText,
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0,
            ),
            Err(InputEncodeError::InvalidKittyKeyEvent(
                KittyKeyEventValidationError::UnidentifiedTextRequiresAssociatedText
            ))
        );
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::Functional(KittyFunctionalKey::F13),
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    "",
                ),
                0,
            ),
            Err(InputEncodeError::UnsupportedKittyLegacyEncoding {
                key: KittyKey::Functional(KittyFunctionalKey::F13),
                modifiers: KittyModifiers::NONE,
            })
        );
    }

    #[test]
    fn kitty_associated_text_is_strictly_bounded_and_control_free() {
        for control in ['\0', '\u{1f}', '\u{7f}', '\u{80}', '\u{9f}'] {
            let mut text = String::new();
            text.push(control);
            assert_eq!(
                kitty_encode(
                    kitty_event(
                        KittyKey::UnidentifiedText,
                        KittyModifiers::NONE,
                        KittyKeyEventType::Press,
                        &text,
                    ),
                    0,
                ),
                Err(InputEncodeError::KittyTextContainsControl(control))
            );
        }

        let maximum = "🙂".repeat(MAX_KITTY_ASSOCIATED_TEXT_SCALARS);
        assert_eq!(maximum.len(), MAX_KITTY_ASSOCIATED_TEXT_BYTES);
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::UnidentifiedText,
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    &maximum,
                ),
                0,
            ),
            Ok(maximum.as_bytes().to_vec())
        );

        let too_many_scalars = "a".repeat(MAX_KITTY_ASSOCIATED_TEXT_SCALARS + 1);
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::UnidentifiedText,
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    &too_many_scalars,
                ),
                0,
            ),
            Err(InputEncodeError::KittyAssociatedTextTooLarge {
                actual_bytes: MAX_KITTY_ASSOCIATED_TEXT_SCALARS + 1,
                maximum_bytes: MAX_KITTY_ASSOCIATED_TEXT_BYTES,
                actual_scalars: MAX_KITTY_ASSOCIATED_TEXT_SCALARS + 1,
                maximum_scalars: MAX_KITTY_ASSOCIATED_TEXT_SCALARS,
            })
        );

        let too_many_bytes = format!("{maximum}a");
        assert_eq!(
            kitty_encode(
                kitty_event(
                    KittyKey::UnidentifiedText,
                    KittyModifiers::NONE,
                    KittyKeyEventType::Press,
                    &too_many_bytes,
                ),
                0,
            ),
            Err(InputEncodeError::KittyAssociatedTextTooLarge {
                actual_bytes: MAX_KITTY_ASSOCIATED_TEXT_BYTES + 1,
                maximum_bytes: MAX_KITTY_ASSOCIATED_TEXT_BYTES,
                actual_scalars: MAX_KITTY_ASSOCIATED_TEXT_SCALARS + 1,
                maximum_scalars: MAX_KITTY_ASSOCIATED_TEXT_SCALARS,
            })
        );
    }

    #[test]
    fn win32_input_encodes_documented_ctrl_f1_records_exactly() {
        let modes = ModeState {
            win32_input_mode: true,
            ..ModeState::default()
        };

        for (event, expected) in [
            (
                win32_key(17, 29, 0, true, 8, 1),
                b"\x1b[17;29;0;1;8;1_".as_slice(),
            ),
            (
                win32_key(112, 59, 0, true, 8, 1),
                b"\x1b[112;59;0;1;8;1_".as_slice(),
            ),
            (
                win32_key(112, 59, 0, false, 8, 1),
                b"\x1b[112;59;0;0;8;1_".as_slice(),
            ),
            (
                win32_key(17, 29, 0, false, 0, 1),
                b"\x1b[17;29;0;0;0;1_".as_slice(),
            ),
        ] {
            assert_eq!(encode(event, &modes), Ok(expected.to_vec()));
        }
    }

    #[test]
    fn win32_input_preserves_zero_and_maximum_field_values() {
        let modes = ModeState {
            win32_input_mode: true,
            ..ModeState::default()
        };

        assert_eq!(
            encode(win32_key(0, 0, 0, false, 0, 0), &modes),
            Ok(b"\x1b[0;0;0;0;0;0_".to_vec())
        );
        let maximum = encode(
            win32_key(u16::MAX, u16::MAX, u16::MAX, true, u32::MAX, u16::MAX),
            &modes,
        );
        let expected = b"\x1b[65535;65535;65535;1;4294967295;65535_";
        assert_eq!(maximum, Ok(expected.to_vec()));
        assert_eq!(expected.len(), MAX_WIN32_INPUT_SEQUENCE_BYTES);
    }

    #[test]
    fn win32_input_requires_its_mode_without_changing_semantic_keys() {
        let disabled = ModeState::default();
        assert_eq!(
            encode(win32_key(112, 59, 0, true, 8, 1), &disabled),
            Err(InputEncodeError::Win32InputModeDisabled)
        );

        let enabled = ModeState {
            win32_input_mode: true,
            ..ModeState::default()
        };
        let semantic_ctrl_f1 = modified_key(
            Key::F1,
            Modifiers {
                control: true,
                ..Modifiers::NONE
            },
        );
        assert_eq!(
            encode(semantic_ctrl_f1, &disabled),
            Ok(b"\x1b[1;5P".to_vec())
        );
        assert_eq!(
            encode(semantic_ctrl_f1, &enabled),
            Ok(b"\x1b[1;5P".to_vec())
        );
        assert_eq!(
            encode_input(win32_key(112, 59, 0, true, 8, 1), &enabled, 0x1f),
            Ok(b"\x1b[112;59;0;1;8;1_".to_vec())
        );
    }

    #[test]
    fn characters_are_utf8_with_control_and_meta_handling() {
        let modes = ModeState::default();
        assert_eq!(
            encode(key(Key::Character('你')), &modes),
            Ok("\u{4f60}".as_bytes().to_vec())
        );
        assert_eq!(
            encode(
                modified_key(
                    Key::Character('c'),
                    Modifiers {
                        control: true,
                        ..Modifiers::NONE
                    }
                ),
                &modes
            ),
            Ok(vec![0x03])
        );
        assert_eq!(
            encode(
                modified_key(
                    Key::Character('B'),
                    Modifiers {
                        alt: true,
                        ..Modifiers::NONE
                    }
                ),
                &modes
            ),
            Ok(b"\x1bB".to_vec())
        );
        assert_eq!(
            encode(
                modified_key(
                    Key::Character('é'),
                    Modifiers {
                        control: true,
                        ..Modifiers::NONE
                    }
                ),
                &modes
            ),
            Err(InputEncodeError::UnsupportedControlCharacter('é'))
        );
    }

    #[test]
    fn modify_other_keys_levels_follow_xterm_function_key_27_encoding() {
        let control = Modifiers {
            control: true,
            ..Modifiers::NONE
        };
        let shift = Modifiers {
            shift: true,
            ..Modifiers::NONE
        };
        let control_shift = Modifiers {
            shift: true,
            control: true,
            ..Modifiers::NONE
        };
        let alt = Modifiers {
            alt: true,
            ..Modifiers::NONE
        };

        let disabled = ModeState::default();
        assert_eq!(
            encode(modified_key(Key::Character('a'), control), &disabled),
            Ok(vec![1])
        );
        assert_eq!(
            encode(modified_key(Key::Character('A'), shift), &disabled),
            Err(InputEncodeError::UnsupportedModifiers {
                key: Key::Character('A'),
                modifiers: shift,
            })
        );

        let level1 = ModeState {
            modify_other_keys: ModifyOtherKeys::Level1,
            ..ModeState::default()
        };
        assert_eq!(
            encode(modified_key(Key::Character('a'), control), &level1),
            Ok(vec![1])
        );
        assert_eq!(
            encode(modified_key(Key::Character('2'), control), &level1),
            Ok(vec![0])
        );
        assert_eq!(
            encode(modified_key(Key::Character('1'), control), &level1),
            Ok(b"\x1b[27;5;49~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Character('A'), control_shift), &level1),
            Ok(b"\x1b[27;6;65~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Character('A'), shift), &level1),
            Ok(b"A".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Character('a'), alt), &level1),
            Ok(b"\x1ba".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Tab, alt), &level1),
            Ok(b"\x1b[27;3;9~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Tab, shift), &level1),
            Ok(b"\x1b[Z".to_vec())
        );

        let level2 = ModeState {
            modify_other_keys: ModifyOtherKeys::Level2,
            ..ModeState::default()
        };
        assert_eq!(
            encode(modified_key(Key::Character('a'), control), &level2),
            Ok(b"\x1b[27;5;97~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Character('2'), control), &level2),
            Ok(b"\x1b[27;5;50~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Character('A'), shift), &level2),
            Ok(b"\x1b[27;2;65~".to_vec())
        );
        let every_modifier = Modifiers {
            shift: true,
            alt: true,
            control: true,
            meta: true,
        };
        assert_eq!(
            encode(modified_key(Key::Character('A'), every_modifier), &level2),
            Ok(b"\x1b[27;16;65~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Tab, shift), &level2),
            Ok(b"\x1b[27;2;9~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Enter, alt), &level2),
            Ok(b"\x1b[27;3;13~".to_vec())
        );
        assert_eq!(
            encode(modified_key(Key::Backspace, control), &level2),
            Ok(b"\x1b[27;5;127~".to_vec())
        );

        let level3 = ModeState {
            modify_other_keys: ModifyOtherKeys::Level3,
            ..ModeState::default()
        };
        assert_eq!(
            encode(key(Key::Character('你')), &level3),
            Ok(b"\x1b[27;1;20320~".to_vec())
        );
        assert_eq!(
            encode(key(Key::Character(' ')), &level3),
            Ok(b"\x1b[27;1;32~".to_vec())
        );
        assert_eq!(encode(key(Key::Tab), &level3), Ok(b"\x1b[27;1;9~".to_vec()));
        assert_eq!(
            encode(key(Key::Escape), &level3),
            Ok(b"\x1b[27;1;27~".to_vec())
        );
    }

    #[test]
    fn modify_other_keys_keeps_special_protocols_and_rejects_ambiguous_character_controls() {
        for modify_other_keys in [
            ModifyOtherKeys::Level1,
            ModifyOtherKeys::Level2,
            ModifyOtherKeys::Level3,
        ] {
            let modes = ModeState {
                modify_other_keys,
                ..ModeState::default()
            };
            assert_eq!(
                encode(key(Key::Character('\t')), &modes),
                Err(InputEncodeError::UnsupportedControlCharacter('\t'))
            );
            assert_eq!(
                encode(
                    modified_key(
                        Key::Keypad(KeypadKey::Digit1),
                        Modifiers {
                            shift: true,
                            ..Modifiers::NONE
                        }
                    ),
                    &modes
                ),
                Err(InputEncodeError::UnsupportedModifiers {
                    key: Key::Keypad(KeypadKey::Digit1),
                    modifiers: Modifiers {
                        shift: true,
                        ..Modifiers::NONE
                    },
                })
            );
            assert_eq!(
                encode(
                    modified_key(
                        Key::Delete,
                        Modifiers {
                            shift: true,
                            ..Modifiers::NONE
                        }
                    ),
                    &modes
                ),
                Ok(b"\x1b[3;2~".to_vec())
            );
            assert_eq!(encode(key(Key::ArrowUp), &modes), Ok(b"\x1b[A".to_vec()));
            assert_eq!(encode(key(Key::F1), &modes), Ok(b"\x1bOP".to_vec()));
        }
    }

    #[test]
    fn xtmodkeys_set_reset_and_snapshot_restore_drive_input_encoding() {
        let mut terminal = Terminal::new(4, 1).expect("terminal");
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[>4;2m",
            })
            .expect("enable modifyOtherKeys level 2");
        let input = InputEvent::Key(KeyEvent {
            key: Key::Character('a'),
            modifiers: Modifiers {
                control: true,
                ..Modifiers::NONE
            },
        });
        assert_eq!(terminal.encode_input(input), Ok(b"\x1b[27;5;97~".to_vec()));

        let snapshot = terminal.export_snapshot_v2().expect("mode snapshot");
        let restored = Terminal::import_snapshot_v2(&snapshot).expect("mode restore");
        assert_eq!(restored.encode_input(input), Ok(b"\x1b[27;5;97~".to_vec()));
        assert_eq!(
            restored.export_snapshot_v2().expect("canonical re-export"),
            snapshot
        );

        terminal
            .apply_event(Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[>4;m",
            })
            .expect("reset one xterm modifier resource");
        assert_eq!(terminal.encode_input(input), Ok(vec![1]));
        terminal
            .apply_event(Event::Output {
                source_sequence: 2,
                bytes: b"\x1b[>4;3m\x1b[>m",
            })
            .expect("empty XTMODKEYS resets implemented resources");
        assert_eq!(terminal.encode_input(input), Ok(vec![1]));
    }

    #[test]
    fn enter_respects_newline_mode_and_literal_modifiers_are_checked() {
        let mut modes = ModeState::default();
        assert_eq!(encode(key(Key::Enter), &modes), Ok(b"\r".to_vec()));
        modes.newline_mode = true;
        assert_eq!(encode(key(Key::Enter), &modes), Ok(b"\r\n".to_vec()));
        assert_eq!(
            encode(
                modified_key(
                    Key::Enter,
                    Modifiers {
                        alt: true,
                        ..Modifiers::NONE
                    }
                ),
                &modes
            ),
            Ok(b"\x1b\r\n".to_vec())
        );

        let unsupported = Modifiers {
            control: true,
            ..Modifiers::NONE
        };
        assert_eq!(
            encode(modified_key(Key::Backspace, unsupported), &modes),
            Err(InputEncodeError::UnsupportedModifiers {
                key: Key::Backspace,
                modifiers: unsupported,
            })
        );
    }

    #[test]
    fn tab_backspace_and_escape_use_conventional_sequences() {
        let modes = ModeState::default();
        assert_eq!(encode(key(Key::Tab), &modes), Ok(b"\t".to_vec()));
        assert_eq!(
            encode(
                modified_key(
                    Key::Tab,
                    Modifiers {
                        shift: true,
                        ..Modifiers::NONE
                    }
                ),
                &modes
            ),
            Ok(b"\x1b[Z".to_vec())
        );
        assert_eq!(encode(key(Key::Backspace), &modes), Ok(vec![0x7f]));
        assert_eq!(encode(key(Key::Escape), &modes), Ok(vec![ESC]));
    }

    #[test]
    fn cursor_mode_selects_csi_or_ss3_and_modifiers_use_csi() {
        let mut modes = ModeState::default();
        assert_eq!(encode(key(Key::ArrowUp), &modes), Ok(b"\x1b[A".to_vec()));
        assert_eq!(encode(key(Key::Home), &modes), Ok(b"\x1b[H".to_vec()));

        modes.cursor_keys_application = true;
        assert_eq!(encode(key(Key::ArrowUp), &modes), Ok(b"\x1bOA".to_vec()));
        assert_eq!(encode(key(Key::End), &modes), Ok(b"\x1bOF".to_vec()));

        let modifiers = Modifiers {
            shift: true,
            alt: true,
            control: true,
            meta: false,
        };
        assert_eq!(
            encode(modified_key(Key::ArrowLeft, modifiers), &modes),
            Ok(b"\x1b[1;8D".to_vec())
        );
    }

    #[test]
    fn editing_navigation_keys_use_xterm_tilde_parameters() {
        let modes = ModeState::default();
        let cases = [
            (Key::Insert, b"\x1b[2~".as_slice()),
            (Key::Delete, b"\x1b[3~".as_slice()),
            (Key::PageUp, b"\x1b[5~".as_slice()),
            (Key::PageDown, b"\x1b[6~".as_slice()),
        ];
        for (key, expected) in cases {
            assert_eq!(
                encode(super::tests::key(key), &modes),
                Ok(expected.to_vec())
            );
        }

        let modifiers = Modifiers {
            control: true,
            meta: true,
            ..Modifiers::NONE
        };
        assert_eq!(
            encode(modified_key(Key::Delete, modifiers), &modes),
            Ok(b"\x1b[3;13~".to_vec())
        );
    }

    #[test]
    fn all_function_keys_have_canonical_plain_and_modified_forms() {
        let modes = ModeState::default();
        let cases = [
            (Key::F1, "\x1bOP", "\x1b[1;6P"),
            (Key::F2, "\x1bOQ", "\x1b[1;6Q"),
            (Key::F3, "\x1bOR", "\x1b[1;6R"),
            (Key::F4, "\x1bOS", "\x1b[1;6S"),
            (Key::F5, "\x1b[15~", "\x1b[15;6~"),
            (Key::F6, "\x1b[17~", "\x1b[17;6~"),
            (Key::F7, "\x1b[18~", "\x1b[18;6~"),
            (Key::F8, "\x1b[19~", "\x1b[19;6~"),
            (Key::F9, "\x1b[20~", "\x1b[20;6~"),
            (Key::F10, "\x1b[21~", "\x1b[21;6~"),
            (Key::F11, "\x1b[23~", "\x1b[23;6~"),
            (Key::F12, "\x1b[24~", "\x1b[24;6~"),
        ];
        let modified = Modifiers {
            shift: true,
            control: true,
            ..Modifiers::NONE
        };
        for (key, plain, with_modifiers) in cases {
            assert_eq!(
                encode(super::tests::key(key), &modes),
                Ok(plain.as_bytes().to_vec())
            );
            assert_eq!(
                encode(modified_key(key, modified), &modes),
                Ok(with_modifiers.as_bytes().to_vec())
            );
        }
    }

    #[test]
    fn keypad_mode_selects_numeric_or_dec_application_sequences() {
        let all_keys = [
            (KeypadKey::Digit0, "0", "\x1bOp"),
            (KeypadKey::Digit1, "1", "\x1bOq"),
            (KeypadKey::Digit2, "2", "\x1bOr"),
            (KeypadKey::Digit3, "3", "\x1bOs"),
            (KeypadKey::Digit4, "4", "\x1bOt"),
            (KeypadKey::Digit5, "5", "\x1bOu"),
            (KeypadKey::Digit6, "6", "\x1bOv"),
            (KeypadKey::Digit7, "7", "\x1bOw"),
            (KeypadKey::Digit8, "8", "\x1bOx"),
            (KeypadKey::Digit9, "9", "\x1bOy"),
            (KeypadKey::Decimal, ".", "\x1bOn"),
            (KeypadKey::Separator, ",", "\x1bOl"),
            (KeypadKey::Add, "+", "\x1bOk"),
            (KeypadKey::Subtract, "-", "\x1bOm"),
            (KeypadKey::Multiply, "*", "\x1bOj"),
            (KeypadKey::Divide, "/", "\x1bOo"),
            (KeypadKey::Equal, "=", "\x1bOX"),
            (KeypadKey::Enter, "\r", "\x1bOM"),
        ];

        let mut modes = ModeState::default();
        for (keypad_key, numeric, _) in all_keys {
            assert_eq!(
                encode(key(Key::Keypad(keypad_key)), &modes),
                Ok(numeric.as_bytes().to_vec())
            );
        }
        modes.keypad_application = true;
        for (keypad_key, _, application) in all_keys {
            assert_eq!(
                encode(key(Key::Keypad(keypad_key)), &modes),
                Ok(application.as_bytes().to_vec())
            );
        }
    }

    #[test]
    fn keypad_modifiers_are_explicitly_unsupported() {
        let modes = ModeState::default();
        let modifiers = Modifiers {
            shift: true,
            ..Modifiers::NONE
        };
        let key = Key::Keypad(KeypadKey::Digit7);
        assert_eq!(
            encode(modified_key(key, modifiers), &modes),
            Err(InputEncodeError::UnsupportedModifiers { key, modifiers })
        );
    }

    #[test]
    fn paste_is_bounded_and_mode_sensitive() {
        let mut modes = ModeState::default();
        assert_eq!(
            encode(InputEvent::Paste(PasteEvent { bytes: b"a\r\nb" }), &modes),
            Ok(b"a\rb".to_vec())
        );
        modes.bracketed_paste = true;
        assert_eq!(
            encode(InputEvent::Paste(PasteEvent { bytes: b"a\r\nb" }), &modes),
            Ok(b"\x1b[200~a\rb\x1b[201~".to_vec())
        );

        let controls = b"echo\x1b[201~\0\x7f\xc2\x80\tX\nY\xff";
        assert_eq!(
            encode(InputEvent::Paste(PasteEvent { bytes: controls }), &modes),
            Ok(b"\x1b[200~echo[201~\tX\rY\xff\x1b[201~".to_vec())
        );

        assert_eq!(
            encode(InputEvent::Paste(PasteEvent { bytes: b"" }), &modes),
            Err(InputEncodeError::EmptyPaste)
        );
        let oversized = vec![b'x'; MAX_PASTE_BYTES + 1];
        assert_eq!(
            encode(InputEvent::Paste(PasteEvent { bytes: &oversized }), &modes),
            Err(InputEncodeError::PasteTooLarge {
                actual: MAX_PASTE_BYTES + 1,
                maximum: MAX_PASTE_BYTES,
            })
        );
    }

    #[test]
    fn focus_events_emit_only_when_dec_focus_reporting_is_enabled() {
        let mut modes = ModeState::default();
        assert_eq!(encode(InputEvent::FocusGained, &modes), Ok(Vec::new()));
        assert_eq!(encode(InputEvent::FocusLost, &modes), Ok(Vec::new()));

        modes.focus_reporting = true;
        assert_eq!(
            encode(InputEvent::FocusGained, &modes),
            Ok(FOCUS_GAINED.to_vec())
        );
        assert_eq!(
            encode(InputEvent::FocusLost, &modes),
            Ok(FOCUS_LOST.to_vec())
        );

        modes.focus_reporting = false;
        assert_eq!(encode(InputEvent::FocusGained, &modes), Ok(Vec::new()));
        assert_eq!(encode(InputEvent::FocusLost, &modes), Ok(Vec::new()));
    }

    #[test]
    fn mouse_tracking_modes_filter_each_event_class() {
        let events = [
            mouse(
                MouseAction::Press(MouseButton::Left),
                buttons(true, false, false),
                Modifiers::NONE,
                0,
                0,
                None,
            ),
            mouse(
                MouseAction::Release(MouseButton::Left),
                MouseButtons::NONE,
                Modifiers::NONE,
                0,
                0,
                None,
            ),
            mouse(
                MouseAction::Move,
                buttons(true, false, false),
                Modifiers::NONE,
                0,
                0,
                None,
            ),
            mouse(
                MouseAction::Move,
                MouseButtons::NONE,
                Modifiers::NONE,
                0,
                0,
                None,
            ),
            mouse(
                MouseAction::Wheel(MouseWheelDirection::Up),
                MouseButtons::NONE,
                Modifiers::NONE,
                0,
                0,
                None,
            ),
        ];
        let cases = [
            (MouseTracking::None, [false, false, false, false, false]),
            (MouseTracking::X10, [true, false, false, false, true]),
            (MouseTracking::Normal, [true, true, false, false, true]),
            (MouseTracking::ButtonEvent, [true, true, true, false, true]),
            (MouseTracking::AnyEvent, [true, true, true, true, true]),
        ];

        for (tracking, expected_output) in cases {
            let modes = mouse_modes(tracking, MouseEncoding::Sgr);
            for (event, expected_output) in events.into_iter().zip(expected_output) {
                let encoded = encode(event, &modes).expect("valid mouse event");
                assert_eq!(
                    !encoded.is_empty(),
                    expected_output,
                    "unexpected filtering for {tracking:?} and {event:?}"
                );
            }
        }
    }

    #[test]
    fn mouse_encodings_have_canonical_byte_forms() {
        let left_press = mouse(
            MouseAction::Press(MouseButton::Left),
            buttons(true, false, false),
            Modifiers::NONE,
            0,
            0,
            None,
        );
        assert_eq!(
            encode(
                left_press,
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Default)
            ),
            Ok(vec![0x1b, b'[', b'M', 0x20, 0x21, 0x21])
        );

        let right_release_with_alt = mouse(
            MouseAction::Release(MouseButton::Right),
            MouseButtons::NONE,
            Modifiers {
                alt: true,
                ..Modifiers::NONE
            },
            4,
            2,
            None,
        );
        assert_eq!(
            encode(
                right_release_with_alt,
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Default)
            ),
            Ok(b"\x1b[M+%#".to_vec())
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Move,
                    buttons(false, false, true),
                    Modifiers {
                        control: true,
                        ..Modifiers::NONE
                    },
                    9,
                    3,
                    None,
                ),
                &mouse_modes(MouseTracking::ButtonEvent, MouseEncoding::Default)
            ),
            Ok(b"\x1b[MR*$".to_vec())
        );

        let utf8_coordinate = mouse(
            MouseAction::Press(MouseButton::Left),
            buttons(true, false, false),
            Modifiers::NONE,
            95,
            0,
            None,
        );
        assert_eq!(
            encode(
                utf8_coordinate,
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Utf8)
            ),
            Ok(vec![0x1b, b'[', b'M', 0x20, 0xc2, 0x80, 0x21])
        );

        let middle_press = mouse(
            MouseAction::Press(MouseButton::Middle),
            buttons(false, true, false),
            Modifiers {
                shift: true,
                control: true,
                ..Modifiers::NONE
            },
            4,
            2,
            None,
        );
        assert_eq!(
            encode(
                middle_press,
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Sgr)
            ),
            Ok(b"\x1b[<21;5;3M".to_vec())
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Release(MouseButton::Right),
                    MouseButtons::NONE,
                    Modifiers {
                        alt: true,
                        meta: true,
                        ..Modifiers::NONE
                    },
                    4,
                    2,
                    None,
                ),
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Sgr)
            ),
            Ok(b"\x1b[<10;5;3m".to_vec())
        );

        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    2,
                    3,
                    None,
                ),
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Urxvt)
            ),
            Ok(b"\x1b[32;3;4M".to_vec())
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Release(MouseButton::Right),
                    MouseButtons::NONE,
                    Modifiers {
                        shift: true,
                        ..Modifiers::NONE
                    },
                    2,
                    3,
                    None,
                ),
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Urxvt)
            ),
            Ok(b"\x1b[39;3;4M".to_vec())
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Move,
                    buttons(false, true, false),
                    Modifiers {
                        control: true,
                        ..Modifiers::NONE
                    },
                    2,
                    3,
                    None,
                ),
                &mouse_modes(MouseTracking::ButtonEvent, MouseEncoding::Urxvt)
            ),
            Ok(b"\x1b[81;3;4M".to_vec())
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Wheel(MouseWheelDirection::Right),
                    MouseButtons::NONE,
                    Modifiers::NONE,
                    2,
                    3,
                    None,
                ),
                &mouse_modes(MouseTracking::Normal, MouseEncoding::Urxvt)
            ),
            Ok(b"\x1b[99;3;4M".to_vec())
        );

        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    9,
                    4,
                    Some(PixelPosition { x: 159, y: 79 }),
                ),
                &mouse_modes(MouseTracking::Normal, MouseEncoding::SgrPixels)
            ),
            Ok(b"\x1b[<0;160;80M".to_vec())
        );
    }

    #[test]
    fn mouse_coordinate_boundaries_are_checked_without_clamping() {
        let modes = mouse_modes(MouseTracking::Normal, MouseEncoding::Default);
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    MAX_DEFAULT_MOUSE_CELL_COORDINATE,
                    0,
                    None,
                ),
                &modes
            ),
            Ok(vec![0x1b, b'[', b'M', 0x20, 0xff, 0x21])
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    MAX_DEFAULT_MOUSE_CELL_COORDINATE + 1,
                    0,
                    None,
                ),
                &modes
            ),
            Err(InputEncodeError::MouseCoordinateOutOfRange {
                value: MAX_DEFAULT_MOUSE_CELL_COORDINATE + 1,
                maximum: MAX_DEFAULT_MOUSE_CELL_COORDINATE,
            })
        );

        let utf8_modes = mouse_modes(MouseTracking::Normal, MouseEncoding::Utf8);
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    MAX_UTF8_MOUSE_CELL_COORDINATE,
                    0,
                    None,
                ),
                &utf8_modes
            ),
            Ok(vec![0x1b, b'[', b'M', 0x20, 0xdf, 0xbf, 0x21])
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    MAX_UTF8_MOUSE_CELL_COORDINATE + 1,
                    0,
                    None,
                ),
                &utf8_modes
            ),
            Err(InputEncodeError::MouseCoordinateOutOfRange {
                value: MAX_UTF8_MOUSE_CELL_COORDINATE + 1,
                maximum: MAX_UTF8_MOUSE_CELL_COORDINATE,
            })
        );
    }

    #[test]
    fn sgr_pixel_mode_requires_and_bounds_pixel_coordinates_only_when_reported() {
        let modes = mouse_modes(MouseTracking::Normal, MouseEncoding::SgrPixels);
        let press_without_pixel = mouse(
            MouseAction::Press(MouseButton::Left),
            buttons(true, false, false),
            Modifiers::NONE,
            0,
            0,
            None,
        );
        assert_eq!(
            encode(press_without_pixel, &modes),
            Err(InputEncodeError::MissingMousePixelPosition)
        );

        let filtered_release = mouse(
            MouseAction::Release(MouseButton::Left),
            MouseButtons::NONE,
            Modifiers::NONE,
            0,
            0,
            None,
        );
        assert_eq!(
            encode(
                filtered_release,
                &mouse_modes(MouseTracking::X10, MouseEncoding::SgrPixels)
            ),
            Ok(Vec::new())
        );

        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    0,
                    0,
                    Some(PixelPosition {
                        x: MAX_MOUSE_PIXEL_COORDINATE,
                        y: MAX_MOUSE_PIXEL_COORDINATE,
                    }),
                ),
                &modes
            ),
            Ok(b"\x1b[<0;4294967295;4294967295M".to_vec())
        );
        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers::NONE,
                    0,
                    0,
                    Some(PixelPosition { x: u32::MAX, y: 0 }),
                ),
                &modes
            ),
            Err(InputEncodeError::MouseCoordinateOutOfRange {
                value: u32::MAX,
                maximum: MAX_MOUSE_PIXEL_COORDINATE,
            })
        );
    }

    #[test]
    fn mouse_button_state_is_checked_and_motion_priority_is_deterministic() {
        let modes = mouse_modes(MouseTracking::AnyEvent, MouseEncoding::Sgr);
        for (event, expected) in [
            (
                mouse(
                    MouseAction::Move,
                    buttons(true, true, true),
                    Modifiers::NONE,
                    0,
                    0,
                    None,
                ),
                b"\x1b[<32;1;1M".as_slice(),
            ),
            (
                mouse(
                    MouseAction::Move,
                    buttons(false, true, true),
                    Modifiers::NONE,
                    0,
                    0,
                    None,
                ),
                b"\x1b[<33;1;1M".as_slice(),
            ),
            (
                mouse(
                    MouseAction::Move,
                    buttons(false, false, true),
                    Modifiers::NONE,
                    0,
                    0,
                    None,
                ),
                b"\x1b[<34;1;1M".as_slice(),
            ),
            (
                mouse(
                    MouseAction::Move,
                    MouseButtons::NONE,
                    Modifiers::NONE,
                    0,
                    0,
                    None,
                ),
                b"\x1b[<35;1;1M".as_slice(),
            ),
        ] {
            assert_eq!(encode(event, &modes), Ok(expected.to_vec()));
        }

        let invalid_press = mouse(
            MouseAction::Press(MouseButton::Left),
            MouseButtons::NONE,
            Modifiers::NONE,
            0,
            0,
            None,
        );
        assert_eq!(
            encode(invalid_press, &modes),
            Err(InputEncodeError::InvalidMouseButtonState {
                action: MouseAction::Press(MouseButton::Left),
                buttons_held: MouseButtons::NONE,
            })
        );

        let invalid_release = mouse(
            MouseAction::Release(MouseButton::Right),
            buttons(false, false, true),
            Modifiers::NONE,
            0,
            0,
            None,
        );
        assert_eq!(
            encode(invalid_release, &modes),
            Err(InputEncodeError::InvalidMouseButtonState {
                action: MouseAction::Release(MouseButton::Right),
                buttons_held: buttons(false, false, true),
            })
        );
    }

    #[test]
    fn all_wheel_directions_and_mouse_modifier_bits_are_stable() {
        let modes = mouse_modes(MouseTracking::X10, MouseEncoding::Sgr);
        for (direction, code) in [
            (MouseWheelDirection::Up, 64),
            (MouseWheelDirection::Down, 65),
            (MouseWheelDirection::Left, 66),
            (MouseWheelDirection::Right, 67),
        ] {
            let event = mouse(
                MouseAction::Wheel(direction),
                MouseButtons::NONE,
                Modifiers::NONE,
                0,
                0,
                None,
            );
            assert_eq!(
                encode(event, &modes),
                Ok(format!("\x1b[<{code};1;1M").into_bytes())
            );
        }

        assert_eq!(
            encode(
                mouse(
                    MouseAction::Press(MouseButton::Left),
                    buttons(true, false, false),
                    Modifiers {
                        shift: true,
                        alt: true,
                        control: true,
                        meta: true,
                    },
                    0,
                    0,
                    None,
                ),
                &modes
            ),
            Ok(b"\x1b[<28;1;1M".to_vec())
        );
    }

    #[test]
    fn terminal_focus_input_tracks_dec_mode_enable_and_disable() {
        let mut terminal = Terminal::new(80, 24).expect("terminal");
        assert_eq!(
            terminal.encode_input(InputEvent::FocusGained),
            Ok(Vec::new())
        );

        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[?1004h",
            })
            .expect("enable focus reporting");
        assert_eq!(
            terminal.encode_input(InputEvent::FocusGained),
            Ok(FOCUS_GAINED.to_vec())
        );
        assert_eq!(
            terminal.encode_input(InputEvent::FocusLost),
            Ok(FOCUS_LOST.to_vec())
        );

        terminal
            .apply_event(Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[?1004l",
            })
            .expect("disable focus reporting");
        assert_eq!(terminal.encode_input(InputEvent::FocusLost), Ok(Vec::new()));
    }

    #[test]
    fn focus_reporting_survives_snapshot_restore_without_input_mutation() {
        let mut terminal = Terminal::new(80, 24).expect("terminal");
        terminal
            .apply_event(Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[?1004h",
            })
            .expect("enable focus reporting");
        let before_encode = terminal.clone();
        assert_eq!(
            terminal.encode_input(InputEvent::FocusGained),
            Ok(FOCUS_GAINED.to_vec())
        );
        assert_eq!(terminal, before_encode);

        let snapshot = terminal.export_snapshot_v2().expect("snapshot");
        let restored = Terminal::import_snapshot_v2(&snapshot).expect("restore snapshot");
        let before_restored_encode = restored.clone();
        assert_eq!(
            restored.encode_input(InputEvent::FocusLost),
            Ok(FOCUS_LOST.to_vec())
        );
        assert_eq!(restored, before_restored_encode);
    }

    #[test]
    fn focus_input_preserves_failed_closed_rejection() {
        let mut terminal = Terminal::new(80, 24).expect("terminal");
        assert_eq!(
            terminal.apply_event(Event::Gap { source_sequence: 0 }),
            Err(TerminalError::Gap)
        );
        let failed_closed = terminal.clone();
        for event in [InputEvent::FocusGained, InputEvent::FocusLost] {
            assert_eq!(
                terminal.encode_input(event),
                Err(InputEncodeError::TerminalFailedClosed)
            );
            assert_eq!(terminal, failed_closed);
        }
    }

    #[test]
    fn every_key_and_modifier_combination_returns_without_panicking() {
        let keys = [
            Key::Character('a'),
            Key::Character('🙂'),
            Key::Character('\n'),
            Key::Enter,
            Key::Backspace,
            Key::Tab,
            Key::Escape,
            Key::Delete,
            Key::Insert,
            Key::Home,
            Key::End,
            Key::PageUp,
            Key::PageDown,
            Key::ArrowUp,
            Key::ArrowDown,
            Key::ArrowLeft,
            Key::ArrowRight,
            Key::F1,
            Key::F2,
            Key::F3,
            Key::F4,
            Key::F5,
            Key::F6,
            Key::F7,
            Key::F8,
            Key::F9,
            Key::F10,
            Key::F11,
            Key::F12,
            Key::Keypad(KeypadKey::Enter),
        ];

        for mode_bits in 0_u8..8 {
            let modes = ModeState {
                cursor_keys_application: mode_bits & 1 != 0,
                keypad_application: mode_bits & 2 != 0,
                newline_mode: mode_bits & 4 != 0,
                ..ModeState::default()
            };
            for modifier_bits in 0_u8..16 {
                let modifiers = Modifiers {
                    shift: modifier_bits & 1 != 0,
                    alt: modifier_bits & 2 != 0,
                    control: modifier_bits & 4 != 0,
                    meta: modifier_bits & 8 != 0,
                };
                for key in keys {
                    let result =
                        std::panic::catch_unwind(|| encode(modified_key(key, modifiers), &modes));
                    assert!(result.is_ok(), "panicked for {key:?} with {modifiers:?}");
                }
            }
        }
    }
}
