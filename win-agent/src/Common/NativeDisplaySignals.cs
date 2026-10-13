#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TermLink.TerminalCore;

namespace TermLink.NativeDisplay;

internal sealed record NativeDisplayAction(ulong SourceSequence, uint SourceOffsetEnd, uint Ordinal, byte[] Payload);

internal sealed record NativeDisplaySignals(Guid SessionId, NativeTerminalHostSignals Progress,
    IReadOnlyList<NativeDisplayAction> Actions)
{
    internal byte[] Encode()
    {
        if (SessionId == Guid.Empty || Actions.Count > 64) throw new InvalidDataException("Invalid display signals.");
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(SessionId.ToByteArray()); writer.Write(Progress.ProgressState); writer.Write(Progress.ProgressPercent);
        writer.Write((ushort)Actions.Count);
        foreach (var effect in Actions) {
            if (!NativeTerminalPlatformAction.IsValid(effect.Payload))
                throw new InvalidDataException("Invalid display action.");
            writer.Write(effect.SourceSequence); writer.Write(effect.SourceOffsetEnd); writer.Write(effect.Ordinal);
            writer.Write((uint)effect.Payload.Length); writer.Write(effect.Payload);
        }
        return output.ToArray();
    }

    internal static NativeDisplaySignals Decode(byte[] bytes)
    {
        if (bytes.Length < 20 || bytes.Length > 20 + 64 * (20 + 16392)) throw new InvalidDataException("Invalid display signals size.");
        var session = new Guid(bytes.AsSpan(0, 16));
        var progressBytes = new byte[] { 84,76,72,83,49,0,0,0,bytes[16],bytes[17],0,0 };
        var progress = NativeTerminalHostSignals.Decode(progressBytes);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18));
        if (session == Guid.Empty || count > 64) throw new InvalidDataException("Invalid display signals count.");
        var actions = new List<NativeDisplayAction>(count);
        var offset = 20; ActionCoordinate? previous = null;
        for (var i = 0; i < count; i++) {
            if (bytes.Length - offset < 20) throw new InvalidDataException("Truncated display action.");
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
            var sourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8));
            var ordinal = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12));
            var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 16)); offset += 20;
            if (rawLength > 16392) throw new InvalidDataException("Oversized display action.");
            var length = (int)rawLength;
            if (sourceOffset == 0 || sourceOffset > 1048576 || bytes.Length - offset < length ||
                !NativeTerminalPlatformAction.IsValid(bytes.AsSpan(offset, length))) throw new InvalidDataException("Invalid display action.");
            var coordinate = new ActionCoordinate(sequence, sourceOffset, ordinal);
            if (previous.HasValue && coordinate.CompareTo(previous.Value) <= 0) throw new InvalidDataException("Display action order regressed.");
            previous = coordinate;
            actions.Add(new NativeDisplayAction(sequence, sourceOffset, ordinal, bytes.AsSpan(offset, length).ToArray()));
            offset += length;
        }
        if (offset != bytes.Length) throw new InvalidDataException("Trailing display signal data.");
        return new(session, progress, actions.AsReadOnly());
    }
}

internal readonly record struct ActionCoordinate(ulong Sequence, uint Offset, uint Ordinal) : IComparable<ActionCoordinate>
{
    public int CompareTo(ActionCoordinate other) {
        var compared = Sequence.CompareTo(other.Sequence); if (compared != 0) return compared;
        compared = Offset.CompareTo(other.Offset); return compared != 0 ? compared : Ordinal.CompareTo(other.Ordinal);
    }
    internal static ActionCoordinate Of(NativeDisplayAction effect) => new(effect.SourceSequence, effect.SourceOffsetEnd, effect.Ordinal);
}

/** A failed disk write suppresses the action; never forget old receipts to make room for new ones. */
internal sealed class TerminalActionReceipts
{
    private readonly string root;
    private bool failed;
    internal TerminalActionReceipts(string root) { this.root = root; }
    internal IReadOnlyList<NativeDisplayAction> Claim(NativeDisplaySignals signals)
    {
        if (failed || signals.Actions.Count == 0) return Array.Empty<NativeDisplayAction>();
        string? temporary = null;
        try {
            using var mutex = new Mutex(false, "Local\\TermLink.TerminalAction." + signals.SessionId.ToString("N"));
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return Array.Empty<NativeDisplayAction>();
            try {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, signals.SessionId.ToString("N") + ".receipt");
                ActionCoordinate? previous = null;
                if (File.Exists(path)) {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length != 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x31414c54)
                        throw new InvalidDataException("Invalid action receipt.");
                    previous = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
                    if (previous.Value.Offset == 0 || previous.Value.Offset > 1048576) throw new InvalidDataException("Invalid action receipt position.");
                } else if (Directory.EnumerateFiles(root, "*.receipt").Take(4096).Count() >= 4096) return Array.Empty<NativeDisplayAction>();
                var actions = signals.Actions.Where(a => !previous.HasValue || ActionCoordinate.Of(a).CompareTo(previous.Value) > 0).ToArray();
                if (actions.Length == 0) return actions;
                for (var i = 1; i < actions.Length; i++) if (ActionCoordinate.Of(actions[i]).CompareTo(ActionCoordinate.Of(actions[i-1])) <= 0)
                    throw new InvalidDataException("Action order regressed.");
                var last = ActionCoordinate.Of(actions[^1]);
                var record = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(record, 0x31414c54);
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), last.Sequence);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), last.Offset);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), last.Ordinal);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    file.Write(record); file.Flush(true);
                }
                File.Move(temporary, path, true); temporary = null;
                return actions;
            } finally { mutex.ReleaseMutex(); }
        } catch { failed = true; return Array.Empty<NativeDisplayAction>(); }
        finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
    }
}
