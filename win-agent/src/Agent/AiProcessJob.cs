using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TermLink.Agent;

// Same Windows Job contract as SessionHost: Agent crash cannot orphan its AI workers.
internal sealed class AiProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly Process portableProcess;
    internal AiProcessJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) { portableProcess = process; return; }
        handle = CreateJobObject(IntPtr.Zero, null);
        var limit = new ExtendedLimit { Basic = new BasicLimit { Flags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>()) ||
            !AssignProcessToJobObject(handle, process.Handle)) {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error);
        }
    }
    public void Dispose()
    {
        if (handle != null) handle.Dispose();
        else if (portableProcess != null) {
            try { if (!portableProcess.HasExited) { portableProcess.Kill(true); if (!portableProcess.WaitForExit(5000)) throw new IOException("AI_PROCESS_EXIT_TIMEOUT"); } }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Counters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit {
        public BasicLimit Basic; public Counters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
