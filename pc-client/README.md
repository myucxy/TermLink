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
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' build .\pc-client\TermLink.Pc.sln
```
