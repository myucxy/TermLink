using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TermLink.Platform;

namespace TermLink.Platform.Unix;

public sealed class PosixTerminalSessionHost : ITerminalSessionHost, IAsyncDisposable
{
    private const int MaximumEnvironmentEntries = 256;
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private int disposed;

    public ValueTask<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUnix();
        ValidateSize(request.Columns, request.Rows);
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Path.IsPathRooted(request.WorkingDirectory))
            throw new InvalidDataException("An absolute terminal working directory is required.");
        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        var command = PosixTerminalCommandDiscovery.Resolve(request.CommandId);
        using var arguments = NativeStringVector.Create([command.Executable, .. command.Arguments]);
        using var environment = NativeStringVector.Create(BuildEnvironment(request.Environment));
        var result = PosixPtyNative.Spawn(
            command.Executable,
            workingDirectory,
            arguments.Pointer,
            environment.Pointer,
            checked((ushort)request.Columns),
            checked((ushort)request.Rows),
            out var masterFileDescriptor,
            out var processId);
        PosixPtyNative.ThrowIfError(result, "forkpty/execve");

        var id = Guid.NewGuid();
        Session? session = null;
        try
        {
            session = new Session(id, processId, request.CommandId, masterFileDescriptor);
            if (!sessions.TryAdd(id, session)) throw new InvalidOperationException("A duplicate terminal session ID was generated.");
            return ValueTask.FromResult(new TerminalLaunchResult(id, processId, request.CommandId));
        }
        catch
        {
            session?.Dispose();
            if (session is null) PosixPtyNative.Close(masterFileDescriptor);
            PosixPtyNative.SignalGroup(processId, (int)TerminalSignal.Kill);
            throw;
        }
    }

    public ValueTask ResizeAsync(Guid sessionId, int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSize(columns, rows);
        var session = GetSession(sessionId);
        var result = PosixPtyNative.Resize(
            session.MasterFileDescriptor,
            checked((ushort)columns),
            checked((ushort)rows));
        PosixPtyNative.ThrowIfError(result, "PTY resize");
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendInputAsync(
        Guid sessionId,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken)
    {
        if (input.IsEmpty) return;
        var session = GetSession(sessionId);
        await session.WriteAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadOutputAsync(
        Guid sessionId,
        Memory<byte> output,
        CancellationToken cancellationToken)
    {
        if (output.IsEmpty) throw new ArgumentException("An output buffer is required.", nameof(output));
        var session = GetSession(sessionId);
        try
        {
            return await session.ReadAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (session.TryGetExitCode(out _))
        {
            // Linux PTY masters commonly return EIO after the slave is closed.
            return 0;
        }
    }

    public ValueTask SignalAsync(Guid sessionId, TerminalSignal signal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(signal)) throw new ArgumentOutOfRangeException(nameof(signal));
        var session = GetSession(sessionId);
        PosixPtyNative.ThrowIfError(
            PosixPtyNative.SignalGroup(session.ProcessId, (int)signal),
            "terminal process-group signal");
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> WaitForExitAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var exitCode = 0;
        while (!session.TryGetExitCode(out exitCode))
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    public async ValueTask StopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(sessionId, out var session)) return;
        if (!session.TryGetExitCode(out _))
        {
            PosixPtyNative.ThrowIfError(
                PosixPtyNative.SignalGroup(session.ProcessId, (int)TerminalSignal.Terminate),
                "terminal process-group termination");
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                while (!session.TryGetExitCode(out _))
                    await Task.Delay(40, grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                PosixPtyNative.ThrowIfError(
                    PosixPtyNative.SignalGroup(session.ProcessId, (int)TerminalSignal.Kill),
                    "terminal process-group kill");
                while (!session.TryGetExitCode(out _))
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        if (sessions.TryRemove(sessionId, out var removed)) removed.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var id in sessions.Keys)
        {
            try { await StopAsync(id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }

    private Session GetSession(Guid id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (id == Guid.Empty || !sessions.TryGetValue(id, out var session))
            throw new KeyNotFoundException("The terminal session is not active.");
        return session;
    }

    private static string[] BuildEnvironment(IReadOnlyDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is string key && item.Value is string value && IsEnvironmentName(key)) values[key] = value;
        }
        foreach (var item in overrides)
        {
            if (!IsEnvironmentName(item.Key) || item.Value.IndexOf('\0') >= 0 || item.Value.Length > 16 * 1024)
                throw new InvalidDataException("A terminal environment entry is invalid.");
            values[item.Key] = item.Value;
        }
        values.TryAdd("TERM", "xterm-256color");
        values.TryAdd("COLORTERM", "truecolor");
        if (values.Count > MaximumEnvironmentEntries) throw new InvalidDataException("Too many terminal environment entries.");
        return values.Select(value => $"{value.Key}={value.Value}").ToArray();
    }

    private static bool IsEnvironmentName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.IndexOfAny(['\0', '=']) < 0;

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 2 or > 1000) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 2 or > 1000) throw new ArgumentOutOfRangeException(nameof(rows));
    }

    private static void EnsureUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("POSIX PTY sessions require Linux or macOS.");
    }

    private sealed class Session : IDisposable
    {
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly object waitSync = new();
        private int? exitCode;
        private int disposed;

        public Session(Guid id, int processId, string shellType, int masterFileDescriptor)
        {
            Id = id;
            ProcessId = processId;
            ShellType = shellType;
            MasterFileDescriptor = masterFileDescriptor;
        }

        public Guid Id { get; }
        public int ProcessId { get; }
        public string ShellType { get; }
        public int MasterFileDescriptor { get; }

        public async ValueTask<int> ReadAsync(Memory<byte> output, CancellationToken cancellationToken)
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(output.Length);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = PosixPtyNative.Read(MasterFileDescriptor, rented, checked((nuint)output.Length));
                    if (count > 0)
                    {
                        rented.AsMemory(0, checked((int)count)).CopyTo(output);
                        return checked((int)count);
                    }
                    if (count == 0) return 0;
                    var error = Marshal.GetLastPInvokeError();
                    if (error == PosixPtyNative.Interrupted) continue;
                    if (error == PosixPtyNative.WouldBlock)
                    {
                        await Task.Delay(8, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (error == PosixPtyNative.IoError && TryGetExitCode(out _)) return 0;
                    throw PosixPtyNative.CreateException("PTY read", error);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var buffer = input.ToArray();
                var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var written = PosixPtyNative.Write(
                            MasterFileDescriptor,
                            IntPtr.Add(pinned.AddrOfPinnedObject(), offset),
                            checked((nuint)(buffer.Length - offset)));
                        if (written > 0)
                        {
                            offset += checked((int)written);
                            continue;
                        }
                        var error = Marshal.GetLastPInvokeError();
                        if (error == PosixPtyNative.Interrupted) continue;
                        if (error == PosixPtyNative.WouldBlock)
                        {
                            await Task.Delay(8, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        throw PosixPtyNative.CreateException("PTY write", error);
                    }
                }
                finally
                {
                    pinned.Free();
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }
            finally
            {
                writeLock.Release();
            }
        }

        public bool TryGetExitCode(out int value)
        {
            lock (waitSync)
            {
                if (exitCode.HasValue)
                {
                    value = exitCode.Value;
                    return true;
                }
                var nativeValue = 255;
                var result = PosixPtyNative.TryWait(ProcessId, out nativeValue);
                PosixPtyNative.ThrowIfError(result, "waitpid");
                if (result == 0)
                {
                    value = default;
                    return false;
                }
                exitCode = nativeValue;
                value = nativeValue;
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            PosixPtyNative.Close(MasterFileDescriptor);
            writeLock.Dispose();
        }
    }

    private sealed class NativeStringVector : IDisposable
    {
        private readonly IntPtr[] strings;
        public IntPtr Pointer { get; }

        private NativeStringVector(IReadOnlyList<string> values)
        {
            strings = new IntPtr[values.Count];
            Pointer = Marshal.AllocCoTaskMem((values.Count + 1) * IntPtr.Size);
            try
            {
                for (var index = 0; index < values.Count; index++)
                {
                    if (values[index].IndexOf('\0') >= 0) throw new InvalidDataException("A native argument contains NUL.");
                    strings[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
                    Marshal.WriteIntPtr(Pointer, index * IntPtr.Size, strings[index]);
                }
                Marshal.WriteIntPtr(Pointer, values.Count * IntPtr.Size, IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public static NativeStringVector Create(IReadOnlyList<string> values) => new(values);

        public void Dispose()
        {
            foreach (var value in strings)
                if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value);
            if (Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(Pointer);
        }
    }
}

internal static class PosixPtyNative
{
    private const string Library = "termlink-pty";
    internal const int Interrupted = 4;
    internal const int IoError = 5;
    internal const int WouldBlock = 11;

    [DllImport(Library, EntryPoint = "termlink_pty_spawn", ExactSpelling = true)]
    internal static extern int Spawn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string executable,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string workingDirectory,
        IntPtr arguments,
        IntPtr environment,
        ushort columns,
        ushort rows,
        out int masterFileDescriptor,
        out int childProcessId);

    [DllImport(Library, EntryPoint = "termlink_pty_resize", ExactSpelling = true)]
    internal static extern int Resize(int masterFileDescriptor, ushort columns, ushort rows);

    [DllImport(Library, EntryPoint = "termlink_pty_signal_group", ExactSpelling = true)]
    internal static extern int SignalGroup(int childProcessId, int signalNumber);

    [DllImport(Library, EntryPoint = "termlink_pty_try_wait", ExactSpelling = true)]
    internal static extern int TryWait(int childProcessId, out int exitCode);

    [DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    internal static extern int Close(int fileDescriptor);

    [DllImport("libc", EntryPoint = "read", ExactSpelling = true, SetLastError = true)]
    internal static extern nint Read(int fileDescriptor, byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "write", ExactSpelling = true, SetLastError = true)]
    internal static extern nint Write(int fileDescriptor, IntPtr buffer, nuint count);

    internal static void ThrowIfError(int result, string operation)
    {
        if (result >= 0) return;
        var error = -result;
        throw CreateException(operation, error);
    }

    internal static IOException CreateException(string operation, int error) =>
        new($"{operation} failed with POSIX errno {error}.",
            new System.ComponentModel.Win32Exception(error));
}
