using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

public sealed class MacPackageUpdateInstaller : IUpdateInstaller
{
    private readonly IMacCommandRunner runner;
    private readonly string? trustedTeamId;
    private readonly string applicationBundlePath;

    public MacPackageUpdateInstaller(string stateDirectory, IMacCommandRunner? runner = null,
        string? trustedTeamId = null, string applicationBundlePath = "/Applications/TermLink.app")
    {
        if (!Path.IsPathFullyQualified(stateDirectory)) throw new ArgumentException("An absolute update directory is required.", nameof(stateDirectory));
        this.runner = runner ?? new MacCommandRunner();
        this.trustedTeamId = trustedTeamId;
        this.applicationBundlePath = applicationBundlePath;
    }

    public async ValueTask VerifyAndStageAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken)
    {
        Validate(package, localPath);
        await VerifyHashAsync(package, localPath, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var signature = await runner.RunAsync("/usr/sbin/pkgutil", ["--check-signature", localPath], deadline.Token).ConfigureAwait(false);
        var assessment = await runner.RunAsync("/usr/sbin/spctl", ["--assess", "--type", "install", "--verbose=4", localPath], deadline.Token).ConfigureAwait(false);
        MacUpdateTrust.RequirePackageSignature(signature, assessment, trustedTeamId!);
        // Gatekeeper validates notarization at runtime. xcrun/stapler is a build
        // tool and would require users to install Xcode command-line tools.
        await VerifyHashAsync(package, localPath, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask LaunchAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken)
    {
        await VerifyAndStageAsync(package, localPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Copy under the elevated user's private directory, then compare the
        // verified digest again. A writable download path must not be swapped
        // between our signature check and the privileged installer opening it.
        var command = "set -eu; stage=$(/usr/bin/mktemp -d /private/tmp/termlink-update.XXXXXX); " +
            "trap '/bin/rm -rf \"$stage\"' EXIT; /bin/chmod 700 \"$stage\"; " +
            "/bin/cp -X " + ShellQuote(localPath) + " \"$stage/update.pkg\"; " +
            "actual=$(/usr/bin/shasum -a 256 \"$stage/update.pkg\"); " +
            "[ \"${actual%% *}\" = " + ShellQuote(package.Sha256.ToLowerInvariant()) + " ]; " +
            "/usr/sbin/installer -pkg \"$stage/update.pkg\" -target /";
        var script = "do shell script \"" + command.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\" with administrator privileges";
        // The OS owns the authorization dialog. Never receive or store an admin
        // password, and do not interrupt a running system installer on UI cancel.
        var result = await runner.RunAsync("/usr/bin/osascript", ["-e", script], CancellationToken.None).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StandardError.Contains("(-128)", StringComparison.Ordinal)
                ? "UPDATE_INSTALLER_CANCELLED" : "UPDATE_INSTALLER_FAILED");
        using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await MacUpdateTrust.VerifyApplicationAsync(runner, applicationBundlePath, trustedTeamId!, package, verification.Token).ConfigureAwait(false);
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("An unconfirmed or unverified previous package cannot be used for rollback."));

    private void Validate(UpdatePackage package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS package updates require macOS.");
        if (trustedTeamId is null || !Regex.IsMatch(trustedTeamId, "^[A-Z0-9]{10}$") ||
            MacUpdateTrust.TeamId(package.SignatureIdentity) != trustedTeamId)
            throw new CryptographicException("A locally pinned Developer ID publisher is required.");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException("The Mac update is missing.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase) || package.PackageKind != "pkg" ||
            !Regex.IsMatch(package.Sha256, "^[a-fA-F0-9]{64}$") || package.Size is <= 0 or > 2L * 1024 * 1024 * 1024 ||
            package.BuildNumber is < 1 or > 65535)
            throw new InvalidDataException("Invalid Mac PKG update metadata.");
    }

    private static async Task VerifyHashAsync(UpdatePackage package, string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (input.Length != package.Size || !CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(input, token).ConfigureAwait(false), Convert.FromHexString(package.Sha256)))
            throw new CryptographicException("The Mac update size or SHA-256 is invalid.");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
