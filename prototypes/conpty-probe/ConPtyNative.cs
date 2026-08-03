using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TermLink.Validation
{
    public sealed class ConPtyCaptureEvent
    {
        public long Sequence { get; set; }
        public long ElapsedMicroseconds { get; set; }
        public string Type { get; set; }
        public long OutputOffset { get; set; }
        public int OutputLength { get; set; }
        public short Columns { get; set; }
        public short Rows { get; set; }
        public string Reason { get; set; }
        public string State { get; set; }
        public int? ExitCode { get; set; }
    }

    public sealed class ConPtyInputStep
    {
        public string Text { get; set; }
        public int DelayBeforeMilliseconds { get; set; }
        public bool CloseInputAfter { get; set; }
        public short Columns { get; set; }
        public short Rows { get; set; }
        public string ResizeReason { get; set; }
    }

    public sealed class ConPtyProbeResult
    {
        public string CommandLine { get; set; }
        public string Output { get; set; }
        public byte[] OutputBytes { get; set; }
        public IList<ConPtyCaptureEvent> Events { get; set; }
        public int ExitCode { get; set; }
        public int ResizeHResult { get; set; }
        public bool OutputCaptured { get; set; }
        public bool InputRoundTripCaptured { get; set; }
        public bool ResizeSucceeded { get; set; }
        public bool ExitCodeCaptured { get; set; }
        public bool TimedOut { get; set; }
        public string TerminationReason { get; set; }

        public bool Passed
        {
            get
            {
                return !TimedOut &&
                       OutputCaptured &&
                       InputRoundTripCaptured &&
                       ResizeSucceeded &&
                       ExitCodeCaptured;
            }
        }
    }

    public static class ConPtyProbe
    {
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const uint STILL_ACTIVE = 259;

        private sealed class CaptureTimeline
        {
            private readonly object sync = new object();
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private readonly List<byte> output = new List<byte>();
            private readonly List<ConPtyCaptureEvent> events = new List<ConPtyCaptureEvent>();
            private long nextSequence = 1;

            public void AddOutput(byte[] buffer, int length)
            {
                lock (sync)
                {
                    var offset = output.Count;
                    for (var index = 0; index < length; index++)
                    {
                        output.Add(buffer[index]);
                    }

                    events.Add(NewEvent("output", new ConPtyCaptureEvent
                    {
                        OutputOffset = offset,
                        OutputLength = length
                    }));
                }
            }

            public void AddResize(short columns, short rows, string reason)
            {
                lock (sync)
                {
                    events.Add(NewEvent("resize", new ConPtyCaptureEvent
                    {
                        Columns = columns,
                        Rows = rows,
                        Reason = reason
                    }));
                }
            }

            public void AddLifecycle(string state, int? exitCode)
            {
                lock (sync)
                {
                    events.Add(NewEvent("lifecycle", new ConPtyCaptureEvent
                    {
                        State = state,
                        ExitCode = exitCode
                    }));
                }
            }

            public byte[] GetOutputBytes()
            {
                lock (sync)
                {
                    return output.ToArray();
                }
            }

            public IList<ConPtyCaptureEvent> GetEvents()
            {
                lock (sync)
                {
                    return new List<ConPtyCaptureEvent>(events);
                }
            }

            private ConPtyCaptureEvent NewEvent(string type, ConPtyCaptureEvent captureEvent)
            {
                captureEvent.Sequence = nextSequence++;
                captureEvent.ElapsedMicroseconds =
                    (long)(stopwatch.ElapsedTicks * (1000000.0 / Stopwatch.Frequency));
                captureEvent.Type = type;
                return captureEvent;
            }
        }

        public static ConPtyProbeResult Run(
            string commandLine,
            string input,
            string expectedOutputMarker,
            string expectedInputMarker,
            int expectedExitCode,
            int timeoutMilliseconds)
        {
            return RunScripted(
                commandLine,
                new[] { new ConPtyInputStep { Text = input, DelayBeforeMilliseconds = 0 } },
                expectedOutputMarker,
                expectedInputMarker,
                expectedExitCode,
                timeoutMilliseconds);
        }

        public static ConPtyProbeResult RunScripted(
            string commandLine,
            IList<ConPtyInputStep> inputSteps,
            string expectedOutputMarker,
            string expectedInputMarker,
            int expectedExitCode,
            int timeoutMilliseconds)
        {
            if (inputSteps == null)
            {
                throw new ArgumentNullException("inputSteps");
            }

            IntPtr pseudoConsoleInput = IntPtr.Zero;
            IntPtr terminalInput = IntPtr.Zero;
            IntPtr terminalOutput = IntPtr.Zero;
            IntPtr pseudoConsoleOutput = IntPtr.Zero;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            PROCESS_INFORMATION processInfo = new PROCESS_INFORMATION();
            var readCompleted = 0;
            Exception readError = null;
            var timeline = new CaptureTimeline();

            try
            {
                CheckWin32(CreatePipe(out pseudoConsoleInput, out terminalInput, IntPtr.Zero, 0), "CreatePipe(input)");
                CheckWin32(CreatePipe(out terminalOutput, out pseudoConsoleOutput, IntPtr.Zero, 0), "CreatePipe(output)");

                var initialSize = new COORD { X = 80, Y = 25 };
                var createHr = CreatePseudoConsole(
                    initialSize,
                    pseudoConsoleInput,
                    pseudoConsoleOutput,
                    0,
                    out pseudoConsole);
                CheckHResult(createHr, "CreatePseudoConsole");
                CloseIfValid(ref pseudoConsoleInput);
                CloseIfValid(ref pseudoConsoleOutput);

                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                if (attributeListSize == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to size process attribute list.");
                }

                attributeList = Marshal.AllocHGlobal(attributeListSize);
                CheckWin32(
                    InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize),
                    "InitializeProcThreadAttributeList");
                CheckWin32(
                    UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        new IntPtr(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE),
                        pseudoConsole,
                        new IntPtr(IntPtr.Size),
                        IntPtr.Zero,
                        IntPtr.Zero),
                    "UpdateProcThreadAttribute(PSEUDOCONSOLE)");

                var startupInfo = new STARTUPINFOEX();
                startupInfo.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                startupInfo.StartupInfo.hStdInput = IntPtr.Zero;
                startupInfo.StartupInfo.hStdOutput = IntPtr.Zero;
                startupInfo.StartupInfo.hStdError = IntPtr.Zero;
                startupInfo.lpAttributeList = attributeList;
                var processSecurity = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES))
                };
                var threadSecurity = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES))
                };

                CheckWin32(
                    CreateProcess(
                        null,
                        commandLine,
                        ref processSecurity,
                        ref threadSecurity,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT,
                        IntPtr.Zero,
                        null,
                        ref startupInfo,
                        out processInfo),
                    "CreateProcessW");

                timeline.AddLifecycle("started", null);
                timeline.AddResize(initialSize.X, initialSize.Y, "initial");

                var outputHandleForReader = terminalOutput;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        ReadAll(outputHandleForReader, timeline);
                    }
                    catch (Exception error)
                    {
                        readError = error;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref readCompleted, 1);
                    }
                });

                var resizedSize = new COORD { X = 100, Y = 30 };
                var resizeHr = ResizePseudoConsole(pseudoConsole, resizedSize);
                var allResizesSucceeded = resizeHr == 0;
                if (resizeHr == 0)
                {
                    timeline.AddResize(resizedSize.X, resizedSize.Y, "probe");
                }

                foreach (var step in inputSteps)
                {
                    if (step == null)
                    {
                        throw new ArgumentException("Input steps must not contain null entries.", "inputSteps");
                    }
                    if (step.DelayBeforeMilliseconds < 0)
                    {
                        throw new ArgumentOutOfRangeException(
                            "inputSteps",
                            "Input step delay must not be negative.");
                    }
                    if (step.DelayBeforeMilliseconds > 0)
                    {
                        Thread.Sleep(step.DelayBeforeMilliseconds);
                    }
                    if (step.Columns != 0 || step.Rows != 0)
                    {
                        if (step.Columns < 2 || step.Columns > 1000 || step.Rows < 2 || step.Rows > 1000)
                        {
                            throw new ArgumentOutOfRangeException(
                                "inputSteps",
                                "Resize steps must specify both columns and rows from 2 through 1000.");
                        }
                        var stepSize = new COORD { X = step.Columns, Y = step.Rows };
                        resizeHr = ResizePseudoConsole(pseudoConsole, stepSize);
                        allResizesSucceeded &= resizeHr == 0;
                        if (resizeHr == 0)
                        {
                            timeline.AddResize(
                                stepSize.X,
                                stepSize.Y,
                                string.IsNullOrEmpty(step.ResizeReason) ? "scripted" : step.ResizeReason);
                        }
                    }
                    if (!string.IsNullOrEmpty(step.Text))
                    {
                        WriteAll(terminalInput, Encoding.UTF8.GetBytes(step.Text));
                    }
                    if (step.CloseInputAfter)
                    {
                        CloseIfValid(ref terminalInput);
                    }
                }

                var timedOut = false;
                var waitResult = WaitForSingleObject(processInfo.hProcess, (uint)timeoutMilliseconds);
                if (waitResult == WAIT_TIMEOUT)
                {
                    CheckWin32(TerminateProcess(processInfo.hProcess, 124), "TerminateProcess(timeout)");
                    waitResult = WaitForSingleObject(processInfo.hProcess, 5000);
                    timedOut = true;
                }
                if (waitResult != WAIT_OBJECT_0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed.");
                }

                uint exitCode;
                CheckWin32(GetExitCodeProcess(processInfo.hProcess, out exitCode), "GetExitCodeProcess");
                if (exitCode == STILL_ACTIVE)
                {
                    throw new InvalidOperationException("Shell is unexpectedly still active.");
                }

                CloseHandle(terminalInput);
                terminalInput = IntPtr.Zero;

                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;

                var drainDeadline = Stopwatch.StartNew();
                while (Interlocked.CompareExchange(ref readCompleted, 0, 0) == 0 &&
                    drainDeadline.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(10);
                }
                if (Interlocked.CompareExchange(ref readCompleted, 0, 0) == 0)
                {
                    throw new TimeoutException("ConPTY output pipe did not close after the Shell exited.");
                }
                if (readError != null)
                {
                    throw new InvalidOperationException("ConPTY output reader failed.", readError);
                }

                timeline.AddLifecycle("ended", unchecked((int)exitCode));
                var outputBytes = timeline.GetOutputBytes();
                var output = Encoding.UTF8.GetString(outputBytes);
                return new ConPtyProbeResult
                {
                    CommandLine = commandLine,
                    Output = output,
                    OutputBytes = outputBytes,
                    Events = timeline.GetEvents(),
                    ExitCode = unchecked((int)exitCode),
                    ResizeHResult = resizeHr,
                    OutputCaptured = output.Contains(expectedOutputMarker),
                    InputRoundTripCaptured = output.Contains(expectedInputMarker),
                    ResizeSucceeded = allResizesSucceeded,
                    ExitCodeCaptured = unchecked((int)exitCode) == expectedExitCode,
                    TimedOut = timedOut,
                    TerminationReason = timedOut ? "timeout" : "exited"
                };
            }
            finally
            {
                if (processInfo.hThread != IntPtr.Zero)
                {
                    CheckWin32(CloseHandle(processInfo.hThread), "CloseHandle(process thread)");
                    processInfo.hThread = IntPtr.Zero;
                }
                if (processInfo.hProcess != IntPtr.Zero)
                {
                    CheckWin32(CloseHandle(processInfo.hProcess), "CloseHandle(process)");
                    processInfo.hProcess = IntPtr.Zero;
                }
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                CloseIfValid(ref terminalInput);
                CloseIfValid(ref pseudoConsoleInput);
                CloseIfValid(ref pseudoConsoleOutput);
                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsole);
                }
                CloseIfValid(ref terminalOutput);
            }
        }

        private static void ReadAll(IntPtr handle, CaptureTimeline timeline)
        {
            var buffer = new byte[4096];
            while (true)
            {
                uint bytesRead;
                var ok = ReadFile(handle, buffer, (uint)buffer.Length, out bytesRead, IntPtr.Zero);
                if (!ok || bytesRead == 0)
                {
                    break;
                }
                timeline.AddOutput(buffer, checked((int)bytesRead));
            }
        }

        private static void WriteAll(IntPtr handle, byte[] data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var remaining = data.Length - offset;
                var chunk = new byte[remaining];
                Buffer.BlockCopy(data, offset, chunk, 0, remaining);
                uint bytesWritten;
                CheckWin32(
                    WriteFile(handle, chunk, (uint)chunk.Length, out bytesWritten, IntPtr.Zero),
                    "WriteFile(ConPTY input)");
                if (bytesWritten == 0)
                {
                    throw new InvalidOperationException("WriteFile completed without writing data.");
                }
                offset += checked((int)bytesWritten);
            }
        }

        private static void CheckWin32(bool succeeded, string operation)
        {
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");
            }
        }

        private static void CheckHResult(int hresult, string operation)
        {
            if (hresult != 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }
        }

        private static void CloseIfValid(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            IntPtr lpPipeAttributes,
            uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            IntPtr hFile,
            [Out] byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            COORD size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            ref SECURITY_ATTRIBUTES lpProcessAttributes,
            ref SECURITY_ATTRIBUTES lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    }
}
