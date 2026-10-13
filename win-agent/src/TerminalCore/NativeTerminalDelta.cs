using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TermLink.TerminalCore;

public enum NativeTerminalDeltaProfile : uint
{
    LegacyV1 = 1,
    Unicode16V1 = 2,
    LegacyStandaloneC1V1 = 3,
    Unicode16StandaloneC1V1 = 4
}

public sealed class NativeTerminalDeltaContext
{
    private readonly byte[] id;

    public NativeTerminalDeltaContext(byte[] id, NativeTerminalDeltaProfile profile)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (id.Length != 32 || Array.TrueForAll(id, value => value == 0))
            throw new ArgumentException("A Delta context requires a nonzero 32-byte authenticated ID.", nameof(id));
        if ((uint)profile is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(profile));
        this.id = (byte[])id.Clone();
        Profile = profile;
    }

    public byte[] Id => (byte[])id.Clone();
    public NativeTerminalDeltaProfile Profile { get; }

    internal unsafe TlTerminalDeltaContextV1 ToNative()
    {
        var value = new TlTerminalDeltaContextV1 { AbiVersion = 1, StructSize = 64, Profile = (uint)Profile };
        byte* destination = value.Id;
        id.AsSpan().CopyTo(new Span<byte>(destination, 32));
        return value;
    }

    internal unsafe TlTerminalDeltaJournalConfigV1 ToJournalConfig(uint maxEntries, ulong maxBytes, ulong maxAgeMillis)
    {
        var value = new TlTerminalDeltaJournalConfigV1
        {
            AbiVersion = 1, StructSize = 80, Profile = (uint)Profile,
            MaxEntries = maxEntries, MaxBytes = maxBytes, MaxAgeMillis = maxAgeMillis
        };
        byte* destination = value.ContextId;
        id.AsSpan().CopyTo(new Span<byte>(destination, 32));
        return value;
    }
}

public sealed class NativeTerminalDeltaState
{
    private readonly byte[] sha256;

    internal NativeTerminalDeltaState(ulong nextSourceSequence, ulong snapshotBytes, byte[] sha256)
    {
        NextSourceSequence = nextSourceSequence;
        SnapshotBytes = snapshotBytes;
        this.sha256 = sha256;
    }

    public ulong NextSourceSequence { get; }
    public ulong SnapshotBytes { get; }
    public byte[] Sha256 => (byte[])sha256.Clone();

    internal unsafe TlTerminalDeltaStateInfoV1 ToNative()
    {
        var value = new TlTerminalDeltaStateInfoV1
        {
            AbiVersion = 1, StructSize = 64,
            NextSourceSequence = NextSourceSequence, SnapshotBytes = SnapshotBytes
        };
        byte* destination = value.Sha256;
        sha256.AsSpan().CopyTo(new Span<byte>(destination, 32));
        return value;
    }
}

public enum NativeTerminalDeltaOutcome { Applied = 1, AlreadyApplied = 2 }

public readonly struct NativeTerminalDeltaApplyResult
{
    internal NativeTerminalDeltaApplyResult(NativeTerminalDeltaOutcome outcome,
        NativeTerminalDeltaState state, NativeTerminal terminal)
    { Outcome = outcome; State = state; Terminal = terminal; }

    public NativeTerminalDeltaOutcome Outcome { get; }
    public NativeTerminalDeltaState State { get; }
    // Only Applied owns a detached terminal. The caller publishes it after its durable transaction.
    public NativeTerminal Terminal { get; }
}

public sealed class NativeTerminalDeltaJournalInfo
{
    internal NativeTerminalDeltaJournalInfo(uint entries, ulong retainedBytes,
        NativeTerminalDeltaState oldest, NativeTerminalDeltaState newest)
    { Entries = entries; RetainedBytes = retainedBytes; Oldest = oldest; Newest = newest; }

    public uint Entries { get; }
    public ulong RetainedBytes { get; }
    public NativeTerminalDeltaState Oldest { get; }
    public NativeTerminalDeltaState Newest { get; }
}

public static class NativeTerminalDelta
{
    public const ulong MaximumDeltaBytes = 128UL * 1024 * 1024;
    private static readonly Lazy<bool> Available = new(CheckExports);

    public static bool SupportsV1() => Available.Value;

    internal static void RequireV1()
    {
        _ = NativeTerminalCoreV2.ValidateAbi();
        if (!SupportsV1()) throw new NotSupportedException("This terminal-core DLL does not provide Delta V1.");
    }

    internal static void CheckMaximum(ulong maxBytes)
    {
        if (maxBytes == 0 || maxBytes > MaximumDeltaBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    internal static unsafe NativeTerminalDeltaState ReadState(TlTerminalDeltaStateInfoV1 value, bool empty = false)
    {
        if (value.AbiVersion != 1 || value.StructSize != 64 || value.Reserved != 0 ||
            (empty ? value.NextSourceSequence != 0 || value.SnapshotBytes != 0 :
                value.SnapshotBytes == 0 ||
                value.SnapshotBytes > NativeTerminalCoreV2.MaximumGraphicsSnapshotBytes))
            throw new NativeTerminalAbiException("Invalid Delta state metadata.");
        var digest = new byte[32];
        byte* source = value.Sha256;
        new ReadOnlySpan<byte>(source, 32).CopyTo(digest);
        if (empty && Array.Exists(digest, b => b != 0))
            throw new NativeTerminalAbiException("Invalid empty Delta state digest.");
        return new NativeTerminalDeltaState(value.NextSourceSequence, value.SnapshotBytes, digest);
    }

    internal static byte[] ReadAndFree(ref TlTerminalBuffer buffer, ulong maximum)
    {
        try
        {
            if (buffer.AbiVersion != 1 || buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                buffer.Data == IntPtr.Zero || buffer.Length == 0 ||
                buffer.Length > maximum || buffer.Length > int.MaxValue)
                throw new NativeTerminalAbiException("Invalid native Delta buffer.");
            var bytes = new byte[(int)buffer.Length];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
            {
                NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "delta.free");
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    throw new NativeTerminalAbiException("Native Delta buffer was not cleared after free.");
            }
        }
    }

    private static bool CheckExports()
    {
        if (Marshal.SizeOf<TlTerminalDeltaContextV1>() != 64 ||
            Marshal.SizeOf<TlTerminalDeltaStateInfoV1>() != 64 ||
            Marshal.SizeOf<TlTerminalDeltaJournalConfigV1>() != 80 ||
            Marshal.SizeOf<TlTerminalDeltaJournalInfoV1>() != 168 ||
            Marshal.SizeOf<TlTerminalDeltaApplyInfoV1>() != 96)
            throw new NativeTerminalAbiException("Managed Delta ABI structure layout mismatch.");
        var library = NativeLibrary.Load("termlink_terminal_ffi.dll", typeof(NativeTerminalDelta).Assembly,
            DllImportSearchPath.AssemblyDirectory);
        try
        {
            foreach (var name in new[]
            {
                "tl_terminal_checkpoint_delta_info_v1", "tl_terminal_checkpoint_export_delta_v1",
                "tl_terminal_checkpoint_apply_delta_v1", "tl_terminal_delta_journal_create_v1",
                "tl_terminal_delta_journal_destroy", "tl_terminal_delta_journal_capture_v1",
                "tl_terminal_delta_journal_get_info_v1", "tl_terminal_delta_journal_export_v1"
            })
                if (!NativeLibrary.TryGetExport(library, name, out _)) return false;
            return true;
        }
        finally { NativeLibrary.Free(library); }
    }
}

public sealed partial class NativeTerminal
{
    public NativeTerminalDeltaJournal BeginDeltaJournal(NativeTerminalDeltaContext context,
        uint maxEntries, ulong maxBytes, ulong maxAgeMillis)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (maxEntries is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes == 0 || maxBytes > 256UL * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxAgeMillis == 0 || maxAgeMillis > 7UL * 24 * 60 * 60 * 1000)
            throw new ArgumentOutOfRangeException(nameof(maxAgeMillis));
        lock (sync)
        {
            ThrowIfUnavailable();
            if (!HasGraphicsProfile) throw new InvalidOperationException("Delta V1 requires a graphics terminal.");
            NativeTerminalDelta.RequireV1();
            return NativeTerminalDeltaJournal.Create(handle, context, maxEntries, maxBytes, maxAgeMillis);
        }
    }
}

public sealed partial class NativeTerminalCheckpoint
{
    internal SafeTlTerminalCheckpointHandle DeltaHandle
    {
        get { lock (sync) { ThrowIfDisposed(); return handle; } }
    }

    public NativeTerminalDeltaState GetDeltaState(NativeTerminalDeltaContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        lock (sync)
        {
            ThrowIfDisposed();
            RequireGraphicsProfile();
            NativeTerminalDelta.RequireV1();
            var nativeContext = context.ToNative();
            var state = new TlTerminalDeltaStateInfoV1 { AbiVersion = 1, StructSize = 64 };
            NativeTerminalInterop.ThrowIfFailed(DeltaNative.CheckpointInfo(handle, in nativeContext, ref state), "delta.info");
            return NativeTerminalDelta.ReadState(state);
        }
    }

    public byte[] ExportDeltaTo(NativeTerminalCheckpoint target, NativeTerminalDeltaContext context,
        ulong maxBytes = NativeTerminalDelta.MaximumDeltaBytes)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (context == null) throw new ArgumentNullException(nameof(context));
        NativeTerminalDelta.CheckMaximum(maxBytes);
        var targetHandle = target.DeltaHandle;
        lock (sync)
        {
            ThrowIfDisposed();
            RequireGraphicsProfile();
            if (!target.HasGraphicsProfile) throw new InvalidOperationException("Delta V1 requires graphics checkpoints.");
            NativeTerminalDelta.RequireV1();
            var nativeContext = context.ToNative();
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                NativeTerminalInterop.ThrowIfFailed(DeltaNative.CheckpointExport(handle, targetHandle,
                    in nativeContext, maxBytes, ref buffer), "delta.export");
                return NativeTerminalDelta.ReadAndFree(ref buffer, maxBytes);
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "delta.export.free");
            }
        }
    }

    public unsafe NativeTerminalDeltaApplyResult ApplyDelta(NativeTerminalDeltaContext context,
        ReadOnlySpan<byte> delta, ulong maxBytes = NativeTerminalDelta.MaximumDeltaBytes)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        NativeTerminalDelta.CheckMaximum(maxBytes);
        if (delta.Length == 0 || (ulong)delta.Length > maxBytes)
            throw new ArgumentOutOfRangeException(nameof(delta));
        lock (sync)
        {
            ThrowIfDisposed();
            RequireGraphicsProfile();
            NativeTerminalDelta.RequireV1();
            var nativeContext = context.ToNative();
            var info = new TlTerminalDeltaApplyInfoV1 { AbiVersion = 1, StructSize = 96 };
            var terminal = IntPtr.Zero;
            try
            {
                fixed (byte* data = delta)
                {
                    var status = DeltaNative.CheckpointApply(handle, in nativeContext, data,
                        (ulong)delta.Length, maxBytes, ref terminal, ref info);
                    if (status != NativeTerminalStatus.Ok && terminal != IntPtr.Zero)
                    {
                        NativeTerminalInterop.TerminalDestroy(terminal);
                        terminal = IntPtr.Zero;
                        throw new NativeTerminalAbiException("Native Delta apply returned a handle after failure.");
                    }
                    NativeTerminalInterop.ThrowIfFailed(status, "delta.apply");
                }
                if (info.AbiVersion != 1 || info.StructSize != 96 || info.Reserved0 != 0 ||
                    info.Reserved1 != 0 || info.Reserved2 != 0 ||
                    (info.Outcome != 1 && info.Outcome != 2) ||
                    (info.Outcome == 1) != (terminal != IntPtr.Zero))
                    throw new NativeTerminalAbiException("Invalid native Delta apply result or ownership slot.");
                var state = NativeTerminalDelta.ReadState(info.State);
                if (info.Outcome == 2)
                    return new NativeTerminalDeltaApplyResult(NativeTerminalDeltaOutcome.AlreadyApplied, state, null);
                var owned = NativeTerminalInterop.OwnTerminal(terminal);
                terminal = IntPtr.Zero;
                return new NativeTerminalDeltaApplyResult(NativeTerminalDeltaOutcome.Applied, state,
                    new NativeTerminal(owned, true));
            }
            finally { if (terminal != IntPtr.Zero) NativeTerminalInterop.TerminalDestroy(terminal); }
        }
    }
}

public sealed class NativeTerminalDeltaJournal : IDisposable
{
    private readonly object sync = new();
    private readonly SafeTlTerminalDeltaJournalHandle handle;
    private bool disposed;

    private NativeTerminalDeltaJournal(SafeTlTerminalDeltaJournalHandle handle) { this.handle = handle; }

    internal static NativeTerminalDeltaJournal Create(SafeTlTerminalHandle terminal,
        NativeTerminalDeltaContext context, uint maxEntries, ulong maxBytes, ulong maxAgeMillis)
    {
        var config = context.ToJournalConfig(maxEntries, maxBytes, maxAgeMillis);
        var pointer = IntPtr.Zero;
        var status = DeltaNative.JournalCreate(terminal, in config, ref pointer);
        if (status != NativeTerminalStatus.Ok && pointer != IntPtr.Zero)
        {
            DeltaNative.JournalDestroy(pointer);
            throw new NativeTerminalAbiException("Native Delta journal returned a handle after failure.");
        }
        NativeTerminalInterop.ThrowIfFailed(status, "delta.journal.create");
        if (pointer == IntPtr.Zero) throw new NativeTerminalAbiException("Native Delta journal returned no handle.");
        return new NativeTerminalDeltaJournal(new SafeTlTerminalDeltaJournalHandle(pointer));
    }

    public NativeTerminalDeltaState Capture(NativeTerminalCheckpoint checkpoint, ulong nowMillis)
    {
        if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
        lock (sync)
        {
            ThrowIfDisposed();
            if (!checkpoint.HasGraphicsProfile) throw new InvalidOperationException("Delta V1 requires a graphics checkpoint.");
            var state = new TlTerminalDeltaStateInfoV1 { AbiVersion = 1, StructSize = 64 };
            NativeTerminalInterop.ThrowIfFailed(DeltaNative.JournalCapture(handle, checkpoint.DeltaHandle,
                nowMillis, ref state), "delta.journal.capture");
            return NativeTerminalDelta.ReadState(state);
        }
    }

    public NativeTerminalDeltaJournalInfo GetInfo(ulong nowMillis)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var info = new TlTerminalDeltaJournalInfoV1 { AbiVersion = 1, StructSize = 168 };
            NativeTerminalInterop.ThrowIfFailed(DeltaNative.JournalInfo(handle, nowMillis, ref info), "delta.journal.info");
            if (info.AbiVersion != 1 || info.StructSize != 168 || info.Entries > 256 ||
                info.Reserved0 != 0 || info.Reserved1 != 0 || info.Reserved2 != 0 ||
                info.RetainedBytes > 256UL * 1024 * 1024)
                throw new NativeTerminalAbiException("Invalid Delta journal metadata.");
            var empty = info.Entries == 0;
            return new NativeTerminalDeltaJournalInfo(info.Entries, info.RetainedBytes,
                NativeTerminalDelta.ReadState(info.Oldest, empty), NativeTerminalDelta.ReadState(info.Newest, empty));
        }
    }

    public byte[] Export(NativeTerminalDeltaState baseState, NativeTerminalDeltaState targetState,
        ulong nowMillis, ulong maxBytes = NativeTerminalDelta.MaximumDeltaBytes)
    {
        if (baseState == null) throw new ArgumentNullException(nameof(baseState));
        if (targetState == null) throw new ArgumentNullException(nameof(targetState));
        NativeTerminalDelta.CheckMaximum(maxBytes);
        lock (sync)
        {
            ThrowIfDisposed();
            var baseNative = baseState.ToNative();
            var targetNative = targetState.ToNative();
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                NativeTerminalInterop.ThrowIfFailed(DeltaNative.JournalExport(handle, in baseNative,
                    in targetNative, nowMillis, maxBytes, ref buffer), "delta.journal.export");
                return NativeTerminalDelta.ReadAndFree(ref buffer, maxBytes);
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "delta.journal.export.free");
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            handle.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    { if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalDeltaJournal)); }
}

internal sealed class SafeTlTerminalDeltaJournalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeTlTerminalDeltaJournalHandle(IntPtr value) : base(true) { SetHandle(value); }
    protected override bool ReleaseHandle() { DeltaNative.JournalDestroy(handle); return true; }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlTerminalDeltaContextV1
{
    internal uint AbiVersion, StructSize, Profile, Reserved0;
    internal fixed byte Id[32];
    internal ulong Reserved1, Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlTerminalDeltaStateInfoV1
{
    internal uint AbiVersion, StructSize;
    internal ulong NextSourceSequence, SnapshotBytes;
    internal fixed byte Sha256[32];
    internal ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlTerminalDeltaJournalConfigV1
{
    internal uint AbiVersion, StructSize, Profile, MaxEntries;
    internal fixed byte ContextId[32];
    internal ulong MaxBytes, MaxAgeMillis, Reserved1, Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlTerminalDeltaJournalInfoV1
{
    internal uint AbiVersion, StructSize, Entries, Reserved0;
    internal ulong RetainedBytes;
    internal TlTerminalDeltaStateInfoV1 Oldest, Newest;
    internal ulong Reserved1, Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlTerminalDeltaApplyInfoV1
{
    internal uint AbiVersion, StructSize, Outcome, Reserved0;
    internal TlTerminalDeltaStateInfoV1 State;
    internal ulong Reserved1, Reserved2;
}

internal static unsafe class DeltaNative
{
    private const string Library = "termlink_terminal_ffi.dll";
    [DllImport(Library, EntryPoint = "tl_terminal_checkpoint_delta_info_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus CheckpointInfo(SafeTlTerminalCheckpointHandle checkpoint,
        in TlTerminalDeltaContextV1 context, ref TlTerminalDeltaStateInfoV1 info);
    [DllImport(Library, EntryPoint = "tl_terminal_checkpoint_export_delta_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus CheckpointExport(SafeTlTerminalCheckpointHandle baseCheckpoint,
        SafeTlTerminalCheckpointHandle targetCheckpoint, in TlTerminalDeltaContextV1 context,
        ulong maxBytes, ref TlTerminalBuffer buffer);
    [DllImport(Library, EntryPoint = "tl_terminal_checkpoint_apply_delta_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus CheckpointApply(SafeTlTerminalCheckpointHandle checkpoint,
        in TlTerminalDeltaContextV1 context, byte* delta, ulong deltaLength, ulong maxBytes,
        ref IntPtr terminal, ref TlTerminalDeltaApplyInfoV1 info);
    [DllImport(Library, EntryPoint = "tl_terminal_delta_journal_create_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus JournalCreate(SafeTlTerminalHandle terminal,
        in TlTerminalDeltaJournalConfigV1 config, ref IntPtr journal);
    [DllImport(Library, EntryPoint = "tl_terminal_delta_journal_destroy", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void JournalDestroy(IntPtr journal);
    [DllImport(Library, EntryPoint = "tl_terminal_delta_journal_capture_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus JournalCapture(SafeTlTerminalDeltaJournalHandle journal,
        SafeTlTerminalCheckpointHandle checkpoint, ulong nowMillis, ref TlTerminalDeltaStateInfoV1 state);
    [DllImport(Library, EntryPoint = "tl_terminal_delta_journal_get_info_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus JournalInfo(SafeTlTerminalDeltaJournalHandle journal,
        ulong nowMillis, ref TlTerminalDeltaJournalInfoV1 info);
    [DllImport(Library, EntryPoint = "tl_terminal_delta_journal_export_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeTerminalStatus JournalExport(SafeTlTerminalDeltaJournalHandle journal,
        in TlTerminalDeltaStateInfoV1 baseState, in TlTerminalDeltaStateInfoV1 targetState,
        ulong nowMillis, ulong maxBytes, ref TlTerminalBuffer buffer);
}
