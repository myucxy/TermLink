using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Linux;

public interface IArtifactSignatureVerifier
{
    ValueTask VerifyAsync(
        string artifactPath,
        string expectedIdentity,
        CancellationToken cancellationToken);
}

public sealed class GpgAppImageSignatureVerifier : IArtifactSignatureVerifier
{
    private readonly string keyringPath;
    private readonly ICommandRunner runner;
    private readonly string? gpgv;

    public GpgAppImageSignatureVerifier(string keyringPath, ICommandRunner? runner = null)
    {
        if (!Path.IsPathRooted(keyringPath)) throw new ArgumentException("A pinned GPG keyring path is required.", nameof(keyringPath));
        this.keyringPath = Path.GetFullPath(keyringPath);
        this.runner = runner ?? new SystemCommandRunner();
        gpgv = ExecutableLocator.Find("gpgv");
    }

    public async ValueTask VerifyAsync(
        string artifactPath,
        string expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (gpgv is null || !File.Exists(keyringPath))
            throw new CryptographicException("The pinned AppImage signature verifier is unavailable.");
        var fingerprint = NormalizeFingerprint(expectedIdentity);
        var signaturePath = artifactPath + ".sig";
        if (!File.Exists(signaturePath)) throw new CryptographicException("The detached AppImage signature is missing.");
        var result = await runner.RunAsync(gpgv,
            ["--status-fd", "1", "--keyring", keyringPath, signaturePath, artifactPath],
            null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new CryptographicException($"AppImage signature verification failed with exit code {result.ExitCode}.");
        var valid = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => line.StartsWith("[GNUPG:] VALIDSIG ", StringComparison.Ordinal) &&
                line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) == fingerprint);
        if (!valid) throw new CryptographicException("The AppImage signature does not match the pinned identity.");
    }

    private static string NormalizeFingerprint(string value)
    {
        var normalized = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != 40 && normalized.Length != 64)
            throw new CryptographicException("The AppImage signer fingerprint is invalid.");
        return normalized;
    }
}

public sealed record LinuxAppImageUpdateOptions(
    string ActiveAppImagePath,
    string StateDirectory,
    IReadOnlyList<string> AgentArguments);

public sealed class LinuxAppImageUpdateInstaller : IUpdateInstaller
{
    private readonly LinuxAppImageUpdateOptions options;
    private readonly IArtifactSignatureVerifier signatureVerifier;

    public LinuxAppImageUpdateInstaller(
        LinuxAppImageUpdateOptions options,
        IArtifactSignatureVerifier signatureVerifier)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        if (!Path.IsPathRooted(options.ActiveAppImagePath) || !Path.IsPathRooted(options.StateDirectory))
            throw new ArgumentException("AppImage update paths must be absolute.", nameof(options));
    }

    public async ValueTask VerifyAndStageAsync(
        UpdatePackage package,
        string localPath,
        CancellationToken cancellationToken)
    {
        ValidatePackage(package, localPath);
        var file = new FileInfo(localPath);
        if (file.Length != package.Size) throw new InvalidDataException("The AppImage size does not match the update manifest.");
        var hash = await HashAsync(localPath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(hash), Convert.FromHexString(package.Sha256)))
            throw new CryptographicException("The AppImage SHA-256 does not match the update manifest.");
        await signatureVerifier.VerifyAsync(localPath, package.SignatureIdentity, cancellationToken).ConfigureAwait(false);
        SetMode(localPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public async ValueTask LaunchAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken)
    {
        await VerifyAndStageAsync(package, localPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(options.StateDirectory);
        var active = Path.GetFullPath(options.ActiveAppImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        var backup = Path.Combine(options.StateDirectory, "previous.AppImage");
        var candidate = active + ".new";
        File.Copy(localPath, candidate, true);
        SetMode(candidate, GetMode(localPath));
        if (File.Exists(active)) File.Copy(active, backup, true);
        File.Move(candidate, active, true);
        WriteState(new UpdateState(package.Version, package.BuildNumber, backup, DateTimeOffset.UtcNow));
        using var process = Start(active, options.AgentArguments);
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(startupTimeout.Token).ConfigureAwait(false);
            var failedExitCode = process.ExitCode;
            RestoreBackup(active, backup);
            using var rollbackProcess = Start(active, options.AgentArguments);
            throw new InvalidOperationException(
                $"The updated AppImage exited during startup with code {failedExitCode}; the previous version was restored.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Remaining alive for the bounded startup window is the local
            // transaction commit point. Relay reconnection is a separate
            // release-health gate and the verified backup remains retained.
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = ReadState() ?? throw new InvalidOperationException("No AppImage rollback transaction is available.");
        if (!File.Exists(state.BackupPath)) throw new FileNotFoundException("The previous AppImage is unavailable.", state.BackupPath);
        var active = Path.GetFullPath(options.ActiveAppImagePath);
        RestoreBackup(active, state.BackupPath);
        using var process = Start(active, options.AgentArguments);
        return ValueTask.CompletedTask;
    }

    private static void ValidatePackage(UpdatePackage package, string localPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!string.Equals(package.PackageKind, "appimage", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Linux automatic updates require an AppImage package.");
        if (!Path.IsPathRooted(localPath) || !File.Exists(localPath)) throw new FileNotFoundException("The AppImage candidate is missing.", localPath);
        if (package.Size <= 0 || package.BuildNumber <= 0 || package.Sha256.Length != 64 ||
            package.Sha256.Any(value => !Uri.IsHexDigit(value)) || string.IsNullOrWhiteSpace(package.SignatureIdentity))
            throw new InvalidDataException("The AppImage update manifest is invalid.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void WriteState(UpdateState state)
    {
        Directory.CreateDirectory(options.StateDirectory);
        var path = StatePath;
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        SetMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }

    private UpdateState? ReadState() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath))
        : null;

    private string StatePath => Path.Combine(options.StateDirectory, "appimage-update.json");

    private static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("The updated AppImage did not start.");
    }

    private static void RestoreBackup(string active, string backup)
    {
        if (!File.Exists(backup)) throw new FileNotFoundException("The previous AppImage is unavailable.", backup);
        var restored = active + ".rollback";
        File.Copy(backup, restored, true);
        SetMode(restored, GetMode(backup));
        File.Move(restored, active, true);
    }

    private static UnixFileMode GetMode(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("AppImage updates require Linux.");
        return File.GetUnixFileMode(path);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("AppImage updates require Linux.");
        File.SetUnixFileMode(path, mode);
    }

    private sealed record UpdateState(string Version, long BuildNumber, string BackupPath, DateTimeOffset InstalledAt);
}
