using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TermLink.Agent;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

internal static class BrokerReplayValidation
{
    private static readonly Guid Pc = Guid.Parse("12345678-abcd-4321-abcd-1234567890ab");
    private static readonly NativeTerminalHostGeometryV1 Geometry =
        new NativeTerminalHostGeometryV1(9, 18, 720, 432, 720, 432);
    private const string File = "\x1b_Ga=T,t=f,f=32,s=1,v=1,S=4,i=31;QzovYS5yZ2Jh\x1b\\";
    private const string SecondFile = "\x1b_Ga=T,t=f,f=32,s=1,v=1,S=4,i=32;QzovYi5yZ2Jh\x1b\\";

    internal static void Run(string root)
    {
        ValidateExternalReplay(root);
        ValidateRejectedExternalReferences(root);
        ValidateInlineFailureReplay(root);
        ValidateLegacyRefusal(root);
    }

    private static NativeTerminal CreateAuthority()
    {
        var terminal = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 1, 20_000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.LegacyV1);
        terminal.ApplyBatchWithHostEffectsV2(NativeTerminalBatchEvent.HostGeometry(1, Geometry));
        return terminal;
    }

    private static (byte[] Canonical, byte[] Snapshot) Capture(string output,
        NativeTerminalBrokerFailure? failure = null)
    {
        using var authority = CreateAuthority();
        byte[] canonical = null;
        authority.ApplyOutputWithCapturedBrokerV1(2, Encoding.ASCII.GetBytes(output),
            request => failure.HasValue
                ? NativeTerminalBrokerCapture.FromFailure(failure.Value)
                : NativeTerminalBrokerCapture.FromBytes(request.Ordinal == 0
                    ? new byte[] { 1, 2, 3, 4 } : new byte[] { 5, 6, 7, 8 }),
            (_, records) => canonical = NativeTerminalBrokerCaptureCodecV1.Encode(2, records));
        Require(canonical != null, "Broker capture was not published.");
        return (canonical, Snapshot(authority));
    }

    private static byte[] Snapshot(NativeTerminal terminal)
    {
        using var checkpoint = terminal.CaptureCheckpoint();
        return checkpoint.ExportGraphicsSnapshot();
    }

    private static ProductionTerminalHistoryStore Store(string root, string name) =>
        new ProductionTerminalHistoryStore(Path.Combine(root, "broker-" + name), Pc,
            _ => TerminalSemanticProfile.Graphics);

    private static ProductionTerminalHistoryProducer Producer(ProductionTerminalHistoryStore store) =>
        new ProductionTerminalHistoryProducer(store, Pc, terminalCoreShadowEnabled: false,
            terminalCoreSnapshotAuthorityEnabled: false, terminalCoreShadowObserver: null);

    private static void Append(ProductionTerminalHistoryProducer producer, Guid session,
        NativeTerminalBrokerOutputReferenceV1 reference)
    {
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.HostGeometry, session, 1,
            Geometry.Encode()));
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.BrokerOutput, session, 2,
            reference.Encode()));
    }

    private static void ValidateExternalReplay(string root)
    {
        var output = "\x1b[5n" + File;
        var (canonical, expected) = Capture(output);
        var session = Guid.NewGuid();
        var store = Store(root, "external");
        var objectId = Guid.NewGuid();
        try
        {
            KittyCaptureStore.CaptureReceipt receipt;
            using (var sidecars = new KittyCaptureStore(store.BrokerCaptureRootPath, session))
                receipt = sidecars.Put(objectId, 2, canonical);
            try
            {
                using var reference = NativeTerminalBrokerOutputReferenceV1.External(2,
                    Encoding.ASCII.GetBytes(output), objectId, canonical.Length, receipt.Sha256);
                using var producer = Producer(store);
                Append(producer, session, reference);
                var result = producer.ProcessSession(session);
                Require(result.SnapshotEligible && result.LastSequence == 2 &&
                    producer.LoadCheckpoint(session).SnapshotPlaintext.SequenceEqual(expected),
                    "External broker WAL replay differs from native authority.");
                var repeated = producer.ProcessSession(session);
                Require(repeated.SnapshotEligible &&
                    producer.LoadCheckpoint(session).SnapshotPlaintext.SequenceEqual(expected),
                    "Repeated history preparation changed broker state or effects.");
                using var restarted = Producer(new ProductionTerminalHistoryStore(store.RootPath, Pc));
                Require(restarted.ProcessSession(session).SnapshotEligible &&
                    restarted.LoadCheckpoint(session).SnapshotPlaintext.SequenceEqual(expected),
                    "Restarted history producer failed to replay external captures.");
            }
            finally { CryptographicOperations.ZeroMemory(receipt.Sha256); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static void ValidateRejectedExternalReferences(string root)
    {
        var (single, snapshot) = Capture(File);
        var (two, secondSnapshot) = Capture(File + SecondFile);
        try
        {
            RejectExternal(root, "missing-sidecar", File, single,
                saveSidecar: false, corruptHash: false);
            RejectExternal(root, "wrong-hash", File, single,
                saveSidecar: true, corruptHash: true);
            RejectExternal(root, "missing-record", File + SecondFile, single,
                saveSidecar: true, corruptHash: false);
            RejectExternal(root, "extra-record", File, two,
                saveSidecar: true, corruptHash: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(single);
            CryptographicOperations.ZeroMemory(snapshot);
            CryptographicOperations.ZeroMemory(two);
            CryptographicOperations.ZeroMemory(secondSnapshot);
        }
    }

    private static void RejectExternal(string root, string name, string output, byte[] canonical,
        bool saveSidecar, bool corruptHash)
    {
        var session = Guid.NewGuid();
        var store = Store(root, name);
        var objectId = Guid.NewGuid();
        var hash = SHA256.HashData(canonical);
        try
        {
            if (saveSidecar)
            {
                using var sidecars = new KittyCaptureStore(store.BrokerCaptureRootPath, session);
                var receipt = sidecars.Put(objectId, 2, canonical);
                CryptographicOperations.ZeroMemory(receipt.Sha256);
            }
            if (corruptHash) hash[0] ^= 1;
            using var reference = NativeTerminalBrokerOutputReferenceV1.External(2,
                Encoding.ASCII.GetBytes(output), objectId, canonical.Length, hash);
            using var producer = Producer(store);
            Append(producer, session, reference);
            var result = producer.ProcessSession(session);
            var checkpoint = producer.LoadCheckpoint(session);
            Require(!result.SnapshotEligible && result.PermanentlyIneligible &&
                checkpoint.SnapshotPlaintext == null && !checkpoint.SnapshotEligible &&
                result.IneligibilityCode == (saveSidecar && !corruptHash
                    ? "BROKER_CAPTURE_INVALID" : corruptHash
                    ? "BROKER_CAPTURE_INVALID" : "BROKER_CAPTURE_MISSING"),
                "Invalid broker reference produced a false graphics snapshot: " + name +
                " / " + result.IneligibilityCode);
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private static void ValidateInlineFailureReplay(string root)
    {
        var (canonical, expected) = Capture(File, NativeTerminalBrokerFailure.Unavailable);
        var session = Guid.NewGuid();
        var store = Store(root, "inline");
        try
        {
            using var reference = NativeTerminalBrokerOutputReferenceV1.InlineFailures(2,
                Encoding.ASCII.GetBytes(File), canonical);
            using var producer = Producer(store);
            Append(producer, session, reference);
            Require(producer.ProcessSession(session).SnapshotEligible &&
                producer.LoadCheckpoint(session).SnapshotPlaintext.SequenceEqual(expected),
                "Inline broker failure did not replay its canonical response.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static void ValidateLegacyRefusal(string root)
    {
        var (canonical, expected) = Capture(File, NativeTerminalBrokerFailure.Unavailable);
        var session = Guid.NewGuid();
        var store = new ProductionTerminalHistoryStore(Path.Combine(root, "broker-legacy"), Pc);
        try
        {
            using var reference = NativeTerminalBrokerOutputReferenceV1.InlineFailures(2,
                Encoding.ASCII.GetBytes(File), canonical);
            using var producer = new ProductionTerminalHistoryProducer(store, Pc,
                terminalCoreShadowEnabled: false, terminalCoreSnapshotAuthorityEnabled: true,
                terminalCoreShadowObserver: null);
            Append(producer, session, reference);
            var result = producer.ProcessSession(session);
            Require(!result.SnapshotEligible && result.PermanentlyIneligible &&
                result.IneligibilityCode == "BROKER_CAPTURE_UNSUPPORTED",
                "Legacy native history silently advanced a broker Output.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
