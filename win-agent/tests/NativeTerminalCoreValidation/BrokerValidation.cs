using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation
{
    internal static class BrokerValidation
    {
        private const string FirstFile = "\x1b_Ga=T,t=f,f=32,s=1,v=1,S=4,i=31;QzovYS5yZ2Jh\x1b\\";
        private const string SecondFile = "\x1b_Ga=T,t=f,f=32,s=1,v=1,S=4,i=32;QzovYi5yZ2Jh\x1b\\";
        private const string SharedMemory = "\x1b_Ga=T,t=s,f=32,s=1,v=1,S=4,O=2,i=17;L25hbWU=\x1b\\";

        internal static void Run()
        {
            Require(Marshal.SizeOf<TlTerminalBrokerRequestV1>() == 96 &&
                NativeTerminalInterop.TerminalBrokerRequestV1StructSize() == 96,
                "Broker request ABI size changed.");
            Require(Marshal.OffsetOf<TlTerminalBrokerRequestV1>("ControlDigest").ToInt32() == 32 &&
                Marshal.OffsetOf<TlTerminalBrokerRequestV1>("ReadOffset").ToInt32() == 64 &&
                Marshal.OffsetOf<TlTerminalBrokerRequestV1>("Reserved1").ToInt32() == 88,
                "Broker request ABI offsets changed.");
            ValidateSuccessfulCapture();
            ValidateSupplyFailureRollsBack();
            ValidatePublisherFailureRollsBack();
            ValidateMultipleCaptures();
            ValidateSharedMemoryFailure();
            ValidateSharedMemoryBytesAndReplay();
            ValidateBeginFailureKeepsOwner();
            ValidateCaptureCodecVector();
            ValidateDurableReplayAndAtomicRejections();
        }

        private static NativeTerminal Create()
        {
            var terminal = NativeTerminalCoreV2.CreateGraphicsV1(
                40, 12, 1, 0, NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
            terminal.ApplyBatch(NativeTerminalBatchEvent.HostGeometry(1,
                new NativeTerminalHostGeometryV1(9, 18, 360, 216, 360, 216)));
            return terminal;
        }

        private static byte[] Snapshot(NativeTerminal terminal)
        {
            using var checkpoint = terminal.CaptureCheckpoint();
            return checkpoint.ExportGraphicsSnapshot();
        }

        private static void ValidateSuccessfulCapture()
        {
            using var terminal = Create();
            var published = 0;
            var resolved = 0;
            NativeTerminalHostEffectFrameV2 frame;
            try { frame = terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile),
                request =>
                {
                    resolved++;
                    try
                    {
                        _ = terminal.GetPresentationInfo();
                        throw new InvalidOperationException("Reentrant terminal access was accepted.");
                    }
                    catch (InvalidOperationException error)
                    {
                        Require(error.Message.Contains("broker Output transaction"),
                            "Broker did not block reentrant terminal access.");
                    }
                    Require(request.Medium == (byte)'f' && request.Reference == "C:/a.rgba" &&
                        request.SourceSequence == 2 && request.Ordinal == 0 &&
                        request.ControlDigest.Count == 32,
                        "Broker request identity or private reference changed.");
                    Require(request.ReadOffset == 0 && request.ReadLength == 4,
                        "Broker read range changed.");
                    return NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3, 4 });
                },
                preview => { published++; Require(preview.RawBytes.Count >= 64, "Empty broker preview."); }); }
            catch (NativeTerminalException error)
            {
                throw new InvalidOperationException("Broker success fixture failed: " + error.Diagnostic, error);
            }
            Require(resolved == 1 && published == 1 && frame.RawBytes.Count >= 64 &&
                terminal.GetPresentationInfo().NextSourceSequence == 3,
                "Broker capture was not committed once.");
        }

        private static void ValidateSupplyFailureRollsBack()
        {
            using var terminal = Create();
            var baseline = Snapshot(terminal);
            var published = false;
            try
            {
                terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile),
                    _ => NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3 }),
                    _ => published = true);
                throw new InvalidOperationException("Invalid capture length was accepted.");
            }
            catch (NativeTerminalException error)
            {
                Require(error.Status == NativeTerminalStatus.Unsupported &&
                    error.Diagnostic?.Contains("capture length does not match S") == true &&
                    !error.Diagnostic.Contains("C:/a.rgba"),
                    "Broker supply did not return a redacted length diagnostic.");
            }
            Require(!published && Snapshot(terminal).SequenceEqual(baseline) &&
                terminal.GetPresentationInfo().NextSourceSequence == 2,
                "Failed supply changed terminal state.");
            terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile),
                _ => NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3, 4 }), _ => { });
        }

        private static void ValidatePublisherFailureRollsBack()
        {
            using var terminal = Create();
            var baseline = Snapshot(terminal);
            try
            {
                terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile),
                    _ => NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3, 4 }),
                    _ => throw new InvalidOperationException("publisher failed"));
                throw new InvalidOperationException("Publisher failure was ignored.");
            }
            catch (InvalidOperationException error)
            {
                Require(error.Message == "publisher failed", "Publisher error was replaced.");
            }
            Require(Snapshot(terminal).SequenceEqual(baseline) &&
                terminal.GetPresentationInfo().NextSourceSequence == 2,
                "Publisher failure changed terminal state.");
            terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile),
                _ => NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3, 4 }), _ => { });
        }

        private static void ValidateMultipleCaptures()
        {
            using var terminal = Create();
            var requests = 0;
            terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile + SecondFile),
                request =>
                {
                    requests++;
                    Require(request.Reference == (requests == 1 ? "C:/a.rgba" : "C:/b.rgba") &&
                        request.Ordinal == requests - 1,
                        "Broker requests were supplied out of parser order.");
                    return NativeTerminalBrokerCapture.FromBytes(requests == 1
                        ? new byte[] { 1, 2, 3, 4 } : new byte[] { 5, 6, 7, 8 });
                }, _ => { });
            Require(requests == 2 && terminal.GetPresentationInfo().NextSourceSequence == 3,
                "Multiple captures did not commit as one Output.");
        }

        private static void ValidateSharedMemoryFailure()
        {
            using var terminal = Create();
            var requests = 0;
            terminal.ApplyOutputWithBrokerV1(2, Encoding.ASCII.GetBytes(SharedMemory),
                request =>
                {
                    requests++;
                    Require(request.Medium == (byte)'s', "Shared-memory medium was lost.");
                    return NativeTerminalBrokerCapture.FromFailure(
                        NativeTerminalBrokerFailure.UnsupportedMedium);
                }, _ => { });
            Require(requests == 1 && terminal.GetPresentationInfo().NextSourceSequence == 3,
                "Unsupported shared-memory result did not resolve the Output.");
        }

        private static void ValidateSharedMemoryBytesAndReplay()
        {
            using var source = Create();
            byte[] encoded = null;
            source.ApplyOutputWithCapturedBrokerV1(2, Encoding.ASCII.GetBytes(SharedMemory),
                request =>
                {
                    Require(request.Medium == (byte)'s' && request.ReadOffset == 2 &&
                        request.ReadLength == 4, "Shared-memory request range changed.");
                    return NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3, 4 });
                }, (_, records) => encoded = NativeTerminalBrokerCaptureCodecV1.Encode(2, records));
            Require(encoded != null, "Shared-memory capture was not persisted.");
            using var group = NativeTerminalBrokerCaptureCodecV1.Decode(encoded);
            using var replay = Create();
            replay.ApplyOutputWithBrokerReplayV1(2, Encoding.ASCII.GetBytes(SharedMemory),
                group.Records, _ => { });
            Require(Snapshot(source).SequenceEqual(Snapshot(replay)),
                "Shared-memory captured bytes changed during replay.");
        }

        private static void ValidateBeginFailureKeepsOwner()
        {
            using var terminal = Create();
            var baseline = Snapshot(terminal);
            try
            {
                terminal.ApplyOutputWithBrokerV1(2,
                    Encoding.ASCII.GetBytes("\x1b_Ga=x,i=20;\x1b\\"),
                    _ => throw new InvalidOperationException("Unexpected request."), _ => { });
                throw new InvalidOperationException("Unsupported graphics control was accepted.");
            }
            catch (NativeTerminalException error)
            {
                Require(error.Status == NativeTerminalStatus.Unsupported &&
                    error.Diagnostic?.Contains("0x") == true,
                    "Broker begin lost the unsupported byte diagnostic.");
            }
            Require(Snapshot(terminal).SequenceEqual(baseline) &&
                terminal.GetPresentationInfo().NextSourceSequence == 2,
                "Failed begin changed owner or terminal state.");
        }

        private static void ValidateCaptureCodecVector()
        {
            var vectorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../../protocol/test-vectors/broker-captures-v1.json"));
            using var vector = JsonDocument.Parse(File.ReadAllText(vectorPath));
            var encoded = Convert.FromHexString(vector.RootElement.GetProperty("encoded_hex").GetString());
            Require(encoded.Length == 244, "Shared TLCAPV1 vector length changed.");
            using var decoded = NativeTerminalBrokerCaptureCodecV1.Decode(encoded);
            Require(decoded.SourceSequence == 2 && decoded.Records.Count == 2 &&
                decoded.Records[0].Outcome == 0 &&
                decoded.Records[0].CopyCaptureBytes().SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
                decoded.Records[1].Outcome == 3 && decoded.Records[1].CopyCaptureBytes().Length == 0,
                "Shared TLCAPV1 vector decoded incorrectly.");
            Require(NativeTerminalBrokerCaptureCodecV1.Encode(decoded.SourceSequence,
                decoded.Records).SequenceEqual(encoded), "TLCAPV1 canonical encoding changed.");

            var malformed = (byte[])encoded.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(24, 4), 243);
            RejectCodec(malformed);
            malformed = (byte[])encoded.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(32 + 28, 4), 1);
            RejectCodec(malformed);
            malformed = (byte[])encoded.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(32 + 24, 4), 0);
            RejectCodec(malformed);
            malformed = (byte[])encoded.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(32 + 100, 4), uint.MaxValue);
            RejectCodec(malformed);
            malformed = (byte[])encoded.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(140 + 96, 4), 99);
            RejectCodec(malformed);
        }

        private static void ValidateDurableReplayAndAtomicRejections()
        {
            byte[] encoded = null;
            using var source = Create();
            source.ApplyOutputWithCapturedBrokerV1(2, Encoding.ASCII.GetBytes(FirstFile),
                _ => NativeTerminalBrokerCapture.FromBytes(new byte[] { 1, 2, 3, 4 }),
                (_, records) => encoded = NativeTerminalBrokerCaptureCodecV1.Encode(2, records));
            Require(encoded != null, "Captured broker group was not persisted.");
            using var decoded = NativeTerminalBrokerCaptureCodecV1.Decode(encoded);
            using var replay = Create();
            replay.ApplyOutputWithBrokerReplayV1(2, Encoding.ASCII.GetBytes(FirstFile),
                decoded.Records, _ => { });
            Require(Snapshot(source).SequenceEqual(Snapshot(replay)),
                "Captured broker replay changed canonical terminal state.");

            using var rejected = Create();
            var baseline = Snapshot(rejected);
            RejectReplay(rejected, Array.Empty<NativeTerminalBrokerRecordV1>(), baseline);
            RejectReplay(rejected, decoded.Records, baseline, FirstFile + SecondFile);
            var identity = decoded.Records[0].ExportRedactedIdentityV1();
            var capture = decoded.Records[0].CopyCaptureBytes();
            using var duplicate = NativeTerminalBrokerRecordV1.FromStored(identity, 0, capture);
            RejectReplay(rejected, new[] { decoded.Records[0], duplicate }, baseline);
            var extraIdentity = (byte[])identity.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(extraIdentity.AsSpan(24, 4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(extraIdentity.AsSpan(28, 4), 1);
            using var extra = NativeTerminalBrokerRecordV1.FromStored(extraIdentity, 3, Array.Empty<byte>());
            RejectReplay(rejected, new[] { decoded.Records[0], extra }, baseline);
            identity[32] ^= 1;
            using var wrongDigest = NativeTerminalBrokerRecordV1.FromStored(identity, 0, capture);
            RejectReplay(rejected, new[] { wrongDigest }, baseline);
            var originalIdentity = decoded.Records[0].ExportRedactedIdentityV1();
            foreach (var (offset, value) in new[]
            {
                (16, (byte)3), // source sequence
                (24, (byte)2), // source offset
                (28, (byte)1), // ordinal
                (12, (byte)'t'), // medium
                (72, (byte)3) // read length
            })
            {
                var changed = (byte[])originalIdentity.Clone();
                changed[offset] = value;
                using var wrong = NativeTerminalBrokerRecordV1.FromStored(changed, 0, capture);
                RejectReplay(rejected, new[] { wrong }, baseline);
            }
            Require(rejected.GetPresentationInfo().NextSourceSequence == 2,
                "Rejected replay advanced the source sequence.");
        }

        private static void RejectReplay(NativeTerminal terminal,
            IReadOnlyList<NativeTerminalBrokerRecordV1> captures, byte[] baseline,
            string output = FirstFile)
        {
            var published = false;
            try
            {
                terminal.ApplyOutputWithBrokerReplayV1(2, Encoding.ASCII.GetBytes(output),
                    captures, _ => published = true);
                throw new InvalidOperationException("Invalid replay was accepted.");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Broker replay ")) { }
            Require(!published && Snapshot(terminal).SequenceEqual(baseline),
                "Rejected replay changed terminal state.");
        }

        private static void RejectCodec(byte[] encoded)
        {
            try
            {
                using var decoded = NativeTerminalBrokerCaptureCodecV1.Decode(encoded);
            }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Malformed TLCAPV1 capture vector was accepted.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
