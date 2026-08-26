using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

public sealed class MacPackageUpdateInstaller : IUpdateInstaller
{
    private readonly string stateDirectory;
    private readonly IMacCommandRunner runner;

    public MacPackageUpdateInstaller(string stateDirectory, IMacCommandRunner? runner = null)
    {
        if (!Path.IsPathRooted(stateDirectory)) throw new ArgumentException("An update state directory is required.", nameof(stateDirectory));
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        this.runner = runner ?? new MacCommandRunner();
    }

    public async ValueTask VerifyAndStageAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken)
    {
        Validate(package, localPath);
        var file = new FileInfo(localPath);
        if (file.Length != package.Size) throw new InvalidDataException("The macOS package size is invalid.");
        await using var stream = file.OpenRead();
        var sha = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sha, Convert.FromHexString(package.Sha256)))
            throw new CryptographicException("The macOS package SHA-256 is invalid.");
        var type = package.PackageKind.Equals("pkg", StringComparison.OrdinalIgnoreCase) ? "install" : "open";
        var assessment = await runner.RunAsync("/usr/sbin/spctl",
            ["--assess", "--type", type, "--verbose=4", localPath], cancellationToken).ConfigureAwait(false);
        var assessmentText = assessment.StandardOutput + "\n" + assessment.StandardError;
        if (assessment.ExitCode != 0 || !assessmentText.Contains(package.SignatureIdentity, StringComparison.Ordinal))
            throw new CryptographicException("Gatekeeper rejected the package or its signer identity.");
        var notarization = await runner.RunAsync("/usr/bin/xcrun",
            ["stapler", "validate", localPath], cancellationToken).ConfigureAwait(false);
        if (notarization.ExitCode != 0)
            throw new CryptographicException("The macOS package has no valid notarization ticket.");
    }

    public async ValueTask LaunchAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken)
    {
        await VerifyAndStageAsync(package, localPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(stateDirectory);
        var retained = Path.Combine(stateDirectory, "verified-previous-package" + Path.GetExtension(localPath));
        if (File.Exists(CurrentPackagePath)) File.Copy(CurrentPackagePath, retained, true);
        File.Copy(localPath, CurrentPackagePath, true);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(package.Version, package.BuildNumber, retained)));
        StartOpen(localPath);
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(StatePath)) throw new InvalidOperationException("No macOS rollback package is recorded.");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ??
            throw new InvalidDataException("The macOS rollback state is invalid.");
        if (!File.Exists(state.PreviousPackage)) throw new FileNotFoundException("The previous macOS package is unavailable.", state.PreviousPackage);
        // Rollback remains user-visible. Silently invoking Installer would
        // bypass platform authorization and is intentionally forbidden.
        StartOpen(state.PreviousPackage);
        return ValueTask.CompletedTask;
    }

    private string CurrentPackagePath => Path.Combine(stateDirectory, "current-package" + ".pkg");
    private string StatePath => Path.Combine(stateDirectory, "macos-update.json");

    private static void StartOpen(string path)
    {
        var start = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
        start.ArgumentList.Add(path);
        _ = Process.Start(start) ?? throw new InvalidOperationException("The macOS package UI did not open.");
    }

    private static void Validate(UpdatePackage package, string localPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS package updates require macOS.");
        if (!Path.IsPathRooted(localPath) || !File.Exists(localPath)) throw new FileNotFoundException("The macOS package is missing.", localPath);
        if (!package.PackageKind.Equals("pkg", StringComparison.OrdinalIgnoreCase) &&
            !package.PackageKind.Equals("dmg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("macOS updates require PKG or DMG.");
        if (package.Sha256.Length != 64 || package.Sha256.Any(value => !Uri.IsHexDigit(value)) ||
            package.Size <= 0 || package.BuildNumber <= 0 || string.IsNullOrWhiteSpace(package.SignatureIdentity))
            throw new InvalidDataException("The macOS update manifest is invalid.");
    }

    private sealed record State(string Version, long BuildNumber, string PreviousPackage);
}
