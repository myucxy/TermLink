using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class DeltaValidation
{
    internal static void Run()
    {
        Require(NativeTerminalDelta.SupportsV1(), "The selected DLL has no Delta V1 exports.");
        var identifier = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var context = new NativeTerminalDeltaContext(identifier, NativeTerminalDeltaProfile.Unicode16V1);
        identifier[0] = 0;
        Require(context.Id[0] == 1, "Delta context retained mutable caller storage.");
        var returnedId = context.Id;
        returnedId[0] = 0;
        Require(context.Id[0] == 1, "Delta context exposed mutable identity storage.");
        var otherContext = new NativeTerminalDeltaContext(Enumerable.Repeat((byte)2, 32).ToArray(),
            NativeTerminalDeltaProfile.Unicode16V1);

        using var terminal = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 1, 512,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
        using var journal = terminal.BeginDeltaJournal(context, 2, 16UL * 1024 * 1024, 60_000);
        Require(journal.GetInfo(999).Entries == 0, "A new Delta journal was not empty.");
        var text = new StringBuilder();
        for (var row = 0; row < 300; row++)
        {
            for (var column = 0; column < 72; column++)
                text.Append((char)('!' + (row * 37 + column * 13) % 90));
            text.Append("\r\n");
        }
        terminal.ApplyOutput(1, Encoding.ASCII.GetBytes(text.ToString()));
        using var baseline = terminal.CaptureCheckpoint();
        var baselineBytes = baseline.ExportGraphicsSnapshot();
        var baseState = baseline.GetDeltaState(context);
        Require(baseState.NextSourceSequence == 2 && baseState.SnapshotBytes == (ulong)baselineBytes.Length &&
            baseState.Sha256.SequenceEqual(SHA256.HashData(baselineBytes)), "Delta baseline identity is not the canonical Snapshot SHA-256.");
        var retainedBase = journal.Capture(baseline, 1000);
        Require(retainedBase.Sha256.SequenceEqual(baseState.Sha256), "Journal captured the wrong baseline.");

        terminal.ApplyOutput(2, Encoding.ASCII.GetBytes("Z"));
        using var target = terminal.CaptureCheckpoint();
        var targetBytes = target.ExportGraphicsSnapshot();
        var targetState = journal.Capture(target, 1001);
        Require(targetState.NextSourceSequence == 3 && targetState.SnapshotBytes == (ulong)targetBytes.Length &&
            targetState.Sha256.SequenceEqual(SHA256.HashData(targetBytes)), "Delta target identity is not canonical.");
        var delta = baseline.ExportDeltaTo(target, context);
        Require(delta.Length > 0 && delta.Length < targetBytes.Length, "Delta was empty or hid a full Snapshot.");
        Require(journal.Export(baseState, targetState, 1002).SequenceEqual(delta), "Journal and direct checkpoint Delta disagree.");
        Require(journal.GetInfo(1002).Entries == 2, "Journal lost a retained checkpoint.");

        var applied = baseline.ApplyDelta(context, delta);
        Require(applied.Outcome == NativeTerminalDeltaOutcome.Applied && applied.Terminal != null &&
            applied.State.Sha256.SequenceEqual(targetState.Sha256), "Delta did not return a detached terminal and target identity.");
        using var restored = applied.Terminal;
        using (var restoredCheckpoint = restored.CaptureCheckpoint())
            Require(restoredCheckpoint.ExportGraphicsSnapshot().SequenceEqual(targetBytes),
                "Applied Delta differs from the target canonical Snapshot.");
        var repeated = target.ApplyDelta(context, delta);
        Require(repeated.Outcome == NativeTerminalDeltaOutcome.AlreadyApplied && repeated.Terminal == null,
            "Repeated Delta allocated a terminal or lost the idempotent outcome.");

        var corrupt = (byte[])delta.Clone();
        corrupt[^1] ^= 1;
        ExpectStatus(NativeTerminalStatus.MalformedDelta, () => baseline.ApplyDelta(context, corrupt));
        ExpectStatus(NativeTerminalStatus.MalformedDelta, () => target.ApplyDelta(context, corrupt));
        ExpectStatus(NativeTerminalStatus.NeedsFullSnapshot, () => baseline.ApplyDelta(otherContext, delta));
        using (var foreign = NativeTerminalCoreV2.CreateGraphicsV1(80, 24, 1, 512,
                   NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16))
        {
            foreign.ApplyOutput(1, Encoding.ASCII.GetBytes("a different branch"));
            using var wrongBase = foreign.CaptureCheckpoint();
            ExpectStatus(NativeTerminalStatus.NeedsFullSnapshot, () => wrongBase.ApplyDelta(context, delta));
            ExpectStatus(NativeTerminalStatus.NeedsFullSnapshot, () => journal.Capture(wrongBase, 1002));
        }
        ExpectStatus(NativeTerminalStatus.NeedsFullSnapshot, () => baseline.ExportDeltaTo(target, context, 1));
        ExpectStatus(NativeTerminalStatus.Unsupported, () => baseline.ExportDelta());
        Require(baseline.ExportGraphicsSnapshot().SequenceEqual(baselineBytes),
            "Rejected Delta changed the source checkpoint.");

        var suffix = Encoding.ASCII.GetBytes("suffix");
        terminal.ApplyOutput(3, suffix);
        restored.ApplyOutput(3, suffix);
        using var finalTarget = terminal.CaptureCheckpoint();
        using var finalRestored = restored.CaptureCheckpoint();
        Require(finalRestored.ExportGraphicsSnapshot().SequenceEqual(finalTarget.ExportGraphicsSnapshot()),
            "Delta restoration changed suffix event behavior.");
        journal.Capture(finalTarget, 1003);
        Require(journal.GetInfo(1004).Entries == 2, "Journal entry bound was ignored.");
        ExpectStatus(NativeTerminalStatus.NeedsFullSnapshot,
            () => journal.Export(baseState, targetState, 1004));

        using var c1Terminal = NativeTerminalCoreV2.CreateGraphicsV1(8, 3, 1, 0,
            NativeTerminalOutputProfile.Utf8WithStandaloneC1, NativeTerminalWidthProfile.Unicode16);
        using var c1Checkpoint = c1Terminal.CaptureCheckpoint();
        var c1Context = new NativeTerminalDeltaContext(context.Id,
            NativeTerminalDeltaProfile.Unicode16StandaloneC1V1);
        Require(c1Checkpoint.GetDeltaState(c1Context).SnapshotBytes > 0,
            "Standalone C1 profile was not accepted by the Windows binding.");
        ExpectStatus(NativeTerminalStatus.IncompatibleDelta,
            () => c1Checkpoint.GetDeltaState(context));
        Console.WriteLine("Delta V1 Windows DLL validation passed.");
    }

    private static void ExpectStatus(NativeTerminalStatus expected, Action action)
    {
        try { action(); }
        catch (NativeTerminalException error) when (error.Status == expected) { return; }
        throw new InvalidOperationException("Expected native Delta status " + expected + ".");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
