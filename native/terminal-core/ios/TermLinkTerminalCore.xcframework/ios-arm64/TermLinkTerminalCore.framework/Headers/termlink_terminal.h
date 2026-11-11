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
#define TL_RENDER_FRAME_V4_FORMAT_VERSION UINT32_C(4)
#define TL_RENDER_FRAME_V4_MAX_BYTES UINT64_C(33554432)
#define TL_RENDER_FRAME_V5_FORMAT_VERSION UINT32_C(5)
#define TL_RENDER_FRAME_V5_MAX_BYTES UINT64_C(67108864)
#define TL_GRAPHICS_SNAPSHOT_MAX_BYTES UINT64_C(134217728)
#define TL_HOST_EFFECT_FRAME_V1_FORMAT_VERSION UINT32_C(1)
#define TL_HOST_EFFECT_FRAME_V1_MAX_BYTES UINT64_C(65710862)
#define TL_HOST_EFFECT_FRAME_V2_FORMAT_VERSION UINT32_C(2)
#define TL_HOST_EFFECT_FRAME_V2_MAX_BYTES UINT64_C(123033008)
#define TL_INPUT_DIAGNOSTIC_V1_MAX_BYTES UINT64_C(2048)

typedef struct TlTerminal TlTerminal;
typedef struct TlTerminalCheckpoint TlTerminalCheckpoint;
typedef struct TlTerminalDeltaJournal TlTerminalDeltaJournal;
typedef struct TlTerminalBrokerOutputV1 TlTerminalBrokerOutputV1;

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
#define TL_STATUS_RENDER_FRAME_VERSION_REQUIRED UINT32_C(15)
#define TL_STATUS_HISTORY_STALE_CAPTURE UINT32_C(16)
#define TL_STATUS_HISTORY_STALE_REVISION UINT32_C(17)
#define TL_STATUS_HISTORY_STALE_ANCHOR UINT32_C(18)
#define TL_STATUS_HISTORY_PAGE_BUDGET UINT32_C(19)
#define TL_STATUS_MALFORMED_DELTA UINT32_C(20)
#define TL_STATUS_INCOMPATIBLE_DELTA UINT32_C(21)
#define TL_STATUS_NEEDS_FULL_SNAPSHOT UINT32_C(22)
#define TL_HISTORY_LATEST_ANCHOR UINT64_MAX
#define TL_HISTORY_PAGE_V1_MAX_BYTES UINT64_C(16777216)
#define TL_HISTORY_PAGE_V2_MAX_BYTES UINT64_C(67108864)
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
/* data contains 32 bytes: LE u32 version=1, cell width/height, physical display
 * width/height, outer window width/height, reserved=0. Unavailable pairs are 0.
 * Dimensions: cell <=1024, display/window <=32768. All other fields zero.
 * Only the selected authoritative display host may publish this journal event. */
#define TL_EVENT_HOST_GEOMETRY_V1 UINT32_C(5)
#define TL_EVENT_HOST_THEME_V1 UINT32_C(6)
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
#define TL_WIDTH_PROFILE_LEGACY_V1 UINT32_C(1)
#define TL_WIDTH_PROFILE_UNICODE_16 UINT32_C(2)
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

/* V4 adds a frozen per-session width profile. No implicit downgrade. */
typedef struct TlTerminalConfigV4 {
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
    uint64_t reserved_v3_compat[2];
    uint32_t width_profile;
    uint32_t reserved1;
    uint64_t reserved[2]; /* Must be zero. */
} TlTerminalConfigV4;

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

/* Parser-produced identity. The reference path is returned separately only
 * to the trusted Windows broker. Android must use the redacted JNI view. */
typedef struct TlTerminalBrokerRequestV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t has_request;
    uint32_t medium; /* ASCII 'f', 't', or 's'. */
    uint64_t source_sequence;
    uint32_t source_offset_end;
    uint32_t ordinal;
    uint8_t control_digest[32];
    uint64_t read_offset;
    uint32_t read_length;
    uint32_t has_read_length;
    uint64_t reserved[2];
} TlTerminalBrokerRequestV1;

#define TL_BROKER_CAPTURE_BYTES UINT32_C(0)
#define TL_BROKER_CAPTURE_DENIED UINT32_C(1)
#define TL_BROKER_CAPTURE_UNAVAILABLE UINT32_C(2)
#define TL_BROKER_CAPTURE_UNSUPPORTED UINT32_C(3)
#define TL_BROKER_CAPTURE_OVER_LIMIT UINT32_C(4)

typedef struct TlTerminalCheckpointInfo {
    uint32_t abi_version;
    uint32_t struct_size;
    uint64_t revision;
    uint64_t next_source_sequence;
    uint64_t reserved[2];
} TlTerminalCheckpointInfo;

/* Delta V1 is opt-in and accepts only Graphics Snapshot 2.22. Profiles 1/2
 * use UTF-8 with LegacyV1/Unicode16 width; 3/4 use standalone-C1 output
 * with LegacyV1/Unicode16 width. The 32-byte
 * context ID identifies an authenticated session, stream and recovery branch;
 * callers must keep it stable and nonzero. No legacy snapshot is upgraded. */
#define TL_STATE_DELTA_PROFILE_V1 UINT32_C(1)
#define TL_STATE_DELTA_PROFILE_UNICODE16_V1 UINT32_C(2)
#define TL_STATE_DELTA_PROFILE_STANDALONE_C1_V1 UINT32_C(3)
#define TL_STATE_DELTA_PROFILE_UNICODE16_STANDALONE_C1_V1 UINT32_C(4)
#define TL_DELTA_APPLIED UINT32_C(1)
#define TL_DELTA_ALREADY_APPLIED UINT32_C(2)
#define TL_STATE_DELTA_MAX_BYTES (UINT64_C(128) * 1024U * 1024U)

typedef struct TlTerminalDeltaContextV1 {
    uint32_t abi_version;
    uint32_t struct_size; /* Exactly sizeof this V1 structure. */
    uint32_t profile; /* TL_STATE_DELTA_PROFILE_*_V1. */
    uint32_t reserved0;
    uint8_t id[32];
    uint64_t reserved[2];
} TlTerminalDeltaContextV1;

typedef struct TlTerminalDeltaStateInfoV1 {
    uint32_t abi_version;
    uint32_t struct_size; /* Exactly sizeof for input; output writes known size. */
    uint64_t next_source_sequence; /* Exclusive source sequence. */
    uint64_t snapshot_bytes;
    uint8_t sha256[32]; /* Complete canonical Snapshot SHA-256. */
    uint64_t reserved[1];
} TlTerminalDeltaStateInfoV1;

typedef struct TlTerminalDeltaJournalConfigV1 {
    uint32_t abi_version;
    uint32_t struct_size; /* Exactly sizeof this V1 structure. */
    uint32_t profile;
    uint32_t max_entries; /* 1..256. */
    uint8_t context_id[32];
    uint64_t max_bytes; /* Up to 256 MiB, inclusive of retained references. */
    uint64_t max_age_millis; /* Host monotonic time, 1..7 days. */
    uint64_t reserved[2];
} TlTerminalDeltaJournalConfigV1;

typedef struct TlTerminalDeltaJournalInfoV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t entries;
    uint32_t reserved0;
    uint64_t retained_bytes;
    TlTerminalDeltaStateInfoV1 oldest; /* Zero state when entries == 0. */
    TlTerminalDeltaStateInfoV1 newest;
    uint64_t reserved[2];
} TlTerminalDeltaJournalInfoV1;

typedef struct TlTerminalDeltaApplyInfoV1 {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t outcome; /* TL_DELTA_APPLIED or TL_DELTA_ALREADY_APPLIED. */
    uint32_t reserved0;
    TlTerminalDeltaStateInfoV1 state;
    uint64_t reserved[2];
} TlTerminalDeltaApplyInfoV1;

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
static_assert(sizeof(TlTerminalConfigV4) == 112, "TlTerminalConfigV4 ABI layout");
static_assert(offsetof(TlTerminalConfigV4, width_profile) == 88, "V4 width offset");
static_assert(offsetof(TlTerminalConfigV4, reserved) == 96, "V4 reserved offset");
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
static_assert(sizeof(TlTerminalBrokerRequestV1) == 96, "TlTerminalBrokerRequestV1 ABI layout");
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
_Static_assert(sizeof(TlTerminalConfigV4) == 112, "TlTerminalConfigV4 ABI layout");
_Static_assert(offsetof(TlTerminalConfigV4, width_profile) == 88, "V4 width offset");
_Static_assert(offsetof(TlTerminalConfigV4, reserved) == 96, "V4 reserved offset");
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
_Static_assert(sizeof(TlTerminalBrokerRequestV1) == 96, "TlTerminalBrokerRequestV1 ABI layout");
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
/* Maximum canonical Snapshot V2 size for the legacy snapshot APIs (64 MiB). */
TL_TERMINAL_API uint64_t tl_terminal_max_snapshot_bytes(void);
/* Explicit experimental graphics Snapshot 2.22 APIs use a separate 128 MiB limit. */
TL_TERMINAL_API uint64_t tl_terminal_max_graphics_snapshot_bytes(void);
/* Renderer-only active-screen wire format discovery. */
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v1_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v1_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v2_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v2_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v3_format_version(void);
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v4_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v3_max_bytes(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v4_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_render_frame_v5_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_render_frame_v5_max_bytes(void);
/* Canonical Host Effect Frame discovery. */
TL_TERMINAL_API uint32_t tl_terminal_host_effect_frame_v1_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_host_effect_frame_v1_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_host_effect_frame_v2_format_version(void);
TL_TERMINAL_API uint64_t tl_terminal_host_effect_frame_v2_max_bytes(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v1_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v2_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v3_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_config_v4_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_event_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_buffer_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_broker_request_v1_struct_size(void);
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

TL_TERMINAL_API TlTerminalStatus tl_terminal_create_v4(
    const TlTerminalConfigV4 *config,
    TlTerminal **out_terminal);
/* Opts in to the experimental graphics profile. Exact ConfigV4 size, known
 * output/width profiles and all reserved fields zero. Legacy creates stay text-only. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_create_graphics_v1(
    const TlTerminalConfigV4 *config,
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
/* Display-only TLPJ1\0\0\0, LE u32 effect/projection sizes and concatenated bodies.
 * Atomic ownership matches the V2 apply API; projection bound is 8 MiB.
 * Core expands macros and omits BEL/DECPS/OSC 9/52. Never fall back to raw bytes. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_with_legacy_projection_v1(
    TlTerminal *terminal,
    const TlTerminalEvent *event,
    TlTerminalBuffer *output);
TL_TERMINAL_API TlTerminalStatus tl_terminal_apply_event_batch_with_host_effects_v2(
    TlTerminal *terminal,
    const TlTerminalEvent *events,
    uint32_t event_count,
    TlTerminalBuffer *out_effect_frame);

/* Broker transaction owns the terminal while pending. On successful begin,
 * *owner becomes NULL; a saved alias must never be accessed. Request returns
 * an owned UTF-8 path only to the trusted Windows broker; free it with the
 * usual buffer API. Supply checks every request identity field and accepts at
 * most 64 requests, 16 MiB per capture and 32 MiB per Output. Failure leaves
 * it pending. The host may cancel between calls to impose a wall-clock budget.
 * Commit publishes Host Effect Frame V2 before applying state or returning the
 * terminal; cancel returns the original terminal unchanged. Both use empty
 * output owner slots and consume the transaction only on success. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_begin_v1(
    TlTerminal **owner, const TlTerminalEvent *event,
    TlTerminalBrokerOutputV1 **out_transaction,
    TlTerminalBuffer *out_diagnostic);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_request_redacted_v1(
    const TlTerminalBrokerOutputV1 *transaction,
    TlTerminalBrokerRequestV1 *out_request);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_request_v1(
    const TlTerminalBrokerOutputV1 *transaction,
    TlTerminalBrokerRequestV1 *out_request, TlTerminalBuffer *out_reference);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_supply_v1(
    TlTerminalBrokerOutputV1 *transaction,
    const TlTerminalBrokerRequestV1 *identity, uint32_t outcome,
    const uint8_t *data, uint64_t data_len);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_diagnostic_v1(
    const TlTerminalBrokerOutputV1 *transaction,
    TlTerminalBuffer *out_diagnostic);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_preview_v1(
    TlTerminalBrokerOutputV1 *transaction, TlTerminalBuffer *out_effect_frame);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_commit_v1(
    TlTerminalBrokerOutputV1 **transaction, TlTerminal **out_terminal,
    TlTerminalBuffer *out_effect_frame);
TL_TERMINAL_API TlTerminalStatus tl_terminal_broker_cancel_v1(
    TlTerminalBrokerOutputV1 **transaction, TlTerminal **out_terminal);

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
/* Accepts only canonical graphics Snapshot 2.22, at most 128 MiB. Legacy import
 * rejects 2.22 even below 64 MiB; neither API implicitly migrates a profile. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_import_snapshot_graphics_v1(
    const uint8_t *snapshot, uint64_t snapshot_len, TlTerminal **out_terminal);

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
/* Immutable primary sealed history. One capture per terminal, up to 64 captures
 * and 256 MiB conservatively charged heap per library instance. A capture may
 * outlive its terminal. Tokens are never reused, including after release; the
 * fixed five-minute lease is not renewed by reading. Expiry is checked on every
 * operation (memory remains bounded while idle). No implicit recapture/eviction.
 * The capture output must contain zeros after its ABI prefix. Caller frees each
 * successful page with tl_terminal_buffer_free, and releases capture on exit.
 * Limits: 128 whole logical lines, 65536 cells, 4 MiB meaningful lead-cell UTF-8.
 * Blank padding is covered by the cell/wire limit, not the text budget.
 * On PAGE_BUDGET required outputs describe the next whole line; other processed
 * requests set them to zero. Failure leaves out_page empty. Exact-size structs.
 * Owner IDs scope a token to its original terminal; they are not authentication. */
typedef struct TlTerminalHistoryCaptureV1 {
    uint32_t abi_version, struct_size;
    uint64_t capture_id, owner_id, revision, accounted_bytes, lease_millis;
} TlTerminalHistoryCaptureV1;
typedef struct TlTerminalHistoryRequestV1 {
    uint32_t abi_version, struct_size;
    uint64_t capture_id, owner_id, revision, before_line_id;
    uint32_t max_lines, max_cells, max_utf8_bytes, reserved0;
    uint64_t reserved;
} TlTerminalHistoryRequestV1;
TL_TERMINAL_API uint32_t tl_terminal_history_capture_v1_struct_size(void);
TL_TERMINAL_API uint32_t tl_terminal_history_request_v1_struct_size(void);
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_begin_v1(const TlTerminal *terminal, TlTerminalHistoryCaptureV1 *out_capture);
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_page_v1(const TlTerminalHistoryRequestV1 *request, TlTerminalBuffer *out_page, uint32_t *out_required_cells, uint32_t *out_required_utf8);
/* begin_v1 produces a format-independent token. Graphics captures require
 * page_v2 / viewport_v2; their V1 equivalents return RENDER_FRAME_VERSION_REQUIRED.
 * V2 also accepts text captures; returns History Page V2 with Render V5 frames. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_page_v2(const TlTerminalHistoryRequestV1 *request, TlTerminalBuffer *out_page, uint32_t *out_required_cells, uint32_t *out_required_utf8);
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_release_v1(uint64_t capture_id, uint64_t owner_id);
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_check_v1(uint64_t capture_id, uint64_t owner_id, uint64_t revision);
/* Frozen Render V4 projection, including captured live tail (not sealed page
 * records). Offset clamps to available_rows; preserves the capture revision. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_viewport_v1(uint64_t capture_id, uint64_t owner_id, uint64_t revision, uint32_t offset_rows, TlTerminalBuffer *out_frame, uint32_t *out_available_rows, uint64_t *out_generation_rows);
/* Frozen Render V5 projection with the same capture, lease and offset contract. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_viewport_v2(uint64_t capture_id, uint64_t owner_id, uint64_t revision, uint32_t offset_rows, TlTerminalBuffer *out_frame, uint32_t *out_available_rows, uint64_t *out_generation_rows);
/* Primary screen only. Missing/evicted/cleared anchors return HISTORY_STALE_ANCHOR. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_history_resolve_anchor_v1(uint64_t capture_id, uint64_t owner_id, uint64_t revision, uint64_t line_id, uint32_t cell_offset, uint32_t *out_offset_rows);

TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_begin(
    const TlTerminal *terminal,
    TlTerminalCheckpoint **out_checkpoint);
TL_TERMINAL_API void tl_terminal_checkpoint_destroy(
    TlTerminalCheckpoint *checkpoint);
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_get_info(
    const TlTerminalCheckpoint *checkpoint,
    TlTerminalCheckpointInfo *out_info);
/* Every delta input is an exact V1 struct with zero reserved fields; output
 * structs are writable/versioned. max_bytes is 1..128 MiB, and buffers use
 * tl_terminal_buffer_free. NEEDS_FULL_SNAPSHOT is a normal resync decision. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_delta_info_v1(
    const TlTerminalCheckpoint *checkpoint,
    const TlTerminalDeltaContextV1 *expected_context,
    TlTerminalDeltaStateInfoV1 *out_info);
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_delta_v1(
    const TlTerminalCheckpoint *base, const TlTerminalCheckpoint *target,
    const TlTerminalDeltaContextV1 *expected_context, uint64_t max_bytes,
    TlTerminalBuffer *out_delta);
/* Applies into a detached terminal; caller decides when to publish it and
 * advance durable ACK. ALREADY_APPLIED returns OK, sets out_terminal=NULL and
 * reports TL_DELTA_ALREADY_APPLIED. A failure leaves both outputs unchanged. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_apply_delta_v1(
    const TlTerminalCheckpoint *current,
    const TlTerminalDeltaContextV1 *expected_context,
    const uint8_t *delta, uint64_t delta_len, uint64_t max_bytes,
    TlTerminal **out_terminal, TlTerminalDeltaApplyInfoV1 *out_info);
/* Journals bind to one local terminal lineage; capture rejects checkpoints
 * from another import/handle. Journal calls require exclusive access. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_delta_journal_create_v1(
    const TlTerminal *terminal, const TlTerminalDeltaJournalConfigV1 *config,
    TlTerminalDeltaJournal **out_journal);
TL_TERMINAL_API void tl_terminal_delta_journal_destroy(TlTerminalDeltaJournal *journal);
TL_TERMINAL_API TlTerminalStatus tl_terminal_delta_journal_capture_v1(
    TlTerminalDeltaJournal *journal, const TlTerminalCheckpoint *checkpoint,
    uint64_t now_millis, TlTerminalDeltaStateInfoV1 *out_state);
TL_TERMINAL_API TlTerminalStatus tl_terminal_delta_journal_get_info_v1(
    TlTerminalDeltaJournal *journal, uint64_t now_millis,
    TlTerminalDeltaJournalInfoV1 *out_info);
TL_TERMINAL_API TlTerminalStatus tl_terminal_delta_journal_export_v1(
    TlTerminalDeltaJournal *journal, const TlTerminalDeltaStateInfoV1 *base,
    const TlTerminalDeltaStateInfoV1 *target, uint64_t now_millis,
    uint64_t max_bytes, TlTerminalBuffer *out_delta);
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
/* Only graphics-profile checkpoints, at most 128 MiB. A profile mismatch in
 * either snapshot export API returns INCOMPATIBLE_SNAPSHOT without output. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_snapshot_graphics_v1(
    const TlTerminalCheckpoint *checkpoint, TlTerminalBuffer *out_snapshot);
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

/* TLSHV1: bounded untrusted shell annotations; does not execute host effects.
 * max_frame_bytes must be in 1..273696. out_frame follows normal buffer ownership. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_shell_state_v1(
    const TlTerminalCheckpoint* checkpoint, uint64_t max_frame_bytes,
    TlTerminalBuffer* out_frame);
/* 12-byte TLHS1 committed progress state; no host action is performed. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_get_host_signals_v1(const TlTerminal* terminal, TlTerminalBuffer* out_state);
/*
 * Exports the live, already-committed TLSHV1 Shell Integration state. This
 * does not require a checkpoint and never exposes an incomplete CSI/OSC/DCS
 * control sequence. max_frame_bytes must be in 1..273696; a successful
 * non-empty out_frame is owned by the caller and must be released with
 * tl_terminal_buffer_free().
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_get_shell_state_v1(
    const TlTerminal* terminal, uint64_t max_frame_bytes,
    TlTerminalBuffer* out_frame);

/* Render V5 is available for both graphics and text checkpoints. Flags must be
 * zero. Owned output is at most 64 MiB; scrollback offsets clamp to retained rows.
 * Graphics checkpoints require V5; old Render V1-V4 never discard graphics. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v5(
    const TlTerminalCheckpoint *checkpoint, uint32_t flags, TlTerminalBuffer *out_frame);
TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v5_at_scrollback(
    const TlTerminalCheckpoint *checkpoint, uint32_t offset_rows,
    uint32_t flags, TlTerminalBuffer *out_frame);

TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v4(
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

TL_TERMINAL_API TlTerminalStatus tl_terminal_checkpoint_export_render_frame_v4_at_scrollback(
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

/*
 * Same-call semantic input failure detail. Legacy encoders above keep their
 * original ABI and behavior. Both outputs must be distinct, non-overlapping,
 * versioned and empty. Success fills only out_encoded; a semantic encoding
 * error leaves out_encoded empty and fills out_diagnostic. Structural argument
 * failures leave both empty. Each filled buffer is independently owned and
 * must be released once with tl_terminal_buffer_free.
 *
 * Failure bytes are ASCII TL-INPUT-DIAG/1 (at most 2048 bytes): direction,
 * status, kind, reason, key, codepoint and modifiers. A codepoint is U+XXXX;
 * arbitrary text, paste content, output coordinates and sequence are absent.
 */
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input_v1_with_diagnostic(
    const TlTerminal *terminal,
    const TlTerminalInputV1 *input,
    TlTerminalBuffer *out_encoded,
    TlTerminalBuffer *out_diagnostic);
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input_v2_with_diagnostic(
    const TlTerminal *terminal,
    const TlTerminalInputV2 *input,
    TlTerminalBuffer *out_encoded,
    TlTerminalBuffer *out_diagnostic);
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_input_v3_with_diagnostic(
    const TlTerminal *terminal,
    const TlTerminalInputV3 *input,
    TlTerminalBuffer *out_encoded,
    TlTerminalBuffer *out_diagnostic);
TL_TERMINAL_API TlTerminalStatus tl_terminal_encode_mouse_input_v1_with_diagnostic(
    const TlTerminal *terminal,
    const TlTerminalMouseInputV1 *input,
    TlTerminalBuffer *out_encoded,
    TlTerminalBuffer *out_diagnostic);

/* Read-only bounded ASCII diagnostic (TL-DIAG/1), also after failed-closed.
 * Returns OK with an empty buffer when there is no output diagnostic.
 * Exact control bytes are escaped; string payloads are redacted. Maximum 4096
 * bytes. Release nonempty buffers using tl_terminal_buffer_free. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_export_failure_diagnostic_v1(
    const TlTerminal *terminal,
    TlTerminalBuffer *out_diagnostic);

/* Additive TL-DIAG/2 (max 4096 ASCII bytes), with build/profile and detection
 * metadata. Same ownership, empty result and failed-closed semantics as V1. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_export_failure_diagnostic_v2(
    const TlTerminal *terminal,
    TlTerminalBuffer *out_diagnostic);

/* Additive export: ordinary failures remain TL-DIAG/2; TL-DIAG/3 appends
 * expanded_* fields for macro bytes. Expanded coordinates use macro ID and
 * decoded byte offset, never physical event coordinates. At most 8192 ASCII
 * bytes. Same ownership and empty-result rules; V1/V2 remain frozen. */
TL_TERMINAL_API TlTerminalStatus tl_terminal_export_failure_diagnostic_v3(
    const TlTerminal *terminal,
    TlTerminalBuffer *out_diagnostic);

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
