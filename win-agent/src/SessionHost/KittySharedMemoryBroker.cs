using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.TerminalCore;

namespace TermLink.SessionHost;

// Cooperative t=s protocol: connect to TERMLINK_KITTY_SHM_PIPE, send "TLKS" plus a
// little-endian uint32 byte count, then read status (0=ready, 1=unavailable,
// 2=over limit), a little-endian uint16 name length and the UTF-8 mapping name.
// Write exactly the requested bytes to that mapping, close it, then emit Kitty
// APC t=s with the returned name as its base64 payload. Each name is single use.
// The Host never opens a name supplied by the terminal process.
// Local\ limits names to the logon session. The map DACL is explicitly limited
// to this Windows user and SYSTEM; CurrentUserOnly protects only the pipe. A
// different process running as the same user could open a leaked mapping name,
// so this is a cooperative same-user boundary, not isolation from a compromised
// process with that user's full privileges.
internal sealed class KittySharedMemoryBroker : IDisposable
{
    internal const string EnvironmentVariable = "TERMLINK_KITTY_SHM_PIPE";
    internal const int MaximumObjectBytes = 16 * 1024 * 1024;
    internal const int MaximumObjects = 32;
    internal const long MaximumReservedBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly object sync = new();
    private readonly IntPtr job;
    private readonly Func<long> milliseconds;
    private readonly Action beforeCaptureCopy;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private NamedPipeServerStream waitingPipe;
    private Task serverTask;
    private Timer sweeper;
    private long reservedBytes;
    private int reservations;
    private bool disposed;
    private Exception backgroundFailure;

    internal string PipeName { get; }
    internal Exception BackgroundFailure { get { lock (sync) return backgroundFailure; } }

    internal KittySharedMemoryBroker(Guid sessionId, IntPtr job, Func<long> milliseconds = null,
        Action beforeCaptureCopy = null)
    {
        if (sessionId == Guid.Empty || job == IntPtr.Zero || job == new IntPtr(-1))
            throw new ArgumentException("A live session Job and ID are required.");
        this.job = job;
        this.milliseconds = milliseconds ?? (() => Environment.TickCount64);
        this.beforeCaptureCopy = beforeCaptureCopy;
        PipeName = "termlink-kitty-shm-" + sessionId.ToString("N") + "-" + Guid.NewGuid().ToString("N");
    }

    internal void Start()
    {
        lock (sync)
        {
            if (disposed || serverTask != null) throw new InvalidOperationException("Shared memory broker cannot start.");
            var initial = CreatePipe();
            try
            {
                waitingPipe = initial;
                sweeper = new Timer(_ => SweepExpired(), null, 1000, 1000);
                serverTask = Task.Run(() => ServeAsync(initial));
            }
            catch
            {
                sweeper?.Dispose();
                waitingPipe = null;
                initial.Dispose();
                throw;
            }
        }
    }

    private NamedPipeServerStream CreatePipe() => new(PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        4096, 4096);

    private async Task ServeAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream next = first;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = next ?? CreatePipe();
                next = null;
                lock (sync) waitingPipe = pipe;
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    if (!AuthorizedClient(pipe)) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(2500);
                    var request = new byte[8];
                    await pipe.ReadExactlyAsync(request, timeout.Token).ConfigureAwait(false);
                    if (!request.AsSpan(0, 4).SequenceEqual("TLKS"u8)) continue;
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4));
                    var (status, name) = Allocate(length);
                    var encodedName = status == 0 ? Encoding.UTF8.GetBytes(name) : Array.Empty<byte>();
                    var response = new byte[3 + encodedName.Length];
                    response[0] = status;
                    BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(1), (ushort)encodedName.Length);
                    encodedName.CopyTo(response.AsSpan(3));
                    try { await pipe.WriteAsync(response, timeout.Token).ConfigureAwait(false); }
                    catch { if (name != null) Revoke(name); throw; }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (IOException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
                finally { lock (sync) if (ReferenceEquals(waitingPipe, pipe)) waitingPipe = null; }
            }
        }
        catch (Exception error)
        {
            lock (sync) backgroundFailure = error;
            Console.Error.WriteLine("__KITTY_SHM_BROKER_FAILURE__:" + error.GetType().Name);
        }
        finally { next?.Dispose(); }
    }

    private bool AuthorizedClient(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0) return false;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        return !process.IsInvalid && IsProcessInJob(process, job, out var belongs) && belongs;
    }

    private (byte Status, string Name) Allocate(uint requested)
    {
        if (requested == 0 || requested > MaximumObjectBytes) return (2, null);
        SweepExpired();
        lock (sync)
        {
            if (disposed || backgroundFailure != null) return (1, null);
            if (entries.Count + reservations >= MaximumObjects ||
                reservedBytes > MaximumReservedBytes - requested) return (2, null);
            reservations++;
            reservedBytes += requested;
        }
        MemoryMappedFile mapping = null;
        var reserved = true;
        var name = "Local\\TermLink.Kitty." + Guid.NewGuid().ToString("N");
        try
        {
            mapping = MemoryMappedFile.CreateNew(name, requested, MemoryMappedFileAccess.ReadWrite,
                MemoryMappedFileOptions.None, HandleInheritability.None);
            ProtectMapping(mapping);
            lock (sync)
            {
                if (disposed) return (1, null);
                entries.Add(name, new Entry(mapping, checked((int)requested),
                    checked(milliseconds() + (long)Lifetime.TotalMilliseconds)));
                reservations--;
                reserved = false;
                mapping = null;
            }
            return (0, name);
        }
        catch (IOException) { return (1, null); }
        catch (UnauthorizedAccessException) { return (1, null); }
        finally
        {
            try { mapping?.Dispose(); }
            finally
            {
                if (reserved)
                {
                    lock (sync)
                    {
                        reservations--;
                        reservedBytes -= requested;
                    }
                }
            }
        }
    }

    private static void ProtectMapping(MemoryMappedFile mapping)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Current Windows user is unavailable.");
        var sddl = "D:P(A;;GA;;;" + user.Value + ")(A;;GA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1,
                out var descriptor, out _))
            throw new IOException("Kitty mapping DACL could not be created.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            if (!SetKernelObjectSecurity(mapping.SafeMemoryMappedFileHandle, 4, descriptor))
                throw new IOException("Kitty mapping DACL could not be applied.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { LocalFree(descriptor); }
    }

    internal (byte[] Bytes, NativeTerminalBrokerFailure? Failure) Capture(string name, ulong offset, uint? length)
    {
        Entry entry;
        lock (sync)
        {
            if (disposed || name == null || !entries.Remove(name, out entry))
                return (null, NativeTerminalBrokerFailure.Unavailable);
            // Keep both bytes and the object slot reserved while the original
            // mapping handle is still live and its private copy is in flight.
            reservations++;
        }
        try
        {
            beforeCaptureCopy?.Invoke();
            if (milliseconds() >= entry.ExpiresAtMilliseconds || offset > (ulong)entry.Length ||
                (length.HasValue && length.Value > (ulong)entry.Length - offset))
                return (null, NativeTerminalBrokerFailure.Unavailable);
            var count = checked((int)(length ?? (uint)(entry.Length - (long)offset)));
            if (count > MaximumObjectBytes) return (null, NativeTerminalBrokerFailure.OverLimit);
            var bytes = new byte[count];
            try
            {
                if (count != 0)
                {
                    using var view = entry.Mapping.CreateViewAccessor(checked((long)offset), count,
                        MemoryMappedFileAccess.Read);
                    if (view.ReadArray(0, bytes, 0, count) != count)
                        throw new IOException("Shared memory capture was incomplete.");
                }
                return (bytes, null);
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        catch (IOException) { return (null, NativeTerminalBrokerFailure.Unavailable); }
        catch (UnauthorizedAccessException) { return (null, NativeTerminalBrokerFailure.Unavailable); }
        finally
        {
            try { entry.Mapping.Dispose(); }
            finally
            {
                lock (sync)
                {
                    reservations--;
                    reservedBytes -= entry.Length;
                }
            }
        }
    }

    private void Revoke(string name)
    {
        Entry entry;
        lock (sync)
        {
            if (!entries.Remove(name, out entry)) return;
            reservations++;
        }
        try { entry.Mapping.Dispose(); }
        finally
        {
            lock (sync) { reservations--; reservedBytes -= entry.Length; }
        }
    }

    private void SweepExpired()
    {
        List<Entry> expired = new();
        lock (sync)
        {
            if (disposed) return;
            var now = milliseconds();
            foreach (var pair in entries)
                if (now >= pair.Value.ExpiresAtMilliseconds) expired.Add(pair.Value);
            if (expired.Count == 0) return;
            foreach (var pair in new List<KeyValuePair<string, Entry>>(entries))
                if (now >= pair.Value.ExpiresAtMilliseconds)
                {
                    entries.Remove(pair.Key);
                    reservations++;
                }
        }
        foreach (var entry in expired)
        {
            try { entry.Mapping.Dispose(); }
            finally { lock (sync) { reservations--; reservedBytes -= entry.Length; } }
        }
    }

    public void Dispose()
    {
        NamedPipeServerStream pipe;
        Task task;
        List<Entry> retired;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pipe = waitingPipe;
            task = serverTask;
            retired = new List<Entry>(entries.Values);
            entries.Clear();
            foreach (var entry in retired) reservedBytes -= entry.Length;
        }
        stop.Cancel();
        sweeper?.Dispose();
        pipe?.Dispose();
        try { task?.Wait(3000); } catch (AggregateException) { }
        foreach (var entry in retired) entry.Mapping.Dispose();
        if (task == null || task.IsCompleted) stop.Dispose();
    }

    private sealed record Entry(MemoryMappedFile Mapping, int Length, long ExpiresAtMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,
        uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeMemoryMappedFileHandle handle,
        uint securityInformation, IntPtr securityDescriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
