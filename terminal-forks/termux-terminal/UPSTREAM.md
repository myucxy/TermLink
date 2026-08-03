# Pinned Termux terminal source

- Repository: <https://github.com/termux/termux-app>
- Commit: `3df69d1da197dd9bd71a3bafd902dffd720576b4`
- Imported modules: selected Java core from `terminal-emulator`; Java/resources from
  `terminal-view`; upstream emulator unit tests.
- Excluded: `TerminalSession`, `TerminalSessionClient`, JNI/native PTY code, Termux app, local
  shell startup, package management and shared app services.
- Patch queue: [`../termux-patches`](../termux-patches)
- Reproduction script: [`../Sync-TermuxTerminal.ps1`](../Sync-TermuxTerminal.ps1)
- License: upstream repository is GPLv3-only with noted component exceptions; see
  [`UPSTREAM_LICENSE.txt`](UPSTREAM_LICENSE.txt).

TermLink's `terminal-engine-termux` module owns the remote transport adapter. The vendored code
must never open an Android local shell or become a network/authentication boundary.
