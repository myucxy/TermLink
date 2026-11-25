// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TermLink.Agent.Connection;

internal static class RelayHttpDeadlineValidation
{
    public static async Task RunAsync()
    {
        await DeadlineCoversStalledAndTricklingBodiesAsync();
        await CallerCancellationStaysCancellationAsync();
        await SizeAndDepthLimitsRemainAsync();
    }

    private static async Task DeadlineCoversStalledAndTricklingBodiesAsync()
    {
        using var lifetime = new CancellationTokenSource();
        var stalled = new SlowBody(trickle: false);
        var trickling = new SlowBody(trickle: true);
        using var stalledClient = new HttpClient(new BodyHandler(stalled)) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var tricklingClient = new HttpClient(new BodyHandler(trickling)) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var stalledResponse = await stalledClient.GetAsync("https://fixture.invalid/grants", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        using var tricklingResponse = await tricklingClient.GetAsync("https://fixture.invalid/devices", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        Require(stalledResponse.StatusCode == HttpStatusCode.OK && tricklingResponse.StatusCode == HttpStatusCode.OK,
            "The body deadline fixture did not receive headers.");
        var clock = Stopwatch.StartNew();
        var reads = new[] { RelayHttp.ReadJsonAsync(stalledResponse, lifetime.Token), RelayHttp.ReadJsonAsync(tricklingResponse, lifetime.Token) };
        try
        {
            await Task.WhenAll(stalled.Started.Task, trickling.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(350);
            Require(reads.All(read => !read.IsCompleted), "HttpClient's 100ms timeout unexpectedly covered post-header body reads.");
            await Task.WhenAll(reads.Select(ExpectBodyDeadlineAsync));
            Require(clock.Elapsed >= TimeSpan.FromSeconds(19) && clock.Elapsed < TimeSpan.FromSeconds(30),
                "The fixed whole-body deadline did not expire at 20 seconds.");
            Require(trickling.BytesRead > 1, "The trickle fixture did not exercise continued body progress.");
            Require(stalled.Disposed && trickling.Disposed, "A timed-out body stream was retained.");
            Console.WriteLine($"PASS HTTP body deadline: stalled and trickling responses stopped after {clock.ElapsedMilliseconds}ms; streams disposed; no network");
        }
        finally
        {
            lifetime.Cancel();
            foreach (var read in reads) { try { using var ignored = await read; } catch (OperationCanceledException) { } catch (RelayOperationException) { } }
        }
    }

    private static async Task ExpectBodyDeadlineAsync(Task<JsonDocument> read)
    {
        try { using var ignored = await read.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (RelayOperationException error) when (error.Code == "RELAY_TIMEOUT") { return; }
        throw new InvalidOperationException("A stalled body returned without RELAY_TIMEOUT.");
    }

    private static async Task CallerCancellationStaysCancellationAsync()
    {
        using var caller = new CancellationTokenSource();
        var body = new SlowBody(trickle: false);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        var read = RelayHttp.ReadJsonAsync(response, caller.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var clock = Stopwatch.StartNew();
        caller.Cancel();
        try { using var ignored = await read.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Caller cancellation was ignored."); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        Require(body.Disposed && clock.Elapsed < TimeSpan.FromSeconds(2), "Caller cancellation retained the response body.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var unopened = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowBody(trickle: false)) };
        try { using var ignored = await RelayHttp.ReadJsonAsync(unopened, cancelled.Token); throw new InvalidOperationException("Pre-cancelled caller was accepted."); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }

    private static async Task SizeAndDepthLimitsRemainAsync()
    {
        using var declared = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65537]) };
        await ExpectOversizeAsync(declared);
        var streamed = new UnseekableBody(new byte[65537]);
        using var unknownLength = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(streamed) };
        Require(unknownLength.Content.Headers.ContentLength is null, "The streamed-size fixture accidentally declared Content-Length.");
        await ExpectOversizeAsync(unknownLength);
        Require(streamed.Disposed, "Oversized streamed body was retained.");
        var exactJson = "{\"v\":\"" + new string('x', 65528) + "\"}";
        using var exact = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(exactJson) };
        using var parsed = await RelayHttp.ReadJsonAsync(exact, CancellationToken.None);
        Require(parsed.RootElement.GetProperty("v").GetString()!.Length == 65528, "The exact 64KiB boundary was narrowed.");
        foreach (var depth in new[] { 24, 25 })
        {
            var bytes = Encoding.UTF8.GetBytes(new string('[', depth) + "0" + new string(']', depth));
            var body = new UnseekableBody(bytes);
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            try
            {
                using var document = await RelayHttp.ReadJsonAsync(response, CancellationToken.None);
                Require(depth == 24, "JSON deeper than MaxDepth24 was admitted.");
            }
            catch (JsonException) when (depth == 25) { }
            Require(body.Disposed, "A completed or malformed JSON body was retained.");
        }
    }

    private static async Task ExpectOversizeAsync(HttpResponseMessage response)
    {
        try { using var ignored = await RelayHttp.ReadJsonAsync(response, CancellationToken.None); }
        catch (RelayOperationException error) when (error.Code == "SERVER_RESPONSE_TOO_LARGE") { return; }
        throw new InvalidOperationException("HTTP JSON size limit was not preserved.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    private abstract class ReadBody : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class SlowBody(bool trickle) : ReadBody
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Task.Delay(trickle ? 200 : Timeout.Infinite, token);
            buffer.Span[0] = (byte)' '; BytesRead++;
            return 1;
        }
    }

    private sealed class UnseekableBody(byte[] bytes) : ReadBody
    {
        private int offset;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var size = Math.Min(buffer.Length, bytes.Length - offset);
            bytes.AsMemory(offset, size).CopyTo(buffer); offset += size;
            return ValueTask.FromResult(size);
        }
    }
}
