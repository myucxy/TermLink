using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

internal static class Program
{
    private static int assertions;
    private static readonly Guid Pc = Guid.Parse("12345678-abcd-4321-abcd-1234567890ab");
    private static readonly ProductionHistoryRecipient Recipient = new ProductionHistoryRecipient
    {
        DeviceId = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-000000000080"),
        PairwiseKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
        KeyEpoch = 7
    };
    private const string Kitty = "\u001b_Ga=T,f=32,s=2,v=2,i=17,x=1,y=0,w=1,h=2,c=2,r=3,z=-7,C=1;" +
        "/wAA/wD/AP8AAP//////gA==\u001b\\";
    private const string Drcs = "\u001bP1;1;0;10;1;2;16;0{ @~~~~~~~~~~\u001b\\\u001b( @!\u001b[2b" +
        "\u001bP1;1;0;10;1;2;16;0{ @AAAAAAAAAA\u001b\\!\u001b(B";

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-graphics-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Require(NativeTerminalCoreV2.SupportsGraphicsV1(), "Graphics ABI is required for this integration validation.");
            ValidateLegacy(root, false);
            ValidateLegacy(root, true);
            ValidateGraphics(root, false);
            ValidateGraphics(root, true);
            ValidateGraphicsUnicode16(root);
            BrokerReplayValidation.Run(root);
            Console.WriteLine("Production graphics history validation passed: " + assertions + " assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Recipient.PairwiseKey);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ProductionTerminalHistoryProducer Producer(ProductionTerminalHistoryStore store, bool native) =>
        new ProductionTerminalHistoryProducer(store, Pc, terminalCoreShadowEnabled: !native,
            terminalCoreSnapshotAuthorityEnabled: native, terminalCoreShadowObserver: null);

    private static ProductionHostHistoryService Service(ProductionTerminalHistoryStore store,
        ProductionTerminalHistoryProducer producer) => new ProductionHostHistoryService(store, producer, Pc, _ => false);

    private static HostHistoryRequestPayload Latest() =>
        new HostHistoryRequestPayload { Kind = HostHistoryRequestKindV1.LatestSnapshot };

    private static void ValidateLegacy(string root, bool native)
    {
        var directory = Path.Combine(root, "legacy-" + native);
        var session = Guid.NewGuid();
        var store = new ProductionTerminalHistoryStore(directory, Pc);
        using var producer = Producer(store, native);
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 1, Encoding.UTF8.GetBytes("legacy")));
        Require(producer.ProcessSession(session).SnapshotEligible, "Legacy production remains eligible.");
        Require(store.GetSessionProfile(session) == TerminalSemanticProfile.Legacy, "Legacy marker remains exact.");
        var checkpoint = producer.LoadCheckpoint(session);
        Require(checkpoint.AuthorityVersion == (native ? 2 : 1), "Legacy authority version remains unchanged.");
        using var selected = Service(store, producer).Select(session, Latest(), Recipient);
        Require(selected.Status == HostHistoryResponseStatusV1.Ok, "Legacy host-history selection succeeds.");
        var item = selected.Object;
        Require(item.FormatName == (native ? SnapshotCrypto.TerminalCoreSnapshotFormatName : SnapshotCrypto.SnapshotFormatName),
            "Legacy snapshot format remains unchanged.");
        Require(item.KeyId == (native ? SnapshotCrypto.TerminalCoreSnapshotKeyId(Recipient.DeviceId, Recipient.KeyEpoch)
            : SnapshotCrypto.SnapshotKeyId(Recipient.DeviceId, Recipient.KeyEpoch)), "Legacy key ID remains unchanged.");
        if (native)
        {
            var opened = Open(item, session, false);
            try
            {
                Require(opened.SequenceEqual(checkpoint.SnapshotPlaintext), "Legacy native bytes survive encrypted delivery.");
                using var imported = NativeTerminalCoreV2.ImportSnapshot(opened);
                Require(imported.GetPresentationInfo().NextSourceSequence == 2, "Legacy native cursor survives import.");
            }
            finally { CryptographicOperations.ZeroMemory(opened); }
        }
        else
        {
            var opened = SnapshotCrypto.OpenSnapshot(Recipient.PairwiseKey, item.SealedObject, session, Pc,
                Recipient.DeviceId, Recipient.KeyEpoch, item.KeyId, item.SnapshotSequence,
                checked((uint)item.Columns), checked((uint)item.Rows));
            Require(opened.ToByteArray().SequenceEqual(checkpoint.SnapshotPlaintext), "Portable legacy bytes survive delivery.");
        }
    }

    private static void ValidateGraphics(string root, bool nativeFlag)
    {
        var directory = Path.Combine(root, "graphics-" + nativeFlag);
        var session = Guid.NewGuid();
        var store = new ProductionTerminalHistoryStore(directory, Pc, _ => TerminalSemanticProfile.Graphics);
        using var producer = Producer(store, nativeFlag);
        var geometry = new NativeTerminalHostGeometryV1(2, 6, 0, 0, 0, 0);
        var frames = new[]
        {
            new LocalFrame(LocalFrameType.HostGeometry, session, 1, geometry.Encode()),
            new LocalFrame(LocalFrameType.LocalInputObserved, session, 2, LocalPayloadCodec.LocalInputEpoch(7)),
            new LocalFrame(LocalFrameType.Output, session, 3, Encoding.UTF8.GetBytes(Kitty)),
            new LocalFrame(LocalFrameType.Output, session, 4, Encoding.UTF8.GetBytes(Drcs))
        };
        using var expected = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 1, 20_000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.LegacyV1);
        expected.ApplyBatchWithHostEffectsV2(NativeTerminalBatchEvent.HostGeometry(1, geometry));
        expected.ApplyAdvanceWithHostEffectsV2(2, 0);
        expected.ApplyOutputWithHostEffectsV2(3, frames[2].Payload);
        expected.ApplyOutputWithHostEffectsV2(4, frames[3].Payload);
        _ = NativeTerminalRenderFrameV5Decoder.Decode(Frame(expected));
        var expectedBytes = Snapshot(expected);
        foreach (var frame in frames) producer.AppendCommittedFrame(frame);
        var result = producer.ProcessSession(session);
        Require(result.SnapshotEligible && result.LastSequence == 4 && result.TranscriptPageCount == 0,
            "Graphics must be native even when the legacy native flag is false: " + result.IneligibilityCode);
        Require(store.GetSessionProfile(session) == TerminalSemanticProfile.Graphics, "Graphics marker survives persistence.");
        var checkpoint = producer.LoadCheckpoint(session);
        Require(checkpoint.AuthorityVersion == 2 && checkpoint.TranscriptPlaintexts.Count == 0,
            "Graphics uses native authority V2 and no external transcript.");
        var outbox = producer.LoadOutbox(session);
        Require(outbox.Artifacts.Count == 1, "Graphics persists exactly its canonical snapshot.");
        var artifact = outbox.Artifacts.Single();
        Require(artifact.FormatName == SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName &&
            artifact.FormatVersion == 1 && artifact.ObservedLocalInputEpoch == 7 && artifact.InitialTranscriptBeforeLineId == 1,
            "Graphics outbox metadata is exact.");

        Require(expectedBytes.SequenceEqual(checkpoint.SnapshotPlaintext), "Native producer snapshot equals direct native replay.");
        using (var selected = Service(store, producer).Select(session, Latest(), Recipient))
        {
            Require(selected.Status == HostHistoryResponseStatusV1.Ok, "Graphics host-history selection succeeds.");
            var item = selected.Object;
            Require(item.FormatName == artifact.FormatName && item.FormatVersion == 1 &&
                item.KeyId == SnapshotCrypto.TerminalCoreGraphicsSnapshotKeyId(Recipient.DeviceId, Recipient.KeyEpoch),
                "Graphics delivery uses its own format and key.");
            Require(TerminalCoreSnapshotCiphertextV2.Parser.ParseFrom(item.SealedObject).ContainerVersion == 3,
                "Graphics ciphertext uses container version 3.");
            var opened = Open(item, session, true);
            try
            {
                Require(opened.SequenceEqual(expectedBytes), "Graphics native bytes survive DPAPI, seal and open.");
                using var imported = NativeTerminalCoreV2.ImportGraphicsSnapshot(opened);
                Require(Snapshot(imported).SequenceEqual(expectedBytes), "Graphics import/export is exact.");
                var actualFrame = Frame(imported);
                Require(actualFrame.SequenceEqual(Frame(expected)), "Graphics V5 frame survives persistence and encrypted delivery.");
                var decoded = NativeTerminalRenderFrameV5Decoder.Decode(actualFrame);
                Require(decoded.Images.Count == 1 && decoded.Glyphs.Count == 4 &&
                    decoded.Text.WidthProfile == NativeTerminalWidthProfile.LegacyV1,
                    "Kitty and old/new DRCS atlases survive with immutable width=1.");
                Throws<NativeTerminalException>(() => NativeTerminalCoreV2.ImportSnapshot(opened), "Legacy import refuses graphics.");
                Throws<InvalidDataException>(() => Open(item, session, false), "Legacy crypto refuses graphics.");
                var response = ProductionHostHistoryResponseBuilder.Build(ByteString.CopyFrom(new byte[16]), selected,
                    0, 1, SHA256.HashData(item.SealedObject), 0, item.SealedObject.Length);
                Require(response.FormatName == item.FormatName && response.KeyId == item.KeyId &&
                    response.ObjectChunk.Span.SequenceEqual(item.SealedObject), "Relay object chunk retains exact graphics metadata and bytes.");
            }
            finally { CryptographicOperations.ZeroMemory(opened); }
        }

        // Force the real sequence-mismatch rebuild, then recover from the persisted WAL.
        GetTerminal(producer, session).ApplyAdvanceWithHostEffectsV2(5, 0);
        var suffix = new LocalFrame(LocalFrameType.Output, session, 5, Encoding.UTF8.GetBytes("tail"));
        producer.AppendCommittedFrame(suffix);
        Throws<ProductionTerminalHistoryReconcileRequiredException>(() => producer.ProcessSession(session, new[] { suffix }),
            "First intrinsic mismatch requests a rebuild.");
        Require(producer.ProcessSession(session).SnapshotEligible, "Graphics rebuild succeeds without profile fallback.");
        expected.ApplyOutputWithHostEffectsV2(5, suffix.Payload);
        Require(producer.LoadCheckpoint(session).SnapshotPlaintext.SequenceEqual(Snapshot(expected)),
            "Rebuilt graphics state equals direct replay.");

        var restartedStore = new ProductionTerminalHistoryStore(directory, Pc);
        using var restartedProducer = Producer(restartedStore, false);
        Require(restartedStore.GetSessionProfile(session) == TerminalSemanticProfile.Graphics &&
            restartedProducer.ProcessSession(session).SnapshotEligible, "Restart derives Graphics from persisted marker.");
        Require(restartedProducer.LoadCheckpoint(session).SnapshotPlaintext.SequenceEqual(Snapshot(expected)),
            "Restart rebuild preserves all native resources.");
        using var noTranscript = Service(restartedStore, restartedProducer).Select(session, new HostHistoryRequestPayload
        {
            Kind = HostHistoryRequestKindV1.TranscriptPage, SnapshotSeq = 5, BeforeLineId = 1
        }, Recipient);
        Require(noTranscript.Status == HostHistoryResponseStatusV1.NotFound, "Graphics does not create portable transcript pages.");

        var wrongOutbox = restartedProducer.LoadOutbox(session);
        wrongOutbox.Artifacts.Single().FormatName = SnapshotCrypto.TerminalCoreSnapshotFormatName;
        wrongOutbox.Artifacts.Single().FormatVersion = checked((int)SnapshotCrypto.TerminalCoreSnapshotFormatVersion);
        Throws<TerminalHistoryProfileException>(() => restartedStore.SaveOutbox(wrongOutbox), "Legacy format is refused under Graphics marker.");
        var wrongStore = new ProductionTerminalHistoryStore(directory, Pc, _ => TerminalSemanticProfile.Legacy);
        using var wrongProducer = Producer(wrongStore, true);
        using var rejected = Service(wrongStore, wrongProducer).Select(session, Latest(), Recipient);
        Require(rejected.Status == HostHistoryResponseStatusV1.Rejected && rejected.ErrorCode == TerminalSessionProfile.Mismatch,
            "Marker/profile mismatch is rejected before host delivery.");
        Throws<TerminalHistoryProfileException>(() => wrongProducer.ProcessSession(session), "Wrong marker cannot recreate native authority.");
        var wrongVersion = restartedProducer.LoadOutbox(session);
        wrongVersion.Artifacts.Single().FormatVersion = 2;
        Throws<InvalidDataException>(() => restartedStore.SaveOutbox(wrongVersion), "Unknown graphics snapshot version is refused.");
        using (var wrongWidth = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 6, 20_000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16))
        {
            var wrongWidthOutbox = restartedProducer.LoadOutbox(session);
            wrongWidthOutbox.Artifacts.Single().Plaintext = Snapshot(wrongWidth);
            Throws<InvalidDataException>(() => restartedStore.SaveOutbox(wrongWidthOutbox),
                "Width=2 graphics snapshot is refused under the immutable width=1 marker.");
        }
        using (var wrongOutput = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 6, 20_000,
            NativeTerminalOutputProfile.Utf8WithStandaloneC1, NativeTerminalWidthProfile.LegacyV1))
        {
            var wrongOutputOutbox = restartedProducer.LoadOutbox(session);
            wrongOutputOutbox.Artifacts.Single().Plaintext = Snapshot(wrongOutput);
            Throws<InvalidDataException>(() => restartedStore.SaveOutbox(wrongOutputOutbox),
                "Output=1 graphics snapshot is refused under the immutable output=0 marker.");
        }

        GetTerminal(restartedProducer, session).ApplyAdvanceWithHostEffectsV2(6, 0);
        restartedProducer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 6, Encoding.UTF8.GetBytes("last")));
        Throws<ProductionTerminalHistoryReconcileRequiredException>(() => restartedProducer.ProcessSession(session),
            "Native failure gets one rebuild attempt.");
        GetTerminal(restartedProducer, session).ApplyAdvanceWithHostEffectsV2(1, 0);
        var unavailable = restartedProducer.ProcessSession(session);
        Require(unavailable.PermanentlyIneligible && !unavailable.SnapshotEligible &&
            unavailable.IneligibilityCode == "NATIVE_SEQUENCEMISMATCH" &&
            restartedProducer.LoadCheckpoint(session).AuthorityVersion == 2,
            "Repeated Graphics native failure is ineligible, without legacy authority fallback.");
    }

    private static void ValidateGraphicsUnicode16(string root)
    {
        var directory = Path.Combine(root, "graphics-unicode16");
        var session = Guid.NewGuid();
        var store = new ProductionTerminalHistoryStore(directory, Pc,
            _ => TerminalSemanticProfile.GraphicsUnicode16);
        using var producer = Producer(store, false);
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.HostGeometry, session, 1,
            new NativeTerminalHostGeometryV1(8, 16, 0, 0, 0, 0).Encode()));
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 2,
            Encoding.UTF8.GetBytes("A😀\r\nline two\r\nline three\r\nline four\r\n")));
        var result = producer.ProcessSession(session);
        Require(result.SnapshotEligible && result.LastSequence == 2 &&
            store.GetSessionProfile(session) == TerminalSemanticProfile.GraphicsUnicode16,
            "Unicode16 graphics history did not retain its authenticated profile.");
        var outbox = producer.LoadOutbox(session);
        using (var restored = NativeTerminalCoreV2.ImportGraphicsSnapshot(outbox.Artifacts.Single().Plaintext))
        using (var checkpoint = restored.CaptureCheckpoint())
            Require(NativeTerminalRenderFrameV5Decoder.Decode(checkpoint.ExportRenderFrameV5()).Text.WidthProfile ==
                NativeTerminalWidthProfile.Unicode16, "Unicode16 history snapshot changed width profile.");
        var reopened = new ProductionTerminalHistoryStore(directory, Pc);
        Require(reopened.GetSessionProfile(session) == TerminalSemanticProfile.GraphicsUnicode16 &&
            reopened.LoadOutbox(session).Artifacts.Count == 1,
            "Unicode16 profile or history was not recovered from disk.");
        using (var wrongWidth = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 3, 20_000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.LegacyV1))
        {
            var invalid = reopened.LoadOutbox(session);
            invalid.Artifacts.Single().Plaintext = Snapshot(wrongWidth);
            Throws<InvalidDataException>(() => reopened.SaveOutbox(invalid),
                "Width=1 snapshot was accepted under the Unicode16 marker.");
        }
        var wrongProfile = new ProductionTerminalHistoryStore(directory, Pc,
            _ => TerminalSemanticProfile.Graphics);
        Throws<TerminalHistoryProfileException>(() => wrongProfile.GetSessionProfile(session),
            "Graphics v1 provider accepted persisted Unicode16 history.");
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 3,
            Encoding.UTF8.GetBytes("new Unicode16 line\r\n")));
        Require(producer.ProcessSession(session).SnapshotEligible,
            "Unicode16 Delta target was not eligible after retained history.");
        using var pair = reopened.ReadDeltaSnapshotPair(session, 0);
        Require(pair?.Previous?.SnapshotSequence == 2 && pair.Current?.SnapshotSequence == 3,
            "Unicode16 graphics history did not retain an exact Delta base and target.");
    }

    private static byte[] Open(ProductionHostHistoryObject item, Guid session, bool graphics)
    {
        ulong epoch;
        var bytes = graphics ? SnapshotCrypto.OpenTerminalCoreGraphicsSnapshot(Recipient.PairwiseKey, item.SealedObject,
            session, Pc, Recipient.DeviceId, Recipient.KeyEpoch, item.KeyId, item.SnapshotSequence,
            checked((uint)item.Columns), checked((uint)item.Rows), item.InitialTranscriptBeforeLineId, out epoch) :
            SnapshotCrypto.OpenTerminalCoreSnapshot(Recipient.PairwiseKey, item.SealedObject,
            session, Pc, Recipient.DeviceId, Recipient.KeyEpoch, item.KeyId, item.SnapshotSequence,
            checked((uint)item.Columns), checked((uint)item.Rows), item.InitialTranscriptBeforeLineId, out epoch);
        if (graphics) Require(epoch == 7, "Observed input epoch remains authenticated.");
        return bytes;
    }

    private static byte[] Snapshot(NativeTerminal terminal)
    {
        Require(terminal.TryCaptureCheckpoint(out var checkpoint, out _), "Capture graphics checkpoint.");
        using (checkpoint) return checkpoint.ExportGraphicsSnapshot();
    }

    private static byte[] Frame(NativeTerminal terminal)
    {
        Require(terminal.TryCaptureCheckpoint(out var checkpoint, out _), "Capture graphics frame.");
        using (checkpoint) return checkpoint.ExportRenderFrameV5();
    }

    private static NativeTerminal GetTerminal(ProductionTerminalHistoryProducer producer, Guid session)
    {
        var states = typeof(ProductionTerminalHistoryProducer).GetField("states", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(producer);
        var state = states.GetType().GetProperty("Item").GetValue(states, new object[] { session });
        Require(state.GetType().GetField("Model").GetValue(state) == null, "Graphics never allocates a Portable projection.");
        return (NativeTerminal)state.GetType().GetField("TerminalCore").GetValue(state);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        assertions++;
    }
}
