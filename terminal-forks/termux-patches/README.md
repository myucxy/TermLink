# Termux terminal fork patch queue

> Historical archive only. This patch queue is not a product build or test
> prerequisite. Do not reconnect it to Android or require an upstream comparison
> for releases. The reproduction command below is retained for source provenance;
> preserve its license and notices. See [archive scope](../../docs/archive/README.md).

TermLink vendors only the terminal core and Android view from
[`termux/termux-app`](https://github.com/termux/termux-app). The pinned upstream commit is
`3df69d1da197dd9bd71a3bafd902dffd720576b4`.

Patch order:

1. `0001-remote-session-port.patch` removes `TerminalView`'s compile-time dependency on the local
   Termux `TerminalSession` and introduces a process-independent emulator callback.
2. `0002-phase0-conpty-compat.patch` keeps unsupported CSI intermediate sequences out of the
   visible grid and bridges xterm focus reporting through the remote session port.

Rebuild a clean patched source tree without overwriting the checked-in fork:

```powershell
.\terminal-forks\Sync-TermuxTerminal.ps1 `
  -Destination "$env:TEMP\termlink-termux-sync-check"
```

The sync script deliberately excludes Termux's local PTY/JNI session, app, package manager and
shell integration. Review and update this patch queue before changing the pinned commit.
