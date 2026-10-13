using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class KittySharedMemoryBrokerValidation
{
    private static readonly byte[] Original = { 255, 0, 0, 255 };
    private static readonly List<Process> Children = new();
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.Validation.exe");

    internal static async Task Run()
    {
        var session = Guid.NewGuid();
        var basePath = Path.Combine(Path.GetTempPath(), "termlink-kitty-shm-" + session.ToString("N"));
        var root = Path.Combine(basePath, "history", "broker-captures");
        var working = Path.Combine(basePath, "working");
        Directory.CreateDirectory(root); Directory.CreateDirectory(working);
        ProtectDirectory(Path.GetDirectoryName(root)!); ProtectDirectory(root);
        var job = CreateJobObject(IntPtr.Zero, null);
        Require(job != IntPtr.Zero && job != new IntPtr(-1), "Test Job creation failed.");
        var now = Environment.TickCount64;
        try
        {
            using var broker = new KittySharedMemoryBroker(session, job, () => now);
            broker.Start();
            using var coordinator = new BrokerCaptureCoordinator(root, working, session);
            coordinator.AttachSharedMemory(broker);
            using var authority = new TerminalCoreAuthority(20, 10, _ => { }, null,
                semanticProfile: TerminalSemanticProfile.Graphics);
            authority.ApplyDurable(new LocalFrame(LocalFrameType.HostGeometry, session, 1,
                new NativeTerminalHostGeometryV1(9, 18, 360, 216, 360, 216).Encode()));

            using (var child = StartChild(broker.PipeName, job, assignToJob: true, 4, "apc"))
            {
                var name = await NameFrom(child);
                var emitted = await ReadLine(child);
                Require(emitted?.StartsWith("APC:", StringComparison.Ordinal) == true,
                    "Cooperative child did not emit a Kitty t=s APC.");
                using (var otherSession = new KittySharedMemoryBroker(Guid.NewGuid(), job))
                    Require(otherSession.Capture(name, 0, 4).Failure == NativeTerminalBrokerFailure.Unavailable,
                        "A different session captured this session's map.");
                Require(broker.Capture("Local\\TermLink.Kitty." + Guid.NewGuid().ToString("N"), 0, 4).Failure ==
                    NativeTerminalBrokerFailure.Unavailable, "Unknown map name was accepted.");
                var captured = ApplyRaw(coordinator, authority, 2, Convert.FromBase64String(emitted[4..]));
                CheckCapture(root, session, captured, 2, Original, 0);
                Require(NativeTerminalRenderFrameV5Decoder.Decode(authority.RenderDisplay(0, 0).Frame).Images.Count == 1,
                    "Successful t=s capture was rejected by the native broker adapter.");
                Require(broker.Capture(name, 0, 4).Failure == NativeTerminalBrokerFailure.Unavailable,
                    "Single-use map was captured twice.");
                child.StandardInput.WriteLine("mutate");
                Require(await ReadLine(child) == "MUTATED", "Cooperative child did not modify its original map.");
                CheckCapture(root, session, captured, 2, Original, 0);
                child.StandardInput.WriteLine("exit");
                Require(child.WaitForExit(3000), "Cooperative child did not exit.");
                CheckCapture(root, session, captured, 2, Original, 0);
            }

            using (var child = StartChild(broker.PipeName, job, assignToJob: false, 4))
            {
                Require(await ReadLine(child) == "REJECTED", "Process outside the Job registered a map.");
                child.StandardInput.WriteLine("exit"); child.WaitForExit(3000);
            }
            using (var child = StartChild(broker.PipeName, job, assignToJob: true, 4))
            {
                var name = await NameFrom(child);
                Require(broker.Capture(name, 5, 1).Failure == NativeTerminalBrokerFailure.Unavailable,
                    "Out-of-range map read succeeded.");
                child.StandardInput.WriteLine("exit"); child.WaitForExit(3000);
            }
            using (var child = StartChild(broker.PipeName, job, assignToJob: true, 4))
            {
                var name = await NameFrom(child);
                now += (long)KittySharedMemoryBroker.Lifetime.TotalMilliseconds + 1000;
                Require(broker.Capture(name, 0, 4).Failure == NativeTerminalBrokerFailure.Unavailable,
                    "Expired registration remained capturable.");
                child.StandardInput.WriteLine("exit"); child.WaitForExit(3000);
                try
                {
                    using var reopened = MemoryMappedFile.OpenExisting(name);
                    throw new InvalidOperationException("Expired map survived release of all handles.");
                }
                catch (FileNotFoundException) { }
            }
            using (var child = StartChild(broker.PipeName, job, assignToJob: true,
                KittySharedMemoryBroker.MaximumObjectBytes + 1))
            {
                Require(await ReadLine(child) == "LIMIT", "Oversized allocation was accepted.");
                child.StandardInput.WriteLine("exit"); child.WaitForExit(3000);
            }
            using (var batch = StartChild(broker.PipeName, job, assignToJob: true, 1, "batch"))
            {
                Require(await ReadLine(batch) == new string('0', KittySharedMemoryBroker.MaximumObjects) + "2",
                    "Per-session shared mapping object quota did not stop the 33rd registration.");
                batch.StandardInput.WriteLine("exit"); batch.WaitForExit(3000);
            }
            now += (long)KittySharedMemoryBroker.Lifetime.TotalMilliseconds + 1000;
            using (var batch = StartChild(broker.PipeName, job, assignToJob: true,
                KittySharedMemoryBroker.MaximumObjectBytes, "batch5"))
            {
                Require(await ReadLine(batch) == "00002",
                    "Per-session shared mapping byte quota did not stop the fifth 16 MiB map.");
                batch.StandardInput.WriteLine("exit"); batch.WaitForExit(3000);
            }
            using (var partial = StartChild(broker.PipeName, job, assignToJob: true, 1, "partial"))
            {
                Require(await ReadLine(partial) == "PARTIAL", "Partial request fixture failed.");
                partial.StandardInput.WriteLine("exit"); partial.WaitForExit(3000);
            }
            now += (long)KittySharedMemoryBroker.Lifetime.TotalMilliseconds + 1000;
            using (var child = StartChild(broker.PipeName, job, assignToJob: true, 4))
            {
                var name = await NameFrom(child);
                var rejected = Apply(coordinator, authority, 3, "Local\\TermLink.Kitty." + Guid.NewGuid().ToString("N"), 42);
                CheckCapture(root, session, rejected, 3, Array.Empty<byte>(),
                    (uint)NativeTerminalBrokerFailure.Unavailable);
                var valid = Apply(coordinator, authority, 4, name, 43);
                CheckCapture(root, session, valid, 4, Original, 0);
                child.StandardInput.WriteLine("exit"); child.WaitForExit(3000);
            }
            using (var child = StartChild(broker.PipeName, job, assignToJob: true, 6, "offset"))
            {
                var name = await NameFrom(child);
                var ranged = Apply(coordinator, authority, 5, name, 44, offset: 2);
                CheckCapture(root, session, ranged, 5, new byte[] { 7, 6, 5, 4 }, 0);
                child.StandardInput.WriteLine("exit"); child.WaitForExit(3000);
            }
            await ValidateInFlightBudget(job, KittySharedMemoryBroker.MaximumObjectBytes,
                "batch3", "000", "In-flight shared mapping bytes were double allocated.");
            await ValidateInFlightBudget(job, 1, "batch31", new string('0', 31),
                "In-flight shared mapping object slot was double allocated.");
            Console.WriteLine("Kitty t=s: real Job child, durable replay, mutation, authorization, TTL, quota and disconnect passed.");
        }
        finally
        {
            foreach (var child in Children)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                child.Dispose();
            }
            Children.Clear();
            CloseHandle(job);
            if (Directory.Exists(basePath)) Directory.Delete(basePath, recursive: true);
        }
    }

    private static async Task ValidateInFlightBudget(IntPtr job, int firstBytes,
        string batchMode, string expectedBatch, string failure)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var broker = new KittySharedMemoryBroker(Guid.NewGuid(), job,
            beforeCaptureCopy: () => { entered.Set(); release.Wait(10000); });
        broker.Start();
        using var first = StartChild(broker.PipeName, job, assignToJob: true, firstBytes);
        var name = await NameFrom(first);
        using (var batch = StartChild(broker.PipeName, job, assignToJob: true, firstBytes, batchMode))
        {
            Require(await ReadLine(batch) == expectedBatch, "In-flight quota setup failed.");
            batch.StandardInput.WriteLine("exit"); batch.WaitForExit(3000);
        }
        var copiedLength = Math.Min(4, firstBytes);
        var capture = Task.Run(() => broker.Capture(name, 0, (uint)copiedLength));
        try
        {
            Require(entered.Wait(3000), "Shared map capture did not reach the controlled copy point.");
            using var extra = StartChild(broker.PipeName, job, assignToJob: true, 1);
            Require(await ReadLine(extra) == "LIMIT", failure);
            extra.StandardInput.WriteLine("exit"); extra.WaitForExit(3000);
        }
        finally { release.Set(); }
        var copied = await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Require(copied.Failure == null && copied.Bytes?.Length == copiedLength,
            "In-flight shared map did not complete its private copy.");
        CryptographicOperations.ZeroMemory(copied.Bytes);
        first.StandardInput.WriteLine("exit"); first.WaitForExit(3000);
        using var after = StartChild(broker.PipeName, job, assignToJob: true, 1);
        Require((await ReadLine(after)).StartsWith("NAME:", StringComparison.Ordinal),
            "Completed map capture did not release its reserved slot and bytes.");
        after.StandardInput.WriteLine("exit"); after.WaitForExit(3000);
    }

    internal static void Child(string pipeName, int bytes, string mode)
    {
        Console.ReadLine(); // Parent first assigns this process to the exact session Job.
        if (mode == "partial")
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(3000); pipe.Write("TLK"u8); pipe.Flush();
            Console.WriteLine("PARTIAL"); Console.Out.Flush(); Console.ReadLine(); return;
        }
        if (mode is "batch" or "batch5" or "batch3" or "batch31")
        {
            var result = new StringBuilder();
            var count = mode switch
            {
                "batch5" => 5,
                "batch3" => 3,
                "batch31" => 31,
                _ => KittySharedMemoryBroker.MaximumObjects + 1
            };
            for (var index = 0; index < count; index++)
                result.Append(Register(pipeName, bytes).Status);
            Console.WriteLine(result); Console.Out.Flush(); Console.ReadLine(); return;
        }
        var (status, name) = Register(pipeName, bytes);
        if (status != '0')
        {
            Console.WriteLine(status == '2' ? "LIMIT" : "REJECTED");
            Console.Out.Flush(); Console.ReadLine(); return;
        }
        using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        using var view = mapping.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
        if (mode == "offset") view.WriteArray(0, new byte[] { 9, 8, 7, 6, 5, 4 }, 0, 6);
        else if (bytes >= 4) view.WriteArray(0, Original, 0, 4);
        view.Flush();
        Console.WriteLine("NAME:" + name); Console.Out.Flush();
        if (mode == "apc")
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
            var apc = Encoding.ASCII.GetBytes($"\x1b_Ga=T,t=s,f=32,s=1,v=1,S=4,i=41;{encoded}\x1b\\");
            Console.WriteLine("APC:" + Convert.ToBase64String(apc)); Console.Out.Flush();
        }
        while (true)
        {
            var action = Console.ReadLine();
            if (action != "mutate") return;
            view.WriteArray(0, new byte[] { 0, 255, 0, 255 }, 0, 4); view.Flush();
            Console.WriteLine("MUTATED"); Console.Out.Flush();
        }
    }

    private static (char Status, string Name) Register(string pipeName, int bytes)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            var request = new byte[8]; "TLKS"u8.CopyTo(request);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), checked((uint)bytes));
            pipe.Write(request); pipe.Flush();
            var header = new byte[3]; pipe.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
            if (length > 128) return ('1', null);
            var name = new byte[length]; pipe.ReadExactly(name);
            return ((char)('0' + header[0]), Encoding.UTF8.GetString(name));
        }
        catch (IOException) { return ('1', null); }
    }

    private static Process StartChild(string pipeName, IntPtr job, bool assignToJob, int bytes,
        string mode = "normal")
    {
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        info.ArgumentList.Add("--kitty-shm-child"); info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(bytes.ToString()); info.ArgumentList.Add(mode);
        var child = Process.Start(info) ?? throw new InvalidOperationException("Test child could not start.");
        if (assignToJob && !AssignProcessToJobObject(job, child.Handle))
        {
            child.Kill(); child.Dispose();
            throw new InvalidOperationException("Test child could not enter the session Job.");
        }
        child.StandardInput.WriteLine("go");
        Children.Add(child);
        return child;
    }

    private static async Task<string> NameFrom(Process child)
    {
        var line = await ReadLine(child);
        Require(line != null && line.StartsWith("NAME:", StringComparison.Ordinal),
            "Authorized Job child could not register a shared map: " + line);
        return line[5..];
    }

    private static async Task<string> ReadLine(Process child) =>
        await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

    private static (LocalFrameType Type, byte[] Payload) Apply(BrokerCaptureCoordinator coordinator,
        TerminalCoreAuthority authority, ulong sequence, string name, int imageId, int offset = 0)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        var range = offset == 0 ? "S=4" : $"O={offset},S=4";
        var output = Encoding.ASCII.GetBytes($"\x1b_Ga=T,t=s,f=32,s=1,v=1,{range},i={imageId};{encoded}\x1b\\");
        return ApplyRaw(coordinator, authority, sequence, output);
    }

    private static (LocalFrameType Type, byte[] Payload) ApplyRaw(BrokerCaptureCoordinator coordinator,
        TerminalCoreAuthority authority, ulong sequence, byte[] output)
    {
        LocalFrameType type = 0; byte[] payload = null;
        coordinator.Apply(authority, sequence, output, (kind, bytes) => { type = kind; payload = bytes; });
        Require(type == LocalFrameType.BrokerOutput && payload != null, "t=s did not produce durable BrokerOutput.");
        return (type, payload);
    }

    private static void CheckCapture(string root, Guid session,
        (LocalFrameType Type, byte[] Payload) frame, ulong sequence, byte[] expected, uint outcome)
    {
        using var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
        byte[] canonical;
        if (reference.IsInline) canonical = reference.CopyInlineCaptures();
        else
        {
            using var store = new KittyCaptureStore(root, session);
            canonical = store.ReadExact(session, sequence, reference.ObjectId,
                reference.CaptureBytes, reference.CopySha256());
        }
        using var group = NativeTerminalBrokerCaptureCodecV1.Decode(canonical);
        Require(group.SourceSequence == sequence && group.Records.Count == 1 &&
            group.Records[0].Outcome == outcome &&
            group.Records[0].CopyCaptureBytes().AsSpan().SequenceEqual(expected),
            "t=s durable capture or replay changed.");
    }

    private static void ProtectDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Test Windows identity is unavailable.");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
