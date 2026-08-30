#ifndef TERMLINK_TERMINAL_H
#define TERMLINK_TERMINAL_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Define TL_TERMINAL_STATIC when consuming the static library on Windows. */
#if defined(_WIN32) && !defined(TL_TERMINAL_STATIC)
#  if defined(TERMLINK_TERMINAL_BUILD)
#    define TL_TERMINAL_API __declspec(dllexport)
#  else
#    define TL_TERMINAL_API __declspec(dllimport)
#  endif
#else
#  define TL_TERMINAL_API
#endif

#define TL_TERMINAL_ABI_VERSION UINT32_C(1)
#define TL_RENDER_FRAME_V1_FORMAT_VERSION UINT32_C(1)
#define TL_RENDER_FRAME_V1_MAX_BYTES UINT64_C(16777216)
#define TL_RENDER_FRAME_V2_FORMAT_VERSION UINT32_C(2)
#define TL_RENDER_FRAME_V2_MAX_BYTES UINT64_C(16777216)
#define TL_RENDER_FRAME_V3_FORMAT_VERSION UINT32_C(3)
#define TL_RENDER_FRAME_V3_MAX_BYTES UINT64_C(16777216)
#define TL_HOST_EFFECT_FRAME_V1_FORMAT_VERSION UINT32_C(1)
#define TL_HOST_EFFECT_FRAME_V1_MAX_BYTES UINT64_C(65710862)
#define TL_HOST_EFFECT_FRAME_V2_FORMAT_VERSION UINT32_C(2)
#define TL_HOST_EFFECT_FRAME_V2_MAX_BYTES UINT64_C(123033008)

typedef struct TlTerminal TlTerminal;
typedef struct TlTerminalCheckpoint TlTerminalCheckpoint;

/* ABI-stable status values. These are uint32_t, not C enums. */
typedef uint32_t TlTerminalStatus;
#define TL_STATUS_OK UINT32_C(0)
#define TL_STATUS_NULL_POINTER UINT32_C(1)
#define TL_STATUS_INVALID_ARGUMENT UINT32_C(2)
#define TL_STATUS_INVALID_DIMENSIONS UINT32_C(3)
#define TL_STATUS_LIMIT_EXCEEDED UINT32_C(4)
#define TL_STATUS_SEQUENCE_MISMATCH UINT32_C(5)
#define TL_STATUS_FAILED_CLOSED UINT32_C(6)
#define TL_STATUS_UNSUPPORTED UINT32_C(7)
#define TL_STATUS_GAP UINT32_C(8)
#define TL_STATUS_MALFORMED_SNAPSHOT UINT32_C(9)
#define TL_STATUS_INCOMPATIBLE_SNAPSHOT UINT32_C(10)
#define TL_STATUS_TIME_OVERFLOW UINT32_C(11)
#define TL_STATUS_SNAPSHOT_NOT_ELIGIBLE UINT32_C(12)
#define TL_STATUS_PRESENTATION_DEFERRED UINT32_C(13)
#define TL_STATUS_HOST_EFFECT_VERSION_REQUIRED UINT32_C(14)
#define TL_STATUS_PANIC UINT32_C(255)

/* ABI-stable presentation eligibility flags. */
typedef uint32_t TlTerminalPresentationFlags;
#define TL_PRESENTATION_FLAG_FAILED_CLOSED UINT32_C(1)
#define TL_PRESENTATION_FLAG_PARSER_INCOMPLETE UINT32_C(2)
#define TL_PRESENTATION_FLAG_SYNCHRONIZED_OUTPUT UINT32_C(4)

/* ABI-stable event kinds. These are uint32_t, not C enums. */
typedef uint32_t TlTerminalEventKind;
#define TL_EVENT_OUTPUT UINT32_C(1)
#define TL_EVENT_RESIZE UINT32_C(2)
#define TL_EVENT_ADVANCE UINT32_C(3)
#define TL_EVENT_GAP UINT32_C(4)

/* ABI-stable semantic input kinds, keys, and modifier flags. */
typedef uint32_t TlTerminalInputKind;
#define TL_INPUT_KIND_KEY UINT32_C(1)
#define TL_INPUT_KIND_PASTE UINT32_C(2)
#define TL_INPUT_KIND_FOCUS_GAINED UINT32_C(3)
#define TL_INPUT_KIND_FOCUS_LOST UINT32_C(4)
#define TL_INPUT_KIND_WIN32_KEY UINT32_C(5)
#define TL_INPUT_KIND_KITTY_KEY UINT32_C(6)

typedef uint32_t TlTerminalKey;
#define TL_INPUT_KEY_CHARACTER UINT32_C(1)
#define TL_INPUT_KEY_ENTER UINT32_C(2)
#define TL_INPUT_KEY_BACKSPACE UINT32_C(3)
#define TL_INPUT_KEY_TAB UINT32_C(4)
#define TL_INPUT_KEY_ESCAPE UINT32_C(5)
#define TL_INPUT_KEY_DELETE UINT32_C(6)
#define TL_INPUT_KEY_INSERT UINT32_C(7)
#define TL_INPUT_KEY_HOME UINT32_C(8)
#define TL_INPUT_KEY_END UINT32_C(9)
#define TL_INPUT_KEY_PAGE_UP UINT32_C(10)
#define TL_INPUT_KEY_PAGE_DOWN UINT32_C(11)
#define TL_INPUT_KEY_ARROW_UP UINT32_C(12)
#define TL_INPUT_KEY_ARROW_DOWN UINT32_C(13)
#define TL_INPUT_KEY_ARROW_LEFT UINT32_C(14)
#define TL_INPUT_KEY_ARROW_RIGHT UINT32_C(15)
#define TL_INPUT_KEY_F1 UINT32_C(16)
#define TL_INPUT_KEY_F2 UINT32_C(17)
#define TL_INPUT_KEY_F3 UINT32_C(18)
#define TL_INPUT_KEY_F4 UINT32_C(19)
#define TL_INPUT_KEY_F5 UINT32_C(20)
#define TL_INPUT_KEY_F6 UINT32_C(21)
#define TL_INPUT_KEY_F7 UINT32_C(22)
#define TL_INPUT_KEY_F8 UINT32_C(23)
#define TL_INPUT_KEY_F9 UINT32_C(24)
#define TL_INPUT_KEY_F10 UINT32_C(25)
#define TL_INPUT_KEY_F11 UINT32_C(26)
#define TL_INPUT_KEY_F12 UINT32_C(27)
#define TL_INPUT_KEY_KEYPAD_0 UINT32_C(100)
#define TL_INPUT_KEY_KEYPAD_1 UINT32_C(101)
#define TL_INPUT_KEY_KEYPAD_2 UINT32_C(102)
#define TL_INPUT_KEY_KEYPAD_3 UINT32_C(103)
#define TL_INPUT_KEY_KEYPAD_4 UINT32_C(104)
#define TL_INPUT_KEY_KEYPAD_5 UINT32_C(105)
#define TL_INPUT_KEY_KEYPAD_6 UINT32_C(106)
#define TL_INPUT_KEY_KEYPAD_7 UINT32_C(107)
#define TL_INPUT_KEY_KEYPAD_8 UINT32_C(108)
#define TL_INPUT_KEY_KEYPAD_9 UINT32_C(109)
#define TL_INPUT_KEY_KEYPAD_DECIMAL UINT32_C(110)
#define TL_INPUT_KEY_KEYPAD_SEPARATOR UINT32_C(111)
#define TL_INPUT_KEY_KEYPAD_ADD UINT32_C(112)
#define TL_INPUT_KEY_KEYPAD_SUBTRACT UINT32_C(113)
#define TL_INPUT_KEY_KEYPAD_MULTIPLY UINT32_C(114)
#define TL_INPUT_KEY_KEYPAD_DIVIDE UINT32_C(115)
#define TL_INPUT_KEY_KEYPAD_EQUAL UINT32_C(116)
#define TL_INPUT_KEY_KEYPAD_ENTER UINT32_C(117)
#define TL_INPUT_KEY_KITTY_PUA_FIRST UINT32_C(57358)
#define TL_INPUT_KEY_KITTY_PUA_LAST UINT32_C(57454)
#define TL_INPUT_KEY_UNIDENTIFIED_TEXT UINT32_MAX

typedef uint32_t TlTerminalInputModifiers;
#define TL_INPUT_MODIFIER_SHIFT UINT32_C(1)
#define TL_INPUT_MODIFIER_ALT UINT32_C(2)
#define TL_INPUT_MODIFIER_CONTROL UINT32_C(4)
#define TL_INPUT_MODIFIER_META UINT32_C(8)

typedef uint32_t TlTerminalInputKeyEventType;
#define TL_INPUT_KEY_EVENT_PRESS UINT32_C(1)
#define TL_INPUT_KEY_EVENT_REPEAT UINT32_C(2)
#define TL_INPUT_KEY_EVENT_RELEASE UINT32_C(3)

/* ABI-stable terminal output-byte interpretation profiles. */
typedef uint32_t TlTerminalOutputProfile;
#define TL_OUTPUT_PROFILE_UTF8 UINT32_C(0)
#define TL_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1 UINT32_C(1)

typedef uint32_t TlTerminalKeyboardModifiers;
#define TL_KEYBOARD_MODIFIER_SHIFT UINT32_C(1)
#define TL_KEYBOARD_MODIFIER_ALT UINT32_C(2)
#define TL_KEYBOARD_MODIFIER_CONTROL UINT32_C(4)
#define TL_KEYBOARD_MODIFIER_SUPER UINT32_C(8)
#define TL_KEYBOARD_MODIFIER_HYPER UINT32_C(16)
#define TL_KEYBOARD_MODIFIER_META UINT32_C(32)
#define TL_KEYBOARD_MODIFIER_CAPS_LOCK UINT32_C(64)
#define TL_KEYBOARD_MODIFIER_NUM_LOCK UINT32_C(128)

/* ABI-stable semantic mouse action and payload values. */
typedef uint32_t TlTerminalMouseAction;
#define TL_MOUSE_ACTION_PRESS UINT32_C(1)
#define TL_MOUSE_ACTION_RELEASE UINT32_C(2)
#define TL_MOUSE_ACTION_MOVE UINT32_C(3)
#define TL_MOUSE_ACTION_WHEEL UINT32_C(4)

typedef uint32_t TlTerminalMouseButton;
#define TL_MOUSE_BUTTON_NONE UINT32_C(0)
#define TL_MOUSE_BUTTON_LEFT UINT32_C(1)
#define TL_MOUSE_BUTTON_MIDDLE UINT32_C(2)
#define TL_MOUSE_BUTTON_RIGHT UINT32_C(3)

typedef uint32_t TlTerminalMouseWheelDirection;
#define TL_MOUSE_WHEEL_NONE UINT32_C(0)
#define TL_MOUSE_WHEEL_UP UINT32_C(1)
#define TL_MOUSE_WHEEL_DOWN UINT32_C(2)
#define TL_MOUSE_WHEEL_LEFT UINT32_C(3)
#define TL_MOUSE_WHEEL_RIGHT UINT32_C(4)

typedef uint32_t TlTerminalMouseButtons;
#define TL_MOUSE_BUTTON_HELD_LEFT UINT32_C(1)
#define TL_MOUSE_BUTTON_HELD_MIDDLE UINT32_C(2)
#define TL_MOUSE_BUTTON_HELD_RIGHT UINT32_C(4)

typedef uint32_t TlTerminalMousePositionFlags;
#define TL_MOUSE_POSITION_PIXEL UINT32_C(1)

/*
 * All public structures start with abi_version and struct_size. Callers must
 * set abi_version to TL_TERMINAL_ABI_VERSION and struct_size to sizeof the
 * structure they allocated. Unless an entry point explicitly requires an
 * exact size, Version 1 accepts values at least as large as the known
 * structure and ignores an unknown tail.
 */
typedef struct TlTerminalConfigV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t columns;
    uint32_t rows;
    uint64_t initial_source_sequence;
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalConfigV1;

typedef struct TlTerminalConfigV2 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t columns;
    uint32_t rows;
    uint64_t initial_source_sequence;
    uint64_t reserved_v1_compat[2]; /* Must remain zero. */
    uint64_t history_capacity_rows;
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalConfigV2;

/* V3 is intentionally fixed at exactly 88 bytes; no unknown tail is accepted. */
typedef struct TlTerminalConfigV3 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t columns;
    uint32_t rows;
    uint64_t initial_source_sequence;
    uint64_t reserved_v1_compat[2]; /* Must remain zero. */
    uint64_t history_capacity_rows;
    uint64_t reserved_v2_compat[2]; /* Must remain zero. */
    TlTerminalOutputProfile output_profile;
    uint32_t reserved0; /* Must be zero. */
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalConfigV3;

typedef struct TlTerminalEvent {
    uint32_t abi_version;
    uint32_t struct_size;
    uint64_t source_sequence;
    TlTerminalEventKind kind;
    uint32_t reserved; /* Must be zero. */
    const uint8_t *data;
#if UINTPTR_MAX == UINT32_MAX
    uint32_t data_pointer_padding; /* Must be zero; fixes cross-target layout. */
#endif
    uint64_t data_len;
    uint32_t columns;
    uint32_t rows;
    uint64_t advance_micros;
} TlTerminalEvent;

typedef struct TlTerminalBuffer {
    uint32_t abi_version;
    uint32_t struct_size;
    const uint8_t *data;
#if UINTPTR_MAX == UINT32_MAX
    uint32_t data_pointer_padding; /* Must be zero; written as zero. */
#endif
    uint64_t len;
} TlTerminalBuffer;

typedef struct TlTerminalCheckpointInfo {
    uint32_t abi_version;
    uint32_t struct_size;
    uint64_t revision;
    uint64_t next_source_sequence;
    uint64_t reserved[2];
} TlTerminalCheckpointInfo;

typedef struct TlTerminalPresentationInfoV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    TlTerminalPresentationFlags flags;
    uint32_t reserved0; /* Written as zero. */
    uint64_t next_source_sequence;
    uint64_t logical_time_micros;
    uint64_t synchronized_output_deadline_micros; /* Zero while inactive. */
    uint64_t reserved[1]; /* Written as zero. */
} TlTerminalPresentationInfoV1;

typedef struct TlTerminalInputV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    TlTerminalInputKind kind;
    TlTerminalKey key; /* Must be zero for paste and focus. */
    TlTerminalInputModifiers modifiers; /* Must be zero for paste and focus. */
    uint32_t character; /* Unicode scalar for CHARACTER; otherwise zero. */
    const uint8_t *data; /* Paste bytes; must be NULL for key and focus. */
#if UINTPTR_MAX == UINT32_MAX
    uint32_t data_pointer_padding; /* Must be zero; fixes cross-target layout. */
#endif
    uint64_t data_len; /* Paste byte count; must be zero for key and focus. */
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalInputV1;

/*
 * Additive input V2. Its first 56 bytes preserve the complete V1 field
 * layout. For V1 kinds, every win32_* field and reserved[] entry must be zero.
 * For TL_INPUT_KIND_WIN32_KEY, key, modifiers, character, data, data_len, and
 * reserved_v1_compat must be zero/NULL. The virtual-key, scan-code, UTF-16
 * code-unit, and repeat-count values must fit uint16_t. win32_key_down is 0 or
 * 1; the complete uint32_t control-key state is passed through unchanged.
 */
typedef struct TlTerminalInputV2 {
    uint32_t abi_version;
    uint32_t struct_size;
    TlTerminalInputKind kind;
    TlTerminalKey key;
    TlTerminalInputModifiers modifiers;
    uint32_t character;
    const uint8_t *data;
#if UINTPTR_MAX == UINT32_MAX
    uint32_t data_pointer_padding; /* Must be zero; fixes cross-target layout. */
#endif
    uint64_t data_len;
    uint64_t reserved_v1_compat[2]; /* Must remain zero. */
    uint32_t win32_virtual_key;
    uint32_t win32_scan_code;
    uint32_t win32_unicode_char; /* One UTF-16 code unit, not a scalar. */
    uint32_t win32_key_down; /* Must be 0 or 1. */
    uint32_t win32_control_key_state;
    uint32_t win32_repeat_count;
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalInputV2;

/*
 * Additive input V3. Its first 96 bytes preserve the complete V2 layout.
 * Kinds 1 through 5 require the complete V3 tail to be zero/NULL and behave
 * exactly as V2. TL_INPUT_KIND_KITTY_KEY requires every legacy modifier,
 * payload, reserved, and Win32 field to be zero/NULL. A CHARACTER identity
 * carries its main Unicode scalar in character; all other identities require
 * character zero. Common functional keys reuse TL_INPUT_KEY_* values. Kitty
 * PUA identities use protocol codepoints 57358 through 57454; unidentified
 * text uses TL_INPUT_KEY_UNIDENTIFIED_TEXT. associated_text is canonical UTF-8:
 * NULL/zero for empty, or non-NULL/nonzero for a readable payload.
 */
typedef struct TlTerminalInputV3 {
    uint32_t abi_version;
    uint32_t struct_size;
    TlTerminalInputKind kind;
    TlTerminalKey key;
    TlTerminalInputModifiers modifiers;
    uint32_t character;
    const uint8_t *data;
#if UINTPTR_MAX == UINT32_MAX
    uint32_t data_pointer_padding; /* Must be zero; fixes cross-target layout. */
#endif
    uint64_t data_len;
    uint64_t reserved_v1_compat[2];
    uint32_t win32_virtual_key;
    uint32_t win32_scan_code;
    uint32_t win32_unicode_char;
    uint32_t win32_key_down;
    uint32_t win32_control_key_state;
    uint32_t win32_repeat_count;
    uint64_t reserved[2];
    TlTerminalInputKeyEventType key_event_type;
    TlTerminalKeyboardModifiers keyboard_modifiers;
    uint32_t shifted_key; /* Zero or one Unicode scalar. */
    uint32_t base_layout_key; /* Zero or one Unicode scalar. */
    const uint8_t *associated_text;
#if UINTPTR_MAX == UINT32_MAX
    uint32_t associated_text_pointer_padding; /* Must be zero. */
#endif
    uint64_t associated_text_len;
    uint64_t reserved_v3[4]; /* Must be zero. */
} TlTerminalInputV3;

/*
 * Fixed 64-byte semantic mouse input. Cell and optional pixel coordinates use
 * a zero-based origin at the viewport's top left. buttons_held is the physical
 * primary-button state after the event. A press must include its button and a
 * release must exclude its button.
 */
typedef struct TlTerminalMouseInputV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    TlTerminalMouseAction action;
    TlTerminalMouseButton button; /* Press/release only; otherwise NONE. */
    TlTerminalMouseWheelDirection wheel; /* Wheel only; otherwise NONE. */
    TlTerminalMouseButtons buttons_held;
    TlTerminalMousePositionFlags position_flags;
    TlTerminalInputModifiers modifiers;
    uint32_t cell_column;
    uint32_t cell_row;
    uint32_t pixel_x; /* Used only when TL_MOUSE_POSITION_PIXEL is set. */
    uint32_t pixel_y; /* Used only when TL_MOUSE_POSITION_PIXEL is set. */
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalMouseInputV1;

/* Compile-time checks for one canonical layout on supported 32/64-bit hosts. */
#if UINTPTR_MAX == UINT64_MAX || UINTPTR_MAX == UINT32_MAX
#  if defined(__cplusplus)
static_assert(sizeof(TlTerminalConfigV1) == 40, "TlTerminalConfigV1 ABI layout");
static_assert(sizeof(TlTerminalConfigV2) == 64, "TlTerminalConfigV2 ABI layout");
static_assert(sizeof(TlTerminalConfigV3) == 88, "TlTerminalConfigV3 ABI layout");
static_assert(offsetof(TlTerminalConfigV3, abi_version) == 0,
              "TlTerminalConfigV3 ABI version offset");
static_assert(offsetof(TlTerminalConfigV3, struct_size) == 4,
              "TlTerminalConfigV3 structure size offset");
static_assert(offsetof(TlTerminalConfigV3, columns) == 8,
              "TlTerminalConfigV3 columns offset");
static_assert(offsetof(TlTerminalConfigV3, rows) == 12,
              "TlTerminalConfigV3 rows offset");
static_assert(offsetof(TlTerminalConfigV3, initial_source_sequence) == 16,
              "TlTerminalConfigV3 initial sequence offset");
static_assert(offsetof(TlTerminalConfigV3, reserved_v1_compat) == 24,
              "TlTerminalConfigV3 V1 reserved offset");
static_assert(offsetof(TlTerminalConfigV3, history_capacity_rows) == 40,
              "TlTerminalConfigV3 history offset");
static_assert(offsetof(TlTerminalConfigV3, reserved_v2_compat) == 48,
              "TlTerminalConfigV3 V2 reserved offset");
static_assert(offsetof(TlTerminalConfigV3, output_profile) == 64,
              "TlTerminalConfigV3 output profile offset");
static_assert(offsetof(TlTerminalConfigV3, reserved0) == 68,
              "TlTerminalConfigV3 reserved0 offset");
static_assert(offsetof(TlTerminalConfigV3, reserved) == 72,
              "TlTerminalConfigV3 reserved offset");
static_assert(sizeof(TlTerminalEvent) == 56, "TlTerminalEvent ABI layout");
static_assert(offsetof(TlTerminalEvent, data) == 24, "TlTerminalEvent data offset");
static_assert(offsetof(TlTerminalEvent, advance_micros) == 48,
              "TlTerminalEvent advance offset");
static_assert(sizeof(TlTerminalBuffer) == 24, "TlTerminalBuffer ABI layout");
static_assert(offsetof(TlTerminalBuffer, data) == 8, "TlTerminalBuffer data offset");
static_assert(offsetof(TlTerminalBuffer, len) == 16, "TlTerminalBuffer len offset");
static_assert(sizeof(TlTerminalInputV1) == 56, "TlTerminalInputV1 ABI layout");
static_assert(offsetof(TlTerminalInputV1, data) == 24,
              "TlTerminalInputV1 data offset");
static_assert(offsetof(TlTerminalInputV1, reserved) == 40,
              "TlTerminalInputV1 reserved offset");
static_assert(sizeof(TlTerminalInputV2) == 96, "TlTerminalInputV2 ABI layout");
static_assert(offsetof(TlTerminalInputV2, data) == 24,
              "TlTerminalInputV2 data offset");
static_assert(offsetof(TlTerminalInputV2, data_len) == 32,
              "TlTerminalInputV2 data length offset");
static_assert(offsetof(TlTerminalInputV2, reserved_v1_compat) == 40,
              "TlTerminalInputV2 V1 reserved offset");
static_assert(offsetof(TlTerminalInputV2, win32_virtual_key) == 56,
              "TlTerminalInputV2 Win32 extension offset");
static_assert(offsetof(TlTerminalInputV2, win32_scan_code) == 60,
              "TlTerminalInputV2 Win32 scan-code offset");
static_assert(offsetof(TlTerminalInputV2, win32_unicode_char) == 64,
              "TlTerminalInputV2 Win32 Unicode offset");
static_assert(offsetof(TlTerminalInputV2, win32_key_down) == 68,
              "TlTerminalInputV2 Win32 key-state offset");
static_assert(offsetof(TlTerminalInputV2, win32_control_key_state) == 72,
              "TlTerminalInputV2 Win32 control-state offset");
static_assert(offsetof(TlTerminalInputV2, win32_repeat_count) == 76,
              "TlTerminalInputV2 Win32 repeat-count offset");
static_assert(offsetof(TlTerminalInputV2, reserved) == 80,
              "TlTerminalInputV2 reserved offset");
static_assert(sizeof(TlTerminalInputV3) == 160, "TlTerminalInputV3 ABI layout");
static_assert(offsetof(TlTerminalInputV3, key_event_type) == 96,
              "TlTerminalInputV3 event-type offset");
static_assert(offsetof(TlTerminalInputV3, keyboard_modifiers) == 100,
              "TlTerminalInputV3 keyboard-modifier offset");
static_assert(offsetof(TlTerminalInputV3, shifted_key) == 104,
              "TlTerminalInputV3 shifted-key offset");
static_assert(offsetof(TlTerminalInputV3, base_layout_key) == 108,
              "TlTerminalInputV3 base-layout-key offset");
static_assert(offsetof(TlTerminalInputV3, associated_text) == 112,
              "TlTerminalInputV3 associated-text offset");
static_assert(offsetof(TlTerminalInputV3, associated_text_len) == 120,
              "TlTerminalInputV3 associated-text length offset");
static_assert(offsetof(TlTerminalInputV3, reserved_v3) == 128,
              "TlTerminalInputV3 reserved offset");
#  elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(TlTerminalConfigV1) == 40, "TlTerminalConfigV1 ABI layout");
_Static_assert(sizeof(TlTerminalConfigV2) == 64, "TlTerminalConfigV2 ABI layout");
_Static_assert(sizeof(TlTerminalConfigV3) == 88, "TlTerminalConfigV3 ABI layout");
_Static_assert(offsetof(TlTerminalConfigV3, abi_version) == 0,
               "TlTerminalConfigV3 ABI version offset");
_Static_assert(offsetof(TlTerminalConfigV3, struct_size) == 4,
               "TlTerminalConfigV3 structure size offset");
_Static_assert(offsetof(TlTerminalConfigV3, columns) == 8,
               "TlTerminalConfigV3 columns offset");
_Static_assert(offsetof(TlTerminalConfigV3, rows) == 12,
               "TlTerminalConfigV3 rows offset");
_Static_assert(offsetof(TlTerminalConfigV3, initial_source_sequence) == 16,
               "TlTerminalConfigV3 initial sequence offset");
_Static_assert(offsetof(TlTerminalConfigV3, reserved_v1_compat) == 24,
               "TlTerminalConfigV3 V1 reserved offset");
_Static_assert(offsetof(TlTerminalConfigV3, history_capacity_rows) == 40,
               "TlTerminalConfigV3 history offset");
_Static_assert(offsetof(TlTerminalConfigV3, reserved_v2_compat) == 48,
               "TlTerminalConfigV3 V2 reserved offset");
_Static_assert(offsetof(TlTerminalConfigV3, output_profile) == 64,
               "TlTerminalConfigV3 output profile offset");
_Static_assert(offsetof(TlTerminalConfigV3, reserved0) == 68,
               "TlTerminalConfigV3 reserved0 offset");
_Static_assert(offsetof(TlTerminalConfigV3, reserved) == 72,
               "TlTerminalConfigV3 reserved offset");
_Static_assert(sizeof(TlTerminalEvent) == 56, "TlTerminalEvent ABI layout");
_Static_assert(offsetof(TlTerminalEvent, data) == 24, "TlTerminalEvent data offset");
_Static_assert(offsetof(TlTerminalEvent, advance_micros) == 48,
               "TlTerminalEvent advance offset");
_Static_assert(sizeof(TlTerminalBuffer) == 24, "TlTerminalBuffer ABI layout");
_Static_assert(offsetof(TlTerminalBuffer, data) == 8, "TlTerminalBuffer data offset");
_Static_assert(offsetof(TlTerminalBuffer, len) == 16, "TlTerminalBuffer len offset");
_Static_assert(sizeof(TlTerminalInputV1) == 56, "TlTerminalInputV1 ABI layout");
_Static_assert(offsetof(TlTerminalInputV1, data) == 24,
               "TlTerminalInputV1 data offset");
_Static_assert(offsetof(TlTerminalInputV1, reserved) == 40,
               "TlTerminalInputV1 reserved offset");
_Static_assert(sizeof(TlTerminalInputV2) == 96, "TlTerminalInputV2 ABI layout");
_Static_assert(offsetof(TlTerminalInputV2, data) == 24,
               "TlTerminalInputV2 data offset");
_Static_assert(offsetof(TlTerminalInputV2, data_len) == 32,
               "TlTerminalInputV2 data length offset");
_Static_assert(offsetof(TlTerminalInputV2, reserved_v1_compat) == 40,
               "TlTerminalInputV2 V1 reserved offset");
_Static_assert(offsetof(TlTerminalInputV2, win32_virtual_key) == 56,
               "TlTerminalInputV2 Win32 extension offset");
_Static_assert(offsetof(TlTerminalInputV2, win32_scan_code) == 60,
               "TlTerminalInputV2 Win32 scan-code offset");
_Static_assert(offsetof(TlTerminalInputV2, win32_unicode_char) == 64,
               "TlTerminalInputV2 Win32 Unicode offset");
_Static_assert(offsetof(TlTerminalInputV2, win32_key_down) == 68,
               "TlTerminalInputV2 Win32 key-state offset");
_Static_assert(offsetof(TlTerminalInputV2, win32_control_key_state) == 72,
               "TlTerminalInputV2 Win32 control-state offset");
_Static_assert(offsetof(TlTerminalInputV2, win32_repeat_count) == 76,
               "TlTerminalInputV2 Win32 repeat-count offset");
_Static_assert(offsetof(TlTerminalInputV2, reserved) == 80,
               "TlTerminalInputV2 reserved offset");
_Static_assert(sizeof(TlTerminalInputV3) == 160, "TlTerminalInputV3 ABI layout");
_Static_assert(offsetof(TlTerminalInputV3, key_event_type) == 96,
               "TlTerminalInputV3 event-type offset");
_Static_assert(offsetof(TlTerminalInputV3, keyboard_modifiers) == 100,
               "TlTerminalInputV3 keyboard-modifier offset");
_Static_assert(offsetof(TlTerminalInputV3, shifted_key) == 104,
               "TlTerminalInputV3 shifted-key offset");
_Static_assert(offsetof(TlTerminalInputV3, base_layout_key) == 108,
               "TlTerminalInputV3 base-layout-key offset");
_Static_assert(offsetof(TlTerminalInputV3, associated_text) == 112,
               "TlTerminalInputV3 associated-text offset");
_Static_assert(offsetof(TlTerminalInputV3, associated_text_len) == 120,
               "TlTerminalInputV3 associated-text length offset");
_Static_assert(offsetof(TlTerminalInputV3, reserved_v3) == 128,
               "TlTerminalInputV3 reserved offset");
#  endif
#else
#  error "TermLink C ABI supports only 32-bit and 64-bit pointer targets"
#endif

TL_TERMINAL_API uint32_t tl_terminal_abi_version(void);
/* Maximum canonical Snapshot V2 size accepted or emitted by this library. */
TL_TERMINAL_API uint64_t tl_terminal_max_snapshot_bytes(void);
/* Renderer-only active-screen wire format discovery. */
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v1_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v1_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v2_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v2_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v3_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v3_max_bytes(void);
/* Canonical Host Effect Frame discovery. */
TL_TERMINAL_API uint32_t tl_terminal_host_effect_frame_v1_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_host_effect_frame_v1_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_host_effect_frame_v2_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_host_effect_frame_v2_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v1_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v2_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v3_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_event_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_buffer_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_checkpoint_info_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_presentation_info_v1_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_input_v1_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_input_v2_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_input_v3_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_mouse_input_v1_struct_size(void);

/*
 * Terminal ownership and synchronization:
 *
 * A successful create/import returns one opaque terminal owned by the caller.
 * A terminal has no internal synchronization. Exactly one thread may access a
 * terminal at a time; single/batch apply, checkpoint_begin, semantic input
 * encoding, and destroy are mutually exclusive. Destroy each successful
 * terminal result exactly once.
 *
 * out_terminal/out_checkpoint are in/out ownership slots. They must contain
 * NULL on entry. An error never overwrites a non-NULL slot.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_create(
    const TlTerminalConfigV1 *config,
    TlTerminal **out_terminal);
TL_TERMINAL_API TlTerminalStatus tl_terminal_create_v2(
    const TlTerminalConfigV2 *config,
    TlTerminal **out_terminal);
/* V3 requires struct_size == sizeof(TlTerminalConfigV3). */
TL_TERMINAL_API TlTerminalStatus tl_terminal_create_v3(
    const TlTerminalConfigV3 *config,
    TlTerminal **out_terminal);
TL_TERMINAL_API void tl_terminal_destroy(TlTerminal *terminal);
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event(
    TlTerminal *terminal,
    const TlTerminalEvent *event);

/*
 * Applies one source batch atomically. events must point to event_count
 * contiguous TlTerminalEvent values, where event_count is 1 through 256.
 * Every array element must use the current ABI version and struct_size exactly
 * equal to sizeof(TlTerminalEvent); extended structures cannot be used because
 * their array stride cannot be inferred. Reserved and kind-unused fields must
 * be zero. Each Output payload must be non-NULL, non-empty, at most 1 MiB, and
 * all Output payloads together must be at most 4 MiB. Payload bytes are only
 * borrowed for the duration of the call.
 *
 * The complete array and all payload metadata are validated before terminal
 * state is touched. Success commits every event. Failure commits no prefix;
 * Gap, forward sequence discontinuity, and unsupported deterministic input
 * retain the terminal core's failed-closed latch semantics.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_batch_v1(
    TlTerminal *terminal,
    const TlTerminalEvent *events,
    uint32_t event_count);

/*
 * Additive host-effect apply APIs. They validate all event metadata, payload
 * bounds, and the output structure before accessing terminal state. The output
 * must use the current ABI, have struct_size >= sizeof(TlTerminalBuffer), and
 * enter with data == NULL and len == 0. Success always returns one owned,
 * canonical Host Effect Frame V1, including a 64-byte frame when no effects
 * were emitted. Release it exactly once with tl_terminal_buffer_free.
 *
 * Candidate terminal state and output ownership are one transaction: any
 * frame allocation, registration, publication, or core failure leaves the
 * output empty and does not commit a successful candidate. Core failed-closed
 * latch semantics for Gap, forward discontinuity, and unsupported input are
 * retained. The batch form uses the same 1..256 event, exact-stride, per-Output
 * 1 MiB, and aggregate 4 MiB limits as tl_terminal_apply_event_batch_v1.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_with_host_effects_v1(
    TlTerminal *terminal,
    const TlTerminalEvent *event,
    TlTerminalBuffer *out_effect_frame);
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_batch_with_host_effects_v1(
    TlTerminal *terminal,
    const TlTerminalEvent *events,
    uint32_t event_count,
    TlTerminalBuffer *out_effect_frame);

/*
 * Additive Host Effect Frame V2 apply APIs. They retain the V1 preflight,
 * atomicity, publish-before-commit, ownership, and batch limits while allowing
 * multiple and bounded extended query replies. Success includes one owned
 * 64-byte empty V2 frame when no effect was emitted. V1-only entry points stay
 * frozen and return TL_STATUS_HOST_EFFECT_VERSION_REQUIRED without committing
 * when a reply cannot be represented losslessly by Host Effect Frame V1.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_with_host_effects_v2(
    TlTerminal *terminal,
    const TlTerminalEvent *event,
    TlTerminalBuffer *out_effect_frame);
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_batch_with_host_effects_v2(
    TlTerminal *terminal,
    const TlTerminalEvent *events,
    uint32_t event_count,
    TlTerminalBuffer *out_effect_frame);

/*
 * Writes current presentation eligibility without mutating terminal state.
 * Callers initialize abi_version and struct_size as for checkpoint info. The
 * known structure is overwritten only after the complete prefix is accepted;
 * an unknown oversized tail is left untouched. This getter succeeds even when
 * FAILED_CLOSED is set so callers can inspect the complete flag combination.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_get_presentation_info_v1(
    const TlTerminal *terminal,
    TlTerminalPresentationInfoV1 *out_info);
TL_TERMINAL_API TlTerminalStatus tl_terminal_import_snapshot(
    const uint8_t *snapshot,
    uint64_t snapshot_len,
    TlTerminal **out_terminal);

/*
 * Checkpoints:
 *
 * checkpoint_begin freezes one immutable terminal revision. Later terminal
 * writes cannot change it. Const checkpoint operations may run concurrently
 * with one another, but destroy must not overlap any checkpoint operation.
 * Destroy each successful checkpoint result exactly once. In ABI v1, revision
 * equals next_source_sequence; both values remain fixed for the checkpoint so
 * future paged and delta reads can verify they use one captured revision.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_begin(
    const TlTerminal *terminal,
    TlTerminalCheckpoint **out_checkpoint);
TL_TERMINAL_API void tl_terminal_checkpoint_destroy(
    TlTerminalCheckpoint *checkpoint);
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_get_info(
    const TlTerminalCheckpoint *checkpoint,
    TlTerminalCheckpointInfo *out_info);
/*
 * Returns retained active-screen scrollback and a runtime-only monotonic row
 * generation. Alternate-screen applications report zero retained rows.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_get_scrollback_info_v1(
    const TlTerminalCheckpoint *checkpoint,
    uint32_t *out_available_rows,
    uint64_t *out_generation_rows);
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_snapshot(
    const TlTerminalCheckpoint *checkpoint,
    TlTerminalBuffer *out_snapshot);
/*
 * Exports canonical Render Frame V1 for the same frozen checkpoint revision.
 * max_frame_bytes must be 1 through tl_terminal_render_frame_v1_max_bytes().
 * A requested limit below the actual frame returns TL_STATUS_LIMIT_EXCEEDED
 * and leaves out_frame empty.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v1(
    const TlTerminalCheckpoint *checkpoint,
    uint64_t max_frame_bytes,
    TlTerminalBuffer *out_frame);
/*
 * Exports canonical Render Frame V2 for the same frozen checkpoint revision.
 * V2 is produced explicitly from core state and is not an upgraded V1 frame.
 * max_frame_bytes must be 1 through tl_terminal_render_frame_v2_max_bytes().
 * A requested limit below the actual frame returns TL_STATUS_LIMIT_EXCEEDED
 * and leaves out_frame empty.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v2(
    const TlTerminalCheckpoint *checkpoint,
    uint64_t max_frame_bytes,
    TlTerminalBuffer *out_frame);
/*
 * Exports canonical Render Frame V3 for the same frozen checkpoint revision.
 * V3 is produced explicitly from core state and is not an upgraded earlier frame.
 * max_frame_bytes must be 1 through tl_terminal_render_frame_v3_max_bytes().
 * A requested limit below the actual frame returns TL_STATUS_LIMIT_EXCEEDED
 * and leaves out_frame empty.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v3(
    const TlTerminalCheckpoint *checkpoint,
    uint64_t max_frame_bytes,
    TlTerminalBuffer *out_frame);
/*
 * Exports V3 at offset_rows above the live tail. Requests older than retained
 * history clamp to the oldest available row.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v3_at_scrollback(
    const TlTerminalCheckpoint *checkpoint,
    uint32_t offset_rows,
    uint64_t max_frame_bytes,
    TlTerminalBuffer *out_frame);

/*
 * Buffer ownership:
 *
 * Every buffer output must be initialized by the caller with the current ABI
 * version, its allocated struct_size, data == NULL, and len == 0. The library
 * never overwrites a live buffer. A successful export places one ownership
 * token in the structure. The bytes are read-only and stay valid until free.
 *
 * tl_terminal_buffer_free accepts a pointer so it can clear data and len after
 * success. Copying a TlTerminalBuffer copies only the token: freeing through
 * any copy invalidates every copy. A second free (including through another
 * copy) remains a caller violation. Metadata changes are rejected.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_buffer_free(
    TlTerminalBuffer *buffer);

/*
 * Encodes one semantic key, paste, or focus event using current terminal input
 * modes. Focus events have no payload and produce bytes only while DEC private
 * mode 1004 is enabled. Paste normalizes LF/CRLF to CR and removes C0 controls
 * other than HT/LF/CR plus DEL and C1 controls before optional bracket framing.
 * A successful output buffer is owned by the caller and must be freed once.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input_v1(
    const TlTerminal *terminal,
    const TlTerminalInputV1 *input,
    TlTerminalBuffer *out_encoded);

/*
 * Additive V2 input encoder. Existing V1 kinds behave identically when the V2
 * extension is zero. TL_INPUT_KIND_WIN32_KEY is accepted only through this
 * entry point and emits a report only while Win32 Input Mode is enabled;
 * otherwise TL_STATUS_UNSUPPORTED is returned and the output stays empty.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input_v2(
    const TlTerminal *terminal,
    const TlTerminalInputV2 *input,
    TlTerminalBuffer *out_encoded);

/*
 * Additive V3 input encoder. Kinds 1 through 5 are byte-for-byte compatible
 * with V2 when the V3 tail is empty. TL_INPUT_KIND_KITTY_KEY supplies the
 * negotiated Kitty event, layout alternates, and associated text explicitly.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input_v3(
    const TlTerminal *terminal,
    const TlTerminalInputV3 *input,
    TlTerminalBuffer *out_encoded);

/*
 * Encodes one independent semantic mouse input using current terminal mouse
 * tracking and wire-encoding modes. Unknown flag bits and unused action fields
 * are rejected. Pixel coordinates must be zero unless the pixel-position flag
 * is set; mode 1016 requires that flag for every report it emits.
 *
 * A successful result always owns one buffer token, including when reporting
 * is disabled and len is zero. Release it once with tl_terminal_buffer_free.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_mouse_input_v1(
    const TlTerminal *terminal,
    const TlTerminalMouseInputV1 *input,
    TlTerminalBuffer *out_encoded);

/* Reserved delta entry point; it returns TL_STATUS_UNSUPPORTED. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_delta(
    const TlTerminalCheckpoint *checkpoint,
    TlTerminalBuffer *out_delta);
/* Legacy raw-input placeholder; use tl_terminal_encode_input_v1 instead. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input(
    const TlTerminal *terminal,
    const uint8_t *input,
    uint64_t input_len,
    TlTerminalBuffer *out_encoded);

#ifdef __cplusplus
}
#endif

#endif
