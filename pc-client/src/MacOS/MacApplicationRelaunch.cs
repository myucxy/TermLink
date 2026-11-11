using System.Diagnostics;

namespace TermLink.Platform.MacOS;

public static class MacApplicationRelaunch
{
    public static void AfterCurrentProcessExits()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        // Fixed program, positional arguments, bounded wait; no package-provided
        // commands or persisted helper script runs after installation.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = "/" };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("i=0; while /bin/kill -0 \"$1\" 2>/dev/null; do i=$((i+1)); [ \"$i\" -ge 150 ] && exit 1; /bin/sleep 0.2; done; exec /usr/bin/open -- \"$2\"");
        start.ArgumentList.Add("termlink-relaunch");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("/Applications/TermLink.app");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("UPDATE_RELAUNCH_FAILED");
    }
}
