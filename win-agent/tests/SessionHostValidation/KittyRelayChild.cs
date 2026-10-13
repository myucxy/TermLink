using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

internal static class KittyRelayChild
{
    private const string TriggerName = "kitty-relay.trigger";
    private const string ImageName = "kitty-relay-source.rgba";
    private const string TemporaryName = "kitty-relay-temp.rgba";
    private const int ImageBytes = 16 * 16 * 4;
    private static readonly string[] Phases = { "f", "mutate-f", "t", "s", "mutate-s" };

    internal static void Child(string workingDirectory)
    {
        workingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(workingDirectory) ||
            (File.GetAttributes(workingDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Kitty relay fixture requires a real working directory.");
        var trigger = Path.Combine(workingDirectory, TriggerName);
        var source = Path.Combine(workingDirectory, ImageName);
        var pixels = new byte[ImageBytes];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255; pixels[index + 1] = 0;
            pixels[index + 2] = 255; pixels[index + 3] = 255;
        }
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        using var output = Console.OpenStandardOutput();
        using var clock = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        MemoryMappedFile mapping = null;
        try
        {
            Write(output, "\x1b[2J\x1b[2;1H__KITTY_RELAY_READY__");
            var next = 0;
            while (!clock.IsCancellationRequested)
            {
                if (!File.Exists(trigger)) { Thread.Sleep(25); continue; }
                if (new FileInfo(trigger).Length > 64)
                    throw new InvalidDataException("Kitty relay trigger exceeds its limit.");
                string phase;
                try { phase = File.ReadAllText(trigger, Encoding.ASCII).Trim().ToLowerInvariant(); }
                catch (IOException) { Thread.Sleep(25); continue; }
                if (phase.Length == 0) { Thread.Sleep(25); continue; }
                File.Delete(trigger);
                if (phase == "exit") break;
                if (next >= Phases.Length || phase != Phases[next])
                    throw new InvalidDataException("Kitty relay phase was out of order.");
                var medium = phase is "mutate-f" ? "f" : phase is "mutate-s" ? "s" : phase;
                var temporaryPath = (string)null;
                var registration = "not_applicable";
                var deleted = false;
                switch (phase)
                {
                    case "f":
                        File.WriteAllBytes(source, pixels);
                        EmitImage(output, "f", ImageName, 101);
                        break;
                    case "mutate-f":
                        File.WriteAllBytes(source, new byte[ImageBytes]);
                        break;
                    case "t":
                        temporaryPath = HostTemporaryPath();
                        File.WriteAllBytes(temporaryPath, pixels);
                        EmitImage(output, "t", temporaryPath, 102);
                        deleted = SpinWait.SpinUntil(() => !File.Exists(temporaryPath), 15000);
                        if (!deleted) throw new IOException("Host did not delete captured t=t file after durable apply.");
                        break;
                    case "s":
                        var pipeName = Environment.GetEnvironmentVariable("TERMLINK_KITTY_SHM_PIPE");
                        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
                            pipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
                            throw new InvalidDataException("Host shared-memory broker pipe is unavailable.");
                        var name = Register(pipeName, ImageBytes);
                        registration = "registered";
                        mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
                        using (var view = mapping.CreateViewAccessor(0, ImageBytes, MemoryMappedFileAccess.ReadWrite))
                        { view.WriteArray(0, pixels, 0, pixels.Length); view.Flush(); }
                        EmitImage(output, "s", name, 103);
                        break;
                    case "mutate-s":
                        using (var view = mapping!.CreateViewAccessor(0, ImageBytes, MemoryMappedFileAccess.ReadWrite))
                        { view.WriteArray(0, new byte[ImageBytes], 0, ImageBytes); view.Flush(); }
                        mapping.Dispose(); mapping = null;
                        break;
                }
                var result = new
                {
                    phase, medium, width = 16, height = 16, payload_sha256 = expectedSha256,
                    source_exists = File.Exists(source),
                    temporary_exists = temporaryPath != null && File.Exists(temporaryPath),
                    temporary_deleted_after_capture = deleted,
                    shared_registration = registration
                };
                File.WriteAllText(Path.Combine(workingDirectory, "kitty-relay-" + phase + ".json"),
                    JsonSerializer.Serialize(result), Encoding.UTF8);
                Write(output, "\x1b[2;1H__KITTY_RELAY_" + phase.ToUpperInvariant() + "_DONE__");
                next++;
                clock.CancelAfter(TimeSpan.FromMinutes(3));
            }
        }
        finally { mapping?.Dispose(); }
    }

    private static string HostTemporaryPath()
    {
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("TEMP") ?? "");
        var tmp = Path.GetFullPath(Environment.GetEnvironmentVariable("TMP") ?? "");
        if (!string.Equals(temp, tmp, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(temp), "N", out _) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(temp)), "broker-temp", StringComparison.Ordinal) ||
            !Directory.Exists(temp) || (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Host dedicated broker TEMP was not injected.");
        return Path.Combine(temp, TemporaryName);
    }

    private static string Register(string pipeName, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
        var request = new byte[8];
        "TLKS"u8.CopyTo(request);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), checked((uint)count));
        pipe.WriteAsync(request.AsMemory(), deadline.Token).GetAwaiter().GetResult();
        pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        var header = new byte[3]; pipe.ReadExactlyAsync(header.AsMemory(), deadline.Token).GetAwaiter().GetResult();
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        if (header[0] != 0 || length == 0 || length > 128)
            throw new IOException("Host shared-memory registration was rejected.");
        var encoded = new byte[length]; pipe.ReadExactlyAsync(encoded.AsMemory(), deadline.Token).GetAwaiter().GetResult();
        var name = new UTF8Encoding(false, true).GetString(encoded);
        const string prefix = "Local\\TermLink.Kitty.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException("Host returned an invalid shared-memory registration.");
        return name;
    }

    private static void EmitImage(Stream output, string medium, string reference, int id)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(reference));
        Write(output, $"\x1b[2J\x1b[H\x1b_Ga=T,t={medium},f=32,s=16,v=16,S={ImageBytes},i={id},q=2;{encoded}\x1b\\");
    }

    private static void Write(Stream output, string value)
    {
        output.Write(Encoding.ASCII.GetBytes(value));
        output.Flush();
    }
}
