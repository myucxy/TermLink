using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly Action<byte[]> outerReplyExpectation;
        private NativeTerminal terminal;
        private WindowsConsoleInputEncoder consoleInputEncoder;
        private ulong lastAppliedSequence;
        private bool failedClosed;
        private bool disposed;

        public TerminalCoreAuthority(
            int columns,
            int rows,
            Action<byte[]> ptyReplyWriter,
            Action<byte[]> outerReplyExpectation)
        {
            if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
            this.ptyReplyWriter = ptyReplyWriter ?? throw new ArgumentNullException(nameof(ptyReplyWriter));
            this.outerReplyExpectation = outerReplyExpectation ??
                throw new ArgumentNullException(nameof(outerReplyExpectation));
            terminal = NativeTerminalCoreV2.Create(
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
                            effects = terminal.ApplyOutputWithHostEffectsV2(
                                frame.Sequence,
                                frame.Payload);
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
                using (checkpoint) return checkpoint.ExportSnapshot();
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

        private void WritePtyReplies(
            ulong sourceSequence,
            NativeTerminalHostEffectFrameV2 effects)
        {
            foreach (var effect in effects.Records)
            {
                if (effect.Kind != NativeTerminalHostEffectKindV2.PtyReply ||
                    effect.SourceSequence != sourceSequence)
                {
                    throw new InvalidDataException(
                        "terminal-core returned an invalid authority host effect.");
                }
                var payload = effect.Payload.ToArray();
                if (payload.Length == 0)
                    throw new InvalidDataException("terminal-core returned an empty PTY reply.");
                // Register before writing the authoritative reply.  When the same query is
                // projected to the outer Windows Terminal, its generated compatibility response
                // is suppressed instead of being delivered to ConPTY a second time.
                outerReplyExpectation(payload);
                ptyReplyWriter(payload);
                Array.Clear(payload, 0, payload.Length);
            }
        }

        private static void ValidateNonTerminalFrame(LocalFrame frame)
        {
            switch (frame.Type)
            {
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
                terminal?.Dispose();
                terminal = null;
                consoleInputEncoder = null;
            }
        }
    }
}
