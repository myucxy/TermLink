//! Fixed, deliberately small Unicode width profile used by Snapshot V2.

pub(crate) const WIDTH_PROFILE_VERSION: u16 = 1;

pub(crate) fn scalar_width(character: char) -> u8 {
    let value = u32::from(character);
    if is_combining(value) {
        0
    } else if is_wide(value) {
        2
    } else {
        1
    }
}

pub(crate) fn grapheme_width(character: char, suffix: &str) -> u8 {
    let mut width = scalar_width(character);
    if suffix
        .chars()
        .any(|scalar| scalar == '\u{fe0f}' || scalar_width(scalar) == 2)
    {
        width = 2;
    }
    width
}

fn is_combining(value: u32) -> bool {
    matches!(
        value,
        0x0300..=0x036f
            | 0x0483..=0x0489
            | 0x0591..=0x05bd
            | 0x05bf
            | 0x05c1..=0x05c2
            | 0x05c4..=0x05c5
            | 0x0610..=0x061a
            | 0x064b..=0x065f
            | 0x0670
            | 0x06d6..=0x06ed
            | 0x0711
            | 0x0730..=0x074a
            | 0x07a6..=0x07b0
            | 0x07eb..=0x07f3
            | 0x0816..=0x082d
            | 0x0859..=0x085b
            | 0x08d3..=0x0902
            | 0x093a
            | 0x093c
            | 0x0941..=0x0948
            | 0x094d
            | 0x0951..=0x0957
            | 0x0962..=0x0963
            | 0x1ab0..=0x1aff
            | 0x1dc0..=0x1dff
            | 0x200b..=0x200f
            | 0x202a..=0x202e
            | 0x2060..=0x206f
            | 0x20d0..=0x20ff
            | 0xfe00..=0xfe0f
            | 0xfe20..=0xfe2f
            | 0x1f3fb..=0x1f3ff
            | 0xe0100..=0xe01ef
    )
}

fn is_wide(value: u32) -> bool {
    matches!(
        value,
        0x1100..=0x115f
            | 0x231a..=0x231b
            | 0x2329..=0x232a
            | 0x23e9..=0x23ec
            | 0x23f0
            | 0x23f3
            | 0x25fd..=0x25fe
            | 0x2614..=0x2615
            | 0x2648..=0x2653
            | 0x267f
            | 0x2693
            | 0x26a1
            | 0x26aa..=0x26ab
            | 0x26bd..=0x26be
            | 0x26c4..=0x26c5
            | 0x26ce
            | 0x26d4
            | 0x26ea
            | 0x26f2..=0x26f3
            | 0x26f5
            | 0x26fa
            | 0x26fd
            | 0x2705
            | 0x270a..=0x270b
            | 0x2728
            | 0x274c
            | 0x274e
            | 0x2753..=0x2755
            | 0x2757
            | 0x2795..=0x2797
            | 0x27b0
            | 0x27bf
            | 0x2b1b..=0x2b1c
            | 0x2b50
            | 0x2b55
            | 0x2e80..=0x303e
            | 0x3040..=0xa4cf
            | 0xac00..=0xd7a3
            | 0xf900..=0xfaff
            | 0xfe10..=0xfe19
            | 0xfe30..=0xfe6f
            | 0xff00..=0xff60
            | 0xffe0..=0xffe6
            | 0x1f1e6..=0x1f1ff
            | 0x1f300..=0x1faff
            | 0x20000..=0x3fffd
    )
}
