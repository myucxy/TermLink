# TermLink PC client

This tree contains the portable desktop client. On Windows, the P4 Preview packages Avalonia as
`TermLink.exe`; the previous WinForms implementation remains in `win-agent` and is packaged only as
`TermLink.Legacy.exe` for one rollback cycle.

The dependency direction is enforced as follows:

```text
Contracts <- Platform.Abstractions <- Presentation <- Desktop
```
Platform implementations may reference Contracts and Platform.Abstractions. Presentation must not
reference Avalonia, WinForms, WPF, a platform implementation, or the production Agent assembly.

Use the pinned SDK:

```powershell
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' build .\pc-client\TermLink.Pc.sln `
    -c Release -p:RestoreLockedMode=true
```

Run the portable architecture and Avalonia headless validations:

```powershell
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' run --project .\pc-client\tests\ArchitectureValidation -c Release
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' run --project .\pc-client\tests\Desktop.HeadlessTests -c Release
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' run `
    --project .\pc-client\tests\WindowsAdapterValidation -c Release -- `
    .\win-agent\src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe
& .\pc-client\tests\Measure-Performance.ps1 `
    -DotNetPath 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe'
```

Avalonia 12.1.1 ships optional Roslyn analyzers built against compiler 4.14, while the inventory-pinned
.NET 8.0.303 SDK contains compiler 4.10. The two Avalonia projects remove only those package analyzers
immediately before compilation and load XAML explicitly with `AvaloniaXamlLoader`. Runtime, compiled XAML,
warnings-as-errors, and headless validation remain enabled. Remove this compatibility target once the
development inventory advances to an SDK that provides Roslyn 4.14 or newer.

On Windows, running without `--preview-fixture` attaches to or starts the separately shipped Agent through
authenticated control Pipe V8. `--preview-fixture` remains available for bounded headless/UI measurements and
never takes production Relay, terminal, identity, updater, or autostart ownership. Linux and macOS now use a
separate portable Agent Host over authenticated Unix-domain IPC; they never fall back to fixture data unless
`--preview-fixture` is explicit. The portable Host currently exposes local platform status and fails remote
Relay mutations closed with `PORTABLE_RELAY_NOT_CONFIGURED` until real Linux/macOS Relay E2E is complete.

Linux native/runtime validation from Windows uses Ubuntu 24.04 WSL2:

```powershell
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' publish `
    .\pc-client\tests\UnixAdapterValidation\TermLink.Platform.Unix.Validation.csproj `
    -c Release -r linux-x64 --self-contained true -p:RestoreLockedMode=true `
    -o D:\Dev\Temp\TermLink-P6-UnixValidation
wsl.exe -d Ubuntu-24.04 -- bash -lc `
    '/mnt/e/WorkSpace/AICodeProject/TermLink/pc-client/native/posix/build-native.sh /mnt/d/Dev/Temp/TermLink-P6-UnixValidation && cd /mnt/d/Dev/Temp/TermLink-P6-UnixValidation && LD_LIBRARY_PATH=. ./TermLink.Platform.Unix.Validation'
```

Production Linux AppImage and macOS PKG scripts are under `pc-client/package`. They intentionally require
native hosts and pinned signing inputs; cross-publish output alone is never a distributable release.
