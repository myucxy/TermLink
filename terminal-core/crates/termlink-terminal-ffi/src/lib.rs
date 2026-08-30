//! Hardened C ABI for the TermLink terminal core.

use std::collections::HashMap;
use std::mem;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::{Mutex, OnceLock};

use termlink_terminal_core::{
    CellPosition, Event, HOST_EFFECT_FRAME_V1_FORMAT_VERSION, HOST_EFFECT_FRAME_V2_FORMAT_VERSION,
    InputEncodeError, InputEvent, Key, KeyEvent, KeypadKey, KittyFunctionalKey, KittyKey,
    KittyKeyEvent, KittyKeyEventType, KittyModifiers, MAX_HOST_EFFECT_FRAME_V1_BYTES,
    MAX_HOST_EFFECT_FRAME_V2_BYTES, MAX_KITTY_ASSOCIATED_TEXT_BYTES, MAX_OUTPUT_BYTES,
    MAX_PASTE_BYTES, MAX_RENDER_FRAME_V1_BYTES, MAX_RENDER_FRAME_V2_BYTES,
    MAX_RENDER_FRAME_V3_BYTES, MAX_SNAPSHOT_BYTES, Modifiers, MouseAction, MouseButton,
    MouseButtons, MouseEvent, MousePosition, MouseWheelDirection, OutputProfile, PasteEvent,
    PixelPosition, PreparedApplyWithHostEffectsV1, PreparedApplyWithHostEffectsV2,
    RENDER_FRAME_V1_FORMAT_VERSION, RENDER_FRAME_V2_FORMAT_VERSION, RENDER_FRAME_V3_FORMAT_VERSION,
    Terminal, TerminalError, TerminalOptions, Win32KeyEvent,
};

pub const ABI_VERSION: u32 = 1;

pub const STATUS_OK: u32 = 0;
pub const STATUS_NULL_POINTER: u32 = 1;
pub const STATUS_INVALID_ARGUMENT: u32 = 2;
pub const STATUS_INVALID_DIMENSIONS: u32 = 3;
pub const STATUS_LIMIT_EXCEEDED: u32 = 4;
pub const STATUS_SEQUENCE_MISMATCH: u32 = 5;
pub const STATUS_FAILED_CLOSED: u32 = 6;
pub const STATUS_UNSUPPORTED: u32 = 7;
pub const STATUS_GAP: u32 = 8;
pub const STATUS_MALFORMED_SNAPSHOT: u32 = 9;
pub const STATUS_INCOMPATIBLE_SNAPSHOT: u32 = 10;
pub const STATUS_TIME_OVERFLOW: u32 = 11;
pub const STATUS_SNAPSHOT_NOT_ELIGIBLE: u32 = 12;
pub const STATUS_PRESENTATION_DEFERRED: u32 = 13;
pub const STATUS_HOST_EFFECT_VERSION_REQUIRED: u32 = 14;
pub const STATUS_PANIC: u32 = 255;

pub const PRESENTATION_FLAG_FAILED_CLOSED: u32 = 1;
pub const PRESENTATION_FLAG_PARSER_INCOMPLETE: u32 = 2;
pub const PRESENTATION_FLAG_SYNCHRONIZED_OUTPUT: u32 = 4;

pub const EVENT_OUTPUT: u32 = 1;
pub const EVENT_RESIZE: u32 = 2;
pub const EVENT_ADVANCE: u32 = 3;
pub const EVENT_GAP: u32 = 4;

pub const INPUT_KIND_KEY: u32 = 1;
pub const INPUT_KIND_PASTE: u32 = 2;
pub const INPUT_KIND_FOCUS_GAINED: u32 = 3;
pub const INPUT_KIND_FOCUS_LOST: u32 = 4;
pub const INPUT_KIND_WIN32_KEY: u32 = 5;
pub const INPUT_KIND_KITTY_KEY: u32 = 6;

pub const INPUT_KEY_CHARACTER: u32 = 1;
pub const INPUT_KEY_ENTER: u32 = 2;
pub const INPUT_KEY_BACKSPACE: u32 = 3;
pub const INPUT_KEY_TAB: u32 = 4;
pub const INPUT_KEY_ESCAPE: u32 = 5;
pub const INPUT_KEY_DELETE: u32 = 6;
pub const INPUT_KEY_INSERT: u32 = 7;
pub const INPUT_KEY_HOME: u32 = 8;
pub const INPUT_KEY_END: u32 = 9;
pub const INPUT_KEY_PAGE_UP: u32 = 10;
pub const INPUT_KEY_PAGE_DOWN: u32 = 11;
pub const INPUT_KEY_ARROW_UP: u32 = 12;
pub const INPUT_KEY_ARROW_DOWN: u32 = 13;
pub const INPUT_KEY_ARROW_LEFT: u32 = 14;
pub const INPUT_KEY_ARROW_RIGHT: u32 = 15;
pub const INPUT_KEY_F1: u32 = 16;
pub const INPUT_KEY_F2: u32 = 17;
pub const INPUT_KEY_F3: u32 = 18;
pub const INPUT_KEY_F4: u32 = 19;
pub const INPUT_KEY_F5: u32 = 20;
pub const INPUT_KEY_F6: u32 = 21;
pub const INPUT_KEY_F7: u32 = 22;
pub const INPUT_KEY_F8: u32 = 23;
pub const INPUT_KEY_F9: u32 = 24;
pub const INPUT_KEY_F10: u32 = 25;
pub const INPUT_KEY_F11: u32 = 26;
pub const INPUT_KEY_F12: u32 = 27;
pub const INPUT_KEY_KEYPAD_0: u32 = 100;
pub const INPUT_KEY_KEYPAD_1: u32 = 101;
pub const INPUT_KEY_KEYPAD_2: u32 = 102;
pub const INPUT_KEY_KEYPAD_3: u32 = 103;
pub const INPUT_KEY_KEYPAD_4: u32 = 104;
pub const INPUT_KEY_KEYPAD_5: u32 = 105;
pub const INPUT_KEY_KEYPAD_6: u32 = 106;
pub const INPUT_KEY_KEYPAD_7: u32 = 107;
pub const INPUT_KEY_KEYPAD_8: u32 = 108;
pub const INPUT_KEY_KEYPAD_9: u32 = 109;
pub const INPUT_KEY_KEYPAD_DECIMAL: u32 = 110;
pub const INPUT_KEY_KEYPAD_SEPARATOR: u32 = 111;
pub const INPUT_KEY_KEYPAD_ADD: u32 = 112;
pub const INPUT_KEY_KEYPAD_SUBTRACT: u32 = 113;
pub const INPUT_KEY_KEYPAD_MULTIPLY: u32 = 114;
pub const INPUT_KEY_KEYPAD_DIVIDE: u32 = 115;
pub const INPUT_KEY_KEYPAD_EQUAL: u32 = 116;
pub const INPUT_KEY_KEYPAD_ENTER: u32 = 117;
pub const INPUT_KEY_KITTY_PUA_FIRST: u32 = 57_358;
pub const INPUT_KEY_KITTY_PUA_LAST: u32 = 57_454;
pub const INPUT_KEY_UNIDENTIFIED_TEXT: u32 = u32::MAX;

pub const INPUT_MODIFIER_SHIFT: u32 = 1;
pub const INPUT_MODIFIER_ALT: u32 = 2;
pub const INPUT_MODIFIER_CONTROL: u32 = 4;
pub const INPUT_MODIFIER_META: u32 = 8;
const INPUT_MODIFIERS_ALL: u32 =
    INPUT_MODIFIER_SHIFT | INPUT_MODIFIER_ALT | INPUT_MODIFIER_CONTROL | INPUT_MODIFIER_META;

pub const INPUT_KEY_EVENT_PRESS: u32 = 1;
pub const INPUT_KEY_EVENT_REPEAT: u32 = 2;
pub const INPUT_KEY_EVENT_RELEASE: u32 = 3;

pub const OUTPUT_PROFILE_UTF8: u32 = 0;
pub const OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1: u32 = 1;

pub const KEYBOARD_MODIFIER_SHIFT: u32 = 1;
pub const KEYBOARD_MODIFIER_ALT: u32 = 2;
pub const KEYBOARD_MODIFIER_CONTROL: u32 = 4;
pub const KEYBOARD_MODIFIER_SUPER: u32 = 8;
pub const KEYBOARD_MODIFIER_HYPER: u32 = 16;
pub const KEYBOARD_MODIFIER_META: u32 = 32;
pub const KEYBOARD_MODIFIER_CAPS_LOCK: u32 = 64;
pub const KEYBOARD_MODIFIER_NUM_LOCK: u32 = 128;
const KEYBOARD_MODIFIERS_ALL: u32 = KEYBOARD_MODIFIER_SHIFT
    | KEYBOARD_MODIFIER_ALT
    | KEYBOARD_MODIFIER_CONTROL
    | KEYBOARD_MODIFIER_SUPER
    | KEYBOARD_MODIFIER_HYPER
    | KEYBOARD_MODIFIER_META
    | KEYBOARD_MODIFIER_CAPS_LOCK
    | KEYBOARD_MODIFIER_NUM_LOCK;

pub const MOUSE_ACTION_PRESS: u32 = 1;
pub const MOUSE_ACTION_RELEASE: u32 = 2;
pub const MOUSE_ACTION_MOVE: u32 = 3;
pub const MOUSE_ACTION_WHEEL: u32 = 4;

pub const MOUSE_BUTTON_NONE: u32 = 0;
pub const MOUSE_BUTTON_LEFT: u32 = 1;
pub const MOUSE_BUTTON_MIDDLE: u32 = 2;
pub const MOUSE_BUTTON_RIGHT: u32 = 3;

pub const MOUSE_WHEEL_NONE: u32 = 0;
pub const MOUSE_WHEEL_UP: u32 = 1;
pub const MOUSE_WHEEL_DOWN: u32 = 2;
pub const MOUSE_WHEEL_LEFT: u32 = 3;
pub const MOUSE_WHEEL_RIGHT: u32 = 4;

pub const MOUSE_BUTTON_HELD_LEFT: u32 = 1;
pub const MOUSE_BUTTON_HELD_MIDDLE: u32 = 2;
pub const MOUSE_BUTTON_HELD_RIGHT: u32 = 4;
const MOUSE_BUTTONS_HELD_ALL: u32 =
    MOUSE_BUTTON_HELD_LEFT | MOUSE_BUTTON_HELD_MIDDLE | MOUSE_BUTTON_HELD_RIGHT;

pub const MOUSE_POSITION_PIXEL: u32 = 1;
const MOUSE_POSITION_FLAGS_ALL: u32 = MOUSE_POSITION_PIXEL;

const MAX_EVENT_BATCH_COUNT: usize = 256;
const MAX_EVENT_BATCH_OUTPUT_BYTES: usize = 4 * 1024 * 1024;

#[repr(C)]
pub struct TlTerminal {
    inner: Terminal,
}

#[repr(C)]
pub struct TlTerminalCheckpoint {
    frozen_terminal: Terminal,
    revision: u64,
    next_source_sequence: u64,
    #[cfg(test)]
    snapshot_export_count: std::sync::atomic::AtomicUsize,
    #[cfg(test)]
    render_frame_v1_export_count: std::sync::atomic::AtomicUsize,
    #[cfg(test)]
    render_frame_v2_export_count: std::sync::atomic::AtomicUsize,
    #[cfg(test)]
    render_frame_v3_export_count: std::sync::atomic::AtomicUsize,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalConfigV1 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub columns: u32,
    pub rows: u32,
    pub initial_source_sequence: u64,
    pub reserved: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalConfigV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub columns: u32,
    pub rows: u32,
    pub initial_source_sequence: u64,
    pub reserved_v1_compat: [u64; 2],
    pub history_capacity_rows: u64,
    pub reserved: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalConfigV3 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub columns: u32,
    pub rows: u32,
    pub initial_source_sequence: u64,
    pub reserved_v1_compat: [u64; 2],
    pub history_capacity_rows: u64,
    pub reserved_v2_compat: [u64; 2],
    pub output_profile: u32,
    pub reserved0: u32,
    pub reserved: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalEvent {
    pub abi_version: u32,
    pub struct_size: u32,
    pub source_sequence: u64,
    pub kind: u32,
    pub reserved: u32,
    pub data: *const u8,
    #[cfg(target_pointer_width = "32")]
    pub data_pointer_padding: u32,
    pub data_len: u64,
    pub columns: u32,
    pub rows: u32,
    pub advance_micros: u64,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalBuffer {
    pub abi_version: u32,
    pub struct_size: u32,
    pub data: *const u8,
    #[cfg(target_pointer_width = "32")]
    pub data_pointer_padding: u32,
    pub len: u64,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalCheckpointInfo {
    pub abi_version: u32,
    pub struct_size: u32,
    pub revision: u64,
    pub next_source_sequence: u64,
    pub reserved: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalPresentationInfoV1 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub flags: u32,
    pub reserved0: u32,
    pub next_source_sequence: u64,
    pub logical_time_micros: u64,
    pub synchronized_output_deadline_micros: u64,
    pub reserved: [u64; 1],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalInputV1 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub kind: u32,
    pub key: u32,
    pub modifiers: u32,
    pub character: u32,
    pub data: *const u8,
    #[cfg(target_pointer_width = "32")]
    pub data_pointer_padding: u32,
    pub data_len: u64,
    pub reserved: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalInputV2 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub kind: u32,
    pub key: u32,
    pub modifiers: u32,
    pub character: u32,
    pub data: *const u8,
    #[cfg(target_pointer_width = "32")]
    pub data_pointer_padding: u32,
    pub data_len: u64,
    pub reserved_v1_compat: [u64; 2],
    pub win32_virtual_key: u32,
    pub win32_scan_code: u32,
    pub win32_unicode_char: u32,
    pub win32_key_down: u32,
    pub win32_control_key_state: u32,
    pub win32_repeat_count: u32,
    pub reserved: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalInputV3 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub kind: u32,
    pub key: u32,
    pub modifiers: u32,
    pub character: u32,
    pub data: *const u8,
    #[cfg(target_pointer_width = "32")]
    pub data_pointer_padding: u32,
    pub data_len: u64,
    pub reserved_v1_compat: [u64; 2],
    pub win32_virtual_key: u32,
    pub win32_scan_code: u32,
    pub win32_unicode_char: u32,
    pub win32_key_down: u32,
    pub win32_control_key_state: u32,
    pub win32_repeat_count: u32,
    pub reserved: [u64; 2],
    pub key_event_type: u32,
    pub keyboard_modifiers: u32,
    pub shifted_key: u32,
    pub base_layout_key: u32,
    pub associated_text: *const u8,
    #[cfg(target_pointer_width = "32")]
    pub associated_text_pointer_padding: u32,
    pub associated_text_len: u64,
    pub reserved_v3: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct TlTerminalMouseInputV1 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub action: u32,
    pub button: u32,
    pub wheel: u32,
    pub buttons_held: u32,
    pub position_flags: u32,
    pub modifiers: u32,
    pub cell_column: u32,
    pub cell_row: u32,
    pub pixel_x: u32,
    pub pixel_y: u32,
    pub reserved: [u64; 2],
}

// Public layouts are intentionally identical on 32- and 64-bit targets. The
// explicit 32-bit pointer padding above prevents an i686 ABI from silently
// shortening structures or moving the following u64 fields.
const _: () = {
    assert!(mem::size_of::<TlTerminalConfigV1>() == 40);
    assert!(mem::size_of::<TlTerminalConfigV2>() == 64);
    assert!(mem::size_of::<TlTerminalConfigV3>() == 88);
    assert!(mem::offset_of!(TlTerminalConfigV3, initial_source_sequence) == 16);
    assert!(mem::offset_of!(TlTerminalConfigV3, reserved_v1_compat) == 24);
    assert!(mem::offset_of!(TlTerminalConfigV3, history_capacity_rows) == 40);
    assert!(mem::offset_of!(TlTerminalConfigV3, reserved_v2_compat) == 48);
    assert!(mem::offset_of!(TlTerminalConfigV3, output_profile) == 64);
    assert!(mem::offset_of!(TlTerminalConfigV3, reserved0) == 68);
    assert!(mem::offset_of!(TlTerminalConfigV3, reserved) == 72);
    assert!(mem::size_of::<TlTerminalEvent>() == 56);
    assert!(mem::offset_of!(TlTerminalEvent, data) == 24);
    assert!(mem::offset_of!(TlTerminalEvent, data_len) == 32);
    assert!(mem::offset_of!(TlTerminalEvent, advance_micros) == 48);
    assert!(mem::size_of::<TlTerminalBuffer>() == 24);
    assert!(mem::offset_of!(TlTerminalBuffer, data) == 8);
    assert!(mem::offset_of!(TlTerminalBuffer, len) == 16);
    assert!(mem::size_of::<TlTerminalInputV1>() == 56);
    assert!(mem::offset_of!(TlTerminalInputV1, data) == 24);
    assert!(mem::offset_of!(TlTerminalInputV1, data_len) == 32);
    assert!(mem::offset_of!(TlTerminalInputV1, reserved) == 40);
    assert!(mem::size_of::<TlTerminalInputV2>() == 96);
    assert!(mem::offset_of!(TlTerminalInputV2, data_len) == 32);
    assert!(mem::offset_of!(TlTerminalInputV2, win32_virtual_key) == 56);
    assert!(mem::offset_of!(TlTerminalInputV2, reserved) == 80);
    assert!(mem::size_of::<TlTerminalInputV3>() == 160);
    assert!(mem::offset_of!(TlTerminalInputV3, data_len) == 32);
    assert!(mem::offset_of!(TlTerminalInputV3, key_event_type) == 96);
    assert!(mem::offset_of!(TlTerminalInputV3, associated_text) == 112);
    assert!(mem::offset_of!(TlTerminalInputV3, associated_text_len) == 120);
    assert!(mem::offset_of!(TlTerminalInputV3, reserved_v3) == 128);
};

#[repr(C)]
#[derive(Clone, Copy)]
struct AbiPrefix {
    abi_version: u32,
    struct_size: u32,
}

#[derive(Clone, Copy, Eq, PartialEq)]
struct BufferToken {
    len: usize,
    allocation_len: usize,
    struct_size: u32,
}

struct PendingOwnedBuffer {
    buffer: TlTerminalBuffer,
    allocation: Box<[u8]>,
    registered: bool,
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_abi_version() -> u32 {
    catch_unwind(|| ABI_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_max_snapshot_bytes() -> u64 {
    catch_unwind(|| u64::try_from(MAX_SNAPSHOT_BYTES).expect("maximum snapshot size fits in u64"))
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_render_frame_v1_format_version() -> u32 {
    catch_unwind(|| RENDER_FRAME_V1_FORMAT_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_render_frame_v1_max_bytes() -> u64 {
    catch_unwind(|| {
        u64::try_from(MAX_RENDER_FRAME_V1_BYTES).expect("maximum render frame size fits in u64")
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_render_frame_v2_format_version() -> u32 {
    catch_unwind(|| RENDER_FRAME_V2_FORMAT_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_render_frame_v2_max_bytes() -> u64 {
    catch_unwind(|| {
        u64::try_from(MAX_RENDER_FRAME_V2_BYTES).expect("maximum render frame size fits in u64")
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_render_frame_v3_format_version() -> u32 {
    catch_unwind(|| RENDER_FRAME_V3_FORMAT_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_render_frame_v3_max_bytes() -> u64 {
    catch_unwind(|| {
        u64::try_from(MAX_RENDER_FRAME_V3_BYTES).expect("maximum render frame size fits in u64")
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_host_effect_frame_v1_format_version() -> u32 {
    catch_unwind(|| HOST_EFFECT_FRAME_V1_FORMAT_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_host_effect_frame_v1_max_bytes() -> u64 {
    catch_unwind(|| {
        u64::try_from(MAX_HOST_EFFECT_FRAME_V1_BYTES)
            .expect("maximum host effect frame size fits in u64")
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_host_effect_frame_v2_format_version() -> u32 {
    catch_unwind(|| HOST_EFFECT_FRAME_V2_FORMAT_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_host_effect_frame_v2_max_bytes() -> u64 {
    catch_unwind(|| {
        u64::try_from(MAX_HOST_EFFECT_FRAME_V2_BYTES)
            .expect("maximum host effect frame size fits in u64")
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_config_v1_struct_size() -> u32 {
    catch_struct_size::<TlTerminalConfigV1>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_config_v2_struct_size() -> u32 {
    catch_struct_size::<TlTerminalConfigV2>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_config_v3_struct_size() -> u32 {
    catch_struct_size::<TlTerminalConfigV3>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_event_struct_size() -> u32 {
    catch_struct_size::<TlTerminalEvent>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_buffer_struct_size() -> u32 {
    catch_struct_size::<TlTerminalBuffer>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_checkpoint_info_struct_size() -> u32 {
    catch_struct_size::<TlTerminalCheckpointInfo>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_presentation_info_v1_struct_size() -> u32 {
    catch_struct_size::<TlTerminalPresentationInfoV1>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_input_v1_struct_size() -> u32 {
    catch_struct_size::<TlTerminalInputV1>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_input_v2_struct_size() -> u32 {
    catch_struct_size::<TlTerminalInputV2>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_input_v3_struct_size() -> u32 {
    catch_struct_size::<TlTerminalInputV3>()
}

#[unsafe(no_mangle)]
pub extern "C" fn tl_terminal_mouse_input_v1_struct_size() -> u32 {
    catch_struct_size::<TlTerminalMouseInputV1>()
}

/// Creates a terminal from a versioned configuration.
///
/// # Safety
///
/// `config` must have a readable ABI prefix and must be readable for the size it
/// declares when that size is accepted. `out_terminal` must be writable and
/// contain null. On success it receives a live terminal owned by the caller.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_create(
    config: *const TlTerminalConfigV1,
    out_terminal: *mut *mut TlTerminal,
) -> u32 {
    catch_status(|| {
        if config.is_null() || out_terminal.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a writable pointer-sized output.
        if let Err(status) = unsafe { require_empty_handle(out_terminal) } {
            return status;
        }
        // SAFETY: The C contract requires a readable versioned configuration.
        let config = match unsafe { read_versioned(config) } {
            Ok(config) => config,
            Err(status) => return status,
        };
        if config.reserved != [0; 2] {
            return STATUS_INVALID_ARGUMENT;
        }

        match Terminal::new_with_sequence(
            config.columns,
            config.rows,
            config.initial_source_sequence,
        ) {
            Ok(inner) => {
                let terminal = Box::into_raw(Box::new(TlTerminal { inner }));
                // SAFETY: The output was validated above and remains writable.
                unsafe { ptr::write_unaligned(out_terminal, terminal) };
                STATUS_OK
            }
            Err(error) => status_for_error(&error),
        }
    })
}

/// Creates a terminal with the additive V2 scrollback configuration.
///
/// # Safety
///
/// The pointer and ownership requirements are identical to `tl_terminal_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_create_v2(
    config: *const TlTerminalConfigV2,
    out_terminal: *mut *mut TlTerminal,
) -> u32 {
    catch_status(|| {
        if config.is_null() || out_terminal.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a writable pointer-sized output.
        if let Err(status) = unsafe { require_empty_handle(out_terminal) } {
            return status;
        }
        // SAFETY: The C contract requires a readable versioned configuration.
        let config = match unsafe { read_versioned(config) } {
            Ok(config) => config,
            Err(status) => return status,
        };
        if config.reserved_v1_compat != [0; 2]
            || config.reserved != [0; 2]
            || config.history_capacity_rows > u64::from(u32::MAX)
        {
            return STATUS_INVALID_ARGUMENT;
        }
        let history_capacity_rows =
            u32::try_from(config.history_capacity_rows).expect("prevalidated history rows");
        match Terminal::new_with_options(
            config.columns,
            config.rows,
            config.initial_source_sequence,
            TerminalOptions {
                history_capacity_rows,
            },
        ) {
            Ok(inner) => {
                let terminal = Box::into_raw(Box::new(TlTerminal { inner }));
                // SAFETY: The output was validated above and remains writable.
                unsafe { ptr::write_unaligned(out_terminal, terminal) };
                STATUS_OK
            }
            Err(error) => status_for_error(&error),
        }
    })
}

/// Creates a terminal with additive scrollback and output-profile configuration.
///
/// # Safety
///
/// The pointer and ownership requirements are identical to `tl_terminal_create`.
/// Unlike earlier configuration entry points, V3 requires `struct_size` to be
/// exactly `tl_terminal_config_v3_struct_size()`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_create_v3(
    config: *const TlTerminalConfigV3,
    out_terminal: *mut *mut TlTerminal,
) -> u32 {
    catch_status(|| {
        if config.is_null() || out_terminal.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a writable pointer-sized output.
        if let Err(status) = unsafe { require_empty_handle(out_terminal) } {
            return status;
        }
        // SAFETY: The C contract requires a readable versioned configuration.
        let config = match unsafe { read_versioned_exact(config) } {
            Ok(config) => config,
            Err(status) => return status,
        };
        if config.reserved_v1_compat != [0; 2]
            || config.reserved_v2_compat != [0; 2]
            || config.reserved0 != 0
            || config.reserved != [0; 2]
            || config.history_capacity_rows > u64::from(u32::MAX)
        {
            return STATUS_INVALID_ARGUMENT;
        }
        let output_profile = match config.output_profile {
            OUTPUT_PROFILE_UTF8 => OutputProfile::Utf8,
            OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1 => OutputProfile::Utf8WithStandaloneC1,
            _ => return STATUS_INVALID_ARGUMENT,
        };
        let history_capacity_rows =
            u32::try_from(config.history_capacity_rows).expect("prevalidated history rows");
        match Terminal::new_with_output_profile(
            config.columns,
            config.rows,
            config.initial_source_sequence,
            TerminalOptions {
                history_capacity_rows,
            },
            output_profile,
        ) {
            Ok(inner) => {
                let terminal = Box::into_raw(Box::new(TlTerminal { inner }));
                // SAFETY: The output was validated above and remains writable.
                unsafe { ptr::write_unaligned(out_terminal, terminal) };
                STATUS_OK
            }
            Err(error) => status_for_error(&error),
        }
    })
}

/// Destroys a terminal created or imported by this library.
///
/// # Safety
///
/// `terminal` must be null or a live pointer returned by this library. A live
/// pointer must not be destroyed more than once or accessed concurrently.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_destroy(terminal: *mut TlTerminal) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !terminal.is_null() {
            // SAFETY: Required by this function's C ABI contract.
            drop(unsafe { Box::from_raw(terminal) });
        }
    }));
}

/// Applies one atomic source event.
///
/// # Safety
///
/// `terminal` must be a live, exclusively accessible terminal. `event` must
/// have a readable ABI prefix and be readable for its accepted declared size.
/// A non-empty output byte region must remain readable for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_apply_event(
    terminal: *mut TlTerminal,
    event: *const TlTerminalEvent,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || event.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned event.
        let event = match unsafe { read_versioned(event) } {
            Ok(event) => event,
            Err(status) => return status,
        };
        let core_event = match decode_terminal_event(&event) {
            Ok(event) => event,
            Err(status) => return status,
        };

        // SAFETY: The C contract requires exclusive access to this live handle.
        let terminal = unsafe { &mut *terminal };
        status_for_result(terminal.inner.apply_event(core_event))
    })
}

/// Applies a fixed-stride array of source events as one atomic transaction.
///
/// # Safety
///
/// `terminal` must be a live, exclusively accessible terminal. `events` must
/// point to `event_count` contiguous, exactly sized `TlTerminalEvent` values.
/// Every non-empty output byte region must remain readable for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_apply_event_batch_v1(
    terminal: *mut TlTerminal,
    events: *const TlTerminalEvent,
    event_count: u32,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable fixed-stride event array
        // and readable payloads for the duration of this call.
        let decoded = match unsafe { decode_terminal_event_batch(events, event_count) } {
            Ok(decoded) => decoded,
            Err(status) => return status,
        };

        // SAFETY: Validation completed without touching terminal state, and
        // the C contract requires exclusive access to this live handle.
        let terminal = unsafe { &mut *terminal };
        status_for_result(terminal.inner.apply_events_atomic(&decoded))
    })
}

/// Applies one source event and returns its canonical Host Effect Frame V1.
///
/// # Safety
///
/// `terminal` must be a live, exclusively accessible terminal. `event` must
/// meet the same readable versioned-input contract as `tl_terminal_apply_event`.
/// `out_effect_frame` must be a current-version, writable, empty buffer. On
/// success it owns a canonical frame, including the 64-byte empty frame.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_apply_event_with_host_effects_v1(
    terminal: *mut TlTerminal,
    event: *const TlTerminalEvent,
    out_effect_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || event.is_null() || out_effect_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned event.
        let event = match unsafe { read_versioned(event) } {
            Ok(event) => event,
            Err(status) => return status,
        };
        let core_event = match decode_terminal_event(&event) {
            Ok(event) => event,
            Err(status) => return status,
        };
        // SAFETY: Validate all known output metadata before terminal access.
        let capacity = match unsafe { empty_buffer_capacity(out_effect_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };

        // SAFETY: All event, payload, and output validation is complete, and
        // the C contract requires exclusive access to this live handle.
        let terminal = unsafe { &mut *terminal };
        let prepared = match terminal
            .inner
            .prepare_event_with_host_effects_v1(core_event)
        {
            Ok(prepared) => prepared,
            Err(error) => return status_for_error(&error),
        };
        // SAFETY: Capacity validation above proves the known output fields are
        // writable and empty for the unique publication below.
        unsafe { publish_prepared_host_effects(prepared, capacity, out_effect_frame) }
    })
}

/// Applies one fixed-stride source batch and returns Host Effect Frame V1.
///
/// # Safety
///
/// `terminal`, `events`, and `out_effect_frame` have the same ownership and
/// readability requirements as the corresponding single-event function. The
/// batch must contain 1 through 256 exact-size events whose Output payloads
/// total no more than 4 MiB.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_apply_event_batch_with_host_effects_v1(
    terminal: *mut TlTerminal,
    events: *const TlTerminalEvent,
    event_count: u32,
    out_effect_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || out_effect_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable fixed-stride event array
        // and readable payloads for the duration of this call.
        let decoded = match unsafe { decode_terminal_event_batch(events, event_count) } {
            Ok(decoded) => decoded,
            Err(status) => return status,
        };
        // SAFETY: Validate all known output metadata before terminal access.
        let capacity = match unsafe { empty_buffer_capacity(out_effect_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };

        // SAFETY: Complete batch and output validation precedes this first
        // terminal access. The caller promises exclusive access to the handle.
        let terminal = unsafe { &mut *terminal };
        let prepared = match terminal
            .inner
            .prepare_events_atomic_with_host_effects_v1(&decoded)
        {
            Ok(prepared) => prepared,
            Err(error) => return status_for_error(&error),
        };
        // SAFETY: Capacity validation above proves the known output fields are
        // writable and empty for the unique publication below.
        unsafe { publish_prepared_host_effects(prepared, capacity, out_effect_frame) }
    })
}

/// Applies one source event and returns its canonical Host Effect Frame V2.
///
/// # Safety
///
/// The pointer, terminal access, event, and output ownership requirements are
/// identical to `tl_terminal_apply_event_with_host_effects_v1`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_apply_event_with_host_effects_v2(
    terminal: *mut TlTerminal,
    event: *const TlTerminalEvent,
    out_effect_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || event.is_null() || out_effect_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned event.
        let event = match unsafe { read_versioned(event) } {
            Ok(event) => event,
            Err(status) => return status,
        };
        let core_event = match decode_terminal_event(&event) {
            Ok(event) => event,
            Err(status) => return status,
        };
        // SAFETY: Validate all known output metadata before terminal access.
        let capacity = match unsafe { empty_buffer_capacity(out_effect_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };

        // SAFETY: All event, payload, and output validation is complete, and
        // the C contract requires exclusive access to this live handle.
        let terminal = unsafe { &mut *terminal };
        let prepared = match terminal
            .inner
            .prepare_event_with_host_effects_v2(core_event)
        {
            Ok(prepared) => prepared,
            Err(error) => return status_for_error(&error),
        };
        // SAFETY: Capacity validation above proves the known output fields are
        // writable and empty for the unique publication below.
        unsafe { publish_prepared_host_effects_v2(prepared, capacity, out_effect_frame) }
    })
}

/// Applies one fixed-stride source batch and returns Host Effect Frame V2.
///
/// # Safety
///
/// The pointer, terminal access, event-array, and output ownership requirements
/// are identical to the V1 batch host-effect function.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_apply_event_batch_with_host_effects_v2(
    terminal: *mut TlTerminal,
    events: *const TlTerminalEvent,
    event_count: u32,
    out_effect_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || out_effect_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable fixed-stride event array
        // and readable payloads for the duration of this call.
        let decoded = match unsafe { decode_terminal_event_batch(events, event_count) } {
            Ok(decoded) => decoded,
            Err(status) => return status,
        };
        // SAFETY: Validate all known output metadata before terminal access.
        let capacity = match unsafe { empty_buffer_capacity(out_effect_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };

        // SAFETY: Complete batch and output validation precedes this first
        // terminal access. The caller promises exclusive access to the handle.
        let terminal = unsafe { &mut *terminal };
        let prepared = match terminal
            .inner
            .prepare_events_atomic_with_host_effects_v2(&decoded)
        {
            Ok(prepared) => prepared,
            Err(error) => return status_for_error(&error),
        };
        // SAFETY: Capacity validation above proves the known output fields are
        // writable and empty for the unique publication below.
        unsafe { publish_prepared_host_effects_v2(prepared, capacity, out_effect_frame) }
    })
}

/// Reports whether current terminal state may be presented or checkpointed.
///
/// # Safety
///
/// `terminal` must remain live for this read-only call. `out_info` must have a
/// readable ABI prefix and be writable for its accepted declared size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_get_presentation_info_v1(
    terminal: *const TlTerminal,
    out_info: *mut TlTerminalPresentationInfoV1,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || out_info.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable and writable output.
        let capacity = match unsafe { output_capacity(out_info) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the terminal to remain live.
        let terminal = unsafe { &*terminal };
        let mut flags = 0;
        if terminal.inner.is_failed_closed() {
            flags |= PRESENTATION_FLAG_FAILED_CLOSED;
        }
        if terminal.inner.is_parser_incomplete() {
            flags |= PRESENTATION_FLAG_PARSER_INCOMPLETE;
        }
        let synchronized_output_deadline_micros =
            terminal.inner.synchronized_output_deadline_micros();
        if synchronized_output_deadline_micros.is_some() {
            flags |= PRESENTATION_FLAG_SYNCHRONIZED_OUTPUT;
        }
        let info = TlTerminalPresentationInfoV1 {
            abi_version: ABI_VERSION,
            struct_size: capacity,
            flags,
            reserved0: 0,
            next_source_sequence: terminal.inner.next_source_sequence(),
            logical_time_micros: terminal.inner.logical_time_micros(),
            synchronized_output_deadline_micros: synchronized_output_deadline_micros.unwrap_or(0),
            reserved: [0; 1],
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_info, info) };
        STATUS_OK
    })
}

/// Captures an immutable checkpoint of a terminal.
///
/// # Safety
///
/// `terminal` must be a live terminal with exclusive access for this call.
/// `out_checkpoint` must be writable and contain null. The returned checkpoint
/// is independent of later terminal mutations.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_begin(
    terminal: *const TlTerminal,
    out_checkpoint: *mut *mut TlTerminalCheckpoint,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || out_checkpoint.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a writable pointer-sized output.
        if let Err(status) = unsafe { require_empty_handle(out_checkpoint) } {
            return status;
        }
        // SAFETY: The C contract excludes concurrent terminal access.
        let terminal = unsafe { &*terminal };
        let eligibility = if terminal.inner.is_failed_closed() {
            STATUS_FAILED_CLOSED
        } else if terminal.inner.is_parser_incomplete() {
            STATUS_SNAPSHOT_NOT_ELIGIBLE
        } else if terminal
            .inner
            .synchronized_output_deadline_micros()
            .is_some()
        {
            STATUS_PRESENTATION_DEFERRED
        } else {
            STATUS_OK
        };
        if eligibility != STATUS_OK {
            return eligibility;
        }
        let next_source_sequence = terminal.inner.next_source_sequence();
        let checkpoint = Box::into_raw(Box::new(TlTerminalCheckpoint {
            frozen_terminal: terminal.inner.clone(),
            revision: next_source_sequence,
            next_source_sequence,
            #[cfg(test)]
            snapshot_export_count: std::sync::atomic::AtomicUsize::new(0),
            #[cfg(test)]
            render_frame_v1_export_count: std::sync::atomic::AtomicUsize::new(0),
            #[cfg(test)]
            render_frame_v2_export_count: std::sync::atomic::AtomicUsize::new(0),
            #[cfg(test)]
            render_frame_v3_export_count: std::sync::atomic::AtomicUsize::new(0),
        }));
        // SAFETY: The output was validated above and remains writable.
        unsafe { ptr::write_unaligned(out_checkpoint, checkpoint) };
        STATUS_OK
    })
}

/// Destroys a checkpoint returned by this library.
///
/// # Safety
///
/// `checkpoint` must be null or a live checkpoint returned by this library. It
/// must not be destroyed more than once or while another thread accesses it.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_destroy(checkpoint: *mut TlTerminalCheckpoint) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !checkpoint.is_null() {
            // SAFETY: Required by this function's C ABI contract.
            drop(unsafe { Box::from_raw(checkpoint) });
        }
    }));
}

/// Writes the revision and next source sequence captured by a checkpoint.
///
/// # Safety
///
/// `checkpoint` must remain live for this call. `out_info` must have a readable
/// ABI prefix and be writable for its accepted declared size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_get_info(
    checkpoint: *const TlTerminalCheckpoint,
    out_info: *mut TlTerminalCheckpointInfo,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_info.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable and writable output.
        let capacity = match unsafe { output_capacity(out_info) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the checkpoint to remain live.
        let checkpoint = unsafe { &*checkpoint };
        let info = TlTerminalCheckpointInfo {
            abi_version: ABI_VERSION,
            struct_size: capacity,
            revision: checkpoint.revision,
            next_source_sequence: checkpoint.next_source_sequence,
            reserved: [0; 2],
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_info, info) };
        STATUS_OK
    })
}

/// Writes the retained active-screen scrollback row count and the runtime
/// monotonic row-capture generation for an immutable checkpoint.
///
/// # Safety
///
/// `checkpoint` must remain live. Both output pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_get_scrollback_info_v1(
    checkpoint: *const TlTerminalCheckpoint,
    out_available_rows: *mut u32,
    out_generation_rows: *mut u64,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_available_rows.is_null() || out_generation_rows.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires the checkpoint to remain live and
        // both scalar outputs to be writable for this call.
        let checkpoint = unsafe { &*checkpoint };
        let (available_rows, generation_rows) = checkpoint.frozen_terminal.scrollback_info();
        unsafe {
            ptr::write_unaligned(out_available_rows, available_rows);
            ptr::write_unaligned(out_generation_rows, generation_rows);
        }
        STATUS_OK
    })
}

/// Exports an owned canonical Snapshot V2 from an immutable checkpoint.
///
/// # Safety
///
/// `checkpoint` must remain live for this call. `out_snapshot` must be a
/// versioned, writable, empty buffer. On success it owns one buffer token that
/// must be released exactly once with `tl_terminal_buffer_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_export_snapshot(
    checkpoint: *const TlTerminalCheckpoint,
    out_snapshot: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_snapshot.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable and writable empty output.
        let capacity = match unsafe { empty_buffer_capacity(out_snapshot) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the checkpoint to remain live.
        let checkpoint = unsafe { &*checkpoint };
        let snapshot = match checkpoint.frozen_terminal.export_snapshot_v2() {
            Ok(snapshot) => snapshot,
            Err(error) => return status_for_error(&error),
        };
        #[cfg(test)]
        checkpoint
            .snapshot_export_count
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let buffer = match owned_buffer(snapshot, capacity) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_snapshot, buffer) };
        STATUS_OK
    })
}

/// Exports one owned renderer-only Render Frame V1 from an immutable checkpoint.
///
/// # Safety
///
/// `checkpoint` must remain live. `out_frame` must be a versioned, writable,
/// empty buffer. `max_frame_bytes` must be in 1..=the library hard maximum.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_export_render_frame_v1(
    checkpoint: *const TlTerminalCheckpoint,
    max_frame_bytes: u64,
    out_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        let requested = match checked_length(max_frame_bytes, MAX_RENDER_FRAME_V1_BYTES) {
            Ok(0) | Err(_) => return STATUS_INVALID_ARGUMENT,
            Ok(requested) => requested,
        };
        // SAFETY: The C contract requires a readable and writable empty output.
        let capacity = match unsafe { empty_buffer_capacity(out_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the checkpoint to remain live.
        let checkpoint = unsafe { &*checkpoint };
        let frame = match checkpoint.frozen_terminal.export_render_frame_v1() {
            Ok(frame) => frame,
            Err(error) => return status_for_error(&error),
        };
        #[cfg(test)]
        checkpoint
            .render_frame_v1_export_count
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        if frame.len() > requested {
            return STATUS_LIMIT_EXCEEDED;
        }
        let buffer = match owned_buffer_with_limit(frame, capacity, MAX_RENDER_FRAME_V1_BYTES) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_frame, buffer) };
        STATUS_OK
    })
}

/// Exports one owned renderer-only Render Frame V2 from an immutable checkpoint.
///
/// # Safety
///
/// `checkpoint` must remain live. `out_frame` must be a versioned, writable,
/// empty buffer. `max_frame_bytes` must be in 1..=the library hard maximum.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_export_render_frame_v2(
    checkpoint: *const TlTerminalCheckpoint,
    max_frame_bytes: u64,
    out_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        let requested = match checked_length(max_frame_bytes, MAX_RENDER_FRAME_V2_BYTES) {
            Ok(0) | Err(_) => return STATUS_INVALID_ARGUMENT,
            Ok(requested) => requested,
        };
        // SAFETY: The C contract requires a readable and writable empty output.
        let capacity = match unsafe { empty_buffer_capacity(out_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the checkpoint to remain live.
        let checkpoint = unsafe { &*checkpoint };
        let frame = match checkpoint.frozen_terminal.export_render_frame_v2() {
            Ok(frame) => frame,
            Err(error) => return status_for_error(&error),
        };
        #[cfg(test)]
        checkpoint
            .render_frame_v2_export_count
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        if frame.len() > requested {
            return STATUS_LIMIT_EXCEEDED;
        }
        let buffer = match owned_buffer_with_limit(frame, capacity, MAX_RENDER_FRAME_V2_BYTES) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_frame, buffer) };
        STATUS_OK
    })
}

/// Exports one owned renderer-only Render Frame V3 from an immutable checkpoint.
///
/// # Safety
///
/// `checkpoint` must remain live. `out_frame` must be a versioned, writable,
/// empty buffer. `max_frame_bytes` must be in 1..=the library hard maximum.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_export_render_frame_v3(
    checkpoint: *const TlTerminalCheckpoint,
    max_frame_bytes: u64,
    out_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        let requested = match checked_length(max_frame_bytes, MAX_RENDER_FRAME_V3_BYTES) {
            Ok(0) | Err(_) => return STATUS_INVALID_ARGUMENT,
            Ok(requested) => requested,
        };
        // SAFETY: The C contract requires a readable and writable empty output.
        let capacity = match unsafe { empty_buffer_capacity(out_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the checkpoint to remain live.
        let checkpoint = unsafe { &*checkpoint };
        let frame = match checkpoint.frozen_terminal.export_render_frame_v3() {
            Ok(frame) => frame,
            Err(error) => return status_for_error(&error),
        };
        #[cfg(test)]
        checkpoint
            .render_frame_v3_export_count
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        if frame.len() > requested {
            return STATUS_LIMIT_EXCEEDED;
        }
        let buffer = match owned_buffer_with_limit(frame, capacity, MAX_RENDER_FRAME_V3_BYTES) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_frame, buffer) };
        STATUS_OK
    })
}

/// Exports one owned renderer-only Render Frame V3 at a retained scrollback
/// offset. Offsets beyond the oldest retained row clamp to that boundary.
///
/// # Safety
///
/// `checkpoint` must remain live. `out_frame` must be a versioned, writable,
/// empty buffer. `max_frame_bytes` must be in 1..=the library hard maximum.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_export_render_frame_v3_at_scrollback(
    checkpoint: *const TlTerminalCheckpoint,
    offset_rows: u32,
    max_frame_bytes: u64,
    out_frame: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_frame.is_null() {
            return STATUS_NULL_POINTER;
        }
        let requested = match checked_length(max_frame_bytes, MAX_RENDER_FRAME_V3_BYTES) {
            Ok(0) | Err(_) => return STATUS_INVALID_ARGUMENT,
            Ok(requested) => requested,
        };
        // SAFETY: The C contract requires a readable and writable empty output.
        let capacity = match unsafe { empty_buffer_capacity(out_frame) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract requires the checkpoint to remain live.
        let checkpoint = unsafe { &*checkpoint };
        let frame = match checkpoint
            .frozen_terminal
            .export_render_frame_v3_at_scrollback(offset_rows)
        {
            Ok(frame) => frame,
            Err(error) => return status_for_error(&error),
        };
        #[cfg(test)]
        checkpoint
            .render_frame_v3_export_count
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        if frame.len() > requested {
            return STATUS_LIMIT_EXCEEDED;
        }
        let buffer = match owned_buffer_with_limit(frame, capacity, MAX_RENDER_FRAME_V3_BYTES) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_frame, buffer) };
        STATUS_OK
    })
}

/// Imports a terminal from a canonical Snapshot V2 byte region.
///
/// # Safety
///
/// `snapshot` must be readable for `snapshot_len` bytes. `out_terminal` must be
/// writable and contain null. On success it receives a live terminal handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_import_snapshot(
    snapshot: *const u8,
    snapshot_len: u64,
    out_terminal: *mut *mut TlTerminal,
) -> u32 {
    catch_status(|| {
        if out_terminal.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a writable pointer-sized output.
        if let Err(status) = unsafe { require_empty_handle(out_terminal) } {
            return status;
        }
        let bytes = match borrowed_bytes(snapshot, snapshot_len, MAX_SNAPSHOT_BYTES) {
            Ok(bytes) => bytes,
            Err(status) => return status,
        };
        match Terminal::import_snapshot_v2(bytes) {
            Ok(inner) => {
                let terminal = Box::into_raw(Box::new(TlTerminal { inner }));
                // SAFETY: The output was validated above and remains writable.
                unsafe { ptr::write_unaligned(out_terminal, terminal) };
                STATUS_OK
            }
            Err(error) => status_for_error(&error),
        }
    })
}

/// Releases one live buffer token and clears its ownership fields in place.
///
/// # Safety
///
/// `buffer` must be a writable buffer structure holding a live token returned
/// by this library. Freeing any copy invalidates every copy of that token.
/// Calling this function again with an invalidated copy is a caller violation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_buffer_free(buffer: *mut TlTerminalBuffer) -> u32 {
    catch_status(|| {
        if buffer.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned buffer.
        let value = match unsafe { read_versioned(buffer.cast_const()) } {
            Ok(value) => value,
            Err(status) => return status,
        };
        #[cfg(target_pointer_width = "32")]
        if value.data_pointer_padding != 0 {
            return STATUS_INVALID_ARGUMENT;
        }
        if value.data.is_null() {
            return STATUS_INVALID_ARGUMENT;
        }
        let len = match checked_length(value.len, MAX_HOST_EFFECT_FRAME_V2_BYTES) {
            Ok(len) => len,
            Err(status) => return status,
        };
        let key = value.data.addr();
        let mut registry = buffer_registry()
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let Some(token) = registry.get(&key).copied() else {
            return STATUS_INVALID_ARGUMENT;
        };
        if token.len != len || token.struct_size != value.struct_size {
            return STATUS_INVALID_ARGUMENT;
        }
        registry.remove(&key);
        drop(registry);

        let slice = ptr::slice_from_raw_parts_mut(value.data.cast_mut(), token.allocation_len);
        // SAFETY: The registry proves this exact pointer and length are live.
        drop(unsafe { Box::from_raw(slice) });
        let empty = TlTerminalBuffer {
            abi_version: ABI_VERSION,
            struct_size: value.struct_size,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            len: 0,
        };
        // SAFETY: The C contract requires the input structure to be writable.
        unsafe { ptr::write_unaligned(buffer, empty) };
        STATUS_OK
    })
}

/// Reserved checkpoint delta entry point. It currently returns unsupported.
///
/// # Safety
///
/// `checkpoint` must be live and `out_delta` must be a versioned, writable,
/// empty buffer. Neither object is modified.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_checkpoint_export_delta(
    checkpoint: *const TlTerminalCheckpoint,
    out_delta: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if checkpoint.is_null() || out_delta.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: Validate the output without overwriting it.
        if let Err(status) = unsafe { empty_buffer_capacity(out_delta) } {
            return status;
        }
        STATUS_UNSUPPORTED
    })
}

/// Encodes one versioned semantic input event using current terminal modes.
///
/// # Safety
///
/// `terminal` must be live and exclusively accessible for this call. `input`
/// must have a readable ABI prefix and be readable for its accepted declared
/// size. A paste byte region must remain readable for this call. `out_encoded`
/// must be a versioned, writable, empty buffer and owns the successful result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_encode_input_v1(
    terminal: *const TlTerminal,
    input: *const TlTerminalInputV1,
    out_encoded: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || input.is_null() || out_encoded.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned input event.
        let input = match unsafe { read_versioned(input) } {
            Ok(input) => input,
            Err(status) => return status,
        };
        if input.reserved != [0; 2] || input.modifiers & !INPUT_MODIFIERS_ALL != 0 {
            return STATUS_INVALID_ARGUMENT;
        }
        // SAFETY: Validate the output without overwriting it.
        let capacity = match unsafe { empty_buffer_capacity(out_encoded) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        let event = match decode_input_event(&input) {
            Ok(event) => event,
            Err(status) => return status,
        };
        // SAFETY: The C contract excludes concurrent terminal access.
        let terminal = unsafe { &*terminal };
        let encoded = match terminal.inner.encode_input(event) {
            Ok(encoded) => encoded,
            Err(error) => return status_for_input_error(&error),
        };
        let buffer = match owned_buffer(encoded, capacity) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_encoded, buffer) };
        STATUS_OK
    })
}

/// Encodes one additive V2 semantic or Win32 key input event.
///
/// # Safety
///
/// The pointer, terminal access, paste payload, and output ownership
/// requirements are identical to `tl_terminal_encode_input_v1`. Unknown
/// oversized input and output structure tails are ignored.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_encode_input_v2(
    terminal: *const TlTerminal,
    input: *const TlTerminalInputV2,
    out_encoded: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || input.is_null() || out_encoded.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned V2 input event.
        let input = match unsafe { read_versioned(input) } {
            Ok(input) => input,
            Err(status) => return status,
        };
        if input.reserved_v1_compat != [0; 2]
            || input.reserved != [0; 2]
            || input.modifiers & !INPUT_MODIFIERS_ALL != 0
        {
            return STATUS_INVALID_ARGUMENT;
        }
        // SAFETY: Validate the output without overwriting it.
        let capacity = match unsafe { empty_buffer_capacity(out_encoded) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        let event = match decode_input_event_v2(&input) {
            Ok(event) => event,
            Err(status) => return status,
        };
        // SAFETY: The C contract excludes concurrent terminal access.
        let terminal = unsafe { &*terminal };
        let encoded = match terminal.inner.encode_input(event) {
            Ok(encoded) => encoded,
            Err(error) => return status_for_input_error(&error),
        };
        let buffer = match owned_buffer(encoded, capacity) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_encoded, buffer) };
        STATUS_OK
    })
}

/// Encodes one additive V3 semantic, Win32, or Kitty key input event.
///
/// # Safety
///
/// The pointer, terminal access, borrowed text, and output ownership
/// requirements are identical to earlier semantic input entry points. A
/// non-empty associated-text region must remain readable for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_encode_input_v3(
    terminal: *const TlTerminal,
    input: *const TlTerminalInputV3,
    out_encoded: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || input.is_null() || out_encoded.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned V3 input event.
        let input = match unsafe { read_versioned(input) } {
            Ok(input) => input,
            Err(status) => return status,
        };
        // SAFETY: Validate the output without overwriting it.
        let capacity = match unsafe { empty_buffer_capacity(out_encoded) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        let event = match decode_input_event_v3(&input) {
            Ok(event) => event,
            Err(status) => return status,
        };
        // SAFETY: The C contract excludes concurrent terminal access.
        let terminal = unsafe { &*terminal };
        let encoded = match terminal.inner.encode_input(event) {
            Ok(encoded) => encoded,
            Err(error) => return status_for_input_error(&error),
        };
        let buffer = match owned_buffer(encoded, capacity) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_encoded, buffer) };
        STATUS_OK
    })
}

/// Encodes one versioned semantic mouse event using current terminal modes.
///
/// # Safety
///
/// `terminal` must be live and exclusively accessible for this call. `input`
/// must have a readable ABI prefix and be readable for its accepted declared
/// size. `out_encoded` must be a versioned, writable, empty buffer and owns the
/// successful result, including a successful zero-length result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_encode_mouse_input_v1(
    terminal: *const TlTerminal,
    input: *const TlTerminalMouseInputV1,
    out_encoded: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || input.is_null() || out_encoded.is_null() {
            return STATUS_NULL_POINTER;
        }
        // SAFETY: The C contract requires a readable versioned mouse event.
        let input = match unsafe { read_versioned(input) } {
            Ok(input) => input,
            Err(status) => return status,
        };
        let event = match decode_mouse_input_event(&input) {
            Ok(event) => event,
            Err(status) => return status,
        };
        // SAFETY: Validate the output without overwriting it.
        let capacity = match unsafe { empty_buffer_capacity(out_encoded) } {
            Ok(capacity) => capacity,
            Err(status) => return status,
        };
        // SAFETY: The C contract excludes concurrent terminal access.
        let terminal = unsafe { &*terminal };
        let encoded = match terminal.inner.encode_input(InputEvent::Mouse(event)) {
            Ok(encoded) => encoded,
            Err(error) => return status_for_input_error(&error),
        };
        let buffer = match owned_buffer(encoded, capacity) {
            Ok(buffer) => buffer,
            Err(status) => return status,
        };
        // SAFETY: The capacity check established writable known fields.
        unsafe { ptr::write_unaligned(out_encoded, buffer) };
        STATUS_OK
    })
}

/// Reserved input encoding entry point. It currently returns unsupported.
///
/// # Safety
///
/// `terminal` must be live, `input` must be readable for `input_len` bytes,
/// and `out_encoded` must be a versioned, writable, empty buffer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tl_terminal_encode_input(
    terminal: *const TlTerminal,
    input: *const u8,
    input_len: u64,
    out_encoded: *mut TlTerminalBuffer,
) -> u32 {
    catch_status(|| {
        if terminal.is_null() || out_encoded.is_null() {
            return STATUS_NULL_POINTER;
        }
        if let Err(status) = validate_byte_region(input, input_len, MAX_OUTPUT_BYTES) {
            return status;
        }
        // SAFETY: Validate the output without overwriting it.
        if let Err(status) = unsafe { empty_buffer_capacity(out_encoded) } {
            return status;
        }
        STATUS_UNSUPPORTED
    })
}

fn decode_terminal_event<'a>(event: &TlTerminalEvent) -> Result<Event<'a>, u32> {
    #[cfg(target_pointer_width = "32")]
    if event.data_pointer_padding != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    if event.reserved != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    match event.kind {
        EVENT_OUTPUT => {
            if event.columns != 0 || event.rows != 0 || event.advance_micros != 0 {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            let bytes = borrowed_bytes(event.data, event.data_len, MAX_OUTPUT_BYTES)?;
            Ok(Event::Output {
                source_sequence: event.source_sequence,
                bytes,
            })
        }
        EVENT_RESIZE => {
            if !event.data.is_null() || event.data_len != 0 || event.advance_micros != 0 {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            Ok(Event::Resize {
                source_sequence: event.source_sequence,
                columns: event.columns,
                rows: event.rows,
            })
        }
        EVENT_ADVANCE => {
            if !event.data.is_null() || event.data_len != 0 || event.columns != 0 || event.rows != 0
            {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            Ok(Event::Advance {
                source_sequence: event.source_sequence,
                micros: event.advance_micros,
            })
        }
        EVENT_GAP => {
            if !event.data.is_null()
                || event.data_len != 0
                || event.columns != 0
                || event.rows != 0
                || event.advance_micros != 0
            {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            Ok(Event::Gap {
                source_sequence: event.source_sequence,
            })
        }
        _ => Err(STATUS_INVALID_ARGUMENT),
    }
}

unsafe fn decode_terminal_event_batch<'a>(
    events: *const TlTerminalEvent,
    event_count: u32,
) -> Result<Vec<Event<'a>>, u32> {
    if events.is_null() {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    let event_count = match usize::try_from(event_count) {
        Ok(count @ 1..=MAX_EVENT_BATCH_COUNT) => count,
        Ok(_) => return Err(STATUS_INVALID_ARGUMENT),
        Err(_) => return Err(STATUS_LIMIT_EXCEEDED),
    };
    let event_size = mem::size_of::<TlTerminalEvent>();
    let array_len = event_count
        .checked_mul(event_size)
        .filter(|&len| len <= isize::MAX as usize)
        .ok_or(STATUS_LIMIT_EXCEEDED)?;
    debug_assert!(array_len >= event_size);

    let mut decoded = Vec::new();
    decoded
        .try_reserve_exact(event_count)
        .map_err(|_| STATUS_LIMIT_EXCEEDED)?;
    let mut total_output_bytes = 0_usize;
    for index in 0..event_count {
        let offset = index
            .checked_mul(event_size)
            .filter(|&offset| offset < array_len)
            .ok_or(STATUS_LIMIT_EXCEEDED)?;
        // SAFETY: The batch contract requires a readable contiguous array
        // with the exact fixed stride checked above. Unaligned reads avoid
        // imposing an additional alignment requirement on the array base.
        let event = unsafe {
            ptr::read_unaligned(events.cast::<u8>().add(offset).cast::<TlTerminalEvent>())
        };
        if event.abi_version != ABI_VERSION || event.struct_size != struct_size::<TlTerminalEvent>()
        {
            return Err(STATUS_INVALID_ARGUMENT);
        }
        if event.kind == EVENT_OUTPUT {
            if event.data.is_null() || event.data_len == 0 {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            let output_len = checked_length(event.data_len, MAX_OUTPUT_BYTES)?;
            total_output_bytes = total_output_bytes
                .checked_add(output_len)
                .filter(|&total| total <= MAX_EVENT_BATCH_OUTPUT_BYTES)
                .ok_or(STATUS_LIMIT_EXCEEDED)?;
        }
        let core_event = decode_terminal_event(&event).map_err(|status| {
            if status == STATUS_NULL_POINTER {
                STATUS_INVALID_ARGUMENT
            } else {
                status
            }
        })?;
        decoded.push(core_event);
    }
    Ok(decoded)
}

unsafe fn publish_prepared_host_effects(
    prepared: PreparedApplyWithHostEffectsV1<'_>,
    struct_capacity: u32,
    out_effect_frame: *mut TlTerminalBuffer,
) -> u32 {
    let mut pending = None;
    let committed = prepared.commit_with(|frame| {
        pending = Some(PendingOwnedBuffer::new(
            frame,
            struct_capacity,
            MAX_HOST_EFFECT_FRAME_V1_BYTES,
        )?);
        // This test-only gate exercises unwind and ordinary publisher failure
        // after token registration. No production build contains a switch.
        #[cfg(test)]
        pending_buffer_test_gate()?;
        Ok(())
    });
    if let Err(status) = committed {
        return status;
    }
    let Some(pending) = pending else {
        return STATUS_PANIC;
    };
    let buffer = pending.buffer;
    // SAFETY: The caller established writable known fields and the pending
    // token remains guarded until immediately after this unique publication.
    unsafe { ptr::write_unaligned(out_effect_frame, buffer) };
    pending.disarm();
    STATUS_OK
}

unsafe fn publish_prepared_host_effects_v2(
    prepared: PreparedApplyWithHostEffectsV2<'_>,
    struct_capacity: u32,
    out_effect_frame: *mut TlTerminalBuffer,
) -> u32 {
    let mut pending = None;
    let committed = prepared.commit_with(|frame| {
        pending = Some(PendingOwnedBuffer::new(
            frame,
            struct_capacity,
            MAX_HOST_EFFECT_FRAME_V2_BYTES,
        )?);
        // This test-only gate exercises unwind and ordinary publisher failure
        // after token registration. No production build contains a switch.
        #[cfg(test)]
        pending_buffer_test_gate()?;
        Ok(())
    });
    if let Err(status) = committed {
        return status;
    }
    let Some(pending) = pending else {
        return STATUS_PANIC;
    };
    let buffer = pending.buffer;
    // SAFETY: The caller established writable known fields and the pending
    // token remains guarded until immediately after this unique publication.
    unsafe { ptr::write_unaligned(out_effect_frame, buffer) };
    pending.disarm();
    STATUS_OK
}

fn decode_input_event<'a>(input: &TlTerminalInputV1) -> Result<InputEvent<'a>, u32> {
    #[cfg(target_pointer_width = "32")]
    if input.data_pointer_padding != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    decode_v1_input_fields(
        input.kind,
        input.key,
        input.modifiers,
        input.character,
        input.data,
        input.data_len,
        input.reserved,
    )
}

fn decode_v1_input_fields<'a>(
    kind: u32,
    key: u32,
    modifiers: u32,
    character: u32,
    data: *const u8,
    data_len: u64,
    reserved: [u64; 2],
) -> Result<InputEvent<'a>, u32> {
    if reserved != [0; 2] || modifiers & !INPUT_MODIFIERS_ALL != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    let decoded_modifiers = decode_modifiers(modifiers);
    match kind {
        INPUT_KIND_KEY => {
            if !data.is_null() || data_len != 0 {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            let key = decode_key(key, character)?;
            Ok(InputEvent::Key(KeyEvent {
                key,
                modifiers: decoded_modifiers,
            }))
        }
        INPUT_KIND_PASTE => {
            if key != 0 || modifiers != 0 || character != 0 {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            let bytes = borrowed_bytes(data, data_len, MAX_PASTE_BYTES)?;
            Ok(InputEvent::Paste(PasteEvent { bytes }))
        }
        INPUT_KIND_FOCUS_GAINED => {
            require_empty_focus_payload(key, modifiers, character, data, data_len, reserved)?;
            Ok(InputEvent::FocusGained)
        }
        INPUT_KIND_FOCUS_LOST => {
            require_empty_focus_payload(key, modifiers, character, data, data_len, reserved)?;
            Ok(InputEvent::FocusLost)
        }
        _ => Err(STATUS_INVALID_ARGUMENT),
    }
}

fn decode_input_event_v2<'a>(input: &TlTerminalInputV2) -> Result<InputEvent<'a>, u32> {
    #[cfg(target_pointer_width = "32")]
    if input.data_pointer_padding != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    if input.reserved_v1_compat != [0; 2]
        || input.reserved != [0; 2]
        || input.modifiers & !INPUT_MODIFIERS_ALL != 0
    {
        return Err(STATUS_INVALID_ARGUMENT);
    }

    if input.kind != INPUT_KIND_WIN32_KEY {
        if input.win32_virtual_key != 0
            || input.win32_scan_code != 0
            || input.win32_unicode_char != 0
            || input.win32_key_down != 0
            || input.win32_control_key_state != 0
            || input.win32_repeat_count != 0
        {
            return Err(STATUS_INVALID_ARGUMENT);
        }
        return decode_v1_input_fields(
            input.kind,
            input.key,
            input.modifiers,
            input.character,
            input.data,
            input.data_len,
            input.reserved_v1_compat,
        );
    }

    if input.key != 0
        || input.modifiers != 0
        || input.character != 0
        || !input.data.is_null()
        || input.data_len != 0
        || input.reserved_v1_compat != [0; 2]
    {
        return Err(STATUS_INVALID_ARGUMENT);
    }

    let virtual_key =
        u16::try_from(input.win32_virtual_key).map_err(|_| STATUS_INVALID_ARGUMENT)?;
    let scan_code = u16::try_from(input.win32_scan_code).map_err(|_| STATUS_INVALID_ARGUMENT)?;
    let unicode_char =
        u16::try_from(input.win32_unicode_char).map_err(|_| STATUS_INVALID_ARGUMENT)?;
    let key_down = match input.win32_key_down {
        0 => false,
        1 => true,
        _ => return Err(STATUS_INVALID_ARGUMENT),
    };
    let repeat_count =
        u16::try_from(input.win32_repeat_count).map_err(|_| STATUS_INVALID_ARGUMENT)?;
    Ok(InputEvent::Win32Key(Win32KeyEvent {
        virtual_key,
        scan_code,
        unicode_char,
        key_down,
        control_key_state: input.win32_control_key_state,
        repeat_count,
    }))
}

fn decode_input_event_v3<'a>(input: &TlTerminalInputV3) -> Result<InputEvent<'a>, u32> {
    #[cfg(target_pointer_width = "32")]
    if input.data_pointer_padding != 0 || input.associated_text_pointer_padding != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    if input.kind != INPUT_KIND_KITTY_KEY {
        if input.key_event_type != 0
            || input.keyboard_modifiers != 0
            || input.shifted_key != 0
            || input.base_layout_key != 0
            || !input.associated_text.is_null()
            || input.associated_text_len != 0
            || input.reserved_v3 != [0; 4]
        {
            return Err(STATUS_INVALID_ARGUMENT);
        }
        let prefix = TlTerminalInputV2 {
            abi_version: input.abi_version,
            struct_size: input.struct_size,
            kind: input.kind,
            key: input.key,
            modifiers: input.modifiers,
            character: input.character,
            data: input.data,
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: input.data_pointer_padding,
            data_len: input.data_len,
            reserved_v1_compat: input.reserved_v1_compat,
            win32_virtual_key: input.win32_virtual_key,
            win32_scan_code: input.win32_scan_code,
            win32_unicode_char: input.win32_unicode_char,
            win32_key_down: input.win32_key_down,
            win32_control_key_state: input.win32_control_key_state,
            win32_repeat_count: input.win32_repeat_count,
            reserved: input.reserved,
        };
        return decode_input_event_v2(&prefix);
    }

    if input.modifiers != 0
        || !input.data.is_null()
        || input.data_len != 0
        || input.reserved_v1_compat != [0; 2]
        || input.win32_virtual_key != 0
        || input.win32_scan_code != 0
        || input.win32_unicode_char != 0
        || input.win32_key_down != 0
        || input.win32_control_key_state != 0
        || input.win32_repeat_count != 0
        || input.reserved != [0; 2]
        || input.reserved_v3 != [0; 4]
        || input.keyboard_modifiers & !KEYBOARD_MODIFIERS_ALL != 0
    {
        return Err(STATUS_INVALID_ARGUMENT);
    }

    let event_type = match input.key_event_type {
        INPUT_KEY_EVENT_PRESS => KittyKeyEventType::Press,
        INPUT_KEY_EVENT_REPEAT => KittyKeyEventType::Repeat,
        INPUT_KEY_EVENT_RELEASE => KittyKeyEventType::Release,
        _ => return Err(STATUS_INVALID_ARGUMENT),
    };
    let key = decode_kitty_key(input.key, input.character)?;
    let shifted_key = decode_optional_scalar(input.shifted_key)?;
    let base_layout_key = decode_optional_scalar(input.base_layout_key)?;
    let associated_text = decode_associated_text(input.associated_text, input.associated_text_len)?;
    let modifiers = input.keyboard_modifiers;
    Ok(InputEvent::KittyKey(KittyKeyEvent {
        key,
        modifiers: KittyModifiers {
            shift: modifiers & KEYBOARD_MODIFIER_SHIFT != 0,
            alt: modifiers & KEYBOARD_MODIFIER_ALT != 0,
            control: modifiers & KEYBOARD_MODIFIER_CONTROL != 0,
            super_key: modifiers & KEYBOARD_MODIFIER_SUPER != 0,
            hyper: modifiers & KEYBOARD_MODIFIER_HYPER != 0,
            meta: modifiers & KEYBOARD_MODIFIER_META != 0,
            caps_lock: modifiers & KEYBOARD_MODIFIER_CAPS_LOCK != 0,
            num_lock: modifiers & KEYBOARD_MODIFIER_NUM_LOCK != 0,
        },
        event_type,
        shifted_key,
        base_layout_key,
        associated_text,
    }))
}

fn decode_optional_scalar(value: u32) -> Result<Option<char>, u32> {
    if value == 0 {
        Ok(None)
    } else {
        char::from_u32(value)
            .map(Some)
            .ok_or(STATUS_INVALID_ARGUMENT)
    }
}

fn decode_associated_text<'a>(data: *const u8, len: u64) -> Result<&'a str, u32> {
    if data.is_null() != (len == 0) {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    let len = checked_length(len, MAX_KITTY_ASSOCIATED_TEXT_BYTES)?;
    if len == 0 {
        return Ok("");
    }
    // SAFETY: The C contract requires the canonical non-empty region to remain
    // readable for the call; its bounded length was validated above.
    let bytes = unsafe { std::slice::from_raw_parts(data, len) };
    std::str::from_utf8(bytes).map_err(|_| STATUS_INVALID_ARGUMENT)
}

fn decode_kitty_key(key: u32, character: u32) -> Result<KittyKey, u32> {
    if key == INPUT_KEY_CHARACTER {
        return char::from_u32(character)
            .map(KittyKey::Character)
            .ok_or(STATUS_INVALID_ARGUMENT);
    }
    if character != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    if key == INPUT_KEY_UNIDENTIFIED_TEXT {
        return Ok(KittyKey::UnidentifiedText);
    }
    if let Some(function) = decode_common_kitty_function(key) {
        return Ok(KittyKey::Functional(function));
    }
    let index = key
        .checked_sub(INPUT_KEY_KITTY_PUA_FIRST)
        .and_then(|value| usize::try_from(value).ok())
        .filter(|&value| value < KITTY_PUA_FUNCTIONS.len())
        .ok_or(STATUS_INVALID_ARGUMENT)?;
    Ok(KittyKey::Functional(KITTY_PUA_FUNCTIONS[index]))
}

fn decode_common_kitty_function(key: u32) -> Option<KittyFunctionalKey> {
    Some(match key {
        INPUT_KEY_ENTER => KittyFunctionalKey::Enter,
        INPUT_KEY_BACKSPACE => KittyFunctionalKey::Backspace,
        INPUT_KEY_TAB => KittyFunctionalKey::Tab,
        INPUT_KEY_ESCAPE => KittyFunctionalKey::Escape,
        INPUT_KEY_DELETE => KittyFunctionalKey::Delete,
        INPUT_KEY_INSERT => KittyFunctionalKey::Insert,
        INPUT_KEY_HOME => KittyFunctionalKey::Home,
        INPUT_KEY_END => KittyFunctionalKey::End,
        INPUT_KEY_PAGE_UP => KittyFunctionalKey::PageUp,
        INPUT_KEY_PAGE_DOWN => KittyFunctionalKey::PageDown,
        INPUT_KEY_ARROW_UP => KittyFunctionalKey::ArrowUp,
        INPUT_KEY_ARROW_DOWN => KittyFunctionalKey::ArrowDown,
        INPUT_KEY_ARROW_LEFT => KittyFunctionalKey::ArrowLeft,
        INPUT_KEY_ARROW_RIGHT => KittyFunctionalKey::ArrowRight,
        INPUT_KEY_F1 => KittyFunctionalKey::F1,
        INPUT_KEY_F2 => KittyFunctionalKey::F2,
        INPUT_KEY_F3 => KittyFunctionalKey::F3,
        INPUT_KEY_F4 => KittyFunctionalKey::F4,
        INPUT_KEY_F5 => KittyFunctionalKey::F5,
        INPUT_KEY_F6 => KittyFunctionalKey::F6,
        INPUT_KEY_F7 => KittyFunctionalKey::F7,
        INPUT_KEY_F8 => KittyFunctionalKey::F8,
        INPUT_KEY_F9 => KittyFunctionalKey::F9,
        INPUT_KEY_F10 => KittyFunctionalKey::F10,
        INPUT_KEY_F11 => KittyFunctionalKey::F11,
        INPUT_KEY_F12 => KittyFunctionalKey::F12,
        INPUT_KEY_KEYPAD_0 => KittyFunctionalKey::Keypad0,
        INPUT_KEY_KEYPAD_1 => KittyFunctionalKey::Keypad1,
        INPUT_KEY_KEYPAD_2 => KittyFunctionalKey::Keypad2,
        INPUT_KEY_KEYPAD_3 => KittyFunctionalKey::Keypad3,
        INPUT_KEY_KEYPAD_4 => KittyFunctionalKey::Keypad4,
        INPUT_KEY_KEYPAD_5 => KittyFunctionalKey::Keypad5,
        INPUT_KEY_KEYPAD_6 => KittyFunctionalKey::Keypad6,
        INPUT_KEY_KEYPAD_7 => KittyFunctionalKey::Keypad7,
        INPUT_KEY_KEYPAD_8 => KittyFunctionalKey::Keypad8,
        INPUT_KEY_KEYPAD_9 => KittyFunctionalKey::Keypad9,
        INPUT_KEY_KEYPAD_DECIMAL => KittyFunctionalKey::KeypadDecimal,
        INPUT_KEY_KEYPAD_SEPARATOR => KittyFunctionalKey::KeypadSeparator,
        INPUT_KEY_KEYPAD_ADD => KittyFunctionalKey::KeypadAdd,
        INPUT_KEY_KEYPAD_SUBTRACT => KittyFunctionalKey::KeypadSubtract,
        INPUT_KEY_KEYPAD_MULTIPLY => KittyFunctionalKey::KeypadMultiply,
        INPUT_KEY_KEYPAD_DIVIDE => KittyFunctionalKey::KeypadDivide,
        INPUT_KEY_KEYPAD_EQUAL => KittyFunctionalKey::KeypadEqual,
        INPUT_KEY_KEYPAD_ENTER => KittyFunctionalKey::KeypadEnter,
        _ => return None,
    })
}

const KITTY_PUA_FUNCTIONS: [KittyFunctionalKey; 97] = [
    KittyFunctionalKey::CapsLock,
    KittyFunctionalKey::ScrollLock,
    KittyFunctionalKey::NumLock,
    KittyFunctionalKey::PrintScreen,
    KittyFunctionalKey::Pause,
    KittyFunctionalKey::Menu,
    KittyFunctionalKey::F1,
    KittyFunctionalKey::F2,
    KittyFunctionalKey::F3,
    KittyFunctionalKey::F4,
    KittyFunctionalKey::F5,
    KittyFunctionalKey::F6,
    KittyFunctionalKey::F7,
    KittyFunctionalKey::F8,
    KittyFunctionalKey::F9,
    KittyFunctionalKey::F10,
    KittyFunctionalKey::F11,
    KittyFunctionalKey::F12,
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
];
const _: () = assert!(
    KITTY_PUA_FUNCTIONS.len()
        == (INPUT_KEY_KITTY_PUA_LAST - INPUT_KEY_KITTY_PUA_FIRST + 1) as usize
);

fn decode_mouse_input_event(input: &TlTerminalMouseInputV1) -> Result<MouseEvent, u32> {
    if input.reserved != [0; 2]
        || input.modifiers & !INPUT_MODIFIERS_ALL != 0
        || input.buttons_held & !MOUSE_BUTTONS_HELD_ALL != 0
        || input.position_flags & !MOUSE_POSITION_FLAGS_ALL != 0
    {
        return Err(STATUS_INVALID_ARGUMENT);
    }

    let action = match input.action {
        MOUSE_ACTION_PRESS => {
            if input.wheel != MOUSE_WHEEL_NONE {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            MouseAction::Press(decode_mouse_button(input.button)?)
        }
        MOUSE_ACTION_RELEASE => {
            if input.wheel != MOUSE_WHEEL_NONE {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            MouseAction::Release(decode_mouse_button(input.button)?)
        }
        MOUSE_ACTION_MOVE => {
            if input.button != MOUSE_BUTTON_NONE || input.wheel != MOUSE_WHEEL_NONE {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            MouseAction::Move
        }
        MOUSE_ACTION_WHEEL => {
            if input.button != MOUSE_BUTTON_NONE {
                return Err(STATUS_INVALID_ARGUMENT);
            }
            MouseAction::Wheel(decode_mouse_wheel(input.wheel)?)
        }
        _ => return Err(STATUS_INVALID_ARGUMENT),
    };

    let pixel = if input.position_flags & MOUSE_POSITION_PIXEL != 0 {
        Some(PixelPosition {
            x: input.pixel_x,
            y: input.pixel_y,
        })
    } else {
        if input.pixel_x != 0 || input.pixel_y != 0 {
            return Err(STATUS_INVALID_ARGUMENT);
        }
        None
    };

    Ok(MouseEvent {
        action,
        position: MousePosition {
            cell: CellPosition {
                column: input.cell_column,
                row: input.cell_row,
            },
            pixel,
        },
        buttons_held: MouseButtons {
            left: input.buttons_held & MOUSE_BUTTON_HELD_LEFT != 0,
            middle: input.buttons_held & MOUSE_BUTTON_HELD_MIDDLE != 0,
            right: input.buttons_held & MOUSE_BUTTON_HELD_RIGHT != 0,
        },
        modifiers: decode_modifiers(input.modifiers),
    })
}

fn decode_mouse_button(button: u32) -> Result<MouseButton, u32> {
    match button {
        MOUSE_BUTTON_LEFT => Ok(MouseButton::Left),
        MOUSE_BUTTON_MIDDLE => Ok(MouseButton::Middle),
        MOUSE_BUTTON_RIGHT => Ok(MouseButton::Right),
        _ => Err(STATUS_INVALID_ARGUMENT),
    }
}

fn decode_mouse_wheel(wheel: u32) -> Result<MouseWheelDirection, u32> {
    match wheel {
        MOUSE_WHEEL_UP => Ok(MouseWheelDirection::Up),
        MOUSE_WHEEL_DOWN => Ok(MouseWheelDirection::Down),
        MOUSE_WHEEL_LEFT => Ok(MouseWheelDirection::Left),
        MOUSE_WHEEL_RIGHT => Ok(MouseWheelDirection::Right),
        _ => Err(STATUS_INVALID_ARGUMENT),
    }
}

const fn decode_modifiers(modifiers: u32) -> Modifiers {
    Modifiers {
        shift: modifiers & INPUT_MODIFIER_SHIFT != 0,
        alt: modifiers & INPUT_MODIFIER_ALT != 0,
        control: modifiers & INPUT_MODIFIER_CONTROL != 0,
        meta: modifiers & INPUT_MODIFIER_META != 0,
    }
}

fn require_empty_focus_payload(
    key: u32,
    modifiers: u32,
    character: u32,
    data: *const u8,
    data_len: u64,
    reserved: [u64; 2],
) -> Result<(), u32> {
    if key != 0
        || modifiers != 0
        || character != 0
        || !data.is_null()
        || data_len != 0
        || reserved != [0; 2]
    {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    Ok(())
}

fn decode_key(key: u32, character: u32) -> Result<Key, u32> {
    if key == INPUT_KEY_CHARACTER {
        return char::from_u32(character)
            .map(Key::Character)
            .ok_or(STATUS_INVALID_ARGUMENT);
    }
    if character != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    match key {
        INPUT_KEY_ENTER => Ok(Key::Enter),
        INPUT_KEY_BACKSPACE => Ok(Key::Backspace),
        INPUT_KEY_TAB => Ok(Key::Tab),
        INPUT_KEY_ESCAPE => Ok(Key::Escape),
        INPUT_KEY_DELETE => Ok(Key::Delete),
        INPUT_KEY_INSERT => Ok(Key::Insert),
        INPUT_KEY_HOME => Ok(Key::Home),
        INPUT_KEY_END => Ok(Key::End),
        INPUT_KEY_PAGE_UP => Ok(Key::PageUp),
        INPUT_KEY_PAGE_DOWN => Ok(Key::PageDown),
        INPUT_KEY_ARROW_UP => Ok(Key::ArrowUp),
        INPUT_KEY_ARROW_DOWN => Ok(Key::ArrowDown),
        INPUT_KEY_ARROW_LEFT => Ok(Key::ArrowLeft),
        INPUT_KEY_ARROW_RIGHT => Ok(Key::ArrowRight),
        INPUT_KEY_F1 => Ok(Key::F1),
        INPUT_KEY_F2 => Ok(Key::F2),
        INPUT_KEY_F3 => Ok(Key::F3),
        INPUT_KEY_F4 => Ok(Key::F4),
        INPUT_KEY_F5 => Ok(Key::F5),
        INPUT_KEY_F6 => Ok(Key::F6),
        INPUT_KEY_F7 => Ok(Key::F7),
        INPUT_KEY_F8 => Ok(Key::F8),
        INPUT_KEY_F9 => Ok(Key::F9),
        INPUT_KEY_F10 => Ok(Key::F10),
        INPUT_KEY_F11 => Ok(Key::F11),
        INPUT_KEY_F12 => Ok(Key::F12),
        INPUT_KEY_KEYPAD_0 => Ok(Key::Keypad(KeypadKey::Digit0)),
        INPUT_KEY_KEYPAD_1 => Ok(Key::Keypad(KeypadKey::Digit1)),
        INPUT_KEY_KEYPAD_2 => Ok(Key::Keypad(KeypadKey::Digit2)),
        INPUT_KEY_KEYPAD_3 => Ok(Key::Keypad(KeypadKey::Digit3)),
        INPUT_KEY_KEYPAD_4 => Ok(Key::Keypad(KeypadKey::Digit4)),
        INPUT_KEY_KEYPAD_5 => Ok(Key::Keypad(KeypadKey::Digit5)),
        INPUT_KEY_KEYPAD_6 => Ok(Key::Keypad(KeypadKey::Digit6)),
        INPUT_KEY_KEYPAD_7 => Ok(Key::Keypad(KeypadKey::Digit7)),
        INPUT_KEY_KEYPAD_8 => Ok(Key::Keypad(KeypadKey::Digit8)),
        INPUT_KEY_KEYPAD_9 => Ok(Key::Keypad(KeypadKey::Digit9)),
        INPUT_KEY_KEYPAD_DECIMAL => Ok(Key::Keypad(KeypadKey::Decimal)),
        INPUT_KEY_KEYPAD_SEPARATOR => Ok(Key::Keypad(KeypadKey::Separator)),
        INPUT_KEY_KEYPAD_ADD => Ok(Key::Keypad(KeypadKey::Add)),
        INPUT_KEY_KEYPAD_SUBTRACT => Ok(Key::Keypad(KeypadKey::Subtract)),
        INPUT_KEY_KEYPAD_MULTIPLY => Ok(Key::Keypad(KeypadKey::Multiply)),
        INPUT_KEY_KEYPAD_DIVIDE => Ok(Key::Keypad(KeypadKey::Divide)),
        INPUT_KEY_KEYPAD_EQUAL => Ok(Key::Keypad(KeypadKey::Equal)),
        INPUT_KEY_KEYPAD_ENTER => Ok(Key::Keypad(KeypadKey::Enter)),
        _ => Err(STATUS_INVALID_ARGUMENT),
    }
}

fn catch_status(function: impl FnOnce() -> u32) -> u32 {
    catch_unwind(AssertUnwindSafe(function)).unwrap_or(STATUS_PANIC)
}

fn catch_struct_size<T>() -> u32 {
    catch_unwind(struct_size::<T>).unwrap_or(0)
}

fn struct_size<T>() -> u32 {
    u32::try_from(mem::size_of::<T>()).expect("public structure size fits u32")
}

unsafe fn read_abi_prefix<T>(value: *const T) -> Result<AbiPrefix, u32> {
    if value.is_null() {
        return Err(STATUS_NULL_POINTER);
    }
    let bytes = value.cast::<u8>();
    // SAFETY: The ABI contract requires a readable eight-byte prefix. Unaligned
    // reads avoid imposing a stronger alignment contract for this first step.
    let abi_version = unsafe { ptr::read_unaligned(bytes.cast::<u32>()) };
    // SAFETY: The second u32 is within the required prefix.
    let declared_size = unsafe { ptr::read_unaligned(bytes.add(4).cast::<u32>()) };
    Ok(AbiPrefix {
        abi_version,
        struct_size: declared_size,
    })
}

unsafe fn read_versioned<T: Copy>(value: *const T) -> Result<T, u32> {
    // SAFETY: Forward the caller's readable-prefix contract.
    let prefix = unsafe { read_abi_prefix(value) }?;
    if prefix.abi_version != ABI_VERSION || prefix.struct_size < struct_size::<T>() {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    // SAFETY: Accepted struct_size promises all known fields are readable.
    Ok(unsafe { ptr::read_unaligned(value) })
}

unsafe fn read_versioned_exact<T: Copy>(value: *const T) -> Result<T, u32> {
    // SAFETY: Forward the caller's readable-prefix contract.
    let prefix = unsafe { read_abi_prefix(value) }?;
    if prefix.abi_version != ABI_VERSION || prefix.struct_size != struct_size::<T>() {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    // SAFETY: The exact accepted struct_size promises all fields are readable.
    Ok(unsafe { ptr::read_unaligned(value) })
}

unsafe fn output_capacity<T>(output: *mut T) -> Result<u32, u32> {
    // SAFETY: The output prefix must be readable before write.
    let prefix = unsafe { read_abi_prefix(output.cast_const()) }?;
    if prefix.abi_version != ABI_VERSION || prefix.struct_size < struct_size::<T>() {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    Ok(prefix.struct_size)
}

unsafe fn empty_buffer_capacity(output: *mut TlTerminalBuffer) -> Result<u32, u32> {
    // SAFETY: Known buffer fields must be readable before write.
    let buffer = unsafe { read_versioned(output.cast_const()) }?;
    #[cfg(target_pointer_width = "32")]
    if buffer.data_pointer_padding != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    if !buffer.data.is_null() || buffer.len != 0 {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    Ok(buffer.struct_size)
}

unsafe fn require_empty_handle<T>(output: *mut *mut T) -> Result<(), u32> {
    if output.is_null() {
        return Err(STATUS_NULL_POINTER);
    }
    // SAFETY: The ABI contract requires a readable pointer-sized output slot.
    if !unsafe { ptr::read_unaligned(output) }.is_null() {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    Ok(())
}

fn checked_length(len: u64, limit: usize) -> Result<usize, u32> {
    let max_isize = u64::try_from(isize::MAX).expect("isize::MAX fits u64");
    let limit = u64::try_from(limit).expect("operation limit fits u64");
    if len > limit || len > max_isize {
        return Err(STATUS_LIMIT_EXCEEDED);
    }
    usize::try_from(len).map_err(|_| STATUS_LIMIT_EXCEEDED)
}

fn validate_length(len: usize, limit: usize) -> Result<(), u32> {
    let len = u64::try_from(len).map_err(|_| STATUS_LIMIT_EXCEEDED)?;
    checked_length(len, limit).map(|_| ())
}

fn validate_byte_region(data: *const u8, len: u64, limit: usize) -> Result<usize, u32> {
    let len = checked_length(len, limit)?;
    if len != 0 && data.is_null() {
        return Err(STATUS_NULL_POINTER);
    }
    Ok(len)
}

fn borrowed_bytes<'a>(data: *const u8, len: u64, limit: usize) -> Result<&'a [u8], u32> {
    let len = validate_byte_region(data, len, limit)?;
    if len == 0 {
        return Ok(&[]);
    }
    // SAFETY: Limits were checked before slice creation; the caller guarantees
    // a readable region for the duration of the call.
    Ok(unsafe { std::slice::from_raw_parts(data, len) })
}

fn owned_buffer(bytes: Vec<u8>, struct_capacity: u32) -> Result<TlTerminalBuffer, u32> {
    owned_buffer_with_limit(bytes, struct_capacity, MAX_SNAPSHOT_BYTES)
}

fn owned_buffer_with_limit(
    bytes: Vec<u8>,
    struct_capacity: u32,
    limit: usize,
) -> Result<TlTerminalBuffer, u32> {
    PendingOwnedBuffer::new(bytes, struct_capacity, limit).map(PendingOwnedBuffer::publish)
}

impl PendingOwnedBuffer {
    fn new(bytes: Vec<u8>, struct_capacity: u32, limit: usize) -> Result<Self, u32> {
        validate_length(bytes.len(), limit)?;
        let len = bytes.len();
        let allocation = if bytes.is_empty() {
            let mut allocation = Vec::new();
            allocation
                .try_reserve_exact(1)
                .map_err(|_| STATUS_LIMIT_EXCEEDED)?;
            allocation.push(0);
            allocation.into_boxed_slice()
        } else {
            bytes.into_boxed_slice()
        };
        let allocation_len = allocation.len();
        let data = allocation.as_ptr();
        let token = BufferToken {
            len,
            allocation_len,
            struct_size: struct_capacity,
        };
        let mut pending = Self {
            buffer: TlTerminalBuffer {
                abi_version: ABI_VERSION,
                struct_size: struct_capacity,
                data,
                #[cfg(target_pointer_width = "32")]
                data_pointer_padding: 0,
                len: u64::try_from(len).expect("validated buffer length fits u64"),
            },
            allocation,
            registered: false,
        };
        let key = data.addr();
        let mut registry = buffer_registry()
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        registry.try_reserve(1).map_err(|_| STATUS_LIMIT_EXCEEDED)?;
        if registry.contains_key(&key) {
            return Err(STATUS_PANIC);
        }
        registry.insert(key, token);
        pending.registered = true;
        drop(registry);

        #[cfg(test)]
        PENDING_BUFFER_TEST_LAST_KEY.with(|last_key| last_key.set(key));
        Ok(pending)
    }

    fn publish(mut self) -> TlTerminalBuffer {
        let buffer = self.buffer;
        self.disarm_in_place();
        buffer
    }

    fn disarm(mut self) {
        self.disarm_in_place();
    }

    fn disarm_in_place(&mut self) {
        self.registered = false;
        mem::forget(mem::take(&mut self.allocation));
    }
}

impl Drop for PendingOwnedBuffer {
    fn drop(&mut self) {
        if self.registered {
            let key = self.buffer.data.addr();
            let mut registry = buffer_registry()
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let removed = registry.remove(&key);
            drop(registry);
            #[cfg(not(test))]
            let _ = removed;
            #[cfg(test)]
            if removed.is_some() {
                PENDING_BUFFER_TEST_ROLLED_BACK_KEY.with(|rolled_back| rolled_back.set(key));
            }
        }
    }
}

#[cfg(test)]
std::thread_local! {
    static PENDING_BUFFER_TEST_MODE: std::cell::Cell<u8> = const { std::cell::Cell::new(0) };
    static PENDING_BUFFER_TEST_LAST_KEY: std::cell::Cell<usize> = const { std::cell::Cell::new(0) };
    static PENDING_BUFFER_TEST_ROLLED_BACK_KEY: std::cell::Cell<usize> = const { std::cell::Cell::new(0) };
}

#[cfg(test)]
fn pending_buffer_test_gate() -> Result<(), u32> {
    PENDING_BUFFER_TEST_MODE.with(|mode| match mode.replace(0) {
        0 => Ok(()),
        1 => Err(STATUS_LIMIT_EXCEEDED),
        2 => panic!("injected pending owned-buffer publication panic"),
        _ => unreachable!("unknown pending buffer test mode"),
    })
}

fn buffer_registry() -> &'static Mutex<HashMap<usize, BufferToken>> {
    static REGISTRY: OnceLock<Mutex<HashMap<usize, BufferToken>>> = OnceLock::new();
    REGISTRY.get_or_init(|| Mutex::new(HashMap::new()))
}

fn status_for_result(result: Result<(), TerminalError>) -> u32 {
    result.map_or_else(|error| status_for_error(&error), |()| STATUS_OK)
}

fn status_for_error(error: &TerminalError) -> u32 {
    match error {
        TerminalError::InvalidDimensions => STATUS_INVALID_DIMENSIONS,
        TerminalError::LimitExceeded | TerminalError::SequenceExhausted => STATUS_LIMIT_EXCEEDED,
        TerminalError::SequenceMismatch { .. } => STATUS_SEQUENCE_MISMATCH,
        TerminalError::FailedClosed => STATUS_FAILED_CLOSED,
        TerminalError::HostEffectVersionRequired => STATUS_HOST_EFFECT_VERSION_REQUIRED,
        TerminalError::Unsupported(_) => STATUS_UNSUPPORTED,
        TerminalError::Gap => STATUS_GAP,
        TerminalError::SnapshotNotEligible => STATUS_SNAPSHOT_NOT_ELIGIBLE,
        TerminalError::PresentationDeferred => STATUS_PRESENTATION_DEFERRED,
        TerminalError::MalformedSnapshot => STATUS_MALFORMED_SNAPSHOT,
        TerminalError::IncompatibleSnapshot => STATUS_INCOMPATIBLE_SNAPSHOT,
        TerminalError::TimeOverflow => STATUS_TIME_OVERFLOW,
    }
}

fn status_for_input_error(error: &InputEncodeError) -> u32 {
    match error {
        InputEncodeError::TerminalFailedClosed => STATUS_FAILED_CLOSED,
        InputEncodeError::EmptyPaste
        | InputEncodeError::InvalidKittyFlags { .. }
        | InputEncodeError::KittyTextContainsControl(_)
        | InputEncodeError::InvalidKittyKeyEvent(_) => STATUS_INVALID_ARGUMENT,
        InputEncodeError::PasteTooLarge { .. }
        | InputEncodeError::KittyAssociatedTextTooLarge { .. }
        | InputEncodeError::AllocationFailed => STATUS_LIMIT_EXCEEDED,
        InputEncodeError::MouseCoordinateOutOfRange { .. } => STATUS_LIMIT_EXCEEDED,
        InputEncodeError::InvalidMouseButtonState { .. }
        | InputEncodeError::MissingMousePixelPosition
        | InputEncodeError::MouseCellOutOfBounds { .. } => STATUS_INVALID_ARGUMENT,
        InputEncodeError::UnsupportedModifiers { .. }
        | InputEncodeError::UnsupportedControlCharacter(_)
        | InputEncodeError::Win32InputModeDisabled
        | InputEncodeError::KittyKeyEventRequired
        | InputEncodeError::UnsupportedKittyLegacyEncoding { .. } => STATUS_UNSUPPORTED,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(columns: u32, rows: u32, initial_source_sequence: u64) -> TlTerminalConfigV1 {
        TlTerminalConfigV1 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalConfigV1>(),
            columns,
            rows,
            initial_source_sequence,
            reserved: [0; 2],
        }
    }

    fn config_v2(
        columns: u32,
        rows: u32,
        initial_source_sequence: u64,
        history_capacity_rows: u64,
    ) -> TlTerminalConfigV2 {
        TlTerminalConfigV2 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalConfigV2>(),
            columns,
            rows,
            initial_source_sequence,
            reserved_v1_compat: [0; 2],
            history_capacity_rows,
            reserved: [0; 2],
        }
    }

    fn config_v3(
        columns: u32,
        rows: u32,
        initial_source_sequence: u64,
        history_capacity_rows: u64,
        output_profile: u32,
    ) -> TlTerminalConfigV3 {
        TlTerminalConfigV3 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalConfigV3>(),
            columns,
            rows,
            initial_source_sequence,
            reserved_v1_compat: [0; 2],
            history_capacity_rows,
            reserved_v2_compat: [0; 2],
            output_profile,
            reserved0: 0,
            reserved: [0; 2],
        }
    }

    fn empty_buffer() -> TlTerminalBuffer {
        TlTerminalBuffer {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalBuffer>(),
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            len: 0,
        }
    }

    fn u32_at(bytes: &[u8], offset: usize) -> u32 {
        u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("u32 field"))
    }

    fn u64_at(bytes: &[u8], offset: usize) -> u64 {
        u64::from_le_bytes(bytes[offset..offset + 8].try_into().expect("u64 field"))
    }

    fn host_effect_payloads(frame: &[u8]) -> Vec<&[u8]> {
        let count = usize::try_from(u32_at(frame, 16)).expect("effect count");
        (0..count)
            .map(|index| {
                let record = 64 + index * 40;
                let offset = usize::try_from(u64_at(frame, record + 24)).expect("payload offset");
                let len = usize::try_from(u32_at(frame, record + 32)).expect("payload length");
                &frame[offset..offset + len]
            })
            .collect()
    }

    fn empty_info() -> TlTerminalCheckpointInfo {
        TlTerminalCheckpointInfo {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalCheckpointInfo>(),
            revision: 0,
            next_source_sequence: 0,
            reserved: [0; 2],
        }
    }

    fn empty_presentation_info() -> TlTerminalPresentationInfoV1 {
        TlTerminalPresentationInfoV1 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalPresentationInfoV1>(),
            flags: 0,
            reserved0: 0,
            next_source_sequence: 0,
            logical_time_micros: 0,
            synchronized_output_deadline_micros: 0,
            reserved: [0; 1],
        }
    }

    fn output(sequence: u64, bytes: &[u8]) -> TlTerminalEvent {
        TlTerminalEvent {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalEvent>(),
            source_sequence: sequence,
            kind: EVENT_OUTPUT,
            reserved: 0,
            data: bytes.as_ptr(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: u64::try_from(bytes.len()).expect("test length"),
            columns: 0,
            rows: 0,
            advance_micros: 0,
        }
    }

    fn resize(sequence: u64, columns: u32, rows: u32) -> TlTerminalEvent {
        TlTerminalEvent {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalEvent>(),
            source_sequence: sequence,
            kind: EVENT_RESIZE,
            reserved: 0,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: 0,
            columns,
            rows,
            advance_micros: 0,
        }
    }

    fn advance(sequence: u64, micros: u64) -> TlTerminalEvent {
        TlTerminalEvent {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalEvent>(),
            source_sequence: sequence,
            kind: EVENT_ADVANCE,
            reserved: 0,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: 0,
            columns: 0,
            rows: 0,
            advance_micros: micros,
        }
    }

    fn gap(sequence: u64) -> TlTerminalEvent {
        TlTerminalEvent {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalEvent>(),
            source_sequence: sequence,
            kind: EVENT_GAP,
            reserved: 0,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: 0,
            columns: 0,
            rows: 0,
            advance_micros: 0,
        }
    }

    fn input(kind: u32, key: u32, modifiers: u32, character: u32) -> TlTerminalInputV1 {
        TlTerminalInputV1 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalInputV1>(),
            kind,
            key,
            modifiers,
            character,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: 0,
            reserved: [0; 2],
        }
    }

    fn input_v2(kind: u32) -> TlTerminalInputV2 {
        TlTerminalInputV2 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalInputV2>(),
            kind,
            key: 0,
            modifiers: 0,
            character: 0,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: 0,
            reserved_v1_compat: [0; 2],
            win32_virtual_key: 0,
            win32_scan_code: 0,
            win32_unicode_char: 0,
            win32_key_down: 0,
            win32_control_key_state: 0,
            win32_repeat_count: 0,
            reserved: [0; 2],
        }
    }

    fn input_v3(kind: u32) -> TlTerminalInputV3 {
        TlTerminalInputV3 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalInputV3>(),
            kind,
            key: 0,
            modifiers: 0,
            character: 0,
            data: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            data_len: 0,
            reserved_v1_compat: [0; 2],
            win32_virtual_key: 0,
            win32_scan_code: 0,
            win32_unicode_char: 0,
            win32_key_down: 0,
            win32_control_key_state: 0,
            win32_repeat_count: 0,
            reserved: [0; 2],
            key_event_type: 0,
            keyboard_modifiers: 0,
            shifted_key: 0,
            base_layout_key: 0,
            associated_text: ptr::null(),
            #[cfg(target_pointer_width = "32")]
            associated_text_pointer_padding: 0,
            associated_text_len: 0,
            reserved_v3: [0; 4],
        }
    }

    fn kitty_input(key: u32, character: u32) -> TlTerminalInputV3 {
        let mut input = input_v3(INPUT_KIND_KITTY_KEY);
        input.key = key;
        input.character = character;
        input.key_event_type = INPUT_KEY_EVENT_PRESS;
        input
    }

    fn win32_input(
        virtual_key: u32,
        scan_code: u32,
        unicode_char: u32,
        key_down: u32,
        control_key_state: u32,
        repeat_count: u32,
    ) -> TlTerminalInputV2 {
        let mut input = input_v2(INPUT_KIND_WIN32_KEY);
        input.win32_virtual_key = virtual_key;
        input.win32_scan_code = scan_code;
        input.win32_unicode_char = unicode_char;
        input.win32_key_down = key_down;
        input.win32_control_key_state = control_key_state;
        input.win32_repeat_count = repeat_count;
        input
    }

    fn mouse_input(
        action: u32,
        button: u32,
        wheel: u32,
        buttons_held: u32,
        cell_column: u32,
        cell_row: u32,
    ) -> TlTerminalMouseInputV1 {
        TlTerminalMouseInputV1 {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalMouseInputV1>(),
            action,
            button,
            wheel,
            buttons_held,
            position_flags: 0,
            modifiers: 0,
            cell_column,
            cell_row,
            pixel_x: 0,
            pixel_y: 0,
            reserved: [0; 2],
        }
    }

    unsafe fn copy_and_free(mut buffer: TlTerminalBuffer) -> Vec<u8> {
        // SAFETY: The caller passes a live owned buffer for its recorded length.
        let bytes = unsafe {
            std::slice::from_raw_parts(
                buffer.data,
                usize::try_from(buffer.len).expect("test buffer length"),
            )
        }
        .to_vec();
        // SAFETY: This helper consumes and releases the one live token.
        assert_eq!(unsafe { tl_terminal_buffer_free(&mut buffer) }, STATUS_OK);
        assert!(buffer.data.is_null());
        assert_eq!(buffer.len, 0);
        bytes
    }

    fn create(config: &TlTerminalConfigV1) -> Result<*mut TlTerminal, u32> {
        let mut terminal = ptr::null_mut();
        // SAFETY: Test provides a readable config and writable empty output.
        let status = unsafe { tl_terminal_create(config, &mut terminal) };
        if status == STATUS_OK {
            Ok(terminal)
        } else {
            Err(status)
        }
    }

    unsafe fn checkpoint(terminal: *const TlTerminal) -> *mut TlTerminalCheckpoint {
        let mut checkpoint = ptr::null_mut();
        // SAFETY: Forwarded test ownership satisfies the checkpoint contract.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_begin(terminal, &mut checkpoint) },
            STATUS_OK
        );
        checkpoint
    }

    unsafe fn export(checkpoint: *const TlTerminalCheckpoint) -> TlTerminalBuffer {
        let mut buffer = empty_buffer();
        // SAFETY: Forwarded test ownership satisfies the export contract.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_export_snapshot(checkpoint, &mut buffer) },
            STATUS_OK
        );
        buffer
    }

    unsafe fn export_render_frame(
        checkpoint: *const TlTerminalCheckpoint,
        max_frame_bytes: u64,
    ) -> TlTerminalBuffer {
        let mut buffer = empty_buffer();
        // SAFETY: Forwarded test ownership satisfies the render export contract.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v1(
                    checkpoint,
                    max_frame_bytes,
                    &mut buffer,
                )
            },
            STATUS_OK
        );
        buffer
    }

    unsafe fn export_render_frame_v2(
        checkpoint: *const TlTerminalCheckpoint,
        max_frame_bytes: u64,
    ) -> TlTerminalBuffer {
        let mut buffer = empty_buffer();
        // SAFETY: Forwarded test ownership satisfies the render export contract.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v2(
                    checkpoint,
                    max_frame_bytes,
                    &mut buffer,
                )
            },
            STATUS_OK
        );
        buffer
    }

    unsafe fn export_render_frame_v3(
        checkpoint: *const TlTerminalCheckpoint,
        max_frame_bytes: u64,
    ) -> TlTerminalBuffer {
        let mut buffer = empty_buffer();
        // SAFETY: Forwarded test ownership satisfies the render export contract.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v3(
                    checkpoint,
                    max_frame_bytes,
                    &mut buffer,
                )
            },
            STATUS_OK
        );
        buffer
    }

    unsafe fn checkpoint_export_counts(checkpoint: *const TlTerminalCheckpoint) -> [usize; 4] {
        // SAFETY: Test callers keep the checkpoint live while reading its
        // test-only atomic instrumentation.
        let checkpoint = unsafe { &*checkpoint };
        [
            checkpoint
                .snapshot_export_count
                .load(std::sync::atomic::Ordering::Relaxed),
            checkpoint
                .render_frame_v1_export_count
                .load(std::sync::atomic::Ordering::Relaxed),
            checkpoint
                .render_frame_v2_export_count
                .load(std::sync::atomic::Ordering::Relaxed),
            checkpoint
                .render_frame_v3_export_count
                .load(std::sync::atomic::Ordering::Relaxed),
        ]
    }

    #[test]
    fn checkpoint_is_immutable_and_preserves_initial_sequence() {
        let terminal = create(&config(12, 3, 5)).expect("create");
        let prefix = output(5, b"prefix");
        // SAFETY: Test exclusively owns the terminal and event bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &prefix) },
            STATUS_OK
        );
        // SAFETY: Test exclusively owns the live terminal.
        let frozen = unsafe { checkpoint(terminal) };

        let mut info = empty_info();
        // SAFETY: Test owns the checkpoint and writable output.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_get_info(frozen, &mut info) },
            STATUS_OK
        );
        assert_eq!(info.revision, 6);
        assert_eq!(info.next_source_sequence, 6);

        let suffix = output(6, b"-suffix");
        // SAFETY: Test exclusively owns the terminal and event bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &suffix) },
            STATUS_OK
        );
        // SAFETY: Test owns the immutable checkpoint.
        let mut frozen_snapshot = unsafe { export(frozen) };
        let mut restored = ptr::null_mut();
        // SAFETY: Snapshot bytes remain live and output is empty.
        assert_eq!(
            unsafe {
                tl_terminal_import_snapshot(
                    frozen_snapshot.data,
                    frozen_snapshot.len,
                    &mut restored,
                )
            },
            STATUS_OK
        );
        // SAFETY: The frozen state was captured before sequence 6.
        assert_eq!(
            unsafe { tl_terminal_apply_event(restored, &suffix) },
            STATUS_OK
        );

        // SAFETY: Both terminals are exclusively owned.
        let original_final = unsafe { checkpoint(terminal) };
        // SAFETY: Both terminals are exclusively owned.
        let restored_final = unsafe { checkpoint(restored) };
        // SAFETY: Both checkpoints remain live.
        let mut original_snapshot = unsafe { export(original_final) };
        // SAFETY: Both checkpoints remain live.
        let mut restored_snapshot = unsafe { export(restored_final) };
        // SAFETY: Returned buffers are live for their recorded lengths.
        let original_bytes = unsafe {
            std::slice::from_raw_parts(
                original_snapshot.data,
                usize::try_from(original_snapshot.len).expect("test length"),
            )
        };
        // SAFETY: Returned buffers are live for their recorded lengths.
        let restored_bytes = unsafe {
            std::slice::from_raw_parts(
                restored_snapshot.data,
                usize::try_from(restored_snapshot.len).expect("test length"),
            )
        };
        assert_eq!(original_bytes, restored_bytes);

        // SAFETY: Each live token and handle is released exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut frozen_snapshot), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut original_snapshot), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut restored_snapshot), STATUS_OK);
            tl_terminal_checkpoint_destroy(frozen);
            tl_terminal_checkpoint_destroy(original_final);
            tl_terminal_checkpoint_destroy(restored_final);
            tl_terminal_destroy(terminal);
            tl_terminal_destroy(restored);
        }
        assert!(frozen_snapshot.data.is_null());
        assert_eq!(frozen_snapshot.len, 0);
    }

    #[test]
    fn checkpoint_exports_are_lazy_stable_and_independent_of_the_source_terminal() {
        let terminal = create(&config(12, 3, 9)).expect("create");
        let content = output(9, b"\x1b]0;lazy-checkpoint\x07\x1b[31;4mwide:\xe7\x95\x8c");
        // SAFETY: Test exclusively owns the terminal and event bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &content) },
            STATUS_OK
        );

        // These direct core exports define the byte-compatible state captured
        // by checkpoint_begin without causing the checkpoint itself to encode.
        // SAFETY: The test has exclusive access to this live terminal.
        let direct = unsafe { &*terminal };
        let expected_snapshot = direct.inner.export_snapshot_v2().expect("snapshot");
        let expected_v1 = direct.inner.export_render_frame_v1().expect("render v1");
        let expected_v2 = direct.inner.export_render_frame_v2().expect("render v2");
        let expected_v3 = direct.inner.export_render_frame_v3().expect("render v3");

        // SAFETY: Test exclusively owns the live terminal.
        let frozen = unsafe { checkpoint(terminal) };
        // SAFETY: The checkpoint remains live.
        assert_eq!(unsafe { checkpoint_export_counts(frozen) }, [0, 0, 0, 0]);

        // A checkpoint owns an immutable terminal clone, not a borrow of the
        // original handle.
        // SAFETY: The source terminal is destroyed exactly once after capture.
        unsafe { tl_terminal_destroy(terminal) };

        let v3_limit = u64::try_from(MAX_RENDER_FRAME_V3_BYTES).expect("v3 limit");
        // SAFETY: Test owns the live checkpoint and consumes the returned token.
        let first_v3 = unsafe { copy_and_free(export_render_frame_v3(frozen, v3_limit)) };
        assert_eq!(first_v3, expected_v3);
        // Requesting only V3 must not generate or retain Snapshot/V1/V2 bytes.
        // SAFETY: The checkpoint remains live.
        assert_eq!(unsafe { checkpoint_export_counts(frozen) }, [0, 0, 0, 1]);

        // SAFETY: Repeated immutable export returns a separately owned token.
        let second_v3 = unsafe { copy_and_free(export_render_frame_v3(frozen, v3_limit)) };
        assert_eq!(second_v3, first_v3);
        // SAFETY: The checkpoint remains live.
        assert_eq!(unsafe { checkpoint_export_counts(frozen) }, [0, 0, 0, 2]);

        // SAFETY: Each helper consumes exactly one separately owned buffer.
        let snapshot = unsafe { copy_and_free(export(frozen)) };
        let v1 = unsafe {
            copy_and_free(export_render_frame(
                frozen,
                u64::try_from(MAX_RENDER_FRAME_V1_BYTES).expect("v1 limit"),
            ))
        };
        let v2 = unsafe {
            copy_and_free(export_render_frame_v2(
                frozen,
                u64::try_from(MAX_RENDER_FRAME_V2_BYTES).expect("v2 limit"),
            ))
        };
        assert_eq!(snapshot, expected_snapshot);
        assert_eq!(v1, expected_v1);
        assert_eq!(v2, expected_v2);
        // SAFETY: The checkpoint remains live.
        assert_eq!(unsafe { checkpoint_export_counts(frozen) }, [1, 1, 1, 2]);

        // SAFETY: The checkpoint is released exactly once.
        unsafe { tl_terminal_checkpoint_destroy(frozen) };
    }

    #[test]
    fn render_frame_discovery_bounds_and_checkpoint_immutability_are_hardened() {
        assert_eq!(
            tl_terminal_render_frame_v1_format_version(),
            RENDER_FRAME_V1_FORMAT_VERSION
        );
        let hard_limit =
            u64::try_from(MAX_RENDER_FRAME_V1_BYTES).expect("render frame hard limit fits u64");
        assert_eq!(tl_terminal_render_frame_v1_max_bytes(), hard_limit);

        let terminal = create(&config(8, 2, 7)).expect("create");
        let prefix = output(7, b"frame");
        // SAFETY: Test owns the terminal and event payload for this call.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &prefix) },
            STATUS_OK
        );
        // SAFETY: Test exclusively owns the terminal.
        let frozen = unsafe { checkpoint(terminal) };
        // SAFETY: Test owns the immutable checkpoint.
        let mut first = unsafe { export_render_frame(frozen, hard_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let first_bytes = unsafe {
            std::slice::from_raw_parts(
                first.data,
                usize::try_from(first.len).expect("frame length"),
            )
        }
        .to_vec();
        assert_eq!(&first_bytes[..8], b"TLRFRM1\0");
        assert_eq!(
            u64::from_le_bytes(first_bytes[24..32].try_into().expect("revision")),
            8
        );
        assert_eq!(
            u64::from_le_bytes(first_bytes[32..40].try_into().expect("sequence")),
            8
        );

        let suffix = output(8, b"-mutated");
        // SAFETY: Test exclusively owns the terminal and event payload.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &suffix) },
            STATUS_OK
        );
        // SAFETY: A checkpoint stays immutable after terminal mutation.
        let mut second = unsafe { export_render_frame(frozen, hard_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let second_bytes = unsafe {
            std::slice::from_raw_parts(
                second.data,
                usize::try_from(second.len).expect("frame length"),
            )
        };
        assert_eq!(second_bytes, first_bytes);

        for invalid_limit in [0, hard_limit + 1, u64::MAX] {
            let mut untouched = empty_buffer();
            // SAFETY: Valid checkpoint/output; invalid limit is rejected.
            assert_eq!(
                unsafe {
                    tl_terminal_checkpoint_export_render_frame_v1(
                        frozen,
                        invalid_limit,
                        &mut untouched,
                    )
                },
                STATUS_INVALID_ARGUMENT
            );
            assert!(untouched.data.is_null());
            assert_eq!(untouched.len, 0);
        }

        let mut too_small = empty_buffer();
        // SAFETY: Limit below the known frame length leaves output empty.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v1(frozen, first.len - 1, &mut too_small)
            },
            STATUS_LIMIT_EXCEEDED
        );
        assert!(too_small.data.is_null());
        assert_eq!(too_small.len, 0);

        // SAFETY: Release each successful buffer token and handle exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut first), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut second), STATUS_OK);
            tl_terminal_checkpoint_destroy(frozen);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn render_frame_v2_is_bounded_immutable_and_coexists_with_v1() {
        assert_eq!(tl_terminal_abi_version(), 1);
        assert_eq!(tl_terminal_render_frame_v2_format_version(), 2);
        assert_eq!(
            tl_terminal_render_frame_v2_format_version(),
            RENDER_FRAME_V2_FORMAT_VERSION
        );
        let hard_limit =
            u64::try_from(MAX_RENDER_FRAME_V2_BYTES).expect("render frame hard limit fits u64");
        assert_eq!(hard_limit, 16_777_216);
        assert_eq!(tl_terminal_render_frame_v2_max_bytes(), hard_limit);

        let terminal = create(&config(8, 2, 11)).expect("create");
        let prefix = output(11, b"\x1b[5 qframe-v2");
        // SAFETY: Test owns the terminal and event payload for this call.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &prefix) },
            STATUS_OK
        );
        // SAFETY: Test exclusively owns the terminal.
        let frozen = unsafe { checkpoint(terminal) };

        // An empty output accepts an independently generated V2 frame.
        // SAFETY: Test owns the immutable checkpoint and empty output.
        let mut first_v2 = unsafe { export_render_frame_v2(frozen, hard_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let first_v2_bytes = unsafe {
            std::slice::from_raw_parts(
                first_v2.data,
                usize::try_from(first_v2.len).expect("frame length"),
            )
        }
        .to_vec();
        assert_eq!(&first_v2_bytes[..8], b"TLRFRM2\0");

        let v1_limit =
            u64::try_from(MAX_RENDER_FRAME_V1_BYTES).expect("render frame hard limit fits u64");
        // SAFETY: V1 remains independently exportable from the same checkpoint.
        let mut v1 = unsafe { export_render_frame(frozen, v1_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let v1_bytes = unsafe {
            std::slice::from_raw_parts(v1.data, usize::try_from(v1.len).expect("frame length"))
        };
        assert_eq!(&v1_bytes[..8], b"TLRFRM1\0");
        assert_ne!(v1_bytes, first_v2_bytes);

        let suffix = output(12, b"-mutated");
        // SAFETY: Test exclusively owns the terminal and event payload.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &suffix) },
            STATUS_OK
        );
        // SAFETY: A checkpoint stays immutable after terminal mutation.
        let mut second_v2 = unsafe { export_render_frame_v2(frozen, hard_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let second_v2_bytes = unsafe {
            std::slice::from_raw_parts(
                second_v2.data,
                usize::try_from(second_v2.len).expect("frame length"),
            )
        };
        assert_eq!(second_v2_bytes, first_v2_bytes);

        for invalid_limit in [0, hard_limit + 1, u64::MAX] {
            let mut untouched = empty_buffer();
            // SAFETY: Valid checkpoint/output; invalid limit is rejected.
            assert_eq!(
                unsafe {
                    tl_terminal_checkpoint_export_render_frame_v2(
                        frozen,
                        invalid_limit,
                        &mut untouched,
                    )
                },
                STATUS_INVALID_ARGUMENT
            );
            assert!(untouched.data.is_null());
            assert_eq!(untouched.len, 0);
        }

        let mut too_small = empty_buffer();
        // SAFETY: Limit below the known frame length leaves output empty.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v2(
                    frozen,
                    first_v2.len - 1,
                    &mut too_small,
                )
            },
            STATUS_LIMIT_EXCEEDED
        );
        assert!(too_small.data.is_null());
        assert_eq!(too_small.len, 0);

        let occupied_before = (v1.data, v1.len);
        // SAFETY: A live V1 token is deliberately passed as a non-empty output.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_export_render_frame_v2(frozen, hard_limit, &mut v1) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!((v1.data, v1.len), occupied_before);

        // SAFETY: A null output pointer is rejected before any write.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v2(frozen, hard_limit, ptr::null_mut())
            },
            STATUS_NULL_POINTER
        );

        // SAFETY: Release each successful buffer token and handle exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut first_v2), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut second_v2), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut v1), STATUS_OK);
            tl_terminal_checkpoint_destroy(frozen);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn render_frame_v3_is_bounded_immutable_and_coexists_with_earlier_formats() {
        assert_eq!(tl_terminal_abi_version(), 1);
        assert_eq!(tl_terminal_render_frame_v3_format_version(), 3);
        assert_eq!(
            tl_terminal_render_frame_v3_format_version(),
            RENDER_FRAME_V3_FORMAT_VERSION
        );
        let hard_limit =
            u64::try_from(MAX_RENDER_FRAME_V3_BYTES).expect("render frame hard limit fits u64");
        assert_eq!(hard_limit, 16_777_216);
        assert_eq!(tl_terminal_render_frame_v3_max_bytes(), hard_limit);

        let terminal = create(&config(8, 2, 21)).expect("create");
        let prefix = output(21, b"\x1b[1;2;3;4:3;53;58:2::17:34:51mframe-v3");
        // SAFETY: Test owns the terminal and event payload for this call.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &prefix) },
            STATUS_OK
        );
        // SAFETY: Test exclusively owns the terminal.
        let frozen = unsafe { checkpoint(terminal) };

        // SAFETY: Test owns the immutable checkpoint and empty output.
        let mut first_v3 = unsafe { export_render_frame_v3(frozen, hard_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let first_v3_bytes = unsafe {
            std::slice::from_raw_parts(
                first_v3.data,
                usize::try_from(first_v3.len).expect("frame length"),
            )
        }
        .to_vec();
        assert_eq!(&first_v3_bytes[..8], b"TLRFRM3\0");

        let v1_limit =
            u64::try_from(MAX_RENDER_FRAME_V1_BYTES).expect("render frame hard limit fits u64");
        let v2_limit =
            u64::try_from(MAX_RENDER_FRAME_V2_BYTES).expect("render frame hard limit fits u64");
        // SAFETY: Earlier formats remain independently exportable from the checkpoint.
        let mut v1 = unsafe { export_render_frame(frozen, v1_limit) };
        // SAFETY: Earlier formats remain independently exportable from the checkpoint.
        let mut v2 = unsafe { export_render_frame_v2(frozen, v2_limit) };
        // SAFETY: Both live buffers are readable for their recorded lengths.
        let (v1_bytes, v2_bytes) = unsafe {
            (
                std::slice::from_raw_parts(v1.data, usize::try_from(v1.len).expect("frame length")),
                std::slice::from_raw_parts(v2.data, usize::try_from(v2.len).expect("frame length")),
            )
        };
        assert_eq!(&v1_bytes[..8], b"TLRFRM1\0");
        assert_eq!(&v2_bytes[..8], b"TLRFRM2\0");
        assert_ne!(v1_bytes, first_v3_bytes);
        assert_ne!(v2_bytes, first_v3_bytes);

        let suffix = output(22, b"-mutated");
        // SAFETY: Test exclusively owns the terminal and event payload.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &suffix) },
            STATUS_OK
        );
        // SAFETY: A checkpoint stays immutable after terminal mutation.
        let mut second_v3 = unsafe { export_render_frame_v3(frozen, hard_limit) };
        // SAFETY: The live buffer is readable for its recorded length.
        let second_v3_bytes = unsafe {
            std::slice::from_raw_parts(
                second_v3.data,
                usize::try_from(second_v3.len).expect("frame length"),
            )
        };
        assert_eq!(second_v3_bytes, first_v3_bytes);

        for invalid_limit in [0, hard_limit + 1, u64::MAX] {
            let mut untouched = empty_buffer();
            // SAFETY: Valid checkpoint/output; invalid limit is rejected.
            assert_eq!(
                unsafe {
                    tl_terminal_checkpoint_export_render_frame_v3(
                        frozen,
                        invalid_limit,
                        &mut untouched,
                    )
                },
                STATUS_INVALID_ARGUMENT
            );
            assert!(untouched.data.is_null());
            assert_eq!(untouched.len, 0);
        }

        let mut too_small = empty_buffer();
        // SAFETY: Limit below the known frame length leaves output empty.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v3(
                    frozen,
                    first_v3.len - 1,
                    &mut too_small,
                )
            },
            STATUS_LIMIT_EXCEEDED
        );
        assert!(too_small.data.is_null());
        assert_eq!(too_small.len, 0);

        let occupied_before = (v2.data, v2.len);
        // SAFETY: A live V2 token is deliberately passed as a non-empty output.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_export_render_frame_v3(frozen, hard_limit, &mut v2) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!((v2.data, v2.len), occupied_before);

        let mut null_checkpoint_output = empty_buffer();
        // SAFETY: A null checkpoint is rejected before the valid output is accessed.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v3(
                    ptr::null(),
                    hard_limit,
                    &mut null_checkpoint_output,
                )
            },
            STATUS_NULL_POINTER
        );
        assert!(null_checkpoint_output.data.is_null());
        assert_eq!(null_checkpoint_output.len, 0);

        // SAFETY: A null output pointer is rejected before any write.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v3(frozen, hard_limit, ptr::null_mut())
            },
            STATUS_NULL_POINTER
        );

        // SAFETY: Release each successful buffer token and handle exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut first_v3), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut second_v3), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut v1), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut v2), STATUS_OK);
            tl_terminal_checkpoint_destroy(frozen);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn short_and_unaligned_versioned_inputs_are_rejected_from_prefix() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        let short = [ABI_VERSION, 8_u32];
        // SAFETY: Exactly the declared eight-byte prefix is readable.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, short.as_ptr().cast()) },
            STATUS_INVALID_ARGUMENT
        );

        let mut storage = [0_u8; 9];
        storage[1..5].copy_from_slice(&ABI_VERSION.to_ne_bytes());
        storage[5..9].copy_from_slice(&8_u32.to_ne_bytes());
        // SAFETY: The intentionally unaligned eight-byte prefix is readable.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, storage.as_ptr().add(1).cast()) },
            STATUS_INVALID_ARGUMENT
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn extended_v1_event_is_accepted_and_oversized_output_is_rejected_early() {
        #[repr(C)]
        struct ExtendedEvent {
            event: TlTerminalEvent,
            future: u64,
        }

        let terminal = create(&config(4, 2, 10)).expect("create");
        let mut extended = ExtendedEvent {
            event: output(10, b"x"),
            future: 77,
        };
        extended.event.struct_size = struct_size::<ExtendedEvent>();
        // SAFETY: Known v1 fields are readable and the tail is ignored.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &extended.event) },
            STATUS_OK
        );

        let mut huge = output(11, &[]);
        huge.data = ptr::dangling();
        huge.data_len = u64::MAX;
        // SAFETY: Length rejection occurs before the dangling pointer is accessed.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &huge) },
            STATUS_LIMIT_EXCEEDED
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn atomic_event_batch_matches_successful_single_event_application() {
        let batch_terminal = create(&config(4, 2, 0)).expect("batch terminal");
        let sequential_terminal = create(&config(4, 2, 0)).expect("sequential terminal");
        let events = [output(0, b"ab"), resize(1, 5, 2), advance(2, 17)];

        // SAFETY: The terminal and exact-stride event array are live and exclusive.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_v1(
                    batch_terminal,
                    events.as_ptr(),
                    u32::try_from(events.len()).expect("event count"),
                )
            },
            STATUS_OK
        );
        for event in &events {
            // SAFETY: Test exclusively owns the terminal and readable event.
            assert_eq!(
                unsafe { tl_terminal_apply_event(sequential_terminal, event) },
                STATUS_OK
            );
        }
        // SAFETY: Both terminals remain live and are not mutably aliased.
        assert_eq!(unsafe { &(*batch_terminal).inner }, unsafe {
            &(*sequential_terminal).inner
        });

        // SAFETY: Release each live terminal exactly once.
        unsafe {
            tl_terminal_destroy(batch_terminal);
            tl_terminal_destroy(sequential_terminal);
        }
    }

    #[test]
    fn atomic_event_batch_rolls_back_a_late_retryable_failure() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        // SAFETY: Test has exclusive access to clone the live core state.
        let before = unsafe { (*terminal).inner.clone() };
        let events = [output(0, b"prefix"), resize(1, 0, 2)];

        // SAFETY: The terminal and exact-stride event array are live and exclusive.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, events.as_ptr(), 2) },
            STATUS_INVALID_DIMENSIONS
        );
        // SAFETY: Failed retryable batches leave the live terminal unchanged.
        assert_eq!(unsafe { &(*terminal).inner }, &before);

        let retry = output(0, b"retry");
        // SAFETY: Sequence zero remains available after complete rollback.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &retry) },
            STATUS_OK
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn atomic_event_batch_late_poisoning_failures_only_latch_failed_closed() {
        let cases = [
            (output(1, b"\x1b[999m"), STATUS_UNSUPPORTED),
            (gap(1), STATUS_GAP),
            (output(2, b"jump"), STATUS_SEQUENCE_MISMATCH),
        ];

        for (second, expected_status) in cases {
            let terminal = create(&config(8, 2, 0)).expect("create");
            let events = [output(0, b"prefix"), second];
            // SAFETY: The terminal and exact-stride event array are live and exclusive.
            assert_eq!(
                unsafe { tl_terminal_apply_event_batch_v1(terminal, events.as_ptr(), 2) },
                expected_status
            );

            let mut expected = Terminal::new(8, 2).expect("expected terminal");
            assert_eq!(
                expected.apply_event(Event::Gap { source_sequence: 0 }),
                Err(TerminalError::Gap)
            );
            // SAFETY: Core equality proves no batch prefix state was retained.
            assert_eq!(unsafe { &(*terminal).inner }, &expected);

            let retry = output(0, b"retry");
            // SAFETY: The terminal is live but has latched failed-closed.
            assert_eq!(
                unsafe { tl_terminal_apply_event(terminal, &retry) },
                STATUS_FAILED_CLOSED
            );
            // SAFETY: Test releases its live terminal exactly once.
            unsafe { tl_terminal_destroy(terminal) };
        }
    }

    #[test]
    fn atomic_event_batch_rejects_late_malformed_input_before_core() {
        let terminal = create(&config(8, 2, 0)).expect("create");
        let mut malformed = advance(1, 0);
        malformed.columns = 1;
        let mut events = [output(0, b"\x1b[999m"), malformed];

        // SAFETY: The second readable event is malformed and rejected before apply.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, events.as_ptr(), 2) },
            STATUS_INVALID_ARGUMENT
        );
        events[1].columns = 0;
        events[1].reserved = 1;
        // SAFETY: Reserved fields are validated before the unsupported prefix reaches core.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, events.as_ptr(), 2) },
            STATUS_INVALID_ARGUMENT
        );
        let retry = output(0, b"ok");
        // SAFETY: No event reached core, so the terminal remains usable at sequence zero.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &retry) },
            STATUS_OK
        );

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn atomic_event_batch_enforces_count_and_exact_stride_contracts() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        // SAFETY: Rejected counts and null arrays are diagnosed before array access.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, ptr::dangling(), 0) },
            STATUS_INVALID_ARGUMENT
        );
        // SAFETY: A null event array is rejected before access.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, ptr::null(), 1) },
            STATUS_INVALID_ARGUMENT
        );
        // SAFETY: An excessive count is rejected before array access.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, ptr::dangling(), 257) },
            STATUS_INVALID_ARGUMENT
        );

        let events: Vec<_> = (0..MAX_EVENT_BATCH_COUNT)
            .map(|sequence| advance(u64::try_from(sequence).expect("sequence fits u64"), 0))
            .collect();
        // SAFETY: The maximum-count exact-stride array is live and canonical.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_v1(
                    terminal,
                    events.as_ptr(),
                    u32::try_from(events.len()).expect("event count"),
                )
            },
            STATUS_OK
        );

        let mut wrong_stride = advance(256, 0);
        wrong_stride.struct_size += 8;
        // SAFETY: The readable element's declared stride is rejected as non-exact.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, &wrong_stride, 1) },
            STATUS_INVALID_ARGUMENT
        );
        wrong_stride.struct_size = struct_size::<TlTerminalEvent>();
        wrong_stride.abi_version += 1;
        // SAFETY: Every batch element must declare the current ABI version.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, &wrong_stride, 1) },
            STATUS_INVALID_ARGUMENT
        );

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn atomic_event_batch_enforces_output_payload_boundaries() {
        let terminal = create(&config(1, 1, 0)).expect("create");
        let payload = vec![0_u8; MAX_OUTPUT_BYTES];
        let exact = [
            output(0, &payload),
            output(1, &payload),
            output(2, &payload),
            output(3, &payload),
        ];
        // SAFETY: Four maximum-size readable payloads exactly meet the total cap.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, exact.as_ptr(), 4) },
            STATUS_OK
        );

        let empty = output(4, b"");
        // SAFETY: Batch Output payloads must be non-empty and non-null.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, &empty, 1) },
            STATUS_INVALID_ARGUMENT
        );
        let mut null_payload = output(4, b"x");
        null_payload.data = ptr::null();
        // SAFETY: A null Output payload is rejected as malformed before access.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, &null_payload, 1) },
            STATUS_INVALID_ARGUMENT
        );
        // SAFETY: The legacy single-event API continues to accept empty Output.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &empty) },
            STATUS_OK
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };

        let terminal = create(&config(1, 1, 0)).expect("create");
        let extra = [
            output(0, &payload),
            output(1, &payload),
            output(2, &payload),
            output(3, &payload),
            output(4, b"x"),
        ];
        // SAFETY: All regions are readable, but their checked sum exceeds the cap.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, extra.as_ptr(), 5) },
            STATUS_LIMIT_EXCEEDED
        );

        let oversized_payload = vec![0_u8; MAX_OUTPUT_BYTES + 1];
        let oversized = output(0, &oversized_payload);
        // SAFETY: The per-Output cap is checked before payload access or core apply.
        assert_eq!(
            unsafe { tl_terminal_apply_event_batch_v1(terminal, &oversized, 1) },
            STATUS_LIMIT_EXCEEDED
        );
        let retry = output(0, b"ok");
        // SAFETY: Both limit failures occurred before core and left sequence zero usable.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &retry) },
            STATUS_OK
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn host_effect_discovery_and_empty_frame_are_canonical() {
        assert_eq!(
            tl_terminal_host_effect_frame_v1_format_version(),
            HOST_EFFECT_FRAME_V1_FORMAT_VERSION
        );
        assert_eq!(
            tl_terminal_host_effect_frame_v1_max_bytes(),
            u64::try_from(MAX_HOST_EFFECT_FRAME_V1_BYTES).expect("published maximum")
        );

        let terminal = create(&config(4, 2, 0)).expect("create");
        let mut frame = empty_buffer();
        // SAFETY: The terminal, event, and empty output are live and exclusive.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &output(0, b"plain"),
                    &mut frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Successful publication owns a readable frame token.
        let bytes = unsafe { copy_and_free(frame) };
        assert_eq!(bytes.len(), 64);
        assert_eq!(&bytes[..8], b"TLHEF1\0\0");
        assert_eq!(u32_at(&bytes, 16), 0);
        assert_eq!(u32_at(&bytes, 20), 64);

        // SAFETY: Legacy apply still accepts reply queries and discards effects.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &output(1, b"\x1b[c")) },
            STATUS_OK
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn host_effect_v2_discovery_queries_and_v1_version_gate_are_transactional() {
        assert_eq!(tl_terminal_abi_version(), 1);
        assert_eq!(tl_terminal_host_effect_frame_v2_format_version(), 2);
        assert_eq!(
            tl_terminal_host_effect_frame_v2_format_version(),
            HOST_EFFECT_FRAME_V2_FORMAT_VERSION
        );
        assert_eq!(tl_terminal_host_effect_frame_v2_max_bytes(), 123_033_008);
        assert_eq!(
            tl_terminal_host_effect_frame_v2_max_bytes(),
            u64::try_from(MAX_HOST_EFFECT_FRAME_V2_BYTES).expect("published V2 maximum")
        );

        let empty_terminal = create(&config(4, 2, 0)).expect("create empty V2");
        let mut empty_frame = empty_buffer();
        // SAFETY: Canonical event/output and exclusive terminal ownership.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v2(
                    empty_terminal,
                    &output(0, b"plain"),
                    &mut empty_frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Successful publication owns a readable V2 frame token.
        let empty_bytes = unsafe {
            std::slice::from_raw_parts(
                empty_frame.data,
                usize::try_from(empty_frame.len).expect("V2 empty frame length"),
            )
        };
        assert_eq!(empty_bytes.len(), 64);
        assert_eq!(&empty_bytes[..8], b"TLHEF2\0\0");

        let query_terminal = create(&config(8, 2, 0)).expect("create query V2");
        // SAFETY: Test exclusively owns the terminal while cloning its state.
        let before = unsafe { (*query_terminal).inner.clone() };
        let query = output(0, b"\x1b[4$p");
        let mut query_frame = empty_buffer();
        // SAFETY: V1 cannot represent this query and must not publish or commit.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    query_terminal,
                    &query,
                    &mut query_frame,
                )
            },
            STATUS_HOST_EFFECT_VERSION_REQUIRED
        );
        assert!(query_frame.data.is_null());
        assert_eq!(query_frame.len, 0);
        // SAFETY: Failure must leave the complete terminal state untouched.
        assert_eq!(unsafe { &(*query_terminal).inner }, &before);

        // SAFETY: The same still-current event succeeds through V2.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v2(
                    query_terminal,
                    &query,
                    &mut query_frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Published bytes remain readable for the live token length.
        let query_bytes = unsafe {
            std::slice::from_raw_parts(
                query_frame.data,
                usize::try_from(query_frame.len).expect("V2 query frame length"),
            )
        };
        assert_eq!(&query_bytes[..8], b"TLHEF2\0\0");
        assert_eq!(
            host_effect_payloads(query_bytes),
            [b"\x1b[4;2$y".as_slice()]
        );
        // SAFETY: Successful V2 publication committed exactly one event.
        assert_eq!(unsafe { (*query_terminal).inner.next_source_sequence() }, 1);

        // SAFETY: Release both successful V2 tokens and handles exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut empty_frame), STATUS_OK);
            assert_eq!(tl_terminal_buffer_free(&mut query_frame), STATUS_OK);
            tl_terminal_destroy(empty_terminal);
            tl_terminal_destroy(query_terminal);
        }
    }

    #[test]
    fn host_effect_v2_batch_and_publisher_failures_preserve_atomicity() {
        let terminal = create(&config(8, 2, 0)).expect("create V2 batch");
        let events = [output(0, b"\x1b["), output(1, b"4$p")];
        let mut frame = empty_buffer();
        // SAFETY: Exact-stride canonical batch and empty output are valid.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v2(
                    terminal,
                    events.as_ptr(),
                    2,
                    &mut frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Successful publication owns a readable V2 frame.
        let bytes = unsafe {
            std::slice::from_raw_parts(
                frame.data,
                usize::try_from(frame.len).expect("V2 batch frame length"),
            )
        };
        assert_eq!(host_effect_payloads(bytes), [b"\x1b[4;2$y".as_slice()]);
        // SAFETY: Release the live token before reusing the output structure.
        assert_eq!(unsafe { tl_terminal_buffer_free(&mut frame) }, STATUS_OK);

        let stale_terminal = create(&config(8, 2, 0)).expect("create stale V2 batch");
        // SAFETY: Test exclusively owns the terminal while cloning its state.
        let stale_before = unsafe { (*stale_terminal).inner.clone() };
        let stale = [output(0, b"prefix"), output(0, b"stale")];
        // SAFETY: Late sequence mismatch must roll back the entire candidate.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v2(
                    stale_terminal,
                    stale.as_ptr(),
                    2,
                    &mut frame,
                )
            },
            STATUS_SEQUENCE_MISMATCH
        );
        assert!(frame.data.is_null());
        assert_eq!(frame.len, 0);
        // SAFETY: Retryable failure preserves the original terminal exactly.
        assert_eq!(unsafe { &(*stale_terminal).inner }, &stale_before);

        for (mode, expected_status) in [(1, STATUS_LIMIT_EXCEEDED), (2, STATUS_PANIC)] {
            let publish_terminal = create(&config(4, 2, 0)).expect("create V2 publisher");
            PENDING_BUFFER_TEST_MODE.with(|test_mode| test_mode.set(mode));
            // SAFETY: The test gate fails after registration and before commit.
            assert_eq!(
                unsafe {
                    tl_terminal_apply_event_with_host_effects_v2(
                        publish_terminal,
                        &output(0, b"plain"),
                        &mut frame,
                    )
                },
                expected_status
            );
            assert!(frame.data.is_null());
            assert_eq!(frame.len, 0);
            // SAFETY: Publication failure must not commit the candidate.
            assert_eq!(
                unsafe { (*publish_terminal).inner.next_source_sequence() },
                0
            );
            // SAFETY: Release the still-live terminal exactly once.
            unsafe { tl_terminal_destroy(publish_terminal) };
        }

        // SAFETY: Release each live handle exactly once.
        unsafe {
            tl_terminal_destroy(terminal);
            tl_terminal_destroy(stale_terminal);
        }
    }

    #[test]
    fn single_host_effect_apply_returns_all_reply_families_with_source_offsets() {
        let terminal = create(&config(4, 2, 7)).expect("create");
        let queries = b"\x1b[c\x1b[>0c\x1b[5n\x1b[6n\x1b[?5n\x1b[?6n";
        let mut frame = empty_buffer();
        // SAFETY: The terminal, event, payload, and output are valid.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &output(7, queries),
                    &mut frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Successful publication owns a readable frame token.
        let bytes = unsafe { copy_and_free(frame) };
        assert_eq!(
            host_effect_payloads(&bytes),
            [
                b"\x1b[?1;2c".as_slice(),
                b"\x1b[>0;0;0c".as_slice(),
                b"\x1b[0n".as_slice(),
                b"\x1b[1;1R".as_slice(),
                b"\x1b[?0n".as_slice(),
                b"\x1b[?1;1R".as_slice(),
            ]
        );
        for (index, expected_offset) in [3, 8, 12, 16, 21, 26].into_iter().enumerate() {
            let record = 64 + index * 40;
            assert_eq!(u32_at(&bytes, record), 1);
            assert_eq!(u64_at(&bytes, record + 8), 7);
            assert_eq!(u32_at(&bytes, record + 16), expected_offset);
            assert_eq!(u32_at(&bytes, record + 20), 0);
        }

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn batch_host_effect_apply_tracks_cross_event_sources_and_offsets() {
        let terminal = create(&config(4, 2, 10)).expect("create");
        let events = [
            output(10, b"\x1b["),
            output(11, b"c\x1b[5"),
            output(12, b"n\x1b[>c"),
        ];
        let mut frame = empty_buffer();
        // SAFETY: The terminal and exact-stride event array are live and exclusive.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    events.as_ptr(),
                    3,
                    &mut frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Successful publication owns a readable frame token.
        let bytes = unsafe { copy_and_free(frame) };
        assert_eq!(
            host_effect_payloads(&bytes),
            [
                b"\x1b[?1;2c".as_slice(),
                b"\x1b[0n".as_slice(),
                b"\x1b[>0;0;0c".as_slice(),
            ]
        );
        for (index, (sequence, offset)) in [(11, 1), (12, 1), (12, 5)].into_iter().enumerate() {
            let record = 64 + index * 40;
            assert_eq!(u64_at(&bytes, record + 8), sequence);
            assert_eq!(u32_at(&bytes, record + 16), offset);
        }

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn host_effect_output_contract_handles_null_short_unaligned_extended_and_nonempty() {
        #[repr(C)]
        struct ExtendedBuffer {
            buffer: TlTerminalBuffer,
            tail: u64,
        }
        #[repr(C)]
        struct ExtendedEvent {
            event: TlTerminalEvent,
            tail: u64,
        }

        let terminal = create(&config(4, 2, 0)).expect("create");
        let event = output(0, b"x");
        let mut empty = empty_buffer();
        // SAFETY: Null ABI arguments are diagnosed before dereference.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(ptr::null_mut(), &event, &mut empty)
            },
            STATUS_NULL_POINTER
        );
        // SAFETY: Null event and output pointers are diagnosed before access.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(terminal, ptr::null(), &mut empty)
            },
            STATUS_NULL_POINTER
        );
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(terminal, &event, ptr::null_mut())
            },
            STATUS_NULL_POINTER
        );

        let mut wrong_abi = empty_buffer();
        wrong_abi.abi_version += 1;
        // SAFETY: A readable wrong-version output is rejected unchanged.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(terminal, &event, &mut wrong_abi)
            },
            STATUS_INVALID_ARGUMENT
        );
        assert!(wrong_abi.data.is_null());

        let mut short = [0_u8; 8];
        short[..4].copy_from_slice(&ABI_VERSION.to_ne_bytes());
        short[4..].copy_from_slice(&8_u32.to_ne_bytes());
        // SAFETY: Exactly the declared short prefix is readable and writable.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &event,
                    short.as_mut_ptr().cast(),
                )
            },
            STATUS_INVALID_ARGUMENT
        );

        let mut occupied = empty_buffer();
        occupied.data = ptr::dangling();
        occupied.len = 1;
        let occupied_before = (occupied.data, occupied.len);
        // SAFETY: Non-empty output metadata is rejected without dereference or overwrite.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(terminal, &event, &mut occupied)
            },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!((occupied.data, occupied.len), occupied_before);

        let mut unaligned_storage = [0_u8; mem::size_of::<TlTerminalBuffer>() + 1];
        unaligned_storage[1..5].copy_from_slice(&ABI_VERSION.to_ne_bytes());
        unaligned_storage[5..9].copy_from_slice(&struct_size::<TlTerminalBuffer>().to_ne_bytes());
        // SAFETY: The intentionally unaligned complete structure is readable and writable.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &event,
                    unaligned_storage.as_mut_ptr().add(1).cast(),
                )
            },
            STATUS_OK
        );
        // SAFETY: The successful unaligned write contains one live token copied out here.
        let mut unaligned_frame = unsafe {
            ptr::read_unaligned(unaligned_storage.as_ptr().add(1).cast::<TlTerminalBuffer>())
        };
        // SAFETY: The copied token is released exactly once.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut unaligned_frame) },
            STATUS_OK
        );

        let mut extended = ExtendedBuffer {
            buffer: empty_buffer(),
            tail: 0xfeed_face_cafe_beef,
        };
        extended.buffer.struct_size = struct_size::<ExtendedBuffer>();
        let mut extended_event = ExtendedEvent {
            event: output(1, b"y"),
            tail: 0x1234,
        };
        extended_event.event.struct_size = struct_size::<ExtendedEvent>();
        // SAFETY: Unknown event/output tails are readable, writable, and ignored.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &extended_event.event,
                    &mut extended.buffer,
                )
            },
            STATUS_OK
        );
        assert_eq!(extended.tail, 0xfeed_face_cafe_beef);
        assert_eq!(extended.buffer.struct_size, struct_size::<ExtendedBuffer>());
        // SAFETY: Release the extended output's live token and terminal once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut extended.buffer), STATUS_OK);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn host_effect_batch_validates_complete_input_before_terminal_access() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        let mut frame = empty_buffer();
        // SAFETY: Invalid counts and arrays are rejected before array access.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    ptr::dangling(),
                    0,
                    &mut frame,
                )
            },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    ptr::null(),
                    1,
                    &mut frame,
                )
            },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    ptr::dangling(),
                    257,
                    &mut frame,
                )
            },
            STATUS_INVALID_ARGUMENT
        );

        let mut wrong_stride = output(0, b"x");
        wrong_stride.struct_size += 8;
        // SAFETY: The readable element declares a forbidden non-exact stride.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    &wrong_stride,
                    1,
                    &mut frame,
                )
            },
            STATUS_INVALID_ARGUMENT
        );

        let oversized_payload = vec![b'x'; MAX_OUTPUT_BYTES + 1];
        // SAFETY: Per-event length is rejected before the payload or terminal is accessed.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &output(0, &oversized_payload),
                    &mut frame,
                )
            },
            STATUS_LIMIT_EXCEEDED
        );
        assert!(frame.data.is_null());
        // SAFETY: The batch form enforces the same per-Output 1 MiB cap.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    &output(0, &oversized_payload),
                    1,
                    &mut frame,
                )
            },
            STATUS_LIMIT_EXCEEDED
        );

        let mut malformed = advance(1, 0);
        malformed.columns = 1;
        let late_metadata = [output(0, b"\x1b[999m"), malformed];
        // SAFETY: The late malformed record prevents the unsupported prefix from reaching core.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    late_metadata.as_ptr(),
                    2,
                    &mut frame,
                )
            },
            STATUS_INVALID_ARGUMENT
        );

        let mut megabyte = vec![b'x'; MAX_OUTPUT_BYTES];
        megabyte[..6].copy_from_slice(b"\x1b[999m");
        let aggregate_overflow = [
            output(0, &megabyte),
            output(1, &megabyte),
            output(2, &megabyte),
            output(3, &megabyte),
            output(4, b"x"),
        ];
        // SAFETY: The checked 4 MiB aggregate cap is enforced before core sees event zero.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    aggregate_overflow.as_ptr(),
                    5,
                    &mut frame,
                )
            },
            STATUS_LIMIT_EXCEEDED
        );
        assert!(frame.data.is_null());

        // A valid sequence-zero retry proves no rejected metadata reached the terminal.
        // SAFETY: The retry and output are valid and exclusively owned.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &output(0, b"retry"),
                    &mut frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Release the successful frame and terminal exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut frame), STATUS_OK);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn host_effect_batch_preserves_late_poisoning_and_stale_atomicity() {
        let poisoning_cases = [
            (output(1, b"\x1b[999m"), STATUS_UNSUPPORTED),
            (gap(1), STATUS_GAP),
            (output(2, b"forward"), STATUS_SEQUENCE_MISMATCH),
        ];
        for (late, expected_status) in poisoning_cases {
            let terminal = create(&config(8, 2, 0)).expect("create");
            let events = [output(0, b"prefix"), late];
            let mut frame = empty_buffer();
            // SAFETY: The exact-stride batch and output are valid and exclusive.
            assert_eq!(
                unsafe {
                    tl_terminal_apply_event_batch_with_host_effects_v1(
                        terminal,
                        events.as_ptr(),
                        2,
                        &mut frame,
                    )
                },
                expected_status
            );
            assert!(frame.data.is_null());
            assert_eq!(frame.len, 0);

            let mut expected = Terminal::new(8, 2).expect("expected");
            assert_eq!(
                expected.apply_event(Event::Gap { source_sequence: 0 }),
                Err(TerminalError::Gap)
            );
            // SAFETY: Equality proves no successful batch prefix was retained.
            assert_eq!(unsafe { &(*terminal).inner }, &expected);
            // SAFETY: Failed-closed rejects the retry without publishing a buffer.
            assert_eq!(
                unsafe {
                    tl_terminal_apply_event_with_host_effects_v1(
                        terminal,
                        &output(0, b"retry"),
                        &mut frame,
                    )
                },
                STATUS_FAILED_CLOSED
            );
            assert!(frame.data.is_null());
            // SAFETY: Test releases its live terminal exactly once.
            unsafe { tl_terminal_destroy(terminal) };
        }

        let terminal = create(&config(8, 2, 0)).expect("create stale");
        // SAFETY: Test exclusively owns the live terminal while cloning state.
        let before = unsafe { (*terminal).inner.clone() };
        let stale = [output(0, b"prefix"), output(0, b"stale")];
        let mut frame = empty_buffer();
        // SAFETY: The valid batch fails on its late stale source sequence.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_batch_with_host_effects_v1(
                    terminal,
                    stale.as_ptr(),
                    2,
                    &mut frame,
                )
            },
            STATUS_SEQUENCE_MISMATCH
        );
        // SAFETY: Stale failure is retryable and rolls back the complete prefix.
        assert_eq!(unsafe { &(*terminal).inner }, &before);
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &output(0, b"retry"),
                    &mut frame,
                )
            },
            STATUS_OK
        );
        // SAFETY: Release successful retry output and terminal exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut frame), STATUS_OK);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn host_effect_buffer_copy_free_and_metadata_tampering_are_hardened() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        let mut original = empty_buffer();
        // SAFETY: Valid call publishes one live host-effect buffer token.
        assert_eq!(
            unsafe {
                tl_terminal_apply_event_with_host_effects_v1(
                    terminal,
                    &output(0, b"\x1b[c"),
                    &mut original,
                )
            },
            STATUS_OK
        );
        let mut copied = original;
        // SAFETY: The live allocation remains readable through either metadata copy.
        let bytes = unsafe {
            std::slice::from_raw_parts(
                copied.data,
                usize::try_from(copied.len).expect("frame length"),
            )
        }
        .to_vec();
        assert_eq!(host_effect_payloads(&bytes), [b"\x1b[?1;2c".as_slice()]);

        let valid_len = copied.len;
        copied.len += 1;
        // SAFETY: Tampered length is rejected without consuming the live token.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut copied) },
            STATUS_INVALID_ARGUMENT
        );
        copied.len = valid_len;
        copied.struct_size += 8;
        // SAFETY: Tampered structure metadata is likewise rejected.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut copied) },
            STATUS_INVALID_ARGUMENT
        );
        copied.struct_size = original.struct_size;
        // SAFETY: Freeing the original metadata clears that structure.
        assert_eq!(unsafe { tl_terminal_buffer_free(&mut original) }, STATUS_OK);
        assert!(original.data.is_null());
        assert_eq!(original.len, 0);
        // SAFETY: The copied metadata no longer identifies a live token.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut copied) },
            STATUS_INVALID_ARGUMENT
        );

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn pending_owned_buffer_failures_roll_back_token_and_terminal() {
        for (mode, expected_status) in [(1, STATUS_LIMIT_EXCEEDED), (2, STATUS_PANIC)] {
            let terminal = create(&config(4, 2, 0)).expect("create");
            let mut frame = empty_buffer();
            PENDING_BUFFER_TEST_LAST_KEY.with(|key| key.set(0));
            PENDING_BUFFER_TEST_ROLLED_BACK_KEY.with(|key| key.set(0));
            PENDING_BUFFER_TEST_MODE.with(|test_mode| test_mode.set(mode));
            // SAFETY: Valid arguments reach the test-only post-registration gate.
            assert_eq!(
                unsafe {
                    tl_terminal_apply_event_with_host_effects_v1(
                        terminal,
                        &output(0, b"\x1b[c"),
                        &mut frame,
                    )
                },
                expected_status
            );
            assert!(frame.data.is_null());
            assert_eq!(frame.len, 0);
            let key = PENDING_BUFFER_TEST_LAST_KEY.with(std::cell::Cell::get);
            assert_ne!(key, 0);
            assert_eq!(
                PENDING_BUFFER_TEST_ROLLED_BACK_KEY.with(std::cell::Cell::get),
                key
            );
            // SAFETY: Publisher failure must leave sequence zero retryable.
            assert_eq!(unsafe { (*terminal).inner.next_source_sequence() }, 0);

            // SAFETY: Retry proves the candidate was not committed; this publication succeeds.
            assert_eq!(
                unsafe {
                    tl_terminal_apply_event_with_host_effects_v1(
                        terminal,
                        &output(0, b"retry"),
                        &mut frame,
                    )
                },
                STATUS_OK
            );
            // SAFETY: Release the successful retry output and terminal exactly once.
            unsafe {
                assert_eq!(tl_terminal_buffer_free(&mut frame), STATUS_OK);
                tl_terminal_destroy(terminal);
            }
        }
    }

    #[test]
    fn outputs_must_be_empty_and_create_errors_do_not_overwrite_handles() {
        let bad = config(0, 2, 1);
        let mut out = ptr::null_mut();
        // SAFETY: Config and empty output are readable/writable.
        assert_eq!(
            unsafe { tl_terminal_create(&bad, &mut out) },
            STATUS_INVALID_DIMENSIONS
        );
        assert!(out.is_null());

        let mut exhausted = ptr::null_mut();
        // SAFETY: Config and empty output are readable/writable.
        assert_eq!(
            unsafe { tl_terminal_create(&config(4, 2, u64::MAX), &mut exhausted) },
            STATUS_LIMIT_EXCEEDED
        );
        assert!(exhausted.is_null());

        let terminal = create(&config(4, 2, 1)).expect("create");
        let mut occupied = terminal;
        // SAFETY: Occupied output is readable and must be rejected unchanged.
        assert_eq!(
            unsafe { tl_terminal_create(&config(4, 2, 1), &mut occupied) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(occupied, terminal);

        // SAFETY: Test exclusively owns the terminal.
        let checkpoint = unsafe { checkpoint(terminal) };
        // SAFETY: Test owns the checkpoint.
        let mut live = unsafe { export(checkpoint) };
        let live_data = live.data;
        // SAFETY: A non-empty buffer output must be rejected without overwrite.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_export_snapshot(checkpoint, &mut live) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(live.data, live_data);

        // SAFETY: Release each owned object exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut live), STATUS_OK);
            tl_terminal_checkpoint_destroy(checkpoint);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn config_v2_is_additive_strict_and_creates_scrollback_terminals() {
        let mut terminal = ptr::null_mut();
        let valid = config_v2(3, 2, 1, 2);
        // SAFETY: Test provides a readable V2 config and an empty output slot.
        assert_eq!(
            unsafe { tl_terminal_create_v2(&valid, &mut terminal) },
            STATUS_OK
        );
        assert!(!terminal.is_null());
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };

        let mut invalid = valid;
        invalid.reserved_v1_compat[0] = 1;
        let mut output = ptr::null_mut();
        // SAFETY: Invalid reserved fields are readable and diagnosed.
        assert_eq!(
            unsafe { tl_terminal_create_v2(&invalid, &mut output) },
            STATUS_INVALID_ARGUMENT
        );
        assert!(output.is_null());

        let mut oversized = valid;
        oversized.history_capacity_rows = u64::from(u32::MAX) + 1;
        // SAFETY: Oversized row capacity is readable and diagnosed.
        assert_eq!(
            unsafe { tl_terminal_create_v2(&oversized, &mut output) },
            STATUS_INVALID_ARGUMENT
        );
        assert!(output.is_null());
    }

    #[test]
    fn scrollback_info_and_offset_render_are_checkpoint_stable_and_bounded() {
        let mut terminal = ptr::null_mut();
        let valid = config_v2(3, 2, 1, 2);
        // SAFETY: Test provides an exact readable V2 config and empty output.
        assert_eq!(
            unsafe { tl_terminal_create_v2(&valid, &mut terminal) },
            STATUS_OK
        );
        // SAFETY: Event and payload remain readable for the call.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &output(1, b"A\r\nB\r\nC")) },
            STATUS_OK
        );
        // SAFETY: Test owns the terminal and resulting checkpoint.
        let frozen = unsafe { checkpoint(terminal) };
        let mut available_rows = 0_u32;
        let mut generation_rows = 0_u64;
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_get_scrollback_info_v1(
                    frozen,
                    &mut available_rows,
                    &mut generation_rows,
                )
            },
            STATUS_OK
        );
        assert_eq!((available_rows, generation_rows), (1, 1));

        let mut history = empty_buffer();
        // SAFETY: Checkpoint remains live and output is empty.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_render_frame_v3_at_scrollback(
                    frozen,
                    u32::MAX,
                    MAX_RENDER_FRAME_V3_BYTES as u64,
                    &mut history,
                )
            },
            STATUS_OK
        );
        let bytes = unsafe { copy_and_free(history) };
        assert_eq!(
            u32::from_le_bytes(bytes[56..60].try_into().expect("cursor flags")) & 1,
            0
        );

        // SAFETY: Release each live owner exactly once.
        unsafe {
            tl_terminal_checkpoint_destroy(frozen);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn config_v3_selects_the_output_profile_and_accepts_standalone_c1() {
        let valid = config_v3(3, 2, 7, 2, OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1);
        let mut terminal = ptr::null_mut();
        // SAFETY: Test provides an exact readable V3 config and an empty output slot.
        assert_eq!(
            unsafe { tl_terminal_create_v3(&valid, &mut terminal) },
            STATUS_OK
        );
        assert!(!terminal.is_null());
        // SAFETY: Test exclusively owns the live terminal.
        assert_eq!(
            unsafe { (*terminal).inner.output_profile() },
            OutputProfile::Utf8WithStandaloneC1
        );

        let c1_csi = output(7, b"\x9b2;2H");
        // SAFETY: The event and payload remain readable for the call.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &c1_csi) },
            STATUS_OK
        );

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };

        let utf8 = config_v3(3, 2, 0, 0, OUTPUT_PROFILE_UTF8);
        let mut terminal = ptr::null_mut();
        // SAFETY: Test provides an exact readable V3 config and an empty output slot.
        assert_eq!(
            unsafe { tl_terminal_create_v3(&utf8, &mut terminal) },
            STATUS_OK
        );
        // SAFETY: Test exclusively owns the live terminal.
        assert_eq!(
            unsafe { (*terminal).inner.output_profile() },
            OutputProfile::Utf8
        );
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn config_v3_rejects_noncanonical_metadata_without_publishing_a_handle() {
        let valid = config_v3(3, 2, 0, 2, OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1);
        let mut invalid_configs = Vec::new();

        let mut invalid = valid;
        invalid.struct_size -= 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.struct_size += 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.abi_version += 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.reserved_v1_compat[0] = 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.reserved_v2_compat[0] = 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.reserved0 = 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.reserved[0] = 1;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.output_profile = 2;
        invalid_configs.push(invalid);
        let mut invalid = valid;
        invalid.history_capacity_rows = u64::from(u32::MAX) + 1;
        invalid_configs.push(invalid);

        for invalid in invalid_configs {
            let mut output = ptr::null_mut();
            // SAFETY: Every rejected config has a readable prefix and output slot.
            assert_eq!(
                unsafe { tl_terminal_create_v3(&invalid, &mut output) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(output.is_null());
        }

        let invalid_dimensions = config_v3(0, 2, 0, 0, OUTPUT_PROFILE_UTF8);
        let mut output = ptr::null_mut();
        // SAFETY: Invalid dimensions are handled without publishing ownership.
        assert_eq!(
            unsafe { tl_terminal_create_v3(&invalid_dimensions, &mut output) },
            STATUS_INVALID_DIMENSIONS
        );
        assert!(output.is_null());

        let occupied_config = config(3, 2, 0);
        let occupied = create(&occupied_config).expect("occupied handle");
        let mut output = occupied;
        // SAFETY: A non-empty ownership slot is readable and must remain unchanged.
        assert_eq!(
            unsafe { tl_terminal_create_v3(&valid, &mut output) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(output, occupied);
        // SAFETY: Test releases the original terminal exactly once.
        unsafe { tl_terminal_destroy(occupied) };
    }

    #[test]
    fn buffer_free_rejects_bad_metadata_and_clears_a_valid_token() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        // SAFETY: Test exclusively owns the terminal.
        let checkpoint = unsafe { checkpoint(terminal) };
        // SAFETY: Test owns the checkpoint.
        let mut buffer = unsafe { export(checkpoint) };
        let valid_len = buffer.len;
        let valid_struct_size = buffer.struct_size;
        buffer.len += 1;
        // SAFETY: The structure is writable; incorrect metadata is rejected.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut buffer) },
            STATUS_INVALID_ARGUMENT
        );
        assert!(!buffer.data.is_null());
        buffer.len = valid_len;
        buffer.struct_size += 8;
        // SAFETY: A changed capacity is not the metadata of the live token.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut buffer) },
            STATUS_INVALID_ARGUMENT
        );
        buffer.struct_size = valid_struct_size;
        // SAFETY: Restored metadata identifies the one live token.
        assert_eq!(unsafe { tl_terminal_buffer_free(&mut buffer) }, STATUS_OK);
        assert!(buffer.data.is_null());
        assert_eq!(buffer.len, 0);
        // SAFETY: An empty buffer is not a second live token.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(&mut buffer) },
            STATUS_INVALID_ARGUMENT
        );

        // SAFETY: Release each owned handle exactly once.
        unsafe {
            tl_terminal_checkpoint_destroy(checkpoint);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn null_short_output_and_reserved_entry_points_are_hardened() {
        let event = output(0, b"x");
        // SAFETY: Null is explicitly diagnosed by the ABI.
        assert_eq!(
            unsafe { tl_terminal_apply_event(ptr::null_mut(), &event) },
            STATUS_NULL_POINTER
        );
        let mut null_terminal = ptr::null_mut();
        // SAFETY: Null inputs are diagnosed without dereference.
        assert_eq!(
            unsafe { tl_terminal_create(ptr::null(), &mut null_terminal) },
            STATUS_NULL_POINTER
        );
        // SAFETY: Null outputs are diagnosed without dereference.
        assert_eq!(
            unsafe { tl_terminal_create(&config(4, 2, 0), ptr::null_mut()) },
            STATUS_NULL_POINTER
        );
        // SAFETY: Null buffers are diagnosed without dereference.
        assert_eq!(
            unsafe { tl_terminal_buffer_free(ptr::null_mut()) },
            STATUS_NULL_POINTER
        );
        let mut imported = ptr::null_mut();
        // SAFETY: Oversized snapshot length is rejected before pointer access.
        assert_eq!(
            unsafe { tl_terminal_import_snapshot(ptr::dangling(), u64::MAX, &mut imported) },
            STATUS_LIMIT_EXCEEDED
        );
        let terminal = create(&config(4, 2, 0)).expect("create");
        // SAFETY: Test exclusively owns the terminal.
        let checkpoint = unsafe { checkpoint(terminal) };
        let mut short = [ABI_VERSION, 8_u32];
        // SAFETY: Exactly the declared output prefix is readable and writable.
        assert_eq!(
            unsafe {
                tl_terminal_checkpoint_export_snapshot(checkpoint, short.as_mut_ptr().cast())
            },
            STATUS_INVALID_ARGUMENT
        );

        let mut output = empty_buffer();
        // SAFETY: Oversized input is rejected before its pointer is accessed.
        assert_eq!(
            unsafe { tl_terminal_encode_input(terminal, ptr::dangling(), u64::MAX, &mut output) },
            STATUS_LIMIT_EXCEEDED
        );
        // SAFETY: Valid objects reach the unsupported placeholder.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_export_delta(checkpoint, &mut output) },
            STATUS_UNSUPPORTED
        );
        assert!(output.data.is_null());
        assert_eq!(catch_status(|| panic!("test panic")), STATUS_PANIC);

        // SAFETY: Release each owned handle exactly once.
        unsafe {
            tl_terminal_checkpoint_destroy(checkpoint);
            tl_terminal_destroy(terminal);
        }
    }

    #[test]
    fn synchronized_output_checkpoint_status_and_presentation_flags_compose() {
        let utf8_terminal = create(&config(8, 2, 0)).expect("create UTF-8 terminal");
        // SAFETY: Test exclusively owns the terminal and borrowed byte prefix.
        assert_eq!(
            unsafe { tl_terminal_apply_event(utf8_terminal, &output(0, &[0xf0, 0x9f])) },
            STATUS_OK
        );
        let mut utf8_info = empty_presentation_info();
        // SAFETY: Test exclusively owns the live terminal and writable output.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(utf8_terminal, &mut utf8_info) },
            STATUS_OK
        );
        assert_eq!(utf8_info.flags, PRESENTATION_FLAG_PARSER_INCOMPLETE);
        // SAFETY: Release the independent UTF-8 terminal exactly once.
        unsafe { tl_terminal_destroy(utf8_terminal) };

        let terminal = create(&config(8, 2, 0)).expect("create");
        let mut info = empty_presentation_info();
        // SAFETY: Test exclusively owns the live terminal and writable output.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, &mut info) },
            STATUS_OK
        );
        assert_eq!(info.flags, 0);
        assert_eq!(info.next_source_sequence, 0);
        assert_eq!(info.logical_time_micros, 0);
        assert_eq!(info.synchronized_output_deadline_micros, 0);

        // SAFETY: Test exclusively owns the terminal and event payload.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &output(0, b"\x1b[?2026h")) },
            STATUS_OK
        );
        let mut checkpoint = ptr::null_mut();
        // SAFETY: The empty ownership slot is writable and remains empty on error.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_begin(terminal, &mut checkpoint) },
            STATUS_PRESENTATION_DEFERRED
        );
        assert!(checkpoint.is_null());
        // SAFETY: Test exclusively owns the live terminal and writable output.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, &mut info) },
            STATUS_OK
        );
        assert_eq!(info.flags, PRESENTATION_FLAG_SYNCHRONIZED_OUTPUT);
        assert_eq!(info.next_source_sequence, 1);
        assert_eq!(info.synchronized_output_deadline_micros, 1_000_000);

        // SAFETY: Complete metadata and borrowed payload satisfy the event contract.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &output(1, b"\x1b")) },
            STATUS_OK
        );
        // SAFETY: A forward discontinuity fails closed without consuming state.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &output(4, b"future")) },
            STATUS_SEQUENCE_MISMATCH
        );
        // SAFETY: The getter is explicitly valid for failed-closed terminals.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, &mut info) },
            STATUS_OK
        );
        assert_eq!(
            info.flags,
            PRESENTATION_FLAG_FAILED_CLOSED
                | PRESENTATION_FLAG_PARSER_INCOMPLETE
                | PRESENTATION_FLAG_SYNCHRONIZED_OUTPUT
        );
        assert_eq!(info.next_source_sequence, 2);
        assert_eq!(info.logical_time_micros, 0);
        assert_eq!(info.synchronized_output_deadline_micros, 1_000_000);
        assert_eq!(info.reserved0, 0);
        assert_eq!(info.reserved, [0; 1]);

        checkpoint = ptr::null_mut();
        // SAFETY: The empty ownership slot is writable and remains empty on
        // error. Failed-closed takes precedence over the simultaneous parser
        // incomplete and synchronized-output deferral states.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_begin(terminal, &mut checkpoint) },
            STATUS_FAILED_CLOSED
        );
        assert!(checkpoint.is_null());

        // SAFETY: Release the one owned terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn presentation_info_getter_honors_prefix_unaligned_tail_and_inout_contracts() {
        let terminal = create(&config(8, 2, 7)).expect("create");

        // SAFETY: Null arguments are explicitly diagnosed without dereference.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(ptr::null(), ptr::null_mut()) },
            STATUS_NULL_POINTER
        );
        // SAFETY: The terminal is live; a null output is explicitly diagnosed.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, ptr::null_mut()) },
            STATUS_NULL_POINTER
        );

        let mut short = [0xa5_u8; 8];
        short[..4].copy_from_slice(&ABI_VERSION.to_ne_bytes());
        short[4..].copy_from_slice(&8_u32.to_ne_bytes());
        let short_before = short;
        // SAFETY: Exactly the required prefix is readable and writable.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, short.as_mut_ptr().cast()) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(short, short_before);

        let mut wrong_abi = empty_presentation_info();
        wrong_abi.abi_version = ABI_VERSION + 1;
        wrong_abi.flags = u32::MAX;
        let wrong_before = unsafe { ptr::read_unaligned(&raw const wrong_abi) };
        // SAFETY: The complete known structure is readable and writable.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, &mut wrong_abi) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(wrong_abi.abi_version, wrong_before.abi_version);
        assert_eq!(wrong_abi.flags, wrong_before.flags);

        let mut unaligned_storage = [0_u8; 49];
        let unaligned =
            unsafe { unaligned_storage.as_mut_ptr().add(1) }.cast::<TlTerminalPresentationInfoV1>();
        // SAFETY: Bytes 1..49 provide a complete writable, deliberately
        // unaligned structure region.
        unsafe { ptr::write_unaligned(unaligned, empty_presentation_info()) };
        // SAFETY: The deliberately unaligned region satisfies the ABI contract.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, unaligned) },
            STATUS_OK
        );
        // SAFETY: The same complete region remains readable.
        let unaligned_info = unsafe { ptr::read_unaligned(unaligned) };
        assert_eq!(unaligned_info.next_source_sequence, 7);
        assert_eq!(unaligned_info.flags, 0);

        let mut oversized = [0x5a_u8; 56];
        let oversized_ptr = oversized
            .as_mut_ptr()
            .cast::<TlTerminalPresentationInfoV1>();
        let mut initial = empty_presentation_info();
        initial.struct_size = 56;
        initial.flags = u32::MAX;
        initial.reserved0 = u32::MAX;
        initial.next_source_sequence = u64::MAX;
        initial.logical_time_micros = u64::MAX;
        initial.synchronized_output_deadline_micros = u64::MAX;
        initial.reserved = [u64::MAX; 1];
        // SAFETY: The first 48 bytes hold the complete known structure.
        unsafe { ptr::write_unaligned(oversized_ptr, initial) };
        let tail_before = oversized[48..].to_vec();
        // SAFETY: The declared 56-byte output is readable and writable.
        assert_eq!(
            unsafe { tl_terminal_get_presentation_info_v1(terminal, oversized_ptr) },
            STATUS_OK
        );
        // SAFETY: The known prefix remains a complete readable structure.
        let actual = unsafe { ptr::read_unaligned(oversized_ptr) };
        assert_eq!(actual.abi_version, ABI_VERSION);
        assert_eq!(actual.struct_size, 56);
        assert_eq!(actual.flags, 0);
        assert_eq!(actual.reserved0, 0);
        assert_eq!(actual.next_source_sequence, 7);
        assert_eq!(actual.logical_time_micros, 0);
        assert_eq!(actual.synchronized_output_deadline_micros, 0);
        assert_eq!(actual.reserved, [0; 1]);
        assert_eq!(&oversized[48..], tail_before);

        // SAFETY: Release the one owned terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn checkpoint_reports_an_incomplete_parser_boundary() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        let partial_escape = output(0, b"\x1b");
        // SAFETY: Test owns the terminal and event bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &partial_escape) },
            STATUS_OK
        );
        let mut checkpoint = ptr::null_mut();
        // SAFETY: Test owns the terminal and empty output.
        assert_eq!(
            unsafe { tl_terminal_checkpoint_begin(terminal, &mut checkpoint) },
            STATUS_SNAPSHOT_NOT_ELIGIBLE
        );
        assert!(checkpoint.is_null());
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn semantic_input_abi_tracks_modes_and_owns_each_result() {
        let terminal = create(&config(8, 2, 0)).expect("create");
        let arrow = input(INPUT_KIND_KEY, INPUT_KEY_ARROW_UP, 0, 0);
        let mut encoded = empty_buffer();
        // SAFETY: Test owns all live inputs and the empty output buffer.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &arrow, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful encoding returned one owned buffer token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1b[A");

        let modes = output(0, b"\x1b[?1h\x1b[20h\x1b[?2004h\x1b=");
        // SAFETY: Test owns the terminal and event bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &modes) },
            STATUS_OK
        );

        let mut encoded = empty_buffer();
        // SAFETY: Test owns all live inputs and the empty output buffer.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &arrow, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful encoding returned one owned buffer token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1bOA");

        let enter = input(INPUT_KIND_KEY, INPUT_KEY_ENTER, 0, 0);
        let mut encoded = empty_buffer();
        // SAFETY: Test owns all live inputs and the empty output buffer.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &enter, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful encoding returned one owned buffer token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\r\n");

        let payload = b"paste\r\nbytes";
        let mut paste = input(INPUT_KIND_PASTE, 0, 0, 0);
        paste.data = payload.as_ptr();
        paste.data_len = u64::try_from(payload.len()).expect("paste length");
        let mut encoded = empty_buffer();
        // SAFETY: Paste bytes remain readable and output is empty.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &paste, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful encoding returned one owned buffer token.
        assert_eq!(
            unsafe { copy_and_free(encoded) },
            b"\x1b[200~paste\rbytes\x1b[201~"
        );

        let keypad = input(INPUT_KIND_KEY, INPUT_KEY_KEYPAD_7, 0, 0);
        let mut encoded = empty_buffer();
        // SAFETY: Test owns all live inputs and the empty output buffer.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &keypad, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful encoding returned one owned buffer token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1bOw");

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn semantic_focus_input_abi_tracks_mode_and_snapshot_restore() {
        let terminal = create(&config(8, 2, 0)).expect("create");
        let focus_gained = input(INPUT_KIND_FOCUS_GAINED, 0, 0, 0);
        let focus_lost = input(INPUT_KIND_FOCUS_LOST, 0, 0, 0);

        let mut encoded = empty_buffer();
        // SAFETY: Test owns the terminal, canonical input, and empty output.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &focus_gained, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Disabled focus reporting returned one empty owned buffer.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"");

        let enable_focus_reporting = output(0, b"\x1b[?1004h");
        // SAFETY: Test exclusively owns the terminal and event bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &enable_focus_reporting) },
            STATUS_OK
        );

        for (input, expected) in [
            (&focus_gained, b"\x1b[I".as_slice()),
            (&focus_lost, b"\x1b[O".as_slice()),
        ] {
            let mut encoded = empty_buffer();
            // SAFETY: Test owns the terminal, canonical input, and empty output.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v1(terminal, input, &mut encoded) },
                STATUS_OK
            );
            // SAFETY: Successful encoding returned one owned buffer token.
            assert_eq!(unsafe { copy_and_free(encoded) }, expected);
        }

        // SAFETY: Test exclusively owns the terminal.
        let checkpoint = unsafe { checkpoint(terminal) };
        // SAFETY: Test owns the immutable checkpoint.
        let mut snapshot = unsafe { export(checkpoint) };
        let mut restored = ptr::null_mut();
        // SAFETY: Snapshot bytes remain readable and the output handle is empty.
        assert_eq!(
            unsafe { tl_terminal_import_snapshot(snapshot.data, snapshot.len, &mut restored) },
            STATUS_OK
        );

        let mut encoded = empty_buffer();
        // SAFETY: Restored terminal is live, input is canonical, and output is empty.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(restored, &focus_lost, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Restored mode produced one owned buffer token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1b[O");

        // SAFETY: Release each owned token and handle exactly once.
        unsafe {
            assert_eq!(tl_terminal_buffer_free(&mut snapshot), STATUS_OK);
            tl_terminal_checkpoint_destroy(checkpoint);
            tl_terminal_destroy(terminal);
            tl_terminal_destroy(restored);
        }
    }

    #[test]
    fn semantic_focus_input_abi_rejects_reserved_payload_fields() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        let mut malformed = [input(INPUT_KIND_FOCUS_GAINED, 0, 0, 0); 6];
        malformed[0].key = INPUT_KEY_ENTER;
        malformed[1].modifiers = INPUT_MODIFIER_SHIFT;
        malformed[2].character = u32::from('x');
        malformed[3].data = ptr::dangling();
        malformed[4].data_len = 1;
        malformed[5].reserved[0] = 1;

        for input in &malformed {
            let mut encoded = empty_buffer();
            // SAFETY: Every malformed field is rejected before any data pointer access.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v1(terminal, input, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
            assert_eq!(encoded.len, 0);
        }

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn semantic_input_abi_rejects_malformed_and_failed_terminal_requests() {
        let terminal = create(&config(4, 2, 0)).expect("create");
        let mut malformed = input(INPUT_KIND_KEY, INPUT_KEY_ENTER, 0, 1);
        let mut encoded = empty_buffer();
        // SAFETY: Readable malformed input is rejected without output.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &malformed, &mut encoded) },
            STATUS_INVALID_ARGUMENT
        );
        assert!(encoded.data.is_null());

        malformed.character = 0;
        malformed.modifiers = 1 << 31;
        // SAFETY: Unknown modifier bits are rejected without output.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &malformed, &mut encoded) },
            STATUS_INVALID_ARGUMENT
        );

        let mut oversized = input(INPUT_KIND_PASTE, 0, 0, 0);
        oversized.data = ptr::dangling();
        oversized.data_len = u64::MAX;
        // SAFETY: Oversized length is rejected before pointer access.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &oversized, &mut encoded) },
            STATUS_LIMIT_EXCEEDED
        );

        let unsupported = output(0, b"\x01");
        // SAFETY: Unsupported output permanently fails the terminal closed.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &unsupported) },
            STATUS_UNSUPPORTED
        );
        let enter = input(INPUT_KIND_KEY, INPUT_KEY_ENTER, 0, 0);
        // SAFETY: A failed-closed terminal is live but unavailable for encoding.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &enter, &mut encoded) },
            STATUS_FAILED_CLOSED
        );
        assert!(encoded.data.is_null());

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn semantic_input_v2_preserves_v1_and_forward_tail_contracts() {
        #[repr(C)]
        struct ExtendedInput {
            input: TlTerminalInputV2,
            tail: [u64; 2],
        }

        #[repr(C)]
        struct ExtendedBuffer {
            buffer: TlTerminalBuffer,
            tail: [u64; 2],
        }

        let terminal = create(&config(8, 2, 0)).expect("create");
        let mut arrow = input_v2(INPUT_KIND_KEY);
        arrow.key = INPUT_KEY_ARROW_UP;
        let mut extended_input = ExtendedInput {
            input: arrow,
            tail: [u64::MAX, 0x1122_3344_5566_7788],
        };
        extended_input.input.struct_size = struct_size::<ExtendedInput>();
        let mut extended_output = ExtendedBuffer {
            buffer: empty_buffer(),
            tail: [0x8877_6655_4433_2211, u64::MAX],
        };
        extended_output.buffer.struct_size = struct_size::<ExtendedBuffer>();

        // SAFETY: Both extended structures expose readable/writable known
        // prefixes; their unknown tails remain owned by this test.
        assert_eq!(
            unsafe {
                tl_terminal_encode_input_v2(
                    terminal,
                    &extended_input.input,
                    &mut extended_output.buffer,
                )
            },
            STATUS_OK
        );
        assert_eq!(extended_input.tail, [u64::MAX, 0x1122_3344_5566_7788]);
        assert_eq!(extended_output.tail, [0x8877_6655_4433_2211, u64::MAX]);
        // SAFETY: Successful V2 encoding returned one owned buffer token.
        assert_eq!(unsafe { copy_and_free(extended_output.buffer) }, b"\x1b[A");

        let v1_win32 = input(INPUT_KIND_WIN32_KEY, 0, 0, 0);
        let mut encoded = empty_buffer();
        // SAFETY: V1 must reject the additive kind without touching output.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &v1_win32, &mut encoded) },
            STATUS_INVALID_ARGUMENT
        );
        assert!(encoded.data.is_null());
        assert_eq!(encoded.len, 0);

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn win32_input_v2_tracks_mode_and_encodes_key_records_exactly() {
        let terminal = create(&config(8, 2, 0)).expect("create");
        let disabled = win32_input(0xffff, 0xffff, 0xffff, 1, u32::MAX, 0xffff);
        let mut encoded = empty_buffer();
        // SAFETY: A canonical Win32 record is readable and output is empty.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, &disabled, &mut encoded) },
            STATUS_UNSUPPORTED
        );
        assert!(encoded.data.is_null());
        assert_eq!(encoded.len, 0);

        let enable = output(0, b"\x1b[?9001h");
        // SAFETY: Test owns the terminal and readable mode sequence.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &enable) },
            STATUS_OK
        );

        let vectors = [
            (
                win32_input(17, 29, 0, 1, 8, 1),
                b"\x1b[17;29;0;1;8;1_".as_slice(),
            ),
            (
                win32_input(112, 59, 0, 1, 8, 1),
                b"\x1b[112;59;0;1;8;1_".as_slice(),
            ),
            (
                win32_input(112, 59, 0, 0, 8, 1),
                b"\x1b[112;59;0;0;8;1_".as_slice(),
            ),
            (
                win32_input(17, 29, 0, 0, 0, 1),
                b"\x1b[17;29;0;0;0;1_".as_slice(),
            ),
        ];
        for (input, expected) in vectors {
            let mut encoded = empty_buffer();
            // SAFETY: Test owns the terminal, canonical record, and output.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v2(terminal, &input, &mut encoded) },
                STATUS_OK
            );
            // SAFETY: Successful encoding returned one owned buffer token.
            assert_eq!(unsafe { copy_and_free(encoded) }, expected);
        }

        let disable = output(1, b"\x1b[?9001l");
        // SAFETY: Test owns the terminal and readable mode sequence.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &disable) },
            STATUS_OK
        );
        let mut encoded = empty_buffer();
        // SAFETY: Disabling the mode restores the unsupported result.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, &vectors[0].0, &mut encoded) },
            STATUS_UNSUPPORTED
        );
        assert!(encoded.data.is_null());
        assert_eq!(encoded.len, 0);

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn win32_input_v2_strictly_validates_disjoint_payloads_and_ranges() {
        let terminal = create(&config(8, 2, 0)).expect("create");
        let canonical = win32_input(112, 59, 0, 1, 8, 1);

        let mut malformed_old_fields = [canonical; 6];
        malformed_old_fields[0].key = INPUT_KEY_F1;
        malformed_old_fields[1].modifiers = INPUT_MODIFIER_CONTROL;
        malformed_old_fields[2].character = u32::from('x');
        malformed_old_fields[3].data = ptr::dangling();
        malformed_old_fields[4].data_len = 1;
        malformed_old_fields[5].reserved_v1_compat[0] = 1;

        let mut malformed_ranges = [canonical; 6];
        malformed_ranges[0].win32_virtual_key = 65_536;
        malformed_ranges[1].win32_scan_code = 65_536;
        malformed_ranges[2].win32_unicode_char = 65_536;
        malformed_ranges[3].win32_key_down = 2;
        malformed_ranges[4].win32_repeat_count = 65_536;
        malformed_ranges[5].reserved[1] = 1;

        let mut v1_kinds_with_extensions = [input_v2(INPUT_KIND_KEY); 6];
        for input in &mut v1_kinds_with_extensions {
            input.key = INPUT_KEY_ENTER;
        }
        v1_kinds_with_extensions[0].win32_virtual_key = 1;
        v1_kinds_with_extensions[1].win32_scan_code = 1;
        v1_kinds_with_extensions[2].win32_unicode_char = 1;
        v1_kinds_with_extensions[3].win32_key_down = 1;
        v1_kinds_with_extensions[4].win32_control_key_state = 1;
        v1_kinds_with_extensions[5].win32_repeat_count = 1;

        for input in malformed_old_fields
            .iter()
            .chain(&malformed_ranges)
            .chain(&v1_kinds_with_extensions)
        {
            let mut encoded = empty_buffer();
            // SAFETY: Invalid disjoint/range fields are rejected without
            // dereferencing the intentionally dangling old payload pointer.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v2(terminal, input, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
            assert_eq!(encoded.len, 0);
        }

        let mut short = canonical;
        short.struct_size = struct_size::<TlTerminalInputV2>() - 1;
        let mut wrong_abi = canonical;
        wrong_abi.abi_version += 1;
        for input in [&short, &wrong_abi] {
            let mut encoded = empty_buffer();
            // SAFETY: Both structures have readable ABI prefixes and remain
            // physically full-sized despite rejected declared metadata.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v2(terminal, input, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
            assert_eq!(encoded.len, 0);
        }

        let mut occupied_output = TlTerminalBuffer {
            abi_version: ABI_VERSION,
            struct_size: struct_size::<TlTerminalBuffer>(),
            data: ptr::dangling(),
            #[cfg(target_pointer_width = "32")]
            data_pointer_padding: 0,
            len: 1,
        };
        // SAFETY: The occupied output metadata is readable and must be rejected
        // before any write or terminal access.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, &canonical, &mut occupied_output) },
            STATUS_INVALID_ARGUMENT
        );
        assert_eq!(occupied_output.data, ptr::dangling());
        assert_eq!(occupied_output.len, 1);

        for mut invalid_output in [
            TlTerminalBuffer {
                abi_version: ABI_VERSION,
                struct_size: struct_size::<TlTerminalBuffer>() - 1,
                data: ptr::null(),
                #[cfg(target_pointer_width = "32")]
                data_pointer_padding: 0,
                len: 0,
            },
            TlTerminalBuffer {
                abi_version: ABI_VERSION + 1,
                struct_size: struct_size::<TlTerminalBuffer>(),
                data: ptr::null(),
                #[cfg(target_pointer_width = "32")]
                data_pointer_padding: 0,
                len: 0,
            },
        ] {
            // SAFETY: Rejected output metadata is fully readable and writable.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v2(terminal, &canonical, &mut invalid_output) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(invalid_output.data.is_null());
            assert_eq!(invalid_output.len, 0);
        }

        let mut encoded = empty_buffer();
        // SAFETY: Each null pointer is diagnosed before dereference.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(ptr::null(), &canonical, &mut encoded) },
            STATUS_NULL_POINTER
        );
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, ptr::null(), &mut encoded) },
            STATUS_NULL_POINTER
        );
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, &canonical, ptr::null_mut()) },
            STATUS_NULL_POINTER
        );
        assert!(encoded.data.is_null());
        assert_eq!(encoded.len, 0);

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn input_v3_zero_tail_preserves_v2_and_flags_zero_kitty_behavior() {
        let terminal = create(&config(8, 2, 0)).expect("create Input V3");

        let mut v2 = input_v2(INPUT_KIND_KEY);
        v2.key = INPUT_KEY_ARROW_UP;
        let mut v3 = input_v3(INPUT_KIND_KEY);
        v3.key = INPUT_KEY_ARROW_UP;
        let mut encoded_v2 = empty_buffer();
        let mut encoded_v3 = empty_buffer();
        // SAFETY: Both canonical inputs have identical known prefixes and empty outputs.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, &v2, &mut encoded_v2) },
            STATUS_OK
        );
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &v3, &mut encoded_v3) },
            STATUS_OK
        );
        // SAFETY: Each successful result owns one live token.
        assert_eq!(unsafe { copy_and_free(encoded_v2) }, b"\x1b[A");
        assert_eq!(unsafe { copy_and_free(encoded_v3) }, b"\x1b[A");

        let character = kitty_input(INPUT_KEY_CHARACTER, u32::from('x'));
        let mut encoded = empty_buffer();
        // SAFETY: A canonical Kitty press is valid before enhancements are enabled.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &character, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful legacy-compatible encoding owns one token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"x");
        encoded = empty_buffer();

        let text = "输入";
        let mut unidentified = kitty_input(INPUT_KEY_UNIDENTIFIED_TEXT, 0);
        unidentified.associated_text = text.as_ptr();
        unidentified.associated_text_len = u64::try_from(text.len()).expect("text length");
        // SAFETY: Canonical UTF-8 text remains readable for this call.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &unidentified, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful unidentified-text encoding owns one token.
        assert_eq!(unsafe { copy_and_free(encoded) }, text.as_bytes());

        // SAFETY: Release the live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn input_v3_negotiated_events_alternates_text_pua_and_f3_are_exact() {
        let terminal = create(&config(8, 2, 0)).expect("create negotiated Input V3");
        let negotiate = output(0, b"\x1b[=31u");
        // SAFETY: Test exclusively owns terminal and negotiation bytes.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &negotiate) },
            STATUS_OK
        );

        let legacy_v1 = input(INPUT_KIND_KEY, INPUT_KEY_ENTER, 0, 0);
        let mut legacy_v2 = input_v2(INPUT_KIND_KEY);
        legacy_v2.key = INPUT_KEY_ENTER;
        let mut encoded = empty_buffer();
        // SAFETY: Negotiated Kitty enhancements reject ambiguous old Key events.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v1(terminal, &legacy_v1, &mut encoded) },
            STATUS_UNSUPPORTED
        );
        assert_eq!(
            unsafe { tl_terminal_encode_input_v2(terminal, &legacy_v2, &mut encoded) },
            STATUS_UNSUPPORTED
        );
        assert!(encoded.data.is_null());
        assert_eq!(encoded.len, 0);

        let associated = "A";
        let mut rich = kitty_input(INPUT_KEY_CHARACTER, u32::from('a'));
        rich.key_event_type = INPUT_KEY_EVENT_REPEAT;
        rich.keyboard_modifiers = KEYBOARD_MODIFIER_SHIFT;
        rich.shifted_key = u32::from('A');
        rich.base_layout_key = u32::from('q');
        rich.associated_text = associated.as_ptr();
        rich.associated_text_len = u64::try_from(associated.len()).expect("text length");
        // SAFETY: Canonical layout-aware event and text are readable.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &rich, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful negotiated encoding owns one token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1b[97:65:113;2:2;65u");
        encoded = empty_buffer();

        let mut all_modifiers = kitty_input(INPUT_KEY_CHARACTER, u32::from('x'));
        all_modifiers.keyboard_modifiers = KEYBOARD_MODIFIERS_ALL;
        // SAFETY: All eight defined modifier bits map losslessly to core.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &all_modifiers, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful negotiated encoding owns one token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1b[120;256:1u");
        encoded = empty_buffer();

        let pua = kitty_input(INPUT_KEY_KITTY_PUA_FIRST, 0);
        // SAFETY: The first protocol PUA key is canonical Caps Lock.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &pua, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful PUA encoding owns one token.
        assert_eq!(unsafe { copy_and_free(encoded) }, b"\x1b[57358;1:1u");
        encoded = empty_buffer();

        let common_f3 = kitty_input(INPUT_KEY_F3, 0);
        let pua_f3 = kitty_input(INPUT_KEY_KITTY_PUA_FIRST + 8, 0);
        // SAFETY: Existing F3 and Kitty's PUA F3 alias are both unambiguous.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &common_f3, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful common F3 encoding owns one token.
        let common_bytes = unsafe { copy_and_free(encoded) };
        encoded = empty_buffer();
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &pua_f3, &mut encoded) },
            STATUS_OK
        );
        // SAFETY: Successful PUA alias encoding owns one token.
        assert_eq!(unsafe { copy_and_free(encoded) }, common_bytes);
        assert_eq!(common_bytes, b"\x1b[13;1:1~");

        // SAFETY: Release the live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn input_v3_rejects_noncanonical_tail_utf8_pointers_scalars_and_reserved_fields() {
        let terminal = create(&config(8, 2, 0)).expect("create malformed Input V3");
        let canonical = kitty_input(INPUT_KEY_CHARACTER, u32::from('x'));
        let bad_utf8 = [0xff_u8];

        let mut invalid_event = canonical;
        invalid_event.key_event_type = 0;
        let mut invalid_modifiers = canonical;
        invalid_modifiers.keyboard_modifiers = 1 << 31;
        let mut invalid_character = canonical;
        invalid_character.character = 0xd800;
        let mut invalid_shifted = canonical;
        invalid_shifted.shifted_key = 0xd800;
        let mut invalid_base = canonical;
        invalid_base.base_layout_key = 0x11_0000;
        let mut noncanonical_empty_pointer = canonical;
        noncanonical_empty_pointer.associated_text = ptr::dangling();
        let mut missing_pointer = canonical;
        missing_pointer.associated_text_len = 1;
        let mut invalid_utf8 = canonical;
        invalid_utf8.associated_text = bad_utf8.as_ptr();
        invalid_utf8.associated_text_len = 1;
        let mut reserved = canonical;
        reserved.reserved_v3[3] = 1;
        let mut legacy_modifier = canonical;
        legacy_modifier.modifiers = INPUT_MODIFIER_SHIFT;
        let mut functional_character = kitty_input(INPUT_KEY_F1, u32::from('x'));
        functional_character.key_event_type = INPUT_KEY_EVENT_PRESS;

        for invalid in [
            invalid_event,
            invalid_modifiers,
            invalid_character,
            invalid_shifted,
            invalid_base,
            noncanonical_empty_pointer,
            missing_pointer,
            invalid_utf8,
            reserved,
            legacy_modifier,
            functional_character,
        ] {
            let mut encoded = empty_buffer();
            // SAFETY: Every rejected pointer is either readable or rejected by
            // canonical metadata before any intentionally dangling access.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v3(terminal, &invalid, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
            assert_eq!(encoded.len, 0);
        }

        let mut oversized = canonical;
        oversized.associated_text = ptr::dangling();
        oversized.associated_text_len =
            u64::try_from(MAX_KITTY_ASSOCIATED_TEXT_BYTES + 1).expect("oversized length");
        let mut encoded = empty_buffer();
        // SAFETY: Oversized text is rejected before the dangling pointer is accessed.
        assert_eq!(
            unsafe { tl_terminal_encode_input_v3(terminal, &oversized, &mut encoded) },
            STATUS_LIMIT_EXCEEDED
        );

        let mut legacy = input_v3(INPUT_KIND_KEY);
        legacy.key = INPUT_KEY_ENTER;
        let mut tail_cases = [legacy; 7];
        tail_cases[0].key_event_type = INPUT_KEY_EVENT_PRESS;
        tail_cases[1].keyboard_modifiers = KEYBOARD_MODIFIER_SHIFT;
        tail_cases[2].shifted_key = u32::from('A');
        tail_cases[3].base_layout_key = u32::from('a');
        tail_cases[4].associated_text = ptr::dangling();
        tail_cases[5].associated_text_len = 1;
        tail_cases[6].reserved_v3[0] = 1;
        for invalid in &tail_cases {
            // SAFETY: Nonzero V3 tails on old kinds are rejected before pointer access.
            assert_eq!(
                unsafe { tl_terminal_encode_input_v3(terminal, invalid, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
            assert_eq!(encoded.len, 0);
        }

        // SAFETY: Release the live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn semantic_mouse_input_abi_covers_actions_tracking_and_wire_modes() {
        let sgr_terminal = create(&config(512, 4, 0)).expect("SGR terminal");
        let enable_sgr = output(0, b"\x1b[?1003h\x1b[?1006h");
        // SAFETY: Test owns the terminal and readable mode sequence.
        assert_eq!(
            unsafe { tl_terminal_apply_event(sgr_terminal, &enable_sgr) },
            STATUS_OK
        );

        let mut press = mouse_input(
            MOUSE_ACTION_PRESS,
            MOUSE_BUTTON_LEFT,
            MOUSE_WHEEL_NONE,
            MOUSE_BUTTON_HELD_LEFT,
            3,
            1,
        );
        press.modifiers = INPUT_MODIFIER_SHIFT
            | INPUT_MODIFIER_ALT
            | INPUT_MODIFIER_CONTROL
            | INPUT_MODIFIER_META;
        let actions = [
            (press, b"\x1b[<28;4;2M".as_slice()),
            (
                mouse_input(
                    MOUSE_ACTION_RELEASE,
                    MOUSE_BUTTON_RIGHT,
                    MOUSE_WHEEL_NONE,
                    0,
                    3,
                    1,
                ),
                b"\x1b[<2;4;2m".as_slice(),
            ),
            (
                mouse_input(
                    MOUSE_ACTION_MOVE,
                    MOUSE_BUTTON_NONE,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_MIDDLE,
                    3,
                    1,
                ),
                b"\x1b[<33;4;2M".as_slice(),
            ),
            (
                mouse_input(
                    MOUSE_ACTION_WHEEL,
                    MOUSE_BUTTON_NONE,
                    MOUSE_WHEEL_DOWN,
                    0,
                    3,
                    1,
                ),
                b"\x1b[<65;4;2M".as_slice(),
            ),
        ];
        for (input, expected) in actions {
            let mut encoded = empty_buffer();
            // SAFETY: Test owns a canonical mouse input and empty output.
            assert_eq!(
                unsafe { tl_terminal_encode_mouse_input_v1(sgr_terminal, &input, &mut encoded) },
                STATUS_OK
            );
            // SAFETY: Successful mouse encoding returned one owned token.
            assert_eq!(unsafe { copy_and_free(encoded) }, expected);
        }
        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(sgr_terminal) };

        let encoding_cases = [
            (
                config(512, 4, 0),
                b"\x1b[?1000h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_PRESS,
                    MOUSE_BUTTON_LEFT,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    3,
                    1,
                ),
                b"\x1b[M $\"".as_slice(),
            ),
            (
                config(512, 4, 0),
                b"\x1b[?1000h\x1b[?1005h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_PRESS,
                    MOUSE_BUTTON_LEFT,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    300,
                    1,
                ),
                b"\x1b[M \xc5\x8d\"".as_slice(),
            ),
            (
                config(512, 4, 0),
                b"\x1b[?1000h\x1b[?1006h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_PRESS,
                    MOUSE_BUTTON_LEFT,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    3,
                    1,
                ),
                b"\x1b[<0;4;2M".as_slice(),
            ),
            (
                config(512, 4, 0),
                b"\x1b[?1000h\x1b[?1015h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_PRESS,
                    MOUSE_BUTTON_LEFT,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    3,
                    1,
                ),
                b"\x1b[32;4;2M".as_slice(),
            ),
            (
                config(512, 4, 0),
                b"\x1b[?1000h\x1b[?1016h".as_slice(),
                {
                    let mut input = mouse_input(
                        MOUSE_ACTION_PRESS,
                        MOUSE_BUTTON_LEFT,
                        MOUSE_WHEEL_NONE,
                        MOUSE_BUTTON_HELD_LEFT,
                        3,
                        1,
                    );
                    input.position_flags = MOUSE_POSITION_PIXEL;
                    input.pixel_x = 49;
                    input.pixel_y = 29;
                    input
                },
                b"\x1b[<0;50;30M".as_slice(),
            ),
        ];
        for (config, modes, input, expected) in encoding_cases {
            let terminal = create(&config).expect("encoding terminal");
            let enable_modes = output(0, modes);
            // SAFETY: Test owns the terminal and readable mode sequence.
            assert_eq!(
                unsafe { tl_terminal_apply_event(terminal, &enable_modes) },
                STATUS_OK
            );
            let mut encoded = empty_buffer();
            // SAFETY: Test owns a canonical mouse input and empty output.
            assert_eq!(
                unsafe { tl_terminal_encode_mouse_input_v1(terminal, &input, &mut encoded) },
                STATUS_OK
            );
            // SAFETY: Successful mouse encoding returned one owned token.
            assert_eq!(unsafe { copy_and_free(encoded) }, expected);
            // SAFETY: Test releases its live terminal exactly once.
            unsafe { tl_terminal_destroy(terminal) };
        }

        let tracking_cases = [
            (
                b"".as_slice(),
                mouse_input(
                    MOUSE_ACTION_PRESS,
                    MOUSE_BUTTON_LEFT,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    1,
                    1,
                ),
                b"".as_slice(),
            ),
            (
                b"\x1b[?9h\x1b[?1006h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_PRESS,
                    MOUSE_BUTTON_LEFT,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    1,
                    1,
                ),
                b"\x1b[<0;2;2M".as_slice(),
            ),
            (
                b"\x1b[?1000h\x1b[?1006h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_MOVE,
                    MOUSE_BUTTON_NONE,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    1,
                    1,
                ),
                b"".as_slice(),
            ),
            (
                b"\x1b[?1002h\x1b[?1006h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_MOVE,
                    MOUSE_BUTTON_NONE,
                    MOUSE_WHEEL_NONE,
                    MOUSE_BUTTON_HELD_LEFT,
                    1,
                    1,
                ),
                b"\x1b[<32;2;2M".as_slice(),
            ),
            (
                b"\x1b[?1003h\x1b[?1006h".as_slice(),
                mouse_input(
                    MOUSE_ACTION_MOVE,
                    MOUSE_BUTTON_NONE,
                    MOUSE_WHEEL_NONE,
                    0,
                    1,
                    1,
                ),
                b"\x1b[<35;2;2M".as_slice(),
            ),
        ];
        for (modes, input, expected) in tracking_cases {
            let terminal = create(&config(8, 4, 0)).expect("tracking terminal");
            if !modes.is_empty() {
                let enable_modes = output(0, modes);
                // SAFETY: Test owns the terminal and readable mode sequence.
                assert_eq!(
                    unsafe { tl_terminal_apply_event(terminal, &enable_modes) },
                    STATUS_OK
                );
            }
            let mut encoded = empty_buffer();
            // SAFETY: Test owns a canonical mouse input and empty output.
            assert_eq!(
                unsafe { tl_terminal_encode_mouse_input_v1(terminal, &input, &mut encoded) },
                STATUS_OK
            );
            assert!(!encoded.data.is_null());
            // SAFETY: Even a successful empty result owns a releasable token.
            assert_eq!(unsafe { copy_and_free(encoded) }, expected);
            // SAFETY: Test releases its live terminal exactly once.
            unsafe { tl_terminal_destroy(terminal) };
        }
    }

    #[test]
    fn semantic_mouse_input_abi_rejects_malformed_flags_and_button_state() {
        let terminal = create(&config(8, 4, 0)).expect("create");
        let canonical = mouse_input(
            MOUSE_ACTION_PRESS,
            MOUSE_BUTTON_LEFT,
            MOUSE_WHEEL_NONE,
            MOUSE_BUTTON_HELD_LEFT,
            1,
            1,
        );
        let mut wrong_version = canonical;
        wrong_version.abi_version = ABI_VERSION + 1;
        let mut short = canonical;
        short.struct_size = struct_size::<TlTerminalMouseInputV1>() - 1;
        for input in [&wrong_version, &short] {
            let mut encoded = empty_buffer();
            // SAFETY: A readable but incompatible ABI prefix is rejected.
            assert_eq!(
                unsafe { tl_terminal_encode_mouse_input_v1(terminal, input, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
        }

        let mut malformed = [canonical; 16];
        malformed[0].action = 0;
        malformed[1].button = MOUSE_BUTTON_NONE;
        malformed[2].wheel = MOUSE_WHEEL_UP;
        malformed[3].button = 99;
        malformed[4].action = MOUSE_ACTION_MOVE;
        malformed[5].action = MOUSE_ACTION_MOVE;
        malformed[5].button = MOUSE_BUTTON_NONE;
        malformed[5].wheel = MOUSE_WHEEL_UP;
        malformed[6].action = MOUSE_ACTION_WHEEL;
        malformed[6].wheel = MOUSE_WHEEL_UP;
        malformed[7].action = MOUSE_ACTION_WHEEL;
        malformed[7].button = MOUSE_BUTTON_NONE;
        malformed[7].wheel = MOUSE_WHEEL_NONE;
        malformed[8].buttons_held = 1 << 31;
        malformed[9].position_flags = 1 << 31;
        malformed[10].pixel_x = 1;
        malformed[11].pixel_y = 1;
        malformed[12].modifiers = 1 << 31;
        malformed[13].reserved[0] = 1;
        malformed[14].reserved[1] = 1;
        malformed[15].action = MOUSE_ACTION_WHEEL;
        malformed[15].button = MOUSE_BUTTON_NONE;
        malformed[15].wheel = 99;

        for input in &malformed {
            let mut encoded = empty_buffer();
            // SAFETY: Every malformed field is rejected without an output token.
            assert_eq!(
                unsafe { tl_terminal_encode_mouse_input_v1(terminal, input, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
            assert_eq!(encoded.len, 0);
        }

        let inconsistent = [
            mouse_input(
                MOUSE_ACTION_PRESS,
                MOUSE_BUTTON_LEFT,
                MOUSE_WHEEL_NONE,
                0,
                1,
                1,
            ),
            mouse_input(
                MOUSE_ACTION_RELEASE,
                MOUSE_BUTTON_RIGHT,
                MOUSE_WHEEL_NONE,
                MOUSE_BUTTON_HELD_RIGHT,
                1,
                1,
            ),
        ];
        for input in &inconsistent {
            let mut encoded = empty_buffer();
            // SAFETY: Core button-state validation maps to invalid argument.
            assert_eq!(
                unsafe { tl_terminal_encode_mouse_input_v1(terminal, input, &mut encoded) },
                STATUS_INVALID_ARGUMENT
            );
            assert!(encoded.data.is_null());
        }

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };
    }

    #[test]
    fn semantic_mouse_input_abi_maps_pixel_coordinates_and_failed_closed() {
        let terminal = create(&config(300, 4, 0)).expect("create");
        let enable_pixels = output(0, b"\x1b[?1000h\x1b[?1016h");
        // SAFETY: Test owns the terminal and readable mode sequence.
        assert_eq!(
            unsafe { tl_terminal_apply_event(terminal, &enable_pixels) },
            STATUS_OK
        );
        let without_pixel = mouse_input(
            MOUSE_ACTION_PRESS,
            MOUSE_BUTTON_LEFT,
            MOUSE_WHEEL_NONE,
            MOUSE_BUTTON_HELD_LEFT,
            1,
            1,
        );
        let mut encoded = empty_buffer();
        // SAFETY: Missing required mode-1016 pixel coordinates are rejected.
        assert_eq!(
            unsafe { tl_terminal_encode_mouse_input_v1(terminal, &without_pixel, &mut encoded) },
            STATUS_INVALID_ARGUMENT
        );

        let mut overflow = without_pixel;
        overflow.position_flags = MOUSE_POSITION_PIXEL;
        overflow.pixel_x = u32::MAX;
        // SAFETY: A representational coordinate overflow maps to the limit status.
        assert_eq!(
            unsafe { tl_terminal_encode_mouse_input_v1(terminal, &overflow, &mut encoded) },
            STATUS_LIMIT_EXCEEDED
        );

        let mut outside = without_pixel;
        outside.cell_column = 300;
        outside.position_flags = MOUSE_POSITION_PIXEL;
        // SAFETY: A cell outside current terminal dimensions is an invalid argument.
        assert_eq!(
            unsafe { tl_terminal_encode_mouse_input_v1(terminal, &outside, &mut encoded) },
            STATUS_INVALID_ARGUMENT
        );
        assert!(encoded.data.is_null());

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(terminal) };

        let default_terminal = create(&config(300, 4, 0)).expect("default terminal");
        let enable_default = output(0, b"\x1b[?1000h");
        // SAFETY: Test owns the terminal and readable mode sequence.
        assert_eq!(
            unsafe { tl_terminal_apply_event(default_terminal, &enable_default) },
            STATUS_OK
        );
        let encoding_overflow = mouse_input(
            MOUSE_ACTION_PRESS,
            MOUSE_BUTTON_LEFT,
            MOUSE_WHEEL_NONE,
            MOUSE_BUTTON_HELD_LEFT,
            223,
            1,
        );
        // SAFETY: In-bounds cell is above the default wire encoding's maximum.
        assert_eq!(
            unsafe {
                tl_terminal_encode_mouse_input_v1(
                    default_terminal,
                    &encoding_overflow,
                    &mut encoded,
                )
            },
            STATUS_LIMIT_EXCEEDED
        );

        let unsupported = output(1, b"\x01");
        // SAFETY: Unsupported output permanently fails the terminal closed.
        assert_eq!(
            unsafe { tl_terminal_apply_event(default_terminal, &unsupported) },
            STATUS_UNSUPPORTED
        );
        let valid = mouse_input(
            MOUSE_ACTION_PRESS,
            MOUSE_BUTTON_LEFT,
            MOUSE_WHEEL_NONE,
            MOUSE_BUTTON_HELD_LEFT,
            1,
            1,
        );
        // SAFETY: A failed-closed terminal is live but unavailable for encoding.
        assert_eq!(
            unsafe { tl_terminal_encode_mouse_input_v1(default_terminal, &valid, &mut encoded) },
            STATUS_FAILED_CLOSED
        );
        assert!(encoded.data.is_null());

        // SAFETY: Test releases its live terminal exactly once.
        unsafe { tl_terminal_destroy(default_terminal) };
    }

    #[test]
    fn rust_layout_and_header_contract_stay_in_sync() {
        assert_eq!(mem::offset_of!(TlTerminalConfigV1, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalConfigV1, struct_size), 4);
        assert_eq!(struct_size::<TlTerminalConfigV1>(), 40);
        assert_eq!(mem::offset_of!(TlTerminalConfigV2, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalConfigV2, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalConfigV2, columns), 8);
        assert_eq!(mem::offset_of!(TlTerminalConfigV2, rows), 12);
        assert_eq!(
            mem::offset_of!(TlTerminalConfigV2, initial_source_sequence),
            16
        );
        assert_eq!(mem::offset_of!(TlTerminalConfigV2, reserved_v1_compat), 24);
        assert_eq!(
            mem::offset_of!(TlTerminalConfigV2, history_capacity_rows),
            40
        );
        assert_eq!(mem::offset_of!(TlTerminalConfigV2, reserved), 48);
        assert_eq!(struct_size::<TlTerminalConfigV2>(), 64);
        assert_eq!(tl_terminal_config_v2_struct_size(), 64);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, columns), 8);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, rows), 12);
        assert_eq!(
            mem::offset_of!(TlTerminalConfigV3, initial_source_sequence),
            16
        );
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, reserved_v1_compat), 24);
        assert_eq!(
            mem::offset_of!(TlTerminalConfigV3, history_capacity_rows),
            40
        );
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, reserved_v2_compat), 48);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, output_profile), 64);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, reserved0), 68);
        assert_eq!(mem::offset_of!(TlTerminalConfigV3, reserved), 72);
        assert_eq!(struct_size::<TlTerminalConfigV3>(), 88);
        assert_eq!(tl_terminal_config_v3_struct_size(), 88);
        assert_eq!(mem::offset_of!(TlTerminalEvent, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalEvent, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalEvent, data), 24);
        assert_eq!(mem::offset_of!(TlTerminalEvent, advance_micros), 48);
        assert_eq!(struct_size::<TlTerminalEvent>(), 56);
        assert_eq!(mem::offset_of!(TlTerminalBuffer, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalBuffer, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalBuffer, data), 8);
        assert_eq!(mem::offset_of!(TlTerminalBuffer, len), 16);
        assert_eq!(struct_size::<TlTerminalBuffer>(), 24);
        assert_eq!(mem::offset_of!(TlTerminalCheckpointInfo, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalCheckpointInfo, struct_size), 4);
        assert_eq!(
            mem::offset_of!(TlTerminalPresentationInfoV1, abi_version),
            0
        );
        assert_eq!(
            mem::offset_of!(TlTerminalPresentationInfoV1, struct_size),
            4
        );
        assert_eq!(mem::offset_of!(TlTerminalPresentationInfoV1, flags), 8);
        assert_eq!(mem::offset_of!(TlTerminalPresentationInfoV1, reserved0), 12);
        assert_eq!(
            mem::offset_of!(TlTerminalPresentationInfoV1, next_source_sequence),
            16
        );
        assert_eq!(
            mem::offset_of!(TlTerminalPresentationInfoV1, logical_time_micros),
            24
        );
        assert_eq!(
            mem::offset_of!(
                TlTerminalPresentationInfoV1,
                synchronized_output_deadline_micros
            ),
            32
        );
        assert_eq!(mem::offset_of!(TlTerminalPresentationInfoV1, reserved), 40);
        assert_eq!(struct_size::<TlTerminalPresentationInfoV1>(), 48);
        assert_eq!(tl_terminal_presentation_info_v1_struct_size(), 48);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, kind), 8);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, key), 12);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, modifiers), 16);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, character), 20);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, data), 24);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, data_len), 32);
        assert_eq!(mem::offset_of!(TlTerminalInputV1, reserved), 40);
        assert_eq!(struct_size::<TlTerminalInputV1>(), 56);
        assert_eq!(tl_terminal_input_v1_struct_size(), 56);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, kind), 8);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, key), 12);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, modifiers), 16);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, character), 20);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, data), 24);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, data_len), 32);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, reserved_v1_compat), 40);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, win32_virtual_key), 56);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, win32_scan_code), 60);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, win32_unicode_char), 64);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, win32_key_down), 68);
        assert_eq!(
            mem::offset_of!(TlTerminalInputV2, win32_control_key_state),
            72
        );
        assert_eq!(mem::offset_of!(TlTerminalInputV2, win32_repeat_count), 76);
        assert_eq!(mem::offset_of!(TlTerminalInputV2, reserved), 80);
        assert_eq!(struct_size::<TlTerminalInputV2>(), 96);
        assert_eq!(tl_terminal_input_v2_struct_size(), 96);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, kind), 8);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, key), 12);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, modifiers), 16);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, character), 20);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, data), 24);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, data_len), 32);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, reserved_v1_compat), 40);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, win32_virtual_key), 56);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, win32_scan_code), 60);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, win32_unicode_char), 64);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, win32_key_down), 68);
        assert_eq!(
            mem::offset_of!(TlTerminalInputV3, win32_control_key_state),
            72
        );
        assert_eq!(mem::offset_of!(TlTerminalInputV3, win32_repeat_count), 76);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, reserved), 80);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, key_event_type), 96);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, keyboard_modifiers), 100);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, shifted_key), 104);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, base_layout_key), 108);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, associated_text), 112);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, associated_text_len), 120);
        assert_eq!(mem::offset_of!(TlTerminalInputV3, reserved_v3), 128);
        assert_eq!(struct_size::<TlTerminalInputV3>(), 160);
        assert_eq!(tl_terminal_input_v3_struct_size(), 160);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, abi_version), 0);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, struct_size), 4);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, action), 8);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, button), 12);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, wheel), 16);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, buttons_held), 20);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, position_flags), 24);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, modifiers), 28);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, cell_column), 32);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, cell_row), 36);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, pixel_x), 40);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, pixel_y), 44);
        assert_eq!(mem::offset_of!(TlTerminalMouseInputV1, reserved), 48);
        assert_eq!(struct_size::<TlTerminalMouseInputV1>(), 64);
        assert_eq!(tl_terminal_mouse_input_v1_struct_size(), 64);

        let header = include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../include/termlink_terminal.h"
        ));
        assert!(header.contains("defined(TL_TERMINAL_STATIC)"));
        assert!(header.contains("typedef uint32_t TlTerminalStatus;"));
        assert!(header.contains("typedef uint32_t TlTerminalEventKind;"));
        assert!(header.contains("typedef uint32_t TlTerminalOutputProfile;"));
        assert!(!header.contains("typedef enum TlTerminal"));
        assert!(header.contains("#define TL_OUTPUT_PROFILE_UTF8 UINT32_C(0)"));
        assert!(header.contains("#define TL_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1 UINT32_C(1)"));
        assert!(header.contains("typedef struct TlTerminalConfigV3"));
        assert!(header.contains("static_assert(sizeof(TlTerminalConfigV3) == 88"));
        assert!(header.contains("_Static_assert(sizeof(TlTerminalConfigV3) == 88"));
        assert!(header.contains("tl_terminal_config_v3_struct_size"));
        assert!(header.contains("tl_terminal_create_v3"));
        assert!(header.contains("tl_terminal_checkpoint_export_snapshot"));
        assert!(header.contains("tl_terminal_checkpoint_export_render_frame_v1"));
        assert!(header.contains("tl_terminal_checkpoint_export_render_frame_v2"));
        assert!(header.contains("tl_terminal_checkpoint_export_render_frame_v3"));
        assert!(header.contains("tl_terminal_checkpoint_get_scrollback_info_v1"));
        assert!(header.contains("tl_terminal_checkpoint_export_render_frame_v3_at_scrollback"));
        assert!(header.contains("#define TL_STATUS_PRESENTATION_DEFERRED UINT32_C(13)"));
        assert!(header.contains("#define TL_STATUS_HOST_EFFECT_VERSION_REQUIRED UINT32_C(14)"));
        assert!(header.contains("typedef struct TlTerminalPresentationInfoV1"));
        assert!(header.contains("tl_terminal_presentation_info_v1_struct_size"));
        assert!(header.contains("tl_terminal_get_presentation_info_v1"));
        assert!(header.contains("#define TL_PRESENTATION_FLAG_FAILED_CLOSED UINT32_C(1)"));
        assert!(header.contains("#define TL_PRESENTATION_FLAG_PARSER_INCOMPLETE UINT32_C(2)"));
        assert!(header.contains("#define TL_PRESENTATION_FLAG_SYNCHRONIZED_OUTPUT UINT32_C(4)"));
        assert!(header.contains("tl_terminal_render_frame_v1_format_version"));
        assert!(header.contains("tl_terminal_render_frame_v1_max_bytes"));
        assert!(header.contains("tl_terminal_render_frame_v2_format_version"));
        assert!(header.contains("tl_terminal_render_frame_v2_max_bytes"));
        assert!(header.contains("tl_terminal_render_frame_v3_format_version"));
        assert!(header.contains("tl_terminal_render_frame_v3_max_bytes"));
        assert!(header.contains("#define TL_RENDER_FRAME_V1_FORMAT_VERSION UINT32_C(1)"));
        assert!(header.contains("#define TL_RENDER_FRAME_V1_MAX_BYTES UINT64_C(16777216)"));
        assert!(header.contains("#define TL_RENDER_FRAME_V2_FORMAT_VERSION UINT32_C(2)"));
        assert!(header.contains("#define TL_RENDER_FRAME_V2_MAX_BYTES UINT64_C(16777216)"));
        assert!(header.contains("#define TL_RENDER_FRAME_V3_FORMAT_VERSION UINT32_C(3)"));
        assert!(header.contains("#define TL_RENDER_FRAME_V3_MAX_BYTES UINT64_C(16777216)"));
        assert!(header.contains(
            "V3 is produced explicitly from core state and is not an upgraded earlier frame."
        ));
        assert!(header.contains("tl_terminal_host_effect_frame_v1_format_version"));
        assert!(header.contains("tl_terminal_host_effect_frame_v1_max_bytes"));
        assert!(header.contains("#define TL_HOST_EFFECT_FRAME_V1_FORMAT_VERSION UINT32_C(1)"));
        assert!(header.contains("#define TL_HOST_EFFECT_FRAME_V1_MAX_BYTES UINT64_C(65710862)"));
        assert!(header.contains("tl_terminal_host_effect_frame_v2_format_version"));
        assert!(header.contains("tl_terminal_host_effect_frame_v2_max_bytes"));
        assert!(header.contains("#define TL_HOST_EFFECT_FRAME_V2_FORMAT_VERSION UINT32_C(2)"));
        assert!(header.contains("#define TL_HOST_EFFECT_FRAME_V2_MAX_BYTES UINT64_C(123033008)"));
        assert!(header.contains("tl_terminal_apply_event_batch_v1"));
        assert!(header.contains("tl_terminal_apply_event_with_host_effects_v1"));
        assert!(header.contains("tl_terminal_apply_event_batch_with_host_effects_v1"));
        assert!(header.contains("tl_terminal_apply_event_with_host_effects_v2"));
        assert!(header.contains("tl_terminal_apply_event_batch_with_host_effects_v2"));
        assert!(header.contains("static_assert(sizeof(TlTerminalEvent) == 56"));
        assert!(header.contains("_Static_assert(sizeof(TlTerminalBuffer) == 24"));
        assert!(header.contains("tl_terminal_encode_input_v1"));
        assert!(header.contains("typedef struct TlTerminalInputV1"));
        assert!(header.contains("static_assert(sizeof(TlTerminalInputV1) == 56"));
        assert!(header.contains("#define TL_INPUT_KIND_WIN32_KEY UINT32_C(5)"));
        assert!(header.contains("tl_terminal_input_v2_struct_size"));
        assert!(header.contains("tl_terminal_encode_input_v2"));
        assert!(header.contains("typedef struct TlTerminalInputV2"));
        assert!(header.contains("static_assert(sizeof(TlTerminalInputV2) == 96"));
        assert!(header.contains("tl_terminal_input_v3_struct_size"));
        assert!(header.contains("tl_terminal_encode_input_v3"));
        assert!(header.contains("typedef struct TlTerminalInputV3"));
        assert!(header.contains("static_assert(sizeof(TlTerminalInputV3) == 160"));
        assert!(header.contains("#define TL_INPUT_KIND_KITTY_KEY UINT32_C(6)"));
        assert!(header.contains("#define TL_INPUT_KEY_EVENT_RELEASE UINT32_C(3)"));
        assert!(header.contains("#define TL_KEYBOARD_MODIFIER_NUM_LOCK UINT32_C(128)"));
        assert!(header.contains("#define TL_INPUT_KEY_KITTY_PUA_FIRST UINT32_C(57358)"));
        assert!(header.contains("#define TL_INPUT_KEY_KITTY_PUA_LAST UINT32_C(57454)"));
        assert!(header.contains("#define TL_INPUT_KEY_UNIDENTIFIED_TEXT UINT32_MAX"));
        assert!(header.contains("tl_terminal_mouse_input_v1_struct_size"));
        assert!(header.contains("tl_terminal_encode_mouse_input_v1"));
        assert!(header.contains("typedef struct TlTerminalMouseInputV1"));
        assert!(header.contains("#define TL_MOUSE_ACTION_PRESS UINT32_C(1)"));
        assert!(header.contains("#define TL_MOUSE_ACTION_RELEASE UINT32_C(2)"));
        assert!(header.contains("#define TL_MOUSE_ACTION_MOVE UINT32_C(3)"));
        assert!(header.contains("#define TL_MOUSE_ACTION_WHEEL UINT32_C(4)"));
        assert!(header.contains("#define TL_MOUSE_BUTTON_RIGHT UINT32_C(3)"));
        assert!(header.contains("#define TL_MOUSE_WHEEL_RIGHT UINT32_C(4)"));
        assert!(header.contains("#define TL_MOUSE_BUTTON_HELD_RIGHT UINT32_C(4)"));
        assert!(header.contains("#define TL_MOUSE_POSITION_PIXEL UINT32_C(1)"));
        assert!(header.contains("#define TL_INPUT_KIND_FOCUS_GAINED UINT32_C(3)"));
        assert!(header.contains("#define TL_INPUT_KIND_FOCUS_LOST UINT32_C(4)"));
        assert!(header.contains("TlTerminalBuffer *buffer"));
    }

    #[test]
    fn max_snapshot_size_query_matches_core_and_header() {
        assert_eq!(
            tl_terminal_max_snapshot_bytes(),
            u64::try_from(MAX_SNAPSHOT_BYTES).expect("maximum snapshot size fits in u64")
        );

        let header = include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../include/termlink_terminal.h"
        ));
        assert!(header.contains("uint64_t tl_terminal_max_snapshot_bytes(void);"));
    }
}
