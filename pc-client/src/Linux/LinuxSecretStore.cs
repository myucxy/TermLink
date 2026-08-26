using System.Security.Cryptography;
using System.Text;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Linux;

public enum LinuxSecretBackend
{
    Unavailable = 0,
    SecretService = 1,
    KWallet = 2,
}

public sealed record LinuxSecretStoreOptions(
    string ServiceName,
    string KWalletName,
    string KWalletFolder,
    LinuxSecretBackend? PreferredBackend = null);

public sealed class LinuxSecretStore : ISecretStore
{
    private readonly LinuxSecretStoreOptions options;
    private readonly ICommandRunner runner;
    private readonly string? secretTool;
    private readonly string? kwalletQuery;

    public LinuxSecretStore(LinuxSecretStoreOptions options, ICommandRunner? runner = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ServiceName) || string.IsNullOrWhiteSpace(options.KWalletName) ||
            string.IsNullOrWhiteSpace(options.KWalletFolder))
            throw new ArgumentException("Linux secret-store identifiers are required.", nameof(options));
        this.runner = runner ?? new SystemCommandRunner();
        secretTool = ExecutableLocator.Find("secret-tool");
        kwalletQuery = ExecutableLocator.Find("kwallet-query");
        Backend = ResolveBackend();
    }

    public LinuxSecretBackend Backend { get; }
    public bool IsAvailable => Backend != LinuxSecretBackend.Unavailable;

    public async ValueTask StoreAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        ValidateName(name);
        RequireAvailable();
        if (secret.IsEmpty || secret.Length > 64 * 1024) throw new ArgumentException("The secret size is invalid.", nameof(secret));
        var encoded = Convert.ToBase64String(secret.Span);
        try
        {
            ProcessResult result;
            if (Backend == LinuxSecretBackend.SecretService)
            {
                result = await runner.RunAsync(secretTool!,
                    ["store", "--label=TermLink", "service", options.ServiceName, "account", name],
                    encoded,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await runner.RunAsync(kwalletQuery!,
                    ["-f", options.KWalletFolder, "-w", EntryName(name), options.KWalletName],
                    encoded,
                    cancellationToken).ConfigureAwait(false);
            }
            RequireSuccess(result, "store");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(encoded));
        }
    }

    public async ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        RequireAvailable();
        ProcessResult result;
        if (Backend == LinuxSecretBackend.SecretService)
        {
            result = await runner.RunAsync(secretTool!,
                ["lookup", "service", options.ServiceName, "account", name],
                null,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await runner.RunAsync(kwalletQuery!,
                ["-f", options.KWalletFolder, "-r", EntryName(name), options.KWalletName],
                null,
                cancellationToken).ConfigureAwait(false);
        }
        if (result.ExitCode != 0)
        {
            if (string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
            RequireSuccess(result, "retrieve");
        }
        var value = result.StandardOutput.Trim();
        if (value.Length == 0) return null;
        try { return Convert.FromBase64String(value); }
        catch (FormatException error) { throw new CryptographicException("The Linux secret store returned invalid data.", error); }
    }

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        RequireAvailable();
        var result = Backend == LinuxSecretBackend.SecretService
            ? await runner.RunAsync(secretTool!,
                ["clear", "service", options.ServiceName, "account", name], null, cancellationToken).ConfigureAwait(false)
            : await runner.RunAsync(kwalletQuery!,
                ["-f", options.KWalletFolder, "-d", EntryName(name), options.KWalletName], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase))
            RequireSuccess(result, "delete");
    }

    private LinuxSecretBackend ResolveBackend()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            return LinuxSecretBackend.Unavailable;
        if (options.PreferredBackend == LinuxSecretBackend.SecretService)
            return secretTool is null ? LinuxSecretBackend.Unavailable : LinuxSecretBackend.SecretService;
        if (options.PreferredBackend == LinuxSecretBackend.KWallet)
            return kwalletQuery is null ? LinuxSecretBackend.Unavailable : LinuxSecretBackend.KWallet;
        if (secretTool is not null) return LinuxSecretBackend.SecretService;
        if (kwalletQuery is not null) return LinuxSecretBackend.KWallet;
        return LinuxSecretBackend.Unavailable;
    }

    private void RequireAvailable()
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException(
                "No unlocked Secret Service or KWallet session is available; plaintext credential fallback is forbidden.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.IndexOf('\0') >= 0)
            throw new ArgumentException("The secret name is invalid.", nameof(name));
    }

    private string EntryName(string name) => $"{options.ServiceName}:{name}";

    private static void RequireSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var detail = result.StandardError.Trim();
        if (detail.Length > 300) detail = detail[..300];
        throw new CryptographicException($"Linux secret-store {operation} failed with exit code {result.ExitCode}: {detail}");
    }
}
