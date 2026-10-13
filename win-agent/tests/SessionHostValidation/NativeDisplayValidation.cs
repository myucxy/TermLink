using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.NativeDisplay;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class NativeDisplayValidation
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var replyCount = 0;
        using (var nativeAuthority = new TerminalCoreAuthority(20, 5, bytes => {
            Require(Encoding.ASCII.GetString(bytes) == "\x1b[0n", "Native authority query reply changed."); replyCount++;
        }, null))
        {
            for (ulong sequence = 1; sequence <= 2048; sequence++)
                nativeAuthority.ApplyDurable(new LocalFrame(LocalFrameType.Output, Guid.Empty, sequence, Encoding.ASCII.GetBytes("\x1b[5n")));
            Require(replyCount == 2048 && !nativeAuthority.FailedClosed, "Native authority accumulated or duplicated projected replies.");
        }
        using var authority = new TerminalCoreAuthority(20, 5, _ => { }, (_, _, _, _) => { });
        var id = Guid.NewGuid();
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 1, Encoding.UTF8.GetBytes("\x1b#6ABCD\x1b[2;1H\x1b#3TOP")));
        using var input = new MemoryStream();
        using var server = new NativeDisplayServer(authority.RenderDisplay, (kind, bytes) => {
            if (kind != NativeDisplayProtocol.Text) throw new InvalidDataException();
            return authority.EncodeAndWriteDisplayText(NativeDisplayProtocol.Utf8.GetString(bytes), false, encoded => { input.Write(encoded); return true; });
        }, (kind, payload) => {
            if (kind == NativeDisplayProtocol.SignalsQuery) {
                NativeDisplayProtocol.RequireSize(payload, 0);
                return (NativeDisplayProtocol.SignalsState, authority.PollDisplaySignals(id));
            }
            return authority.ShellDisplay(kind, payload);
        });
        server.Start(Environment.ProcessId);
        using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, deadline.Token);
        var query = new byte[12];
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        var result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Frame, "No authoritative frame returned.");
        var frame = NativeTerminalRenderFrameV4Decoder.Decode(result.Payload);
        Require(frame.NextSourceSequence == 2 && frame.RenderRows[0].LineRendition == NativeTerminalLineRendition.DoubleWidth, "Wrong authoritative row state.");
        BinaryPrimitives.WriteUInt64LittleEndian(query, frame.PresentationRevision);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Unchanged && result.Payload.Length == 0, "Idle window exports duplicate full frames.");
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Text, Encoding.UTF8.GetBytes("A中😀"), NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Accepted && Encoding.UTF8.GetString(input.ToArray()) == "A中😀", "Native input duplicated or lost text.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 2, Encoding.UTF8.GetBytes("\x1b[6;1H\r\n2\r\n3\r\n4\r\n5\r\n6")));
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(8), 10_000);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        var history = NativeTerminalRenderFrameV4Decoder.Decode(result.Payload);
        Require(!history.Cursor.Visible, "Historical view exposed live cursor.");
        BinaryPrimitives.WriteUInt64LittleEndian(query, history.PresentationRevision);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Unchanged, "Idle history view exports duplicate full frames.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output,id,3,Encoding.UTF8.GetBytes("new\r\n\x1b[3J")));
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Unchanged, "Live output replaced frozen history.");
        BinaryPrimitives.WriteUInt64LittleEndian(query,0);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(NativeTerminalRenderFrameV4Decoder.Decode(result.Payload).PresentationRevision == history.PresentationRevision,
            "Frozen history recaptured after ED3.");
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(8), 0);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, query, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Frame && NativeTerminalRenderFrameV4Decoder.Decode(result.Payload).Cursor.Visible,
            "Returning live at the same revision left the historical frame visible.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 4, Encoding.UTF8.GetBytes("\x1b[?9001h")));
        using var win32 = new MemoryStream(); var writes = 0;
        authority.EncodeAndWriteDisplayKeyPair(new NativeTerminalWin32KeyEvent(13, 28, '\r', true, 0, 1),
            new NativeTerminalWin32KeyEvent(13, 28, '\r', false, 0, 1), bytes => { writes++; win32.Write(bytes); return true; });
        Require(writes == 1 && Encoding.ASCII.GetString(win32.ToArray()) == "\x1b[13;28;13;1;0;1_\x1b[13;28;13;0;0;1_", "Enter record was lost or split across authority writes.");

        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 5, Encoding.UTF8.GetBytes("\x1b]7;file:///C:/work\x1b\\\x1b]133;A\x1b\\prompt\x1b]133;B\x1b\\\r\n1\r\n2\r\n3\r\n4\r\n5\r\n")));
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.ShellQuery, new byte[] { 1 }, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.ShellState, "Shell query lost.");
        var shell = NativeTerminalShellState.Decode(result.Payload.AsSpan(8).ToArray(), BinaryPrimitives.ReadUInt64LittleEndian(result.Payload));
        Require(shell.CwdUri == "file:///C:/work" && shell.Commands.Count == 1, "Shell state mismatch.");
        var seek = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(seek, shell.Commands[0].Id);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.ShellSeek, seek, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.ShellAnchor && BinaryPrimitives.ReadUInt32LittleEndian(result.Payload) > 0, "Shell seek lost.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 6, Encoding.UTF8.GetBytes("\x1b[3J")));
        _ = authority.RenderDisplay(0, 0);
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.ShellSeek, seek, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.Rejected, "Evicted shell anchor guessed a position.");

        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 7, Encoding.UTF8.GetBytes("\a\x1b]9;4;2;66\x1b\\")));
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.SignalsQuery, Array.Empty<byte>(), NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        var signals = NativeDisplaySignals.Decode(result.Payload);
        Require(result.Kind == NativeDisplayProtocol.SignalsState && signals.SessionId == id && signals.Progress.ProgressState == 2 &&
            signals.Progress.ProgressPercent == 66 && signals.Actions.Count == 1 && signals.Actions[0].SourceSequence == 7, "Live actions/progress were lost.");
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.SignalsQuery, Array.Empty<byte>(), NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(NativeDisplaySignals.Decode(result.Payload).Actions.Count == 0, "Polling repeated a platform action.");

        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, 8, Encoding.UTF8.GetBytes("\x1b]7;file:///C:/uncommitted")));
        await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.ShellQuery, new byte[] { 0 }, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumFrameBytes, deadline.Token);
        Require(result.Kind == NativeDisplayProtocol.ShellState, "A partial OSC made committed Shell state unavailable.");
        var pendingShell = NativeTerminalShellState.Decode(result.Payload.AsSpan(8).ToArray(), BinaryPrimitives.ReadUInt64LittleEndian(result.Payload));
        Require(pendingShell.CwdUri == shell.CwdUri, "Shell query exposed an uncommitted directory marker.");

        // Reject the length before allocating or waiting for an attacker-selected body.
        using var invalid = new MemoryStream(new byte[] { 1, 1, 255, 255, 255, 127 });
        try { await NativeDisplayProtocol.ReadAsync(invalid, 32, deadline.Token); throw new Exception("Oversized message accepted."); }
        catch (InvalidDataException) { }
        using var fatalInputServer = new NativeDisplayServer(authority.RenderDisplay, (_, _) =>
            throw CreateTerminalFailure(NativeTerminalStatus.FailedClosed));
        fatalInputServer.Start(Environment.ProcessId);
        using var fatalInputClient = new NamedPipeClientStream(".", fatalInputServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await fatalInputClient.ConnectAsync(5000, deadline.Token);
        await NativeDisplayProtocol.WriteAsync(fatalInputClient, NativeDisplayProtocol.Text,
            Encoding.UTF8.GetBytes("x"), NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
        Require(SpinWait.SpinUntil(() => fatalInputServer.Failure != null, 3000),
            "FailedClosed input diagnostic was incorrectly downgraded to a recoverable rejection.");
        using var wrongServer = new NativeDisplayServer(authority.RenderDisplay, (_, _) => true);
        wrongServer.Start(Environment.ProcessId + 1);
        using var wrongClient = new NamedPipeClientStream(".", wrongServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await wrongClient.ConnectAsync(5000, deadline.Token);
        Require(SpinWait.SpinUntil(() => wrongServer.Failure != null, 3000), "Wrong client PID was accepted.");
        Console.WriteLine("Native display pipe: authority frames, idle suppression, text input, history, size budget and process identity passed.");
    }

    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    private static NativeTerminalException CreateTerminalFailure(NativeTerminalStatus status) =>
        (NativeTerminalException)typeof(NativeTerminalException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(NativeTerminalStatus), typeof(string) }, null)!
            .Invoke(new object[] { "input", status,
                "TL-INPUT-DIAG/1\ndirection=input_encoding\nunsupported_scalar=U+1F6A7\n" });
}
