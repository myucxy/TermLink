//! Repository-owned ConPTY compatibility captures.
//!
//! These capture replays verify parser/state-machine acceptance without
//! fail-closing. They do not replace visual/frame oracles for state semantics.

use termlink_terminal_core::{Event, Terminal};

fn replay(name: &str, bytes: &[u8]) {
    let mut terminal = Terminal::new(100, 30).expect("golden terminal dimensions");
    for (offset, byte) in bytes.iter().copied().enumerate() {
        terminal
            .apply_event(Event::Output {
                source_sequence: u64::try_from(offset).expect("capture offset fits u64"),
                bytes: std::slice::from_ref(&byte),
            })
            .unwrap_or_else(|error| {
                let context_start = offset.saturating_sub(31);
                panic!(
                    "{name} failed at byte {offset} (0x{byte:02x}), context {:02x?}: {error:?}",
                    &bytes[context_start..=offset]
                )
            });
    }
}

macro_rules! golden_stream {
    ($test:ident, $case:literal) => {
        #[test]
        fn $test() {
            replay(
                $case,
                include_bytes!(concat!(
                    "../../../../testdata/terminal-golden-streams/",
                    $case,
                    "/output.bin"
                )),
            );
        }
    };
}

golden_stream!(ansi_unicode_pwsh_v1, "ansi-unicode-pwsh-v1");
golden_stream!(smoke_cmd_v1, "smoke-cmd-v1");
golden_stream!(smoke_powershell_v1, "smoke-powershell-v1");
golden_stream!(smoke_pwsh_v1, "smoke-pwsh-v1");
golden_stream!(smoke_wsl_v1, "smoke-wsl-v1");
golden_stream!(terminal_features_pwsh_v1, "terminal-features-pwsh-v1");
golden_stream!(tui_fzf_wsl_v1, "tui-fzf-wsl-v1");
golden_stream!(tui_htop_wsl_v1, "tui-htop-wsl-v1");
golden_stream!(tui_less_wsl_v1, "tui-less-wsl-v1");
golden_stream!(tui_vim_wsl_v1, "tui-vim-wsl-v1");
