using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using TermLink.UpdateCore;

namespace TermLink.Ai;

// Reuse the installed, signed desktop engine when WindowsApps blocks direct execution.
internal static class CodexDesktopRuntime
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly string[] Files = { "codex.exe", "codex-code-mode-host.exe", "codex-command-runner.exe", "codex-windows-sandbox-setup.exe" };

    internal static async Task<string> PrepareAsync(string executable, CancellationToken token, string cacheRoot = null)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(executable))!;
            if (!Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected Codex desktop executable.");
            using var source = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyPublisher(executable);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)).ToLowerInvariant();
            cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink", "Agent", "CodexDesktopRuntime");
            var root = Path.GetFullPath(cacheRoot);
            var destination = Path.Combine(root, digest);
            ProtectDirectory(root);
            ProtectDirectory(destination);
            foreach (var name in Files) {
                token.ThrowIfCancellationRequested();
                var original = Path.Combine(sourceRoot, name);
                if (!File.Exists(original)) { if (name == "codex.exe") throw new FileNotFoundException(); continue; }
                using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = name == "codex.exe" ? digest : Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).ToLowerInvariant();
                var target = Path.Combine(destination, name);
                if (File.Exists(target)) {
                    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime file is redirected.");
                    using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(existing, token).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                    throw new CryptographicException("Cached Codex runtime changed; remove this cache version before retrying.");
                }
                if (name != "codex.exe") VerifyPublisher(original);
                var temporary = Path.Combine(destination, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try {
                    input.Position = 0;
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true)) {
                        await input.CopyToAsync(output, token).ConfigureAwait(false);
                        output.Position = 0;
                        var copied = Convert.ToHexString(await SHA256.HashDataAsync(output, token).ConfigureAwait(false));
                        if (!copied.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Codex runtime copy is incomplete.");
                        output.Flush(true);
                    }
                    File.Move(temporary, target);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return Path.Combine(destination, "codex.exe");
        } finally { Gate.Release(); }
    }

    private static void VerifyPublisher(string path)
    {
        if (WindowsUpdateSecurity.VerifyEmbeddedSignature(path) != WindowsUpdateSecurity.TrustSuccess)
            throw new CryptographicException("Codex desktop engine signature is not trusted.");
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        if (certificate.GetNameInfo(X509NameType.SimpleName, false) != "OpenAI OpCo, LLC")
            throw new CryptographicException("Codex desktop engine publisher is not OpenAI.");
    }

    private static void ProtectDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime cache directory is redirected.");
        var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows identity unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (Directory.Exists(path)) new DirectoryInfo(path).SetAccessControl(security);
        else FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
    }
}
