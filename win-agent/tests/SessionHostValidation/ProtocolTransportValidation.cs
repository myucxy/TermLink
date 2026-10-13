using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.SessionHost;

internal static class ProtocolTransportValidation
{
    private static readonly (string Name, string Sequence)[] Samples = {
        ("SIXEL", "\u001bP0;1;0q\"1;1;1;6#1;2;100;0;0#1~\u001b\\"),
        ("Kitty", "\u001b_Ga=q,i=42,s=1,v=1,f=24;AAAA\u001b\\"),
        ("DRCS", "\u001bP1;0;0;1;0;0;6;0{ @~\u001b\\"),
        ("OSC7", "\u001b]7;file:///C:/termlink-probe\u001b\\"),
        ("OSC133", "\u001b]133;A\u001b\\"),
        ("SGRStack", "\u001b[#{"),
        ("UPSS", "\u001bP1!uB\u001b\\\u001b[&u"),
        ("CharacterSet", "\u001b-B\u000e!\u000f"),
        ("C1Transmit", "\u001b G\u001b[5n\u001b F"),
        ("VT52", "\u001b[?2l\u001bH\u001bY\"%V\u001bZ\u001b<"),
        ("DOCS", "\u001b%@\u001b%G"),
        ("Macro", "\u001bP1;0;1!z4F4B\u001b\\\u001b[1*z"),
        ("Pages", "\u001b[2 P\u001b[?64l\u001b[1 P\u001b[1;1;1;1;1;2;2;2$v\u001b[?64h"),
        ("PageSize", "\u001b[96$|\u001b[25*|"),
    };

    internal static void Child()
    {
        var handle = GetStdHandle(-11);
        if (!GetConsoleMode(handle, out var mode) || !SetConsoleMode(handle, mode | 4))
            throw new InvalidOperationException("VT output unavailable in probe child.");
        Console.Write("__TRANSPORT_BEGIN__");
        foreach (var (name, sequence) in Samples) Console.Write("__" + name + "__" + sequence);
        Console.Write("__TRANSPORT_END__");
        Console.Out.Flush();
        Thread.Sleep(200);
    }

    internal static void Run()
    {
        var child = "\"" + Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.Validation.exe") + "\" --protocol-probe-child";
        using var host = new ConPtySessionHost(SessionHostOptions.Parse(new[] { "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(child)) }));
        var type = typeof(ConPtySessionHost);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coord = type.GetNestedType("COORD", flags)!;
        var size = Activator.CreateInstance(coord)!;
        coord.GetField("X")!.SetValue(size, (short)120);
        coord.GetField("Y")!.SetValue(size, (short)30);
        type.GetMethod("CreateInnerPseudoConsole", flags)!.Invoke(host, new[] { size });
        var handle = (IntPtr)type.GetField("terminalOutput", flags)!.GetValue(host)!;
        using var output = new FileStream(new SafeFileHandle(handle, false), FileAccess.Read);
        var text = new StringBuilder();
        var reader = Task.Run(() => {
            var buffer = new byte[4096];
            try { int count; while ((count = output.Read(buffer, 0, buffer.Length)) > 0)
                lock (text) text.Append(Encoding.UTF8.GetString(buffer, 0, count)); }
            catch (IOException) { }
        });
        try {
            type.GetMethod("StartChildSuspended", flags)!.Invoke(host, null);
            if (!SpinWait.SpinUntil(() => { lock (text) return text.ToString().Contains("__TRANSPORT_END__", StringComparison.Ordinal); }, 8000))
                throw new InvalidOperationException("Protocol transport probe did not finish.");
            string captured; lock (text) captured = text.ToString();
            Console.WriteLine("ConPTY protocol preservation on " + Environment.OSVersion.VersionString);
            foreach (var (name, sequence) in Samples)
                Console.WriteLine(name + ": raw_sequence_preserved=" + captured.Contains(sequence, StringComparison.Ordinal));
            Console.WriteLine("Captured synthetic output (Base64): " + Convert.ToBase64String(Encoding.UTF8.GetBytes(captured)));
        }
        finally {
            type.GetMethod("TerminateProcessTree", flags)!.Invoke(host, new object[] { (uint)0 });
            host.Dispose();
            if (!reader.Wait(5000)) throw new InvalidOperationException("Protocol probe output failed to drain.");
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
