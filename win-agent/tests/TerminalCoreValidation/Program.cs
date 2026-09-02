using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

if (args.Contains("--history-root") || args.Contains("--current-production-session"))
    RunIsolatedWalValidation(args);

var encoder = new VtTerminalInputEncoder();

Equal(Encoding.UTF8.GetBytes("中文🙂"), encoder.Encode(TerminalInput.FromText("中文🙂")), "UTF-8 text");
Equal(new byte[] { 0x03 }, encoder.Encode(TerminalInput.FromText("c", TerminalInputModifiers.Control)), "Ctrl+C");
Equal(Encoding.ASCII.GetBytes("\u001b[1;7A"), encoder.Encode(TerminalInput.FromKey(
    TerminalKey.ArrowUp, TerminalInputModifiers.Control | TerminalInputModifiers.Alt)), "Ctrl+Alt+Up");
Equal(new byte[] { 0x00, 0x1b, 0x7f, 0xff }, encoder.Encode(TerminalInput.FromRawBytes(
    new byte[] { 0x00, 0x1b, 0x7f, 0xff })), "raw bytes");

var wire = new TerminalInputV1
{
    FormatVersion = 1,
    Kind = TerminalInputKindV1.Text,
    Text = "终端🙂",
    Modifiers = new TerminalInputModifiersV1()
};
if (!ProductionAgentRuntime.TryEncodeTypedInput(wire, out var encoded))
    throw new InvalidOperationException("typed input was rejected");
try { Equal(Encoding.UTF8.GetBytes("终端🙂"), encoded, "wire typed input"); }
finally { Array.Clear(encoded); }

var output = new OutputChunkPayload
{
    Output = ByteString.CopyFromUtf8("x"),
    SourceEventSeq = 42,
    PresentationRevision = 42
};
var roundTrip = OutputChunkPayload.Parser.ParseFrom(output.ToByteArray());
if (roundTrip.SourceEventSeq != 42 || roundTrip.PresentationRevision != 42)
    throw new InvalidOperationException("presentation metadata did not round-trip");

if (!ProductionAgentRuntime.IsTypedInputRuntimeEnabled(null) ||
    ProductionAgentRuntime.IsTypedInputRuntimeEnabled("0"))
    throw new InvalidOperationException("typed-input rollback flag is invalid");

using (var enabledHello = JsonDocument.Parse(ProductionAgentRuntime.BuildHelloPayloadWithFeatures(true, true)))
{
    var capabilities = enabledHello.RootElement.GetProperty("capabilities")
        .EnumerateArray().Select(item => item.GetString()).ToArray();
    if (!capabilities.Contains(TerminalCapabilities.PresentationMetadataV1) ||
        !capabilities.Contains(TerminalCapabilities.TypedInputV1) ||
        !capabilities.Contains("terminal_snapshot_v2"))
        throw new InvalidOperationException("enabled capability set is incomplete");
}
using (var rollbackHello = JsonDocument.Parse(ProductionAgentRuntime.BuildHelloPayloadWithFeatures(false, false)))
{
    var capabilities = rollbackHello.RootElement.GetProperty("capabilities")
        .EnumerateArray().Select(item => item.GetString()).ToArray();
    if (capabilities.Contains(TerminalCapabilities.TypedInputV1) ||
        capabilities.Contains("terminal_snapshot_v2"))
        throw new InvalidOperationException("rollback capability set still advertises disabled features");
}

Console.WriteLine("Terminal core validation passed");

static void Equal(byte[] expected, byte[] actual, string name)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{name} mismatch: {Convert.ToHexString(actual)}");
}

static void RunIsolatedWalValidation(string[] values)
{
    string Option(string name)
    {
        var index = Array.IndexOf(values, name);
        if (index < 0 || index + 1 >= values.Length)
            throw new ArgumentException($"Missing {name}.");
        return values[index + 1];
    }

    string sourceRoot;
    Guid pcDeviceId;
    Guid sessionId;
    if (Array.IndexOf(values, "--current-production-session") >= 0)
    {
        sessionId = Guid.Parse(Option("--current-production-session"));
        var config = ProductionAgentConfig.TryLoad() ??
            throw new InvalidOperationException("Current production Agent config was not found.");
        using var credentials = ProductionCredentials.TryLoad() ??
            throw new InvalidOperationException("Current production credentials were not found.");
        sourceRoot = Path.Combine(
            Path.GetDirectoryName(ProductionAgentConfig.DefaultPath)!,
            "production-history",
            config.PcId.ToString("N"));
        pcDeviceId = credentials.DeviceId;
    }
    else
    {
        sourceRoot = Path.GetFullPath(Option("--history-root"));
        pcDeviceId = Guid.Parse(Option("--pc-device-id"));
        sessionId = Guid.Parse(Option("--session-id"));
    }
    var sourceSession = Path.Combine(sourceRoot, sessionId.ToString("N"));
    if (!Directory.Exists(Path.Combine(sourceSession, "wal")))
        throw new DirectoryNotFoundException("The selected session has no terminal WAL.");

    var isolatedRoot = Path.Combine(Path.GetTempPath(), "termlink-codex-wal-validation-" + Guid.NewGuid().ToString("N"));
    try
    {
        CopyDirectory(sourceSession, Path.Combine(isolatedRoot, sessionId.ToString("N")));
        var store = new ProductionTerminalHistoryStore(isolatedRoot, pcDeviceId);
        var frames = store.ReadFrames(sessionId);
        var frameCount = frames.Count;
        using (var shadow = new ProductionTerminalCoreShadow(80, 24))
        {
            foreach (var frame in frames) shadow.Apply(frame);
            var observed = shadow.ObserveCheckpoint(
                portable: null,
                expectedSequence: frames.Last().Sequence,
                portableReasonCode: "ISOLATED_WAL_VALIDATION");
            if (observed.Status == "SHADOW_RETIRED")
                throw new InvalidOperationException(
                    $"terminal-core rejected the isolated WAL: {observed.ReasonCode}");
        }
        var result = new ProductionTerminalHistoryProducer(store, pcDeviceId).ProcessSession(sessionId);
        foreach (var frame in frames) Array.Clear(frame.Payload);
        Console.WriteLine(
            "Isolated encrypted WAL validation passed; " +
            $"frames={frameCount}; last_sequence={result.LastSequence}; " +
            $"snapshot_eligible={result.SnapshotEligible}; " +
            $"transcript_pages={result.TranscriptPageCount}; " +
            $"reason={(result.IneligibilityCode ?? string.Empty)}");
    }
    finally
    {
        if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true);
    }
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
    foreach (var directory in Directory.GetDirectories(source))
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
}
