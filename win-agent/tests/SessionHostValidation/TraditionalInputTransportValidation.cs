using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.SessionHost;

internal static class TraditionalInputTransportValidation
{
    internal static void Child()
    {
        var input = GetStdHandle(-10); var output = GetStdHandle(-11);
        GetConsoleMode(input, out var im); GetConsoleMode(output, out var om);
        SetConsoleMode(output, om | 4); SetConsoleMode(input, (im & ~(1U | 2U | 4U)) | 512);
        SetConsoleCP(65001);
        Console.Write("__C1_READY__"); Console.Out.Flush();
        using var stream = Console.OpenStandardInput();
        var bytes = new List<byte>();
        while (bytes.Count < 512) { var value = stream.ReadByte(); if (value < 0 || value == 'X') break; bytes.Add((byte)value); }
        Console.Write("__C1_BYTES__:" + Convert.ToHexString(bytes.ToArray()) + ":END"); Console.Out.Flush();
        SetConsoleMode(input, im & ~(1U | 2U | 4U | 512U));
        Console.Write("__WIN32_READY__"); Console.Out.Flush();
        var chars = new List<ushort>(); var records = new Record[32];
        while (chars.Count < 256) {
            if (!ReadConsoleInputW(input, records, 32, out var read)) throw new IOException("ReadConsoleInput failed");
            for (var i = 0; i < read; i++) {
                if (records[i].Type != 1 || records[i].Down == 0) continue;
                if (records[i].Character == 'q') { Console.Write("__WIN32_CHARS__:" + string.Join(",", chars) + ":END__DONE__"); Console.Out.Flush(); return; }
                chars.Add(records[i].Character);
            }
        }
    }

    internal static void Run()
    {
        ValidateUtf8Boundaries();
        RunScenario("raw C1", new byte[] { 0x9b });
        RunScenario("UTF8 C1", new byte[] { 0xc2, 0x9b });
        RunScenario("seven bit", new byte[] { 0x1b, 0x5b });
        RunScenario("SessionHost transcoder", new byte[] { 0x9b }, transcode: true);
    }

    private static void ValidateUtf8Boundaries()
    {
        var text = Encoding.UTF8.GetBytes("Лܐݝ中文😀");
        var source = text.Concat(new byte[] { 0x9b, (byte)'0', (byte)'n', 0x90, 0x9c }).Concat(text).ToArray();
        var expected = text.Concat(new byte[] { 0xc2, 0x9b, (byte)'0', (byte)'n', 0xc2, 0x90, 0xc2, 0x9c }).Concat(text).ToArray();
        for (var split = 0; split <= source.Length; split++) {
            var transcoder = new ConPtyInputTranscoder();
            using var actual = new MemoryStream();
            foreach (var part in new[] { source.AsSpan(0, split).ToArray(), source.AsSpan(split).ToArray() }) {
                var bytes = transcoder.Convert(part, part.Length, out var length); actual.Write(bytes, 0, length);
            }
            if (!actual.ToArray().SequenceEqual(expected)) throw new Exception("C1 transcoder changed split UTF8 text");
        }
        var plain = new ConPtyInputTranscoder();
        if (!ReferenceEquals(text, plain.Convert(text, text.Length, out _))) throw new Exception("UTF8-only input allocated an unnecessary copy");
    }

    private static void RunScenario(string label, byte[] csi, bool transcode = false)
    {
        var child = "\"" + Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.Validation.exe") + "\" --traditional-input-child";
        using var host = new ConPtySessionHost(SessionHostOptions.Parse(new[] { "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(child)) }));
        var type = typeof(ConPtySessionHost); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coord = type.GetNestedType("COORD", flags)!; var size = Activator.CreateInstance(coord)!;
        coord.GetField("X")!.SetValue(size, (short)120); coord.GetField("Y")!.SetValue(size, (short)30);
        type.GetMethod("CreateInnerPseudoConsole", flags)!.Invoke(host, new[] { size });
        using var input = new FileStream(new SafeFileHandle((IntPtr)type.GetField("terminalInput", flags)!.GetValue(host)!, false), FileAccess.Write);
        using var output = new FileStream(new SafeFileHandle((IntPtr)type.GetField("terminalOutput", flags)!.GetValue(host)!, false), FileAccess.Read);
        var text = new StringBuilder();
        var reader = Task.Run(() => { var buffer = new byte[4096]; try { int read; while ((read = output.Read(buffer)) > 0) lock (text) text.Append(Encoding.UTF8.GetString(buffer, 0, read)); } catch (IOException) { } });
        void Wait(string marker) { if (!SpinWait.SpinUntil(() => { lock (text) return text.ToString().Contains(marker); }, 7000)) throw new IOException("Traditional input probe timeout: " + text); }
        void Write(byte[] bytes) {
            var length = bytes.Length;
            if (transcode) {
                if (!(bool)type.GetMethod("WriteInput", flags)!.Invoke(host, new object[] { bytes, length })!) throw new IOException("SessionHost input failed");
            } else { input.Write(bytes, 0, length); input.Flush(); }
        }
        try {
            type.GetMethod("StartChildSuspended", flags)!.Invoke(host, null);
            Wait("__C1_READY__"); Write(csi.Concat(new byte[] { 0x30, 0x6e, 0x58 }).ToArray());
            Wait("__WIN32_READY__");
            Write(csi.Concat(Encoding.ASCII.GetBytes("65;30;97;1;0;1_\u001b[81;16;113;1;0;1_")).ToArray());
            Wait("__DONE__");
            lock (text) {
                var captured = text.ToString();
                if (transcode && (!captured.Contains("__C1_BYTES__:C29B306E:END") || !captured.Contains("__WIN32_CHARS__:97:END")))
                    throw new Exception("C1 input was corrupted by the real ConPTY");
                Console.WriteLine("Traditional ConPTY input " + label + ": " + captured.Substring(captured.IndexOf("__C1_READY__", StringComparison.Ordinal)));
            }
        } finally { type.GetMethod("TerminateProcessTree", flags)!.Invoke(host, new object[] { (uint)0 }); host.Dispose(); if (!reader.Wait(5000)) throw new IOException("Probe failed to drain"); }
    }
    [StructLayout(LayoutKind.Explicit, Size = 20)] private struct Record {
        [FieldOffset(0)] public ushort Type; [FieldOffset(4)] public int Down; [FieldOffset(14)] public ushort Character;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCP(uint codePage);
    [DllImport("kernel32.dll")] private static extern bool ReadConsoleInputW(IntPtr handle, [Out] Record[] records, uint length, out uint read);
}
