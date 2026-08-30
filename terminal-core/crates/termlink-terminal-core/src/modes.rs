/// The mutually exclusive mouse event selection modes.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum MouseTracking {
    #[default]
    None,
    X10,
    Normal,
    ButtonEvent,
    AnyEvent,
}

impl MouseTracking {
    pub(crate) fn for_private_mode(parameter: u16) -> Option<Self> {
        match parameter {
            9 => Some(Self::X10),
            1000 => Some(Self::Normal),
            1002 => Some(Self::ButtonEvent),
            1003 => Some(Self::AnyEvent),
            _ => None,
        }
    }
}

/// The mutually exclusive wire encodings used for mouse reports.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum MouseEncoding {
    #[default]
    Default,
    Utf8,
    Sgr,
    Urxvt,
    SgrPixels,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum AttributeChangeExtent {
    #[default]
    Stream,
    Rectangle,
}

/// XTerm's `modifyOtherKeys` resource levels.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum ModifyOtherKeys {
    #[default]
    Disabled,
    Level1,
    Level2,
    Level3,
}

impl ModifyOtherKeys {
    pub(crate) const fn from_parameter(parameter: u16) -> Option<Self> {
        match parameter {
            0 => Some(Self::Disabled),
            1 => Some(Self::Level1),
            2 => Some(Self::Level2),
            3 => Some(Self::Level3),
            _ => None,
        }
    }

    pub(crate) const fn parameter(self) -> u16 {
        match self {
            Self::Disabled => 0,
            Self::Level1 => 1,
            Self::Level2 => 2,
            Self::Level3 => 3,
        }
    }
}

pub(crate) const KITTY_KEYBOARD_SUPPORTED_FLAGS: u8 = 0x1f;
pub(crate) const KITTY_KEYBOARD_STACK_CAPACITY: usize = 8;

/// One screen's bounded Kitty keyboard-protocol flag stack.
///
/// Entries are stored from oldest to top. An empty stack has the same active
/// flags as Kitty's initial value (`0`) while remaining distinguishable for
/// snapshot profile selection.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct KeyboardProtocolStack {
    values: [u8; KITTY_KEYBOARD_STACK_CAPACITY],
    len: u8,
}

impl KeyboardProtocolStack {
    pub(crate) fn flags(self) -> u8 {
        self.as_slice().last().copied().unwrap_or(0)
    }

    pub(crate) fn is_empty(self) -> bool {
        self.len == 0
    }

    pub(crate) fn as_slice(&self) -> &[u8] {
        &self.values[..usize::from(self.len)]
    }

    pub(crate) fn replace(&mut self, flags: u16) {
        self.set_top(mask_keyboard_flags(flags));
    }

    pub(crate) fn union(&mut self, flags: u16) {
        self.set_top(self.flags() | mask_keyboard_flags(flags));
    }

    pub(crate) fn subtract(&mut self, flags: u16) {
        self.set_top(self.flags() & !mask_keyboard_flags(flags));
    }

    pub(crate) fn push(&mut self, flags: u16) {
        let flags = mask_keyboard_flags(flags);
        if usize::from(self.len) == KITTY_KEYBOARD_STACK_CAPACITY {
            self.values.copy_within(1.., 0);
            self.values[KITTY_KEYBOARD_STACK_CAPACITY - 1] = flags;
        } else {
            self.values[usize::from(self.len)] = flags;
            self.len += 1;
        }
    }

    pub(crate) fn pop(&mut self, count: u16) {
        let removed = usize::from(count).min(usize::from(self.len));
        let new_len = usize::from(self.len) - removed;
        self.values[new_len..usize::from(self.len)].fill(0);
        self.len = u8::try_from(new_len).expect("Kitty stack length fits u8");
    }

    pub(crate) fn from_slice(values: &[u8]) -> Option<Self> {
        if values.len() > KITTY_KEYBOARD_STACK_CAPACITY
            || values
                .iter()
                .any(|flags| flags & !KITTY_KEYBOARD_SUPPORTED_FLAGS != 0)
        {
            return None;
        }
        let mut result = Self::default();
        result.values[..values.len()].copy_from_slice(values);
        result.len = u8::try_from(values.len()).ok()?;
        Some(result)
    }

    fn set_top(&mut self, flags: u8) {
        if self.len == 0 {
            self.len = 1;
        }
        self.values[usize::from(self.len) - 1] = flags;
    }
}

fn mask_keyboard_flags(flags: u16) -> u8 {
    u8::try_from(flags & u16::from(KITTY_KEYBOARD_SUPPORTED_FLAGS))
        .expect("masked Kitty flags fit u8")
}

impl MouseEncoding {
    pub(crate) fn for_private_mode(parameter: u16) -> Option<Self> {
        match parameter {
            1005 => Some(Self::Utf8),
            1006 => Some(Self::Sgr),
            1015 => Some(Self::Urxvt),
            1016 => Some(Self::SgrPixels),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum AnsiMode {
    Insert,
    Newline,
}

impl AnsiMode {
    fn from_parameter(parameter: u16) -> Option<Self> {
        match parameter {
            4 => Some(Self::Insert),
            20 => Some(Self::Newline),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PrivateMode {
    CursorKeysApplication,
    ReverseVideo,
    Origin,
    AutoWrap,
    NationalReplacementCharacterSet,
    KeypadApplication,
    LeftRightMargin,
    FocusReporting,
    BracketedPaste,
    GraphemeClustering,
    ColorSchemeReporting,
    Win32Input,
    MouseTracking(MouseTracking),
    MouseEncoding(MouseEncoding),
}

impl PrivateMode {
    fn from_parameter(parameter: u16) -> Option<Self> {
        if let Some(tracking) = MouseTracking::for_private_mode(parameter) {
            return Some(Self::MouseTracking(tracking));
        }
        if let Some(encoding) = MouseEncoding::for_private_mode(parameter) {
            return Some(Self::MouseEncoding(encoding));
        }

        match parameter {
            1 => Some(Self::CursorKeysApplication),
            5 => Some(Self::ReverseVideo),
            6 => Some(Self::Origin),
            7 => Some(Self::AutoWrap),
            42 => Some(Self::NationalReplacementCharacterSet),
            66 => Some(Self::KeypadApplication),
            69 => Some(Self::LeftRightMargin),
            1004 => Some(Self::FocusReporting),
            2004 => Some(Self::BracketedPaste),
            2027 => Some(Self::GraphemeClustering),
            2031 => Some(Self::ColorSchemeReporting),
            9001 => Some(Self::Win32Input),
            _ => None,
        }
    }
}

/// Terminal modes whose values affect output processing or input encoding.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct ModeState {
    pub(crate) insert_mode: bool,
    pub(crate) newline_mode: bool,
    pub(crate) origin_mode: bool,
    pub(crate) auto_wrap: bool,
    pub(crate) national_replacement_character_set_mode: bool,
    pub(crate) reverse_video: bool,
    pub(crate) cursor_keys_application: bool,
    pub(crate) keypad_application: bool,
    pub(crate) bracketed_paste: bool,
    pub(crate) focus_reporting: bool,
    pub(crate) grapheme_clustering: bool,
    pub(crate) color_scheme_reporting: bool,
    pub(crate) win32_input_mode: bool,
    pub(crate) left_right_margin: bool,
    pub(crate) mouse_tracking: MouseTracking,
    pub(crate) mouse_encoding: MouseEncoding,
    pub(crate) attribute_change_extent: AttributeChangeExtent,
    pub(crate) modify_other_keys: ModifyOtherKeys,
}

impl Default for ModeState {
    fn default() -> Self {
        Self {
            insert_mode: false,
            newline_mode: false,
            origin_mode: false,
            auto_wrap: true,
            national_replacement_character_set_mode: false,
            reverse_video: false,
            cursor_keys_application: false,
            keypad_application: false,
            bracketed_paste: false,
            focus_reporting: false,
            grapheme_clustering: false,
            color_scheme_reporting: false,
            win32_input_mode: false,
            left_right_margin: false,
            mouse_tracking: MouseTracking::None,
            mouse_encoding: MouseEncoding::Default,
            attribute_change_extent: AttributeChangeExtent::Stream,
            modify_other_keys: ModifyOtherKeys::Disabled,
        }
    }
}

impl ModeState {
    /// Applies an ANSI mode (`SM`/`RM`), returning false for an unknown mode.
    pub(crate) fn set_ansi_mode(&mut self, parameter: u16, enabled: bool) -> bool {
        let Some(mode) = AnsiMode::from_parameter(parameter) else {
            return false;
        };
        match mode {
            AnsiMode::Insert => self.insert_mode = enabled,
            AnsiMode::Newline => self.newline_mode = enabled,
        }
        true
    }

    /// Queries an ANSI mode (`DECRQM`).
    ///
    /// Returns `Some(true)` for a set mode, `Some(false)` for a reset mode, and
    /// `None` when the mode is not recognized.
    pub(crate) fn query_ansi_mode(&self, parameter: u16) -> Option<bool> {
        Some(match AnsiMode::from_parameter(parameter)? {
            AnsiMode::Insert => self.insert_mode,
            AnsiMode::Newline => self.newline_mode,
        })
    }

    /// Applies a DEC private mode (`DECSET`/`DECRST`).
    ///
    /// Mouse tracking and encoding selections are represented as enums. Enabling
    /// one selection replaces the previous selection. Disabling a selection only
    /// clears it when that exact selection is active.
    pub(crate) fn set_private_mode(&mut self, parameter: u16, enabled: bool) -> bool {
        let Some(mode) = PrivateMode::from_parameter(parameter) else {
            return false;
        };
        match mode {
            PrivateMode::CursorKeysApplication => self.cursor_keys_application = enabled,
            PrivateMode::ReverseVideo => self.reverse_video = enabled,
            PrivateMode::Origin => self.origin_mode = enabled,
            PrivateMode::AutoWrap => self.auto_wrap = enabled,
            PrivateMode::NationalReplacementCharacterSet => {
                self.national_replacement_character_set_mode = enabled;
            }
            PrivateMode::KeypadApplication => self.keypad_application = enabled,
            PrivateMode::LeftRightMargin => self.left_right_margin = enabled,
            PrivateMode::FocusReporting => self.focus_reporting = enabled,
            PrivateMode::BracketedPaste => self.bracketed_paste = enabled,
            PrivateMode::GraphemeClustering => self.grapheme_clustering = enabled,
            PrivateMode::ColorSchemeReporting => self.color_scheme_reporting = enabled,
            PrivateMode::Win32Input => self.win32_input_mode = enabled,
            PrivateMode::MouseTracking(tracking) => {
                if enabled {
                    self.mouse_tracking = tracking;
                } else if self.mouse_tracking == tracking {
                    self.mouse_tracking = MouseTracking::None;
                }
            }
            PrivateMode::MouseEncoding(encoding) => {
                if enabled {
                    self.mouse_encoding = encoding;
                } else if self.mouse_encoding == encoding {
                    self.mouse_encoding = MouseEncoding::Default;
                }
            }
        }
        true
    }

    /// Queries a DEC private mode (`DECRQM`).
    ///
    /// Returns `Some(true)` for a set mode, `Some(false)` for a reset mode, and
    /// `None` when the mode is not recognized. For mutually exclusive mouse
    /// modes, only the currently selected mode is reported as set.
    pub(crate) fn query_private_mode(&self, parameter: u16) -> Option<bool> {
        Some(match PrivateMode::from_parameter(parameter)? {
            PrivateMode::CursorKeysApplication => self.cursor_keys_application,
            PrivateMode::ReverseVideo => self.reverse_video,
            PrivateMode::Origin => self.origin_mode,
            PrivateMode::AutoWrap => self.auto_wrap,
            PrivateMode::NationalReplacementCharacterSet => {
                self.national_replacement_character_set_mode
            }
            PrivateMode::KeypadApplication => self.keypad_application,
            PrivateMode::LeftRightMargin => self.left_right_margin,
            PrivateMode::FocusReporting => self.focus_reporting,
            PrivateMode::BracketedPaste => self.bracketed_paste,
            PrivateMode::GraphemeClustering => self.grapheme_clustering,
            PrivateMode::ColorSchemeReporting => self.color_scheme_reporting,
            PrivateMode::Win32Input => self.win32_input_mode,
            PrivateMode::MouseTracking(tracking) => self.mouse_tracking == tracking,
            PrivateMode::MouseEncoding(encoding) => self.mouse_encoding == encoding,
        })
    }

    /// Sets the application keypad state selected by `ESC =` and `ESC >`.
    pub(crate) fn set_keypad_application(&mut self, enabled: bool) {
        self.keypad_application = enabled;
    }

    /// Sets xterm's `modifyOtherKeys` resource from an XTMODKEYS value.
    pub(crate) fn set_modify_other_keys(&mut self, parameter: u16) -> bool {
        let Some(mode) = ModifyOtherKeys::from_parameter(parameter) else {
            return false;
        };
        self.modify_other_keys = mode;
        true
    }

    /// Resets xterm's `modifyOtherKeys` resource to its initial value.
    pub(crate) fn reset_modify_other_keys(&mut self) {
        self.modify_other_keys = ModifyOtherKeys::Disabled;
    }

    pub(crate) fn hard_reset(&mut self) {
        *self = Self::default();
    }
}

/// Zero-based, half-open terminal margins.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct Margins {
    pub(crate) top: u32,
    pub(crate) bottom: u32,
    pub(crate) left: u32,
    pub(crate) right: u32,
    pub(crate) vertical_follows_screen: bool,
    pub(crate) horizontal_follows_screen: bool,
}

impl Margins {
    pub(crate) const fn full(columns: u32, rows: u32) -> Self {
        Self {
            top: 0,
            bottom: rows,
            left: 0,
            right: columns,
            vertical_follows_screen: true,
            horizontal_follows_screen: true,
        }
    }

    /// Returns whether both half-open axes are non-empty and within the screen.
    pub(crate) const fn validate(&self, columns: u32, rows: u32) -> bool {
        self.top < self.bottom
            && self.bottom <= rows
            && self.left < self.right
            && self.right <= columns
            && (!self.vertical_follows_screen || (self.top == 0 && self.bottom == rows))
            && (!self.horizontal_follows_screen || (self.left == 0 && self.right == columns))
            && (self.vertical_follows_screen || self.bottom - self.top >= 2)
            && (self.horizontal_follows_screen || self.right - self.left >= 2)
    }

    /// Installs vertical margins if they form a non-empty in-bounds range.
    /// Invalid ranges leave the current margins unchanged.
    pub(crate) fn set_vertical(&mut self, top: u32, bottom: u32, rows: u32) -> bool {
        if bottom.saturating_sub(top) < 2 || bottom > rows {
            return false;
        }
        self.top = top;
        self.bottom = bottom;
        self.vertical_follows_screen = top == 0 && bottom == rows;
        true
    }

    /// Installs horizontal margins if they form a non-empty in-bounds range.
    /// Invalid ranges leave the current margins unchanged.
    pub(crate) fn set_horizontal(&mut self, left: u32, right: u32, columns: u32) -> bool {
        if right.saturating_sub(left) < 2 || right > columns {
            return false;
        }
        self.left = left;
        self.right = right;
        self.horizontal_follows_screen = left == 0 && right == columns;
        true
    }

    #[cfg(test)]
    pub(crate) fn reset_vertical(&mut self, rows: u32) {
        self.top = 0;
        self.bottom = rows;
        self.vertical_follows_screen = true;
    }

    pub(crate) fn reset_horizontal(&mut self, columns: u32) {
        self.left = 0;
        self.right = columns;
        self.horizontal_follows_screen = true;
    }

    /// Clamps both axes to a resized screen.
    ///
    /// A region that is completely clipped away resets to the full resized axis.
    /// With a zero-sized axis no non-empty margin exists, so that axis becomes
    /// `0..0`; callers can detect the invalid terminal size with [`Self::validate`].
    pub(crate) fn resize(&mut self, columns: u32, rows: u32) {
        (self.top, self.bottom, self.vertical_follows_screen) =
            resize_axis(self.top, self.bottom, rows, self.vertical_follows_screen);
        (self.left, self.right, self.horizontal_follows_screen) = resize_axis(
            self.left,
            self.right,
            columns,
            self.horizontal_follows_screen,
        );
    }

    #[cfg(test)]
    pub(crate) const fn contains(&self, column: u32, row: u32) -> bool {
        row >= self.top && row < self.bottom && column >= self.left && column < self.right
    }
}

fn resize_axis(start: u32, end: u32, limit: u32, follows_screen: bool) -> (u32, u32, bool) {
    if limit == 0 {
        return (0, 0, follows_screen);
    }
    if follows_screen {
        return (0, limit, true);
    }
    if start >= limit {
        return (0, limit, true);
    }
    let resized_start = start.min(limit - 1);
    let resized_end = end.min(limit);
    if resized_end.saturating_sub(resized_start) >= 2 {
        (resized_start, resized_end, false)
    } else {
        (0, limit, true)
    }
}

#[cfg(test)]
mod tests {
    use super::{
        AttributeChangeExtent, KITTY_KEYBOARD_STACK_CAPACITY, KeyboardProtocolStack, Margins,
        ModeState, ModifyOtherKeys, MouseEncoding, MouseTracking,
    };

    #[test]
    fn kitty_keyboard_stack_masks_updates_and_bounds_entries() {
        let mut stack = KeyboardProtocolStack::default();
        assert_eq!(stack.flags(), 0);
        assert!(stack.is_empty());

        stack.replace(0xffff);
        assert_eq!(stack.as_slice(), &[0x1f]);
        stack.union(0x20 | 2);
        assert_eq!(stack.as_slice(), &[0x1f]);
        stack.subtract(0x20 | 5);
        assert_eq!(stack.as_slice(), &[0x1a]);

        for value in 1..=KITTY_KEYBOARD_STACK_CAPACITY {
            stack.push(value as u16);
        }
        assert_eq!(stack.as_slice(), &[1, 2, 3, 4, 5, 6, 7, 8]);
        stack.push(9);
        assert_eq!(stack.as_slice(), &[2, 3, 4, 5, 6, 7, 8, 9]);
        stack.pop(0);
        assert_eq!(stack.as_slice(), &[2, 3, 4, 5, 6, 7, 8, 9]);
        stack.pop(3);
        assert_eq!(stack.as_slice(), &[2, 3, 4, 5, 6]);
        stack.pop(u16::MAX);
        assert!(stack.is_empty());
        assert_eq!(stack.flags(), 0);
    }

    #[test]
    fn kitty_keyboard_stack_snapshot_constructor_is_strict() {
        assert_eq!(
            KeyboardProtocolStack::from_slice(&[0, 1, 0x1f])
                .expect("valid stack")
                .as_slice(),
            &[0, 1, 0x1f]
        );
        assert!(KeyboardProtocolStack::from_slice(&[0x20]).is_none());
        assert!(KeyboardProtocolStack::from_slice(&[0; 9]).is_none());
    }

    #[test]
    fn default_modes_match_terminal_reset_defaults() {
        assert_eq!(
            ModeState::default(),
            ModeState {
                insert_mode: false,
                newline_mode: false,
                origin_mode: false,
                auto_wrap: true,
                national_replacement_character_set_mode: false,
                reverse_video: false,
                cursor_keys_application: false,
                keypad_application: false,
                bracketed_paste: false,
                focus_reporting: false,
                grapheme_clustering: false,
                color_scheme_reporting: false,
                win32_input_mode: false,
                left_right_margin: false,
                mouse_tracking: MouseTracking::None,
                mouse_encoding: MouseEncoding::Default,
                attribute_change_extent: AttributeChangeExtent::Stream,
                modify_other_keys: ModifyOtherKeys::Disabled,
            }
        );
    }

    #[test]
    fn modify_other_keys_accepts_only_xterm_levels_zero_through_three() {
        let mut modes = ModeState::default();
        for (parameter, expected) in [
            (1, ModifyOtherKeys::Level1),
            (2, ModifyOtherKeys::Level2),
            (3, ModifyOtherKeys::Level3),
            (0, ModifyOtherKeys::Disabled),
        ] {
            assert!(modes.set_modify_other_keys(parameter));
            assert_eq!(modes.modify_other_keys, expected);
            assert_eq!(expected.parameter(), parameter);
        }
        assert!(!modes.set_modify_other_keys(4));
        assert_eq!(modes.modify_other_keys, ModifyOtherKeys::Disabled);

        modes.modify_other_keys = ModifyOtherKeys::Level3;
        modes.reset_modify_other_keys();
        assert_eq!(modes.modify_other_keys, ModifyOtherKeys::Disabled);
    }

    #[test]
    fn ansi_and_private_boolean_modes_toggle_independently() {
        let mut modes = ModeState::default();
        assert!(modes.set_ansi_mode(4, true));
        assert!(modes.set_ansi_mode(20, true));
        for parameter in [1, 5, 6, 42, 66, 69, 1004, 2004, 2027, 2031, 9001] {
            assert!(modes.set_private_mode(parameter, true));
        }
        assert!(modes.insert_mode);
        assert!(modes.newline_mode);
        assert!(modes.cursor_keys_application);
        assert!(modes.reverse_video);
        assert!(modes.origin_mode);
        assert!(modes.national_replacement_character_set_mode);
        assert!(modes.keypad_application);
        assert!(modes.left_right_margin);
        assert!(modes.focus_reporting);
        assert!(modes.bracketed_paste);
        assert!(modes.grapheme_clustering);
        assert!(modes.color_scheme_reporting);
        assert!(modes.win32_input_mode);

        assert!(modes.set_private_mode(7, false));
        assert!(!modes.auto_wrap);
        assert!(modes.set_private_mode(9001, false));
        assert!(!modes.win32_input_mode);
        modes.set_keypad_application(false);
        assert!(!modes.keypad_application);
    }

    #[test]
    fn mode_queries_follow_set_and_reset_for_every_boolean_mode() {
        let mut modes = ModeState::default();
        for parameter in [4, 20] {
            assert_eq!(modes.query_ansi_mode(parameter), Some(false));
            assert!(modes.set_ansi_mode(parameter, true));
            assert_eq!(modes.query_ansi_mode(parameter), Some(true));
            assert!(modes.set_ansi_mode(parameter, false));
            assert_eq!(modes.query_ansi_mode(parameter), Some(false));
        }

        for parameter in [1, 5, 6, 7, 42, 66, 69, 1004, 2004, 2027, 2031, 9001] {
            assert!(modes.set_private_mode(parameter, true));
            assert_eq!(modes.query_private_mode(parameter), Some(true));
            assert!(modes.set_private_mode(parameter, false));
            assert_eq!(modes.query_private_mode(parameter), Some(false));
        }
    }

    #[test]
    fn unknown_modes_are_rejected_without_mutation() {
        let mut modes = ModeState::default();
        let original = modes;
        assert!(!modes.set_ansi_mode(999, true));
        assert!(!modes.set_private_mode(999, true));
        assert_eq!(modes.query_ansi_mode(999), None);
        assert_eq!(modes.query_private_mode(999), None);
        assert_eq!(modes, original);
    }

    #[test]
    fn mouse_tracking_selections_are_mutually_exclusive() {
        let mut modes = ModeState::default();
        let cases = [
            (9, MouseTracking::X10),
            (1000, MouseTracking::Normal),
            (1002, MouseTracking::ButtonEvent),
            (1003, MouseTracking::AnyEvent),
        ];
        for (parameter, expected) in cases {
            assert!(modes.set_private_mode(parameter, true));
            assert_eq!(modes.mouse_tracking, expected);
            for (candidate, _) in cases {
                assert_eq!(
                    modes.query_private_mode(candidate),
                    Some(candidate == parameter)
                );
            }
        }

        assert!(modes.set_private_mode(1000, false));
        assert_eq!(modes.mouse_tracking, MouseTracking::AnyEvent);
        assert_eq!(modes.query_private_mode(1003), Some(true));
        assert!(modes.set_private_mode(1003, false));
        assert_eq!(modes.mouse_tracking, MouseTracking::None);
        for (parameter, _) in cases {
            assert_eq!(modes.query_private_mode(parameter), Some(false));
        }
    }

    #[test]
    fn mouse_encoding_selections_are_mutually_exclusive() {
        let mut modes = ModeState::default();
        let cases = [
            (1005, MouseEncoding::Utf8),
            (1006, MouseEncoding::Sgr),
            (1015, MouseEncoding::Urxvt),
            (1016, MouseEncoding::SgrPixels),
        ];
        for (parameter, expected) in cases {
            assert!(modes.set_private_mode(parameter, true));
            assert_eq!(modes.mouse_encoding, expected);
            for (candidate, _) in cases {
                assert_eq!(
                    modes.query_private_mode(candidate),
                    Some(candidate == parameter)
                );
            }
        }

        assert!(modes.set_private_mode(1006, false));
        assert_eq!(modes.mouse_encoding, MouseEncoding::SgrPixels);
        assert_eq!(modes.query_private_mode(1016), Some(true));
        assert!(modes.set_private_mode(1016, false));
        assert_eq!(modes.mouse_encoding, MouseEncoding::Default);
        for (parameter, _) in cases {
            assert_eq!(modes.query_private_mode(parameter), Some(false));
        }
    }

    #[test]
    fn hard_reset_restores_every_mode_default() {
        let mut modes = ModeState::default();
        modes.set_ansi_mode(4, true);
        modes.set_ansi_mode(20, true);
        modes.set_private_mode(1, true);
        modes.set_private_mode(6, true);
        modes.set_private_mode(7, false);
        modes.set_private_mode(42, true);
        modes.set_private_mode(1003, true);
        modes.set_private_mode(1016, true);
        modes.set_private_mode(1004, true);
        modes.set_private_mode(2004, true);
        modes.set_private_mode(9001, true);
        modes.modify_other_keys = ModifyOtherKeys::Level3;
        modes.hard_reset();
        assert_eq!(modes, ModeState::default());
        assert_eq!(modes.query_private_mode(42), Some(false));
    }

    #[test]
    fn full_margins_are_zero_based_half_open_and_valid() {
        let margins = Margins::full(80, 24);
        assert_eq!((margins.top, margins.bottom), (0, 24));
        assert_eq!((margins.left, margins.right), (0, 80));
        assert!(margins.validate(80, 24));
        assert!(margins.contains(0, 0));
        assert!(margins.contains(79, 23));
        assert!(!margins.contains(80, 23));
        assert!(!margins.contains(79, 24));
        assert!(!Margins::full(0, 24).validate(0, 24));
        assert!(!Margins::full(80, 0).validate(80, 0));
    }

    #[test]
    fn margin_setters_reject_invalid_ranges_without_mutation() {
        let mut margins = Margins::full(80, 24);
        assert!(margins.set_vertical(2, 20, 24));
        assert!(margins.set_horizontal(4, 70, 80));
        let valid = margins;
        assert!(!margins.set_vertical(20, 20, 24));
        assert!(!margins.set_vertical(20, 21, 24));
        assert!(!margins.set_vertical(2, 25, 24));
        assert!(!margins.set_horizontal(70, 4, 80));
        assert!(!margins.set_horizontal(70, 71, 80));
        assert!(!margins.set_horizontal(4, 81, 80));
        assert_eq!(margins, valid);
    }

    #[test]
    fn resize_clamps_partial_regions_and_resets_fully_clipped_axes() {
        let mut margins = Margins {
            top: 2,
            bottom: 20,
            left: 4,
            right: 70,
            vertical_follows_screen: false,
            horizontal_follows_screen: false,
        };
        margins.resize(40, 10);
        assert_eq!(
            margins,
            Margins {
                top: 2,
                bottom: 10,
                left: 4,
                right: 40,
                vertical_follows_screen: false,
                horizontal_follows_screen: false,
            }
        );
        assert!(margins.validate(40, 10));

        margins.top = 9;
        margins.bottom = 10;
        margins.left = 39;
        margins.right = 40;
        margins.resize(8, 4);
        assert_eq!(margins, Margins::full(8, 4));

        let mut narrowed = Margins {
            top: 1,
            bottom: 4,
            left: 2,
            right: 5,
            vertical_follows_screen: false,
            horizontal_follows_screen: false,
        };
        narrowed.resize(3, 2);
        assert_eq!(narrowed, Margins::full(3, 2));
    }

    #[test]
    fn axis_resets_are_independent_and_zero_resize_is_deterministic() {
        let mut margins = Margins {
            top: 2,
            bottom: 20,
            left: 4,
            right: 70,
            vertical_follows_screen: false,
            horizontal_follows_screen: false,
        };
        margins.reset_vertical(30);
        assert_eq!((margins.top, margins.bottom), (0, 30));
        assert_eq!((margins.left, margins.right), (4, 70));
        margins.reset_horizontal(90);
        assert_eq!((margins.left, margins.right), (0, 90));

        margins.resize(0, 0);
        assert_eq!(margins, Margins::full(0, 0));
        assert!(!margins.validate(0, 0));
    }

    #[test]
    fn full_axes_expand_but_clipped_custom_axes_remain_custom() {
        let mut full = Margins::full(5, 4);
        full.resize(8, 7);
        assert_eq!(full, Margins::full(8, 7));

        let mut custom = Margins::full(5, 4);
        assert!(custom.set_vertical(0, 3, 4));
        assert!(custom.set_horizontal(0, 4, 5));
        custom.resize(3, 2);
        assert_eq!((custom.top, custom.bottom), (0, 2));
        assert_eq!((custom.left, custom.right), (0, 3));
        assert!(!custom.vertical_follows_screen);
        assert!(!custom.horizontal_follows_screen);

        custom.resize(8, 7);
        assert_eq!((custom.top, custom.bottom), (0, 2));
        assert_eq!((custom.left, custom.right), (0, 3));
        assert!(!custom.vertical_follows_screen);
        assert!(!custom.horizontal_follows_screen);
    }
}
