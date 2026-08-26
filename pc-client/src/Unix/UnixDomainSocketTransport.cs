using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TermLink.Platform;

namespace TermLink.Platform.Unix;

public sealed class UnixDomainSocketTransport : ILocalIpcTransport
{
    public async ValueTask<Stream> ConnectAsync(
        string endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var path = ValidateEndpoint(endpoint);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeoutSource.Token).ConfigureAwait(false);
            UnixPeerCredentials.RequireCurrentUser(socket);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public ValueTask<ILocalIpcListener> ListenAsync(string endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Unix sockets require Linux or macOS.");
        var path = ValidateEndpoint(endpoint);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("IPC directory is missing.");
        Directory.CreateDirectory(directory);
        EnsurePrivateDirectory(directory);
        RemoveStaleEndpoint(path);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            SetPrivateFileMode(path);
            socket.Listen(32);
            return ValueTask.FromResult<ILocalIpcListener>(new Listener(socket, path));
        }
        catch
        {
            socket.Dispose();
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public static string DefaultEndpoint()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime))
            runtime = Path.Combine(Path.GetTempPath(), $"termlink-{UnixPeerCredentials.EffectiveUserId}");
        return Path.Combine(runtime, "termlink", "agent-v1.sock");
    }

    private static string ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.IndexOf('\0') >= 0 || !Path.IsPathRooted(endpoint))
            throw new ArgumentException("An absolute Unix socket endpoint is required.", nameof(endpoint));
        var path = Path.GetFullPath(endpoint);
        if (System.Text.Encoding.UTF8.GetByteCount(path) > 100)
            throw new PathTooLongException("The Unix socket endpoint exceeds the portable 100-byte limit.");
        return path;
    }

    private static void RemoveStaleEndpoint(string path)
    {
        if (!File.Exists(path)) return;
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
            UnixPeerCredentials.RequireCurrentUser(probe);
            throw new IOException("A local Agent is already listening on the Unix socket.");
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionRefused or
            SocketError.AddressNotAvailable or SocketError.NotConnected)
        {
            File.Delete(path);
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        UnixFileMode mode;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            mode = File.GetUnixFileMode(directory);
        else
            throw new PlatformNotSupportedException("Unix sockets require Linux or macOS.");
        const UnixFileMode disallowed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute;
        if ((mode & disallowed) != 0)
        {
            SetPrivateDirectoryMode(directory);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else
            throw new PlatformNotSupportedException("Unix sockets require Linux or macOS.");
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            throw new PlatformNotSupportedException("Unix sockets require Linux or macOS.");
    }

    private sealed class Listener(Socket socket, string path) : ILocalIpcListener
    {
        private int disposed;

        public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var accepted = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                UnixPeerCredentials.RequireCurrentUser(accepted);
                return new NetworkStream(accepted, ownsSocket: true);
            }
            catch
            {
                accepted.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
            socket.Dispose();
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }
}

internal static class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    public static uint EffectiveUserId => GetEffectiveUserId();

    public static void RequireCurrentUser(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var actual = OperatingSystem.IsLinux() ? GetLinuxPeerUserId(socket.SafeHandle) :
            OperatingSystem.IsMacOS() ? GetMacPeerUserId(socket.SafeHandle) :
            throw new PlatformNotSupportedException("Peer credentials require Linux or macOS.");
        if (actual != EffectiveUserId)
            throw new UnauthorizedAccessException("The local IPC peer belongs to a different user.");
    }

    private static uint GetLinuxPeerUserId(SafeSocketHandle handle)
    {
        var credentials = new LinuxUCred();
        var length = (uint)Marshal.SizeOf<LinuxUCred>();
        var result = GetSocketOption(handle, SolSocket, SoPeerCred, ref credentials, ref length);
        if (result != 0 || length != Marshal.SizeOf<LinuxUCred>())
            throw new IOException("SO_PEERCRED failed.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return credentials.UserId;
    }

    private static uint GetMacPeerUserId(SafeSocketHandle handle)
    {
        var result = GetPeerEffectiveIds(handle, out var userId, out _);
        if (result != 0)
            throw new IOException("getpeereid failed.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return userId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxUCred
    {
        public int ProcessId;
        public uint UserId;
        public uint GroupId;
    }

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true, ExactSpelling = true)]
    private static extern int GetSocketOption(
        SafeSocketHandle socket,
        int level,
        int name,
        ref LinuxUCred value,
        ref uint length);

    [DllImport("libc", EntryPoint = "getpeereid", SetLastError = true, ExactSpelling = true)]
    private static extern int GetPeerEffectiveIds(
        SafeSocketHandle socket,
        out uint userId,
        out uint groupId);
}
