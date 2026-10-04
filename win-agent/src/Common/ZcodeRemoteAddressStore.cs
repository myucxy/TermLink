using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Ai;

/// <summary>Pairing links are credentials. Only the current Windows user can decrypt them.</summary>
public sealed class ZcodeRemoteAddressStore
{
    public static ZcodeRemoteAddressStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink", "Agent", "zcode-remote-url.dpapi"));
    private readonly string path;
    public ZcodeRemoteAddressStore(string path) { this.path = path; }

    public static bool IsAllowed(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192 &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Host.Equals("zcode.z.ai", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath == "/remote" || uri.AbsolutePath.StartsWith("/remote/", StringComparison.Ordinal));

    public string Read()
    {
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 32 * 1024) throw new IOException("ZCode remote configuration is invalid.");
        var value = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        if (!IsAllowed(value)) throw new IOException("ZCode remote configuration is invalid.");
        return value;
    }

    public void Save(string value)
    {
        value = value?.Trim() ?? "";
        if (!IsAllowed(value)) throw new ArgumentException("请输入 https://zcode.z.ai/remote/ 下的完整远控地址。");
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Clear() => File.Delete(path);
}
