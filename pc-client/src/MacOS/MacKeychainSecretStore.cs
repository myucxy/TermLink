using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

public sealed class MacKeychainSecretStore(string serviceName) : ISecretStore
{
    public bool IsAvailable => OperatingSystem.IsMacOS();

    public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(name, secret.Length);
        EnsureMacOS();
        var service = Encoding.UTF8.GetBytes(serviceName);
        var account = Encoding.UTF8.GetBytes(name);
        var password = secret.ToArray();
        IntPtr existing = IntPtr.Zero;
        IntPtr content = IntPtr.Zero;
        try
        {
            var status = SecurityNative.FindGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length), service,
                checked((uint)account.Length), account,
                out _, out content, out existing);
            if (content != IntPtr.Zero) SecurityNative.FreeContent(IntPtr.Zero, content);
            content = IntPtr.Zero;
            if (status == SecurityNative.ItemNotFound)
            {
                RequireSuccess(SecurityNative.AddGenericPassword(
                    IntPtr.Zero,
                    checked((uint)service.Length), service,
                    checked((uint)account.Length), account,
                    checked((uint)password.Length), password,
                    out existing), "add");
            }
            else
            {
                RequireSuccess(status, "find");
                RequireSuccess(SecurityNative.ModifyItem(existing, IntPtr.Zero,
                    checked((uint)password.Length), password), "update");
            }
        }
        finally
        {
            if (content != IntPtr.Zero) SecurityNative.FreeContent(IntPtr.Zero, content);
            if (existing != IntPtr.Zero) CoreFoundationNative.Release(existing);
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(service);
            CryptographicOperations.ZeroMemory(account);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(name, 1);
        EnsureMacOS();
        var service = Encoding.UTF8.GetBytes(serviceName);
        var account = Encoding.UTF8.GetBytes(name);
        IntPtr content = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecurityNative.FindGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length), service,
                checked((uint)account.Length), account,
                out var length, out content, out item);
            if (status == SecurityNative.ItemNotFound) return ValueTask.FromResult<byte[]?>(null);
            RequireSuccess(status, "retrieve");
            if (length == 0 || length > 64 * 1024) throw new CryptographicException("The Keychain secret size is invalid.");
            var result = new byte[checked((int)length)];
            Marshal.Copy(content, result, 0, checked((int)length));
            return ValueTask.FromResult<byte[]?>(result);
        }
        finally
        {
            if (content != IntPtr.Zero) SecurityNative.FreeContent(IntPtr.Zero, content);
            if (item != IntPtr.Zero) CoreFoundationNative.Release(item);
            CryptographicOperations.ZeroMemory(service);
            CryptographicOperations.ZeroMemory(account);
        }
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(name, 1);
        EnsureMacOS();
        var service = Encoding.UTF8.GetBytes(serviceName);
        var account = Encoding.UTF8.GetBytes(name);
        IntPtr content = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecurityNative.FindGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length), service,
                checked((uint)account.Length), account,
                out _, out content, out item);
            if (status == SecurityNative.ItemNotFound) return ValueTask.CompletedTask;
            RequireSuccess(status, "find for deletion");
            RequireSuccess(SecurityNative.DeleteItem(item), "delete");
            return ValueTask.CompletedTask;
        }
        finally
        {
            if (content != IntPtr.Zero) SecurityNative.FreeContent(IntPtr.Zero, content);
            if (item != IntPtr.Zero) CoreFoundationNative.Release(item);
            CryptographicOperations.ZeroMemory(service);
            CryptographicOperations.ZeroMemory(account);
        }
    }

    private void Validate(string name, int secretLength)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.IndexOf('\0') >= 0 ||
            string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 128 || serviceName.IndexOf('\0') >= 0)
            throw new ArgumentException("The Keychain item identity is invalid.", nameof(name));
        if (secretLength is <= 0 or > 65536) throw new ArgumentException("The Keychain secret size is invalid.");
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Keychain requires macOS.");
    }

    private static void RequireSuccess(int status, string operation)
    {
        if (status != 0) throw new CryptographicException($"Keychain {operation} failed with OSStatus {status}.");
    }
}

internal static class SecurityNative
{
    internal const int ItemNotFound = -25300;

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainFindGenericPassword")]
    internal static extern int FindGenericPassword(
        IntPtr keychain,
        uint serviceLength,
        byte[] service,
        uint accountLength,
        byte[] account,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr item);

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainAddGenericPassword")]
    internal static extern int AddGenericPassword(
        IntPtr keychain,
        uint serviceLength,
        byte[] service,
        uint accountLength,
        byte[] account,
        uint passwordLength,
        byte[] password,
        out IntPtr item);

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    internal static extern int ModifyItem(IntPtr item, IntPtr attributes, uint length, byte[] data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainItemDelete")]
    internal static extern int DeleteItem(IntPtr item);

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainItemFreeContent")]
    internal static extern int FreeContent(IntPtr attributes, IntPtr data);
}

internal static class CoreFoundationNative
{
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    internal static extern void Release(IntPtr value);
}
