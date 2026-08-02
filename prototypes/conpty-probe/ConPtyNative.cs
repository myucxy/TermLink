using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TermLink.Validation
{
    public sealed class ConPtyProbeResult
    {
        public string CommandLine { get; set; }
        public string Output { get; set; }
        public int ExitCode { get; set; }
        public int ResizeHResult { get; set; }
        public bool OutputCaptured { get; set; }
        public bool InputRoundTripCaptured { get; set; }
        public bool ResizeSucceeded { get; set; }
        public bool ExitCodeCaptured { get; set; }

        public bool Passed
        {
            get
            {
                return OutputCaptured &&
                       InputRoundTripCaptured &&
                       ResizeSucceeded &&
                       ExitCodeCaptured;
            }
        }
    }

    public static class ConPtyProbe
    {
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const uint STILL_ACTIVE = 259;

        public static ConPtyProbeResult Run(
            string commandLine,
            string input,
            string expectedOutputMarker,
            string expectedInputMarker,
            int expectedExitCode,
            int timeoutMilliseconds)
        {
            IntPtr pseudoConsoleInput = IntPtr.Zero;
            IntPtr terminalInput = IntPtr.Zero;
            IntPtr terminalOutput = IntPtr.Zero;
            IntPtr pseudoConsoleOutput = IntPtr.Zero;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            PROCESS_INFORMATION processInfo = new PROCESS_INFORMATION();
            Task<byte[]> readTask = null;

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
                startupInfo.lpAttributeList = attributeList;
                var processSecurity = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES))
                };
                var threadSecurity = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES))
                };

                var previousStandardInput = GetStdHandle(-10);
                var previousStandardOutput = GetStdHandle(-11);
                var previousStandardError = GetStdHandle(-12);
                try
                {
                    SetStdHandle(-10, IntPtr.Zero);
                    SetStdHandle(-11, IntPtr.Zero);
                    SetStdHandle(-12, IntPtr.Zero);
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
                }
                finally
                {
                    SetStdHandle(-10, previousStandardInput);
                    SetStdHandle(-11, previousStandardOutput);
                    SetStdHandle(-12, previousStandardError);
                }

                var outputHandleForReader = terminalOutput;
                readTask = Task.Run(() => ReadAll(outputHandleForReader));

                var resizeHr = ResizePseudoConsole(pseudoConsole, new COORD { X = 100, Y = 30 });

                WriteAll(terminalInput, Encoding.UTF8.GetBytes(input));

                var waitResult = WaitForSingleObject(processInfo.hProcess, (uint)timeoutMilliseconds);
                if (waitResult == WAIT_TIMEOUT)
                {
                    TerminateProcess(processInfo.hProcess, 124);
                    WaitForSingleObject(processInfo.hProcess, 5000);
                    throw new TimeoutException("Shell did not exit within " + timeoutMilliseconds + " ms.");
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
                CloseHandle(pseudoConsoleInput);
                pseudoConsoleInput = IntPtr.Zero;
                CloseHandle(pseudoConsoleOutput);
                pseudoConsoleOutput = IntPtr.Zero;

                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;

                if (!readTask.Wait(5000))
                {
                    throw new TimeoutException("ConPTY output pipe did not close after the Shell exited.");
                }

                var output = Encoding.UTF8.GetString(readTask.Result);
                return new ConPtyProbeResult
                {
                    CommandLine = commandLine,
                    Output = output,
                    ExitCode = unchecked((int)exitCode),
                    ResizeHResult = resizeHr,
                    OutputCaptured = output.Contains(expectedOutputMarker),
                    InputRoundTripCaptured = output.Contains(expectedInputMarker),
                    ResizeSucceeded = resizeHr == 0,
                    ExitCodeCaptured = unchecked((int)exitCode) == expectedExitCode
                };
            }
            finally
            {
                if (processInfo.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInfo.hThread);
                }
                if (processInfo.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInfo.hProcess);
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

        private static byte[] ReadAll(IntPtr handle)
        {
            var collected = new List<byte>();
            var buffer = new byte[4096];
            while (true)
            {
                uint bytesRead;
                var ok = ReadFile(handle, buffer, (uint)buffer.Length, out bytesRead, IntPtr.Zero);
                if (!ok || bytesRead == 0)
                {
                    break;
                }
                for (var index = 0; index < bytesRead; index++)
                {
                    collected.Add(buffer[index]);
                }
            }
            return collected.ToArray();
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);
    }
}
