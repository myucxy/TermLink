using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;

namespace TermLink.Agent
{
    internal static class TerminalMainChannelLimits
    {
        // A terminal-main callback owns one durable sequence and therefore
        // cannot be split after it reaches this bridge. Keep worst-case UTF-8
        // output comfortably below the Relay's exclusive 1 MiB Envelope cap;
        // terminal-main must chunk larger callbacks before assigning sequence.
        public const int MaximumOutputCodeUnits = 256 * 1024;
        public const int MaximumOutputUtf8Bytes = MaximumOutputCodeUnits * 3;
    }

    // This is the boundary owned by terminal-main.  It deliberately contains
    // no terminal UI or backend types so that the Agent can
    // be built and tested without linking against the terminal-main binary.
    internal enum TerminalMainChannelEventType : byte
    {
        Output = 1,
        Resize = 2,
        Lifecycle = 3,
        LocalInputObserved = 4,
        Gap = 5,
        Title = 6
    }

    internal sealed class TerminalMainChannelEvent
    {
        private readonly char[] outputCodeUnits;
        private TerminalMainChannelEvent(
            Guid sessionId,
            ulong sequence,
            TerminalMainChannelEventType type,
            char[] output,
            short columns,
            short rows,
            LocalLifecycleState lifecycle,
            bool hasExitCode,
            int exitCode,
            ulong localInputEpoch,
            ulong gapStart,
            ulong gapEnd,
            string title)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("session ID is required", "sessionId");
            if (sequence == 0) throw new ArgumentOutOfRangeException("sequence");
            SessionId = sessionId;
            Sequence = sequence;
            Type = type;
            outputCodeUnits = output ?? new char[0];
            Columns = columns;
            Rows = rows;
            LifecycleState = lifecycle;
            HasExitCode = hasExitCode;
            ExitCode = exitCode;
            LocalInputEpoch = localInputEpoch;
            GapStart = gapStart;
            GapEnd = gapEnd;
            TitleText = title ?? string.Empty;
        }

        public Guid SessionId { get; private set; }
        public ulong Sequence { get; private set; }
        public TerminalMainChannelEventType Type { get; private set; }
        // The array is owned by the event and exposed read-only.  A producer
        // must create a fresh event for every callback.
        public ReadOnlyMemory<char> OutputCodeUnits { get { return outputCodeUnits; } }
        public short Columns { get; private set; }
        public short Rows { get; private set; }
        public LocalLifecycleState LifecycleState { get; private set; }
        public bool HasExitCode { get; private set; }
        public int ExitCode { get; private set; }
        public ulong LocalInputEpoch { get; private set; }
        public ulong GapStart { get; private set; }
        public ulong GapEnd { get; private set; }
        public string TitleText { get; private set; }

        public static TerminalMainChannelEvent Output(Guid sessionId, ulong sequence, char[] codeUnits)
        {
            if (codeUnits == null) throw new ArgumentNullException("codeUnits");
            if (codeUnits.Length > TerminalMainChannelLimits.MaximumOutputCodeUnits)
                throw new ArgumentOutOfRangeException("codeUnits", "output chunk exceeds the local frame limit");
            var copy = new char[codeUnits.Length];
            Array.Copy(codeUnits, copy, codeUnits.Length);
            return new TerminalMainChannelEvent(
                sessionId, sequence, TerminalMainChannelEventType.Output, copy,
                0, 0, LocalLifecycleState.Running, false, 0, 0, 0, 0, null);
        }

        public static TerminalMainChannelEvent Resize(Guid sessionId, ulong sequence, short columns, short rows)
        {
            // Validate through the existing local IPC contract before an event
            // can enter the Agent pipeline.
            LocalPayloadCodec.Resize(columns, rows);
            return new TerminalMainChannelEvent(
                sessionId, sequence, TerminalMainChannelEventType.Resize, null,
                columns, rows, LocalLifecycleState.Running, false, 0, 0, 0, 0, null);
        }

        public static TerminalMainChannelEvent Lifecycle(
            Guid sessionId,
            ulong sequence,
            LocalLifecycleState state,
            int? exitCode)
        {
            if (!Enum.IsDefined(typeof(LocalLifecycleState), state))
                throw new ArgumentOutOfRangeException("state");
            return new TerminalMainChannelEvent(
                sessionId, sequence, TerminalMainChannelEventType.Lifecycle, null,
                0, 0, state, exitCode.HasValue, exitCode.GetValueOrDefault(), 0, 0, 0, null);
        }

        public static TerminalMainChannelEvent LocalInputObserved(Guid sessionId, ulong sequence, ulong epoch)
        {
            if (epoch == 0) throw new ArgumentOutOfRangeException("epoch");
            return new TerminalMainChannelEvent(
                sessionId, sequence, TerminalMainChannelEventType.LocalInputObserved, null,
                0, 0, LocalLifecycleState.Running, false, 0, epoch, 0, 0, null);
        }

        // The sequence of a gap marker is the first sequence after the missing
        // range.  This lets LocalFrame retain the same monotonic sequence space
        // used by the existing Agent frame pipeline.
        public static TerminalMainChannelEvent Gap(
            Guid sessionId,
            ulong sequence,
            ulong missingStart,
            ulong missingEnd)
        {
            if (missingStart == 0 || missingEnd < missingStart || sequence <= missingEnd)
                throw new ArgumentOutOfRangeException("missingStart");
            return new TerminalMainChannelEvent(
                sessionId, sequence, TerminalMainChannelEventType.Gap, null,
                0, 0, LocalLifecycleState.Running, false, 0, 0, missingStart, missingEnd, null);
        }

        public static TerminalMainChannelEvent Title(Guid sessionId, ulong sequence, string title)
        {
            var normalized = LocalPayloadCodec.ParseTitle(LocalPayloadCodec.Title(title));
            return new TerminalMainChannelEvent(
                sessionId, sequence, TerminalMainChannelEventType.Title, null,
                0, 0, LocalLifecycleState.Running, false, 0, 0, 0, 0, normalized);
        }
    }

    internal interface ITerminalMainAsyncChannel
    {
        Guid SessionId { get; }
        // null means the channel drained and closed.  Events are replayable
        // until AcknowledgeAsync returns successfully.
        ValueTask<TerminalMainChannelEvent> ReadAsync(CancellationToken cancellationToken);
        ValueTask AcknowledgeAsync(ulong sequence, CancellationToken cancellationToken);
    }

    internal interface ITerminalMainFrameCommitSink
    {
        // CommitAsync must durably append every frame before it returns.  A
        // batch is atomic and idempotent by (session, sequence): it may
        // contain frames whose UTF-16 output was split across callbacks, and
        // it may be replayed when commit succeeded but source ACK failed.
        ValueTask CommitAsync(IReadOnlyList<LocalFrame> frames, CancellationToken cancellationToken);
    }

    internal sealed class TerminalMainBridgeStatistics
    {
        public long CommittedEvents { get; internal set; }
        public long AcknowledgedEvents { get; internal set; }
        public long DuplicateEvents { get; internal set; }
        public long GapEvents { get; internal set; }
        public long DroppedSourceEvents { get; internal set; }
        public long MalformedUtf16CodeUnits { get; internal set; }
        public long RejectedEvents { get; internal set; }
    }

    // Converts terminal-main UTF-16 callback chunks into the UTF-8 byte
    // payload expected by LocalFrameType.Output, then commits and ACKs in that
    // order.  It is intentionally an adapter contract: construction of a
    // terminal-main channel and registration with SessionRegistry remain
    // product/runtime integration work outside this file.
    internal sealed class TerminalMainChannelBridge
    {
        private const int MaximumOutputCodeUnits = TerminalMainChannelLimits.MaximumOutputCodeUnits;
        private const int MaximumPendingOutputEvents = 256;
        private const int MaximumPendingOutputBytes = 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        private readonly ITerminalMainAsyncChannel channel;
        private readonly ITerminalMainFrameCommitSink sink;
        private readonly Encoder encoder = Utf8.GetEncoder();
        private readonly List<PendingOutput> pendingOutput = new List<PendingOutput>();
        private readonly TerminalMainBridgeStatistics statistics = new TerminalMainBridgeStatistics();
        private ulong lastAcknowledgedSequence;
        private ulong nextExpectedSequence;
        private bool pendingHighSurrogate;
        private int pendingOutputBytes;
        private int runStarted;
        private bool terminalSeen;

        private sealed class PendingOutput
        {
            public ulong Sequence;
            public byte[] Bytes;
        }

        public TerminalMainChannelBridge(
            ITerminalMainAsyncChannel channel,
            ITerminalMainFrameCommitSink sink,
            ulong resumeFromSequence = 0)
        {
            if (channel == null) throw new ArgumentNullException("channel");
            if (sink == null) throw new ArgumentNullException("sink");
            if (channel.SessionId == Guid.Empty) throw new ArgumentException("channel session ID is required", "channel");
            lastAcknowledgedSequence = resumeFromSequence;
            nextExpectedSequence = resumeFromSequence == ulong.MaxValue ? ulong.MaxValue : resumeFromSequence + 1;
            this.channel = channel;
            this.sink = sink;
        }

        public TerminalMainBridgeStatistics Statistics { get { return statistics; } }
        public ulong LastAcknowledgedSequence { get { return lastAcknowledgedSequence; } }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref runStarted, 1) != 0)
                throw new InvalidOperationException("a terminal-main bridge can only have one active reader");
            if (lastAcknowledgedSequence == ulong.MaxValue)
                return;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var channelEvent = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (channelEvent == null)
                {
                    await FlushPendingOutputAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (channelEvent.SessionId != channel.SessionId)
                {
                    statistics.RejectedEvents++;
                    throw new InvalidDataException("terminal-main event belongs to another session.");
                }
                if (channelEvent.Sequence <= lastAcknowledgedSequence)
                {
                    statistics.DuplicateEvents++;
                    // A reconnecting source may replay an event that was
                    // durably acknowledged before the transport dropped its
                    // ACK.  Re-send the cumulative cursor so the source can
                    // retire that replay instead of waiting indefinitely.
                    await channel.AcknowledgeAsync(lastAcknowledgedSequence, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var isGap = channelEvent.Type == TerminalMainChannelEventType.Gap;
                if ((!isGap && channelEvent.Sequence != nextExpectedSequence) ||
                    (isGap && (channelEvent.GapStart != nextExpectedSequence ||
                        channelEvent.Sequence != checked(channelEvent.GapEnd + 1))))
                {
                    statistics.RejectedEvents++;
                    throw new InvalidDataException(
                        "terminal-main event sequence is not contiguous or gap-aligned: expected " +
                        nextExpectedSequence + ", got " + channelEvent.Sequence + ".");
                }

                if (channelEvent.Type == TerminalMainChannelEventType.Output)
                {
                    await ProcessOutputAsync(channelEvent, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // A non-output event is an ordering boundary.  If the
                    // preceding output ended in a high surrogate, flush it as
                    // U+FFFD and count the malformed code unit explicitly.
                    await FlushPendingOutputAsync(cancellationToken).ConfigureAwait(false);
                    var frame = MapFrame(channelEvent);
                    await CommitAndAcknowledgeAsync(
                        new[] { frame }, channelEvent.Sequence, cancellationToken).ConfigureAwait(false);
                    if (channelEvent.Type == TerminalMainChannelEventType.Lifecycle &&
                        (channelEvent.LifecycleState == LocalLifecycleState.Ended ||
                            channelEvent.LifecycleState == LocalLifecycleState.Interrupted))
                    {
                        terminalSeen = true;
                    }
                }
                if (terminalSeen || channelEvent.Sequence == ulong.MaxValue)
                    return;
                nextExpectedSequence = channelEvent.Sequence + 1;
            }
        }

        private async Task ProcessOutputAsync(
            TerminalMainChannelEvent channelEvent,
            CancellationToken cancellationToken)
        {
            var codeUnits = channelEvent.OutputCodeUnits.ToArray();
            if (codeUnits.Length > MaximumOutputCodeUnits)
            {
                statistics.RejectedEvents++;
                throw new InvalidDataException("terminal-main output chunk exceeds the local frame limit.");
            }
            var wasPending = pendingHighSurrogate;
            var bytes = Encode(codeUnits, false);
            if (bytes.Length > TerminalMainChannelLimits.MaximumOutputUtf8Bytes)
            {
                statistics.RejectedEvents++;
                throw new InvalidDataException("terminal-main UTF-8 output chunk exceeds the realtime frame limit.");
            }
            var malformed = statistics.MalformedUtf16CodeUnits;
            pendingHighSurrogate = EndsWithUnpairedHighSurrogate(
                codeUnits, wasPending, ref malformed);
            statistics.MalformedUtf16CodeUnits = malformed;
            pendingOutput.Add(new PendingOutput { Sequence = channelEvent.Sequence, Bytes = bytes });
            pendingOutputBytes = checked(pendingOutputBytes + bytes.Length);
            if (pendingHighSurrogate)
            {
                if (pendingOutput.Count > MaximumPendingOutputEvents || pendingOutputBytes > MaximumPendingOutputBytes)
                {
                    // Do not ACK a prefix whose UTF-8 bytes still depend on a
                    // later UTF-16 callback. Stop explicitly instead of
                    // corrupting replay state or growing without bound.
                    statistics.RejectedEvents++;
                    throw new InvalidDataException("pending UTF-16 surrogate window exceeded the bridge limit");
                }
                return;
            }
            await FlushPendingOutputAsync(cancellationToken).ConfigureAwait(false);
        }

        private byte[] Encode(char[] codeUnits, bool flush)
        {
            var capacity = Math.Max(4, Utf8.GetMaxByteCount(codeUnits == null ? 0 : codeUnits.Length) + 4);
            var bytes = new byte[capacity];
            var count = encoder.GetBytes(codeUnits ?? new char[0], 0, codeUnits == null ? 0 : codeUnits.Length, bytes, 0, flush);
            if (count == bytes.Length) return bytes;
            var result = new byte[count];
            Buffer.BlockCopy(bytes, 0, result, 0, count);
            return result;
        }

        private static bool EndsWithUnpairedHighSurrogate(
            char[] codeUnits,
            bool carriedHighSurrogate,
            ref long malformedCount)
        {
            var pending = carriedHighSurrogate;
            for (var index = 0; index < codeUnits.Length; index++)
            {
                var value = codeUnits[index];
                if (pending)
                {
                    if (char.IsLowSurrogate(value))
                    {
                        pending = false;
                        continue;
                    }
                    malformedCount++;
                    pending = false;
                }
                if (char.IsHighSurrogate(value)) pending = true;
                else if (char.IsLowSurrogate(value)) malformedCount++;
            }
            return pending;
        }

        private async Task FlushPendingOutputAsync(CancellationToken cancellationToken)
        {
            var count = pendingOutput.Count;
            if (count == 0) return;
            if (pendingHighSurrogate)
            {
                var replacement = Encode(new char[0], true);
                pendingHighSurrogate = false;
                statistics.MalformedUtf16CodeUnits++;
                if (replacement.Length > 0)
                {
                    var last = pendingOutput[count - 1];
                    var combined = new byte[last.Bytes.Length + replacement.Length];
                    Buffer.BlockCopy(last.Bytes, 0, combined, 0, last.Bytes.Length);
                    Buffer.BlockCopy(replacement, 0, combined, last.Bytes.Length, replacement.Length);
                    last.Bytes = combined;
                    pendingOutputBytes = checked(pendingOutputBytes + replacement.Length);
                }
            }
            var frames = new List<LocalFrame>(count);
            var bytesToRemove = 0;
            for (var index = 0; index < count; index++)
            {
                var output = pendingOutput[index];
                frames.Add(new LocalFrame(
                    LocalFrameType.Output,
                    channel.SessionId,
                    output.Sequence,
                    output.Bytes));
                bytesToRemove = checked(bytesToRemove + output.Bytes.Length);
            }
            var sequence = frames[frames.Count - 1].Sequence;
            await CommitAndAcknowledgeAsync(frames, sequence, cancellationToken).ConfigureAwait(false);
            pendingOutput.RemoveRange(0, count);
            pendingOutputBytes -= bytesToRemove;
        }

        private LocalFrame MapFrame(TerminalMainChannelEvent channelEvent)
        {
            byte[] payload;
            LocalFrameType type;
            switch (channelEvent.Type)
            {
                case TerminalMainChannelEventType.Resize:
                    type = LocalFrameType.Resize;
                    payload = LocalPayloadCodec.Resize(channelEvent.Columns, channelEvent.Rows);
                    break;
                case TerminalMainChannelEventType.Lifecycle:
                    if (channelEvent.LifecycleState == LocalLifecycleState.Ended && !channelEvent.HasExitCode)
                    {
                        // LocalPayloadCodec's legacy lifecycle payload has no
                        // unknown exit-code representation.  Refuse to
                        // fabricate zero and make the rejection observable.
                        statistics.RejectedEvents++;
                        throw new InvalidDataException("ended lifecycle has no exit code in the legacy frame contract.");
                    }
                    type = LocalFrameType.Lifecycle;
                    payload = LocalPayloadCodec.Lifecycle(channelEvent.LifecycleState, channelEvent.ExitCode);
                    break;
                case TerminalMainChannelEventType.LocalInputObserved:
                    type = LocalFrameType.LocalInputObserved;
                    payload = LocalPayloadCodec.LocalInputEpoch(channelEvent.LocalInputEpoch);
                    break;
                case TerminalMainChannelEventType.Title:
                    type = LocalFrameType.Title;
                    payload = LocalPayloadCodec.Title(channelEvent.TitleText);
                    break;
                case TerminalMainChannelEventType.Gap:
                    type = LocalFrameType.Gap;
                    payload = LocalPayloadCodec.Gap(channelEvent.GapStart, channelEvent.GapEnd);
                    statistics.GapEvents++;
                    var rangeLength = channelEvent.GapEnd - channelEvent.GapStart;
                    if (rangeLength != ulong.MaxValue) rangeLength++;
                    var remaining = long.MaxValue - statistics.DroppedSourceEvents;
                    statistics.DroppedSourceEvents += rangeLength > (ulong)remaining ? remaining : (long)rangeLength;
                    break;
                default:
                    statistics.RejectedEvents++;
                    throw new InvalidDataException("terminal-main event type cannot enter the frame pipeline.");
            }
            return new LocalFrame(type, channel.SessionId, channelEvent.Sequence, payload);
        }

        private async Task CommitAndAcknowledgeAsync(
            IReadOnlyList<LocalFrame> frames,
            ulong sequence,
            CancellationToken cancellationToken)
        {
            await sink.CommitAsync(frames, cancellationToken).ConfigureAwait(false);
            statistics.CommittedEvents += frames.Count;
            await channel.AcknowledgeAsync(sequence, cancellationToken).ConfigureAwait(false);
            lastAcknowledgedSequence = sequence;
            statistics.AcknowledgedEvents++;
        }
    }
}
