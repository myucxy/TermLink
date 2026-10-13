using System;
using System.IO;
using System.Security.Cryptography;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

namespace TermLink.Agent;

internal sealed class ProductionBrokerCaptureReplayException : IOException
{
    internal ProductionBrokerCaptureReplayException(string code, Exception inner = null)
        : base(code, inner) => Code = code;

    internal string Code { get; }
}

/// <summary>Reconstructs a durable broker Output without opening a terminal-provided path.</summary>
internal static class ProductionBrokerCaptureReplay
{
    internal static NativeTerminalHostEffectFrameV2 Apply(
        ProductionTerminalHistoryStore history, Guid sessionId, LocalFrame frame,
        NativeTerminal terminal, out int outputLength)
    {
        if (history == null) throw new ArgumentNullException(nameof(history));
        if (frame == null || frame.Type != LocalFrameType.BrokerOutput ||
            frame.SessionId != sessionId || frame.Sequence == 0)
            throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID");
        if (terminal == null || !terminal.HasGraphicsProfile)
            throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_UNSUPPORTED");

        byte[] output = null;
        byte[] sha256 = null;
        byte[] captured = null;
        outputLength = 0;
        try
        {
            using var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
            if (reference.SourceSequence != frame.Sequence)
                throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID");
            output = reference.CopyOutput();
            outputLength = output.Length;
            if (reference.IsInline)
            {
                captured = reference.CopyInlineCaptures();
            }
            else
            {
                sha256 = reference.CopySha256();
                try
                {
                    using var sidecars = new KittyCaptureStore(history.BrokerCaptureRootPath, sessionId);
                    captured = sidecars.ReadExact(sessionId, frame.Sequence, reference.ObjectId,
                        reference.CaptureBytes, sha256);
                }
                catch (InvalidDataException error)
                {
                    throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID", error);
                }
                catch (IOException error)
                {
                    throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_MISSING", error);
                }
            }
            using var group = NativeTerminalBrokerCaptureCodecV1.Decode(captured);
            if (group.SourceSequence != frame.Sequence)
                throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID");
            try
            {
                // History is a mirror. Validation here runs before native commit; no PTY or OS
                // effect is emitted by the callback, including on repeated WAL preparation.
                return terminal.ApplyOutputWithBrokerReplayV1(frame.Sequence, output, group.Records,
                    preview => ValidateEffects(preview, frame.Sequence, output.Length));
            }
            catch (InvalidOperationException error)
            {
                throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID", error);
            }
        }
        catch (ProductionBrokerCaptureReplayException) { throw; }
        catch (InvalidDataException error)
        {
            throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID", error);
        }
        catch (ArgumentException error)
        {
            throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID", error);
        }
        finally
        {
            if (output != null) CryptographicOperations.ZeroMemory(output);
            if (sha256 != null) CryptographicOperations.ZeroMemory(sha256);
            if (captured != null) CryptographicOperations.ZeroMemory(captured);
        }
    }

    private static void ValidateEffects(NativeTerminalHostEffectFrameV2 effects,
        ulong sourceSequence, int outputLength)
    {
        if (effects == null)
            throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID");
        foreach (var effect in effects.Records)
        {
            if ((effect.Kind != NativeTerminalHostEffectKindV2.PtyReply &&
                 effect.Kind != NativeTerminalHostEffectKindV2.ResizeRequest &&
                 effect.Kind != NativeTerminalHostEffectKindV2.PlatformAction) ||
                effect.SourceSequence != sourceSequence || effect.SourceOffsetEnd < 1 ||
                effect.SourceOffsetEnd > outputLength || effect.Payload == null ||
                effect.Payload.Count == 0)
                throw new ProductionBrokerCaptureReplayException("BROKER_CAPTURE_INVALID");
        }
    }
}
