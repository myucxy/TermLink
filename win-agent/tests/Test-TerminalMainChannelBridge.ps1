[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-TerminalMainBridge-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

try {
    $projectPath = Join-Path $temporaryDirectory 'TerminalMainBridgeValidation.csproj'
    $programPath = Join-Path $temporaryDirectory 'Program.cs'
    $bridgePath = (Join-Path $sourceRoot 'src\Agent\TerminalMainChannelBridge.cs').Replace('\', '/')
    $ipcPath = (Join-Path $sourceRoot 'src\Common\LocalIpc.cs').Replace('\', '/')
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$bridgePath" Link="TerminalMainChannelBridge.cs" />
    <Compile Include="$ipcPath" Link="LocalIpc.cs" />
    <Compile Include="Program.cs" />
  </ItemGroup>
</Project>
"@
    Set-Content -LiteralPath $projectPath -Value $project -Encoding UTF8

    $program = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.LocalIpc;

internal static class Program
{
    private sealed class FakeChannel : ITerminalMainAsyncChannel
    {
        private readonly Queue<TerminalMainChannelEvent> events;
        private readonly List<string> operations;
        private readonly bool requireAckAfterSecondRead;
        private int readCount;
        internal readonly List<ulong> Acks = new List<ulong>();
        internal FakeChannel(
            Guid sessionId,
            IEnumerable<TerminalMainChannelEvent> events,
            List<string> operations = null,
            bool requireAckAfterSecondRead = false)
        {
            SessionId = sessionId;
            this.events = new Queue<TerminalMainChannelEvent>(events);
            this.operations = operations;
            this.requireAckAfterSecondRead = requireAckAfterSecondRead;
        }
        public Guid SessionId { get; private set; }
        public ValueTask<TerminalMainChannelEvent> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (requireAckAfterSecondRead && readCount >= 2 && !Acks.Contains(2))
                throw new InvalidOperationException("bridge read past the split pair before ACK 2");
            readCount++;
            return new ValueTask<TerminalMainChannelEvent>(events.Count == 0 ? null : events.Dequeue());
        }
        public ValueTask AcknowledgeAsync(ulong sequence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (operations != null) operations.Add("ack:" + sequence);
            Acks.Add(sequence);
            return default(ValueTask);
        }
    }

    private sealed class FakeSink : ITerminalMainFrameCommitSink
    {
        private readonly List<string> operations;
        internal FakeSink(List<string> operations = null) { this.operations = operations; }
        internal readonly List<LocalFrame> Frames = new List<LocalFrame>();
        public ValueTask CommitAsync(IReadOnlyList<LocalFrame> frames, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (operations != null)
                operations.Add("commit:" + frames[0].Sequence + "-" + frames[frames.Count - 1].Sequence);
            Frames.AddRange(frames);
            return default(ValueTask);
        }
    }

    public static async Task Main()
    {
        var sessionId = Guid.NewGuid();
        var operations = new List<string>();
        var channel = new FakeChannel(sessionId, new[]
        {
            TerminalMainChannelEvent.Output(sessionId, 1, new[] { 'A', '\ud83d' }),
            TerminalMainChannelEvent.Output(sessionId, 2, new[] { '\ude00', 'B' }),
            TerminalMainChannelEvent.Resize(sessionId, 3, 120, 40),
            TerminalMainChannelEvent.Title(sessionId, 4, "PowerShell — build"),
            TerminalMainChannelEvent.Lifecycle(sessionId, 5, LocalLifecycleState.Ended, 7)
        }, operations, true);
        var sink = new FakeSink(operations);
        var bridge = new TerminalMainChannelBridge(channel, sink);
        await bridge.RunAsync(CancellationToken.None);
        Require(sink.Frames.Count == 5, "split-surrogate stream did not preserve one frame per event");
        Require(Encoding.UTF8.GetString(sink.Frames[0].Payload) == "A", "first UTF-8 chunk changed");
        Require(Encoding.UTF8.GetString(sink.Frames[1].Payload) == "😀B", "split surrogate was not encoded continuously");
        Require(sink.Frames[3].Type == LocalFrameType.Title &&
            LocalPayloadCodec.ParseTitle(sink.Frames[3].Payload) == "PowerShell — build",
            "title event did not preserve its ordered normalized value");
        Require(channel.Acks.SequenceEqual(new[] { 2UL, 3UL, 4UL, 5UL }), "ACK was sent before the split output batch committed");
        Require(operations.IndexOf("commit:1-2") < operations.IndexOf("ack:2"), "source ACK preceded durable commit");
        Require(bridge.Statistics.MalformedUtf16CodeUnits == 0, "valid split surrogate counted as malformed");

        var malformedChannel = new FakeChannel(sessionId, new[]
        {
            TerminalMainChannelEvent.Output(sessionId, 1, new[] { '\ud83d' }),
            TerminalMainChannelEvent.Resize(sessionId, 2, 80, 24)
        });
        var malformedSink = new FakeSink();
        var malformedBridge = new TerminalMainChannelBridge(malformedChannel, malformedSink);
        await malformedBridge.RunAsync(CancellationToken.None);
        Require(malformedBridge.Statistics.MalformedUtf16CodeUnits == 1, "malformed UTF-16 was not counted");
        Require(Encoding.UTF8.GetString(malformedSink.Frames[0].Payload) == "�", "malformed UTF-16 was not made explicit");

        var gapChannel = new FakeChannel(sessionId, new[]
        {
            TerminalMainChannelEvent.Output(sessionId, 1, new[] { 'x' }),
            TerminalMainChannelEvent.Gap(sessionId, 5, 2, 4),
            TerminalMainChannelEvent.Lifecycle(sessionId, 6, LocalLifecycleState.Ended, 0)
        });
        var gapSink = new FakeSink();
        var gapBridge = new TerminalMainChannelBridge(gapChannel, gapSink);
        await gapBridge.RunAsync(CancellationToken.None);
        Require(gapBridge.Statistics.GapEvents == 1 && gapBridge.Statistics.DroppedSourceEvents == 3, "gap statistics changed");
        ulong gapStart, gapEnd;
        LocalPayloadCodec.ParseGap(gapSink.Frames[1].Payload, out gapStart, out gapEnd);
        Require(gapStart == 2 && gapEnd == 4, "gap range changed");

        var foreignSession = Guid.NewGuid();
        var isolatedSink = new FakeSink();
        var isolatedBridge = new TerminalMainChannelBridge(
            new FakeChannel(sessionId, new[]
            {
                TerminalMainChannelEvent.Output(foreignSession, 1, new[] { 'B' })
            }),
            isolatedSink);
        var crossSessionRejected = false;
        try { await isolatedBridge.RunAsync(CancellationToken.None); }
        catch (InvalidDataException) { crossSessionRejected = true; }
        Require(crossSessionRejected && isolatedSink.Frames.Count == 0,
            "Session B output entered Session A's durable stream");

        var oversizedRejected = false;
        try
        {
            TerminalMainChannelEvent.Output(
                sessionId,
                1,
                new char[TerminalMainChannelLimits.MaximumOutputCodeUnits + 1]);
        }
        catch (ArgumentOutOfRangeException) { oversizedRejected = true; }
        Require(oversizedRejected,
            "terminal-main accepted a callback that cannot fit below the Relay Envelope limit");

        var boundedOutput = Enumerable.Repeat('\u0800', TerminalMainChannelLimits.MaximumOutputCodeUnits).ToArray();
        var boundedSink = new FakeSink();
        var boundedBridge = new TerminalMainChannelBridge(
            new FakeChannel(sessionId, new[]
            {
                TerminalMainChannelEvent.Output(sessionId, 1, boundedOutput),
                TerminalMainChannelEvent.Lifecycle(sessionId, 2, LocalLifecycleState.Ended, 0)
            }),
            boundedSink);
        await boundedBridge.RunAsync(CancellationToken.None);
        Require(boundedSink.Frames[0].Payload.Length == TerminalMainChannelLimits.MaximumOutputUtf8Bytes &&
            boundedSink.Frames[0].Payload.Length < 1024 * 1024,
            "maximum terminal-main output did not retain strict Relay Envelope headroom");
        Console.WriteLine("TerminalMainChannelBridge validation passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
'@
    Set-Content -LiteralPath $programPath -Value $program -Encoding UTF8

    & $DotNetPath run --project $projectPath --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "TerminalMainChannelBridge validation failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
