# TermLink PC client

This tree contains the portable desktop migration. The released WinForms client remains in `win-agent`
until the P4 Windows parity gate is met.

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
```

Avalonia 12.1.1 ships optional Roslyn analyzers built against compiler 4.14, while the inventory-pinned
.NET 8.0.303 SDK contains compiler 4.10. The two Avalonia projects remove only those package analyzers
immediately before compilation and load XAML explicitly with `AvaloniaXamlLoader`. Runtime, compiled XAML,
warnings-as-errors, and headless validation remain enabled. Remove this compatibility target once the
development inventory advances to an SDK that provides Roslyn 4.14 or newer.

The P2 executable is deliberately isolated from the released Agent. Run with `--preview-fixture` to show
bounded sample data; without that argument it displays `PC-PREVIEW-NO_AGENT` and does not claim production
Agent, Relay, terminal, identity, updater, or autostart ownership.
