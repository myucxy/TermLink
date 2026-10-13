using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

namespace TermLink.SessionHost
{
    /// <summary>
    /// Owns the one production terminal-core instance beside its ConPTY.  A LocalFrame reaches
    /// this class only after SessionAgent has crash-durably journaled it and returned its
    /// cumulative ACK.  Non-terminal LocalFrames consume a zero-time Advance so the native source
    /// cursor remains identical to the common Host/Agent journal cursor.
    /// </summary>
    internal sealed class TerminalCoreAuthority : IDisposable
    {
        internal const ulong HistoryCapacityRows = 20_000;

        private readonly object sync = new object();
        private readonly Action<byte[]> ptyReplyWriter;
        private readonly Action<byte[], ulong, uint, uint> outerReplyExpectation;
        private readonly Action<NativeTerminalHostEffectRecordV2> resizeRequest;
        public TerminalSemanticProfile SemanticProfile { get; }
        private NativeTerminal terminal;
        private WindowsConsoleInputEncoder consoleInputEncoder;
        private ulong lastAppliedSequence;
        private bool failedClosed;
        private bool disposed;
        private NativeTerminalHistoryCapture displayHistory;
        private readonly Queue<NativeTerminalHostEffectRecordV2> displayActions = new();
        private ulong projectedSequence;
        private byte[] projectedOutput;

        public byte[] TakeLegacyProjection(ulong sequence) {
            lock (sync) {
                ThrowIfUnavailable();
                if (projectedOutput == null || projectedSequence != sequence) throw new InvalidDataException("Legacy projection is not synchronized with the authority.");
                var output = projectedOutput; projectedOutput = null; return output;
            }
        }

        public byte[] PollDisplaySignals(Guid sessionId)
        {
            lock (sync) {
                ThrowIfUnavailable();
                var response = new TermLink.NativeDisplay.NativeDisplaySignals(sessionId, terminal.HostSignals(), displayActions.Select(a =>
                    new TermLink.NativeDisplay.NativeDisplayAction(a.SourceSequence, a.SourceOffsetEnd, a.Ordinal, a.Payload.ToArray())).ToArray()).Encode();
                displayActions.Clear();
                return response;
            }
        }

        public TerminalCoreAuthority(
            int columns,
            int rows,
            Action<byte[]> ptyReplyWriter,
            Action<byte[], ulong, uint, uint> outerReplyExpectation,
            Action<NativeTerminalHostEffectRecordV2> resizeRequest = null,
            TerminalSemanticProfile semanticProfile = null)
        {
            if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
            SemanticProfile = semanticProfile ?? TerminalSemanticProfile.Legacy;
            if (SemanticProfile.IsGraphics && outerReplyExpectation != null)
                throw new ArgumentException("The graphics authority requires a native display.", nameof(outerReplyExpectation));
            this.ptyReplyWriter = ptyReplyWriter ?? throw new ArgumentNullException(nameof(ptyReplyWriter));
            // A native display never projects queries to another terminal.
            this.outerReplyExpectation = outerReplyExpectation;
            this.resizeRequest = resizeRequest;
            terminal = SemanticProfile.IsGraphics ? NativeTerminalCoreV2.CreateGraphicsV1(
                checked((uint)columns), checked((uint)rows), initialSourceSequence: 1,
                historyCapacityRows: HistoryCapacityRows, outputProfile: NativeTerminalOutputProfile.Utf8,
                widthProfile: (NativeTerminalWidthProfile)SemanticProfile.WidthProfile) : NativeTerminalCoreV2.Create(
                checked((uint)columns),
                checked((uint)rows),
                initialSourceSequence: 1,
                historyCapacityRows: HistoryCapacityRows,
                outputProfile: NativeTerminalOutputProfile.Utf8);
            consoleInputEncoder = new WindowsConsoleInputEncoder(terminal);
        }

        public ulong LastAppliedSequence
        {
            get { lock (sync) return lastAppliedSequence; }
        }

        public bool FailedClosed
        {
            get { lock (sync) return failedClosed; }
        }

        public void ApplyDurable(LocalFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            lock (sync)
            {
                ThrowIfUnavailable();
                var expected = checked(lastAppliedSequence + 1);
                if (frame.Type == LocalFrameType.Gap)
                {
                    LocalPayloadCodec.ParseGap(frame.Payload, out var gapStart, out var gapEnd);
                    if (gapStart != expected || gapEnd + 1 != frame.Sequence)
                    {
                        FailClosed();
                        throw new InvalidDataException(
                            "terminal-core authority received inconsistent journal gap metadata.");
                    }
                    try
                    {
                        terminal.ApplyGapWithHostEffectsV2(expected);
                    }
                    catch (Exception error)
                    {
                        throw new InvalidDataException(
                            "terminal-core authority received an unrecoverable journal gap " +
                            "before common sequence " + frame.Sequence + ".",
                            error);
                    }
                    finally { FailClosed(); }
                    throw new InvalidDataException(
                        "terminal-core authority accepted a journal gap unexpectedly.");
                }
                if (frame.Sequence != expected)
                {
                    FailClosed();
                    throw new InvalidDataException(
                        "terminal-core authority sequence mismatch: expected " + expected +
                        ", received " + frame.Sequence + ".");
                }

                NativeTerminalHostEffectFrameV2 effects;
                try
                {
                    switch (frame.Type)
                    {
                        case LocalFrameType.Output:
                            if (outerReplyExpectation != null) {
                                var projection = terminal.ApplyOutputWithLegacyProjection(frame.Sequence, frame.Payload);
                                effects = projection.Effects;
                                projectedSequence = frame.Sequence; projectedOutput = projection.Projection;
                                break;
                            }
                            effects = terminal.ApplyOutputWithHostEffectsV2(
                                frame.Sequence,
                                frame.Payload);
                            break;
                        case LocalFrameType.HostTheme:
                            effects = terminal.ApplyBatchWithHostEffectsV2(NativeTerminalBatchEvent.HostTheme(
                                frame.Sequence, NativeTerminalHostThemeV1.Decode(frame.Payload)));
                            break;
                    case LocalFrameType.HostGeometry:
                            effects = terminal.ApplyBatchWithHostEffectsV2(NativeTerminalBatchEvent.HostGeometry(
                                frame.Sequence, NativeTerminalHostGeometryV1.Decode(frame.Payload)));
                            break;
                        case LocalFrameType.Resize:
                            LocalPayloadCodec.ParseResize(
                                frame.Payload,
                                out var columns,
                                out var rows);
                            effects = terminal.ApplyResizeWithHostEffectsV2(
                                frame.Sequence,
                                checked((uint)columns),
                                checked((uint)rows));
                            break;
                        default:
                            ValidateNonTerminalFrame(frame);
                            effects = terminal.ApplyAdvanceWithHostEffectsV2(frame.Sequence, 0);
                            break;
                    }

                    WritePtyReplies(frame.Sequence, effects);
                    lastAppliedSequence = frame.Sequence;
                }
                catch
                {
                    FailClosed();
                    throw;
                }
            }
        }

        internal void ApplyCapturedOutput(ulong sequence, byte[] output,
            Func<NativeTerminalBrokerRequest, NativeTerminalBrokerCapture> resolve,
            Action<NativeTerminalHostEffectFrameV2, IReadOnlyList<NativeTerminalBrokerRecordV1>> publishDurable)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                if (!SemanticProfile.IsGraphics || sequence != checked(lastAppliedSequence + 1))
                    throw new InvalidDataException("Broker Output authority sequence or profile mismatch.");
                try
                {
                    var effects = terminal.ApplyOutputWithCapturedBrokerV1(sequence, output, resolve, publishDurable);
                    WritePtyReplies(sequence, effects);
                    lastAppliedSequence = sequence;
                }
                catch (KittyCaptureQuotaExceededException)
                {
                    // Native aborted before publishing; the caller retries this same Output as OverLimit.
                    throw;
                }
                catch
                {
                    FailClosed();
                    throw;
                }
            }
        }

        public byte[] ExportSnapshot()
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                if (!terminal.TryCaptureCheckpoint(out var checkpoint, out var status))
                {
                    throw new InvalidOperationException(
                        "terminal-core checkpoint is temporarily unavailable: " + status + ".");
                }
                using (checkpoint) return SemanticProfile.IsGraphics ? checkpoint.ExportGraphicsSnapshot() : checkpoint.ExportSnapshot();
            }
        }

        public (byte Status, byte[] Frame) RenderDisplay(ulong revision, uint scrollbackRows)
        {
            NativeTerminalCheckpoint checkpoint;
            NativeTerminalHistoryCapture history = null;
            lock (sync)
            {
                ThrowIfUnavailable();
                if (scrollbackRows != 0)
                {
                    try { history = displayHistory ??= terminal.CaptureHistory(); }
                    catch (NativeTerminalException error) when (error.Status is NativeTerminalStatus.PresentationDeferred or NativeTerminalStatus.SnapshotNotEligible)
                    { return (TermLink.NativeDisplay.NativeDisplayProtocol.NotReady, Array.Empty<byte>()); }
                    catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.LimitExceeded)
                    { return (TermLink.NativeDisplay.NativeDisplayProtocol.HistoryLimit, Array.Empty<byte>()); }
                    checkpoint = null;
                }
                else
                {
                    displayHistory?.Dispose(); displayHistory = null;
                    if (revision == lastAppliedSequence + 1)
                        return (TermLink.NativeDisplay.NativeDisplayProtocol.Unchanged, Array.Empty<byte>());
                    if (!terminal.TryCaptureCheckpoint(out checkpoint, out _))
                        return (TermLink.NativeDisplay.NativeDisplayProtocol.NotReady, Array.Empty<byte>());
                }
            }
            if (history != null)
            {
                try {
                    history.CheckLease();
                    if (revision == history.Revision) return (TermLink.NativeDisplay.NativeDisplayProtocol.Unchanged,Array.Empty<byte>());
                    return SemanticProfile.IsGraphics ?
                        (TermLink.NativeDisplay.NativeDisplayProtocol.FrameV5, history.ViewportV2(scrollbackRows)) :
                        (TermLink.NativeDisplay.NativeDisplayProtocol.Frame, history.Viewport(scrollbackRows));
                }
                catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.HistoryStaleCapture)
                { return (TermLink.NativeDisplay.NativeDisplayProtocol.HistoryExpired, Array.Empty<byte>()); }
            }
            // Encoding an immutable checkpoint never blocks authority input/output under its lock.
            using (checkpoint) return SemanticProfile.IsGraphics ?
                (TermLink.NativeDisplay.NativeDisplayProtocol.FrameV5, checkpoint.ExportRenderFrameV5AtScrollback(scrollbackRows)) :
                (TermLink.NativeDisplay.NativeDisplayProtocol.Frame, checkpoint.ExportRenderFrameV4AtScrollback(scrollbackRows));
        }

        public (byte Status, byte[] Payload) ShellDisplay(byte kind, byte[] payload)
        {
            const byte rejected = TermLink.NativeDisplay.NativeDisplayProtocol.Rejected;
            lock (sync) {
                ThrowIfUnavailable();
                if (kind == TermLink.NativeDisplay.NativeDisplayProtocol.ShellQuery) {
                    TermLink.NativeDisplay.NativeDisplayProtocol.RequireSize(payload, 1);
                    if (payload[0] > 1) throw new InvalidDataException("Invalid shell menu action.");
                    var bytes = terminal.ExportShellStateV1(); var response = new byte[8 + bytes.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response, terminal.GetPresentationInfo().NextSourceSequence);
                    bytes.CopyTo(response, 8);
                    return (TermLink.NativeDisplay.NativeDisplayProtocol.ShellState, response);
                }
                if (!terminal.TryCaptureCheckpoint(out var checkpoint, out _)) return (rejected, Array.Empty<byte>());
                using (checkpoint) {
                    TermLink.NativeDisplay.NativeDisplayProtocol.RequireSize(payload, 8);
                    var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload);
                    var command = checkpoint.ShellState().Commands.FirstOrDefault(c => c.Id == id);
                    if (command == null || command.Prompt.Alternate || command.Prompt.Page != 1) return (rejected, Array.Empty<byte>());
                    if (displayHistory != null && command.Prompt.Sequence >= displayHistory.Revision) {
                        displayHistory.Dispose(); displayHistory = null;
                    }
                    var existing = displayHistory;
                    NativeTerminalHistoryCapture capture;
                    try { capture = existing ?? terminal.CaptureHistory(); }
                    catch (NativeTerminalException error) when (error.Status is NativeTerminalStatus.LimitExceeded or
                        NativeTerminalStatus.PresentationDeferred or NativeTerminalStatus.SnapshotNotEligible) {
                        return (rejected, Array.Empty<byte>());
                    }
                    try {
                        var offset = capture.ResolveAnchor(command.Prompt.LineId, command.Prompt.CellOffset);
                        displayHistory = capture;
                        var response = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(response, offset);
                        return (TermLink.NativeDisplay.NativeDisplayProtocol.ShellAnchor, response);
                    } catch (NativeTerminalException error) when (error.Status is NativeTerminalStatus.HistoryStaleAnchor or NativeTerminalStatus.HistoryStaleCapture) {
                        if (existing == null) capture.Dispose(); return (rejected, Array.Empty<byte>());
                    } catch { if (existing == null) capture.Dispose(); throw; }
                }
            }
        }

        public bool EncodeAndWriteDisplayText(string text, bool paste, Func<byte[], bool> writer)
        {
            if (text == null || text.Length > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(text));
            lock (sync)
            {
                ThrowIfUnavailable();
                if (paste) return writer(terminal.EncodePaste(Encoding.UTF8.GetBytes(text)));
                using var encodedText = new MemoryStream();
                foreach (var scalar in text.EnumerateRunes())
                {
                    foreach (var down in new[] { true, false })
                        foreach (var character in scalar.ToString())
                        {
                            var encoded = consoleInputEncoder.Encode(new NativeTerminalWin32KeyEvent(0, 0, character, down, 0, 1));
                            if (encodedText.Length + encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException("Encoded display input exceeds its budget.");
                            encodedText.Write(encoded);
                        }
                }
                return encodedText.Length == 0 || writer(encodedText.ToArray());
            }
        }

        public bool EncodeAndWriteDisplayFocus(bool focused, Func<byte[], bool> writer)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var bytes = focused ? terminal.EncodeFocusGained() : terminal.EncodeFocusLost();
                return bytes.Length == 0 || writer(bytes);
            }
        }

        public byte[] EncodeConsoleKey(NativeTerminalWin32KeyEvent keyEvent)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                return consoleInputEncoder.Encode(keyEvent);
            }
        }

        public bool EncodeAndWriteDisplayKeyPair(NativeTerminalWin32KeyEvent down, NativeTerminalWin32KeyEvent up, Func<byte[], bool> writer)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                using var batch = new MemoryStream();
                batch.Write(consoleInputEncoder.Encode(down));
                batch.Write(consoleInputEncoder.Encode(up));
                return batch.Length == 0 || writer(batch.ToArray());
            }
        }

        public bool EncodeAndWriteConsoleKey(
            NativeTerminalWin32KeyEvent keyEvent,
            Func<byte[], bool> writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            lock (sync)
            {
                ThrowIfUnavailable();
                var encoded = consoleInputEncoder.Encode(keyEvent);
                return encoded.Length == 0 || writer(encoded);
            }
        }

        public bool EncodeAndWriteConsoleMouse(
            NativeTerminalMouseEvent input, Func<byte[], bool> writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            lock (sync)
            {
                ThrowIfUnavailable();
                byte[] encoded;
                try { encoded = terminal.EncodeMouse(input); }
                // A resize can make a queued cell stale. Win32 cell records also cannot
                // represent mode 1016 pixels; legacy encodings have a coordinate ceiling.
                // Never invent coordinates or kill a session for these local records.
                catch (NativeTerminalException error) when (
                    error.Status == NativeTerminalStatus.InvalidArgument ||
                    error.Status == NativeTerminalStatus.LimitExceeded)
                { return true; }
                return encoded.Length == 0 || writer(encoded);
            }
        }

        private void WritePtyReplies(
            ulong sourceSequence,
            NativeTerminalHostEffectFrameV2 effects)
        {
            foreach (var effect in effects.Records)
            {
                if (effect.SourceSequence != sourceSequence)
                {
                    throw new InvalidDataException(
                        "terminal-core returned an invalid authority host effect.");
                }
                // Platform actions are never PTY input. Their consented live dispatcher is separate.
                if (effect.Kind == NativeTerminalHostEffectKindV2.PlatformAction) {
                    if (displayActions.Count < 64) displayActions.Enqueue(effect);
                    continue;
                }
                if (effect.Kind == NativeTerminalHostEffectKindV2.ResizeRequest)
                {
                    if (resizeRequest == null) throw new InvalidOperationException("No host resize handler is installed.");
                    resizeRequest(effect);
                    continue;
                }
                if (effect.Kind != NativeTerminalHostEffectKindV2.PtyReply) throw new InvalidDataException("Unknown host effect kind.");
                var payload = effect.Payload.ToArray();
                if (payload.Length == 0)
                    throw new InvalidDataException("terminal-core returned an empty PTY reply.");
                // Register before writing the authoritative reply.  When the same query is
                // projected to the outer Windows Terminal, its generated compatibility response
                // is suppressed instead of being delivered to ConPTY a second time.
                outerReplyExpectation?.Invoke(
                    payload,
                    effect.SourceSequence,
                    effect.SourceOffsetEnd,
                    effect.Ordinal);
                ptyReplyWriter(payload);
                Array.Clear(payload, 0, payload.Length);
            }
        }

        private static void ValidateNonTerminalFrame(LocalFrame frame)
        {
            switch (frame.Type)
            {
                case LocalFrameType.HostResizeResult:
                    if (LocalPayloadCodec.ParseHostResizeResult(frame.Payload).SourceSequence >= frame.Sequence)
                        throw new InvalidDataException("Host resize result precedes its source request.");
                    return;
                case LocalFrameType.Title:
                    LocalPayloadCodec.ParseTitle(frame.Payload);
                    return;
                case LocalFrameType.Lifecycle:
                    LocalPayloadCodec.ParseLifecycle(frame.Payload, out _);
                    return;
                case LocalFrameType.LocalInputObserved:
                    LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload);
                    return;
                case LocalFrameType.RemoteInputResult:
                    LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out _);
                    return;
                case LocalFrameType.SessionRemoved:
                    LocalPayloadCodec.ParseSessionRemoved(frame.Payload);
                    return;
                default:
                    throw new InvalidDataException(
                        "Local frame type cannot enter terminal-core authority: " + frame.Type + ".");
            }
        }

        private void FailClosed()
        {
            failedClosed = true;
        }

        private void ThrowIfUnavailable()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TerminalCoreAuthority));
            if (failedClosed || terminal == null)
                throw new InvalidOperationException("terminal-core authority is failed closed.");
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                displayHistory?.Dispose(); displayHistory = null;
                terminal?.Dispose();
                terminal = null;
                consoleInputEncoder = null;
            }
        }
    }
}
