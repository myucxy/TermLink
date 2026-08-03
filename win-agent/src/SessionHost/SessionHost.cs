using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;

namespace TermLink.SessionHost
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                var options = SessionHostOptions.Parse(args);
                using (var host = new ConPtySessionHost(options))
                {
                    return host.Run();
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("TermLink Session Host failed: " + error.Message);
                return 125;
            }
        }
    }

    internal sealed class SessionHostOptions
    {
        public string CommandLine { get; private set; }
        public int TimeoutMilliseconds { get; private set; }
        public short FallbackColumns { get; private set; }
        public short FallbackRows { get; private set; }
        public bool Diagnostics { get; private set; }
        public string AgentPipeName { get; private set; }
        public Guid SessionId { get; private set; }
        public byte[] HandshakeToken { get; private set; }
        public int AgentBufferBytes { get; private set; }
        public bool UseAgentPipe { get { return !string.IsNullOrEmpty(AgentPipeName); } }

        public static SessionHostOptions Parse(string[] args)
        {
            string encodedCommand = null;
            var timeout = Timeout.Infinite;
            short columns = 80;
            short rows = 25;
            var diagnostics = false;
            string agentPipeName = null;
            string sessionIdText = null;
            string handshakeTokenText = null;
            var agentBufferBytes = 4 * 1024 * 1024;
            var hasTestAgentBuffer = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--command-base64":
                        encodedCommand = RequiredValue(args, ref index, "--command-base64");
                        break;
                    case "--timeout-ms":
                        timeout = ParseInteger(RequiredValue(args, ref index, "--timeout-ms"), 1, 86400000, "timeout");
                        break;
                    case "--columns":
                        columns = checked((short)ParseInteger(RequiredValue(args, ref index, "--columns"), 2, 1000, "columns"));
                        break;
                    case "--rows":
                        rows = checked((short)ParseInteger(RequiredValue(args, ref index, "--rows"), 2, 1000, "rows"));
                        break;
                    case "--diagnostics":
                        diagnostics = true;
                        break;
                    case "--agent-pipe":
                        agentPipeName = RequiredValue(args, ref index, "--agent-pipe");
                        break;
                    case "--session-id":
                        sessionIdText = RequiredValue(args, ref index, "--session-id");
                        break;
                    case "--handshake-token":
                        handshakeTokenText = RequiredValue(args, ref index, "--handshake-token");
                        break;
                    case "--test-agent-buffer-bytes":
                        agentBufferBytes = ParseInteger(
                            RequiredValue(args, ref index, "--test-agent-buffer-bytes"),
                            64 * 1024,
                            4 * 1024 * 1024,
                            "test Agent buffer bytes");
                        hasTestAgentBuffer = true;
                        break;
                    default:
                        throw new ArgumentException("Unknown argument: " + args[index]);
                }
            }

            if (string.IsNullOrWhiteSpace(encodedCommand))
            {
                throw new ArgumentException("--command-base64 is required.");
            }

            string commandLine;
            try
            {
                commandLine = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCommand));
            }
            catch (FormatException error)
            {
                throw new ArgumentException("--command-base64 is not valid Base64.", error);
            }
            if (string.IsNullOrWhiteSpace(commandLine) || commandLine.IndexOf('\0') >= 0 || commandLine.Length > 32766)
            {
                throw new ArgumentException("Decoded command line is empty or exceeds the Windows limit.");
            }

            var hasAnyAgentOption = !string.IsNullOrEmpty(agentPipeName) ||
                !string.IsNullOrEmpty(sessionIdText) || !string.IsNullOrEmpty(handshakeTokenText) ||
                hasTestAgentBuffer;
            Guid sessionId = Guid.Empty;
            byte[] handshakeToken = null;
            if (hasAnyAgentOption)
            {
                if (string.IsNullOrWhiteSpace(agentPipeName) || agentPipeName.Length > 200 ||
                    agentPipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
                {
                    throw new ArgumentException("--agent-pipe is invalid.");
                }
                if (!Guid.TryParse(sessionIdText, out sessionId) || sessionId == Guid.Empty)
                {
                    throw new ArgumentException("--session-id must be a non-empty GUID.");
                }
                if (string.IsNullOrWhiteSpace(handshakeTokenText))
                {
                    throw new ArgumentException("--handshake-token is required with --agent-pipe.");
                }
                handshakeToken = LocalPayloadCodec.ParseToken(handshakeTokenText);
            }
            if (hasTestAgentBuffer && !diagnostics)
            {
                throw new ArgumentException("--test-agent-buffer-bytes requires --diagnostics.");
            }

            return new SessionHostOptions
            {
                CommandLine = commandLine,
                TimeoutMilliseconds = timeout,
                FallbackColumns = columns,
                FallbackRows = rows,
                Diagnostics = diagnostics,
                AgentPipeName = agentPipeName,
                SessionId = sessionId,
                HandshakeToken = handshakeToken,
                AgentBufferBytes = agentBufferBytes
            };
        }

        private static string RequiredValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrEmpty(args[index]))
            {
                throw new ArgumentException(option + " requires a value.");
            }
            return args[index];
        }

        private static int ParseInteger(string value, int minimum, int maximum, string label)
        {
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < minimum || parsed > maximum)
            {
                throw new ArgumentException(label + " must be from " + minimum + " to " + maximum + ".");
            }
            return parsed;
        }
    }

    internal sealed class ConPtySessionHost : IDisposable
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateSuspended = 0x00000004;
        private const int StartfUseStdHandles = 0x00000100;
        private const uint ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private const uint StillActive = 259;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int ErrorBrokenPipe = 109;
        private const int ErrorNoData = 232;
        private const int ErrorOperationAborted = 995;
        private const uint EnableProcessedInput = 0x0001;
        private const uint EnableLineInput = 0x0002;
        private const uint EnableEchoInput = 0x0004;
        private const uint EnableWindowInput = 0x0008;
        private const uint EnableMouseInput = 0x0010;
        private const uint EnableQuickEditMode = 0x0040;
        private const uint EnableExtendedFlags = 0x0080;
        private const uint ThreadTerminate = 0x0001;
        private const ushort KeyEvent = 0x0001;
        private const uint RightAltPressed = 0x0001;
        private const uint LeftAltPressed = 0x0002;
        private const uint RightCtrlPressed = 0x0004;
        private const uint LeftCtrlPressed = 0x0008;
        private const uint ShiftPressed = 0x0010;
        private const int OutputBatchBytes = 64 * 1024;
        private const int OutputCoalesceMilliseconds = 2;

        private readonly SessionHostOptions options;
        private readonly object inputSync = new object();
        private readonly object terminalWriteSync = new object();
        private readonly object resizeSync = new object();
        private readonly ManualResetEventSlim inputThreadReady = new ManualResetEventSlim(false);
        private IntPtr pseudoConsoleInput = IntPtr.Zero;
        private IntPtr terminalInput = IntPtr.Zero;
        private IntPtr terminalOutput = IntPtr.Zero;
        private IntPtr pseudoConsoleOutput = IntPtr.Zero;
        private IntPtr pseudoConsole = IntPtr.Zero;
        private IntPtr attributeList = IntPtr.Zero;
        private IntPtr job = IntPtr.Zero;
        private IntPtr inputThread = IntPtr.Zero;
        private IntPtr outerConsoleInput = IntPtr.Zero;
        private PROCESS_INFORMATION processInfo;
        private uint outerConsoleInputMode;
        private uint outerConsoleInputCodePage;
        private string inputKind = "pipe";
        private long inputUnitsRead;
        private long inputBytesWritten;
        private ulong localInputEpoch;
        private uint inputEventsPending;
        private bool restoreOuterConsoleInputMode;
        private AgentPipeBridge agentPipe;
        private COORD innerSize;
        private volatile bool stopInput;
        private volatile bool stopResize;
        private volatile bool outputDisconnected;
        private bool disposed;

        public ConPtySessionHost(SessionHostOptions options)
        {
            this.options = options;
        }

        public int Run()
        {
            var outerInput = GetStdHandle(-10);
            var outerOutput = GetStdHandle(-11);
            CheckHandle(outerInput, "standard input");
            CheckHandle(outerOutput, "standard output");

            var size = QueryOuterSize(outerOutput, options.FallbackColumns, options.FallbackRows);
            CreateInnerPseudoConsole(size);
            StartChildSuspended();

            if (options.UseAgentPipe)
            {
                agentPipe = new AgentPipeBridge(
                    options.AgentPipeName,
                    options.SessionId,
                    options.HandshakeToken,
                    ApplyAgentInput,
                    ApplyAgentRemoteInput,
                    ApplyAgentResize,
                    options.AgentBufferBytes);
                agentPipe.Start();
            }

            outerInput = GetStdHandle(-10);
            outerOutput = GetStdHandle(-11);
            CheckHandle(outerInput, "standard input after child start");
            CheckHandle(outerOutput, "standard output after child start");
            var inputIsConsole = ConfigureOuterInput(outerInput);

            var outputTask = Task.Run(() => BridgeOutput(outerOutput));
            var inputTask = Task.Run(() => BridgeInput(outerInput, inputIsConsole));
            var resizeTask = Task.Run(() => PollResize(outerOutput, size));
            if (!inputThreadReady.Wait(1000))
            {
                throw new InvalidOperationException("Input bridge did not start.");
            }
            PublishAgentFrame(
                LocalFrameType.Lifecycle,
                LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0));
            PublishAgentFrame(LocalFrameType.Resize, LocalPayloadCodec.Resize(size.X, size.Y));
            var handlesAtStart = Process.GetCurrentProcess().HandleCount;
            var stopwatch = Stopwatch.StartNew();
            var reason = "exited";

            while (true)
            {
                var wait = WaitForSingleObject(processInfo.hProcess, 25);
                if (wait == WaitObject0) break;
                if (wait != WaitTimeout)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject(child) failed.");
                }
                if (outputDisconnected)
                {
                    reason = "output-disconnected";
                    CloseInput();
                    TerminateProcessTree(125);
                    break;
                }
                if (options.TimeoutMilliseconds != Timeout.Infinite &&
                    stopwatch.ElapsedMilliseconds >= options.TimeoutMilliseconds)
                {
                    reason = "timeout";
                    CloseInput();
                    TerminateProcessTree(124);
                    break;
                }
            }

            WaitForSingleObject(processInfo.hProcess, 5000);
            uint rawExitCode;
            CheckWin32(GetExitCodeProcess(processInfo.hProcess, out rawExitCode), "GetExitCodeProcess");
            if (rawExitCode == StillActive)
            {
                reason = "forced";
                TerminateProcessTree(125);
                WaitForSingleObject(processInfo.hProcess, 5000);
                CheckWin32(GetExitCodeProcess(processInfo.hProcess, out rawExitCode), "GetExitCodeProcess(forced)");
            }

            CloseInput();
            if (inputIsConsole) GetNumberOfConsoleInputEvents(outerInput, out inputEventsPending);
            StopInputBridge(inputTask);
            RestoreOuterInputMode();
            stopResize = true;
            resizeTask.Wait(1000);
            ClosePseudoConsoleForDrain();
            if (!outputTask.Wait(5000))
            {
                outputDisconnected = true;
                CloseIfValid(ref terminalOutput);
            }

            var endedSequence = PublishAgentFrame(
                LocalFrameType.Lifecycle,
                LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, unchecked((int)rawExitCode)));
            if (agentPipe != null && endedSequence > 0) agentPipe.WaitForAck(endedSequence, 2000);

            CloseChildHandles();
            CloseIfValid(ref job);
            ReleaseAttributeList();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var handlesAtEnd = Process.GetCurrentProcess().HandleCount;

            if (options.Diagnostics && !outputDisconnected)
            {
                Console.Error.WriteLine(
                    "__SESSION_HOST_RESULT__:reason=" + reason +
                    ";handles_start=" + handlesAtStart +
                    ";handles_end=" + handlesAtEnd +
                    ";input=" + inputKind +
                    ";input_units=" + Interlocked.Read(ref inputUnitsRead) +
                    ";input_bytes=" + Interlocked.Read(ref inputBytesWritten) +
                    ";input_events=" + inputEventsPending +
                    ";input_mode=0x" + outerConsoleInputMode.ToString("X") +
                    ";input_cp=" + outerConsoleInputCodePage +
                    ";agent_ack=" + (agentPipe == null ? 0 : agentPipe.LastAcknowledgedSequence));
            }
            return reason == "timeout" ? 124 : unchecked((int)rawExitCode);
        }

        private void CreateInnerPseudoConsole(COORD size)
        {
            CheckWin32(CreatePipe(out pseudoConsoleInput, out terminalInput, IntPtr.Zero, 0), "CreatePipe(input)");
            CheckWin32(CreatePipe(out terminalOutput, out pseudoConsoleOutput, IntPtr.Zero, 0), "CreatePipe(output)");
            CheckHResult(CreatePseudoConsole(size, pseudoConsoleInput, pseudoConsoleOutput, 0, out pseudoConsole), "CreatePseudoConsole");
            innerSize = size;
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
                    new IntPtr(ProcThreadAttributePseudoConsole),
                    pseudoConsole,
                    new IntPtr(IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero),
                "UpdateProcThreadAttribute(PSEUDOCONSOLE)");
        }

        private void StartChildSuspended()
        {
            job = CreateJobObject(IntPtr.Zero, null);
            CheckHandle(job, "job object");
            var limit = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limit.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            CheckWin32(
                SetInformationJobObject(
                    job,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    ref limit,
                    (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))),
                "SetInformationJobObject(KILL_ON_JOB_CLOSE)");

            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            startup.StartupInfo.dwFlags = StartfUseStdHandles;
            startup.StartupInfo.hStdInput = IntPtr.Zero;
            startup.StartupInfo.hStdOutput = IntPtr.Zero;
            startup.StartupInfo.hStdError = IntPtr.Zero;
            startup.lpAttributeList = attributeList;
            var commandLine = new StringBuilder(options.CommandLine);
            CheckWin32(
                CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateSuspended,
                    IntPtr.Zero,
                    null,
                    ref startup,
                    out processInfo),
                "CreateProcessW");
            CheckWin32(AssignProcessToJobObject(job, processInfo.hProcess), "AssignProcessToJobObject");
            if (ResumeThread(processInfo.hThread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
            }
            CloseIfValid(ref processInfo.hThread);
        }

        private bool ConfigureOuterInput(IntPtr outerInput)
        {
            uint mode;
            if (!GetConsoleMode(outerInput, out mode)) return false;

            outerConsoleInput = outerInput;
            outerConsoleInputMode = mode;
            outerConsoleInputCodePage = GetConsoleCP();
            inputKind = "console";
            var rawMode = (mode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput | EnableQuickEditMode)) |
                EnableExtendedFlags | EnableWindowInput | EnableMouseInput;
            CheckWin32(SetConsoleMode(outerInput, rawMode), "SetConsoleMode(raw input records)");
            restoreOuterConsoleInputMode = true;
            return true;
        }

        private void BridgeInput(IntPtr outerInput, bool inputIsConsole)
        {
            try
            {
                lock (inputSync)
                {
                    inputThread = OpenThread(ThreadTerminate, false, GetCurrentThreadId());
                    CheckHandle(inputThread, "input bridge thread");
                }
                inputThreadReady.Set();

                if (inputIsConsole) BridgeConsoleInput(outerInput);
                else BridgePipeInput(outerInput);
            }
            finally
            {
                inputThreadReady.Set();
                CloseInput();
            }
        }

        private void BridgeConsoleInput(IntPtr outerInput)
        {
            var records = new INPUT_RECORD[128];
            while (!stopInput)
            {
                uint recordsRead;
                if (!ReadConsoleInput(outerInput, records, (uint)records.Length, out recordsRead))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (stopInput || error == ErrorOperationAborted || error == ErrorBrokenPipe) break;
                    throw new Win32Exception(error, "ReadConsoleInputW(input bridge) failed.");
                }
                if (recordsRead == 0) break;
                Interlocked.Add(ref inputUnitsRead, recordsRead);

                var input = new StringBuilder();
                for (var index = 0; index < recordsRead; index++)
                {
                    var record = records[index];
                    if (record.EventType != KeyEvent || !record.KeyEvent.KeyDown) continue;
                    AppendKeyInput(input, record.KeyEvent);
                }
                if (input.Length == 0) continue;
                var bytes = Encoding.UTF8.GetBytes(input.ToString());
                if (!WriteLocalInput(bytes, bytes.Length)) break;
            }
        }

        private static void AppendKeyInput(StringBuilder input, KEY_EVENT_RECORD key)
        {
            var repeat = Math.Max(1, (int)key.RepeatCount);
            if (key.UnicodeChar != '\0')
            {
                var alt = (key.ControlKeyState & (LeftAltPressed | RightAltPressed)) != 0;
                for (var index = 0; index < repeat; index++)
                {
                    if (alt && key.UnicodeChar != '\x1b') input.Append('\x1b');
                    input.Append(key.UnicodeChar);
                }
                return;
            }

            var suffix = SpecialKeySuffix(key.VirtualKeyCode);
            if (suffix == null) return;
            var modifier = 1;
            if ((key.ControlKeyState & ShiftPressed) != 0) modifier += 1;
            if ((key.ControlKeyState & (LeftAltPressed | RightAltPressed)) != 0) modifier += 2;
            if ((key.ControlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0) modifier += 4;
            var sequence = FormatSpecialKey(suffix, modifier);
            for (var index = 0; index < repeat; index++) input.Append(sequence);
        }

        private static string SpecialKeySuffix(ushort virtualKey)
        {
            switch (virtualKey)
            {
                case 0x26: return "A";  // Up
                case 0x28: return "B";  // Down
                case 0x27: return "C";  // Right
                case 0x25: return "D";  // Left
                case 0x24: return "H";  // Home
                case 0x23: return "F";  // End
                case 0x2D: return "2~"; // Insert
                case 0x2E: return "3~"; // Delete
                case 0x21: return "5~"; // Page Up
                case 0x22: return "6~"; // Page Down
                case 0x70: return "P";  // F1
                case 0x71: return "Q";  // F2
                case 0x72: return "R";  // F3
                case 0x73: return "S";  // F4
                case 0x74: return "15~";
                case 0x75: return "17~";
                case 0x76: return "18~";
                case 0x77: return "19~";
                case 0x78: return "20~";
                case 0x79: return "21~";
                case 0x7A: return "23~";
                case 0x7B: return "24~";
                default: return null;
            }
        }

        private static string FormatSpecialKey(string suffix, int modifier)
        {
            if (suffix.Length == 1)
            {
                if (modifier == 1 && suffix[0] >= 'P' && suffix[0] <= 'S') return "\x1bO" + suffix;
                if (modifier == 1) return "\x1b[" + suffix;
                return "\x1b[1;" + modifier + suffix;
            }
            if (modifier == 1) return "\x1b[" + suffix;
            return "\x1b[" + suffix.Substring(0, suffix.Length - 1) + ";" + modifier + "~";
        }

        private void BridgePipeInput(IntPtr outerInput)
        {
            var buffer = new byte[8192];
            while (!stopInput)
            {
                uint read;
                if (!ReadFile(outerInput, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (stopInput || error == ErrorOperationAborted || error == ErrorBrokenPipe) break;
                    throw new Win32Exception(error, "ReadFile(input bridge) failed.");
                }
                if (read == 0) break;
                Interlocked.Add(ref inputUnitsRead, read);
                if (!WriteLocalInput(buffer, checked((int)read))) break;
            }
        }

        private bool WriteInput(byte[] buffer, int length)
        {
            IntPtr destination;
            lock (inputSync) destination = terminalInput;
            if (!IsValid(destination)) return false;
            lock (terminalWriteSync)
            {
                if (!WriteAll(destination, buffer, length)) return false;
            }
            Interlocked.Add(ref inputBytesWritten, length);
            return true;
        }

        private bool WriteLocalInput(byte[] buffer, int length)
        {
            IntPtr destination;
            lock (inputSync) destination = terminalInput;
            if (!IsValid(destination)) return false;
            lock (terminalWriteSync)
            {
                localInputEpoch = checked(localInputEpoch + 1);
                if (agentPipe != null)
                {
                    agentPipe.Publish(LocalFrameType.LocalInputObserved, LocalPayloadCodec.LocalInputEpoch(localInputEpoch));
                }
                if (!WriteAll(destination, buffer, length)) return false;
            }
            Interlocked.Add(ref inputBytesWritten, length);
            return true;
        }

        private void ApplyAgentInput(Guid inputId, byte[] bytes)
        {
            if (!WriteInput(bytes, bytes.Length))
            {
                throw new IOException("ConPTY input pipe is closed for input " + inputId.ToString("D") + ".");
            }
        }

        private LocalRemoteInputResult ApplyAgentRemoteInput(Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes)
        {
            IntPtr destination;
            lock (inputSync) destination = terminalInput;
            if (!IsValid(destination)) return LocalRemoteInputResult.ConPtyClosed;
            lock (terminalWriteSync)
            {
                if (expectedLocalInputEpoch != localInputEpoch)
                {
                    return LocalRemoteInputResult.LocalInputSuperseded;
                }
                if (!WriteAll(destination, bytes, bytes.Length))
                {
                    return LocalRemoteInputResult.WriteFailed;
                }
            }
            Interlocked.Add(ref inputBytesWritten, bytes.Length);
            return LocalRemoteInputResult.Accepted;
        }

        private void ApplyAgentResize(short columns, short rows)
        {
            var requested = new COORD { X = columns, Y = rows };
            if (!ResizeInner(requested)) throw new COMException("Agent resize failed.");
        }

        private void StopInputBridge(Task inputTask)
        {
            stopInput = true;
            lock (inputSync)
            {
                if (IsValid(inputThread)) CancelSynchronousIo(inputThread);
            }
            if (!inputTask.Wait(1000))
            {
                throw new InvalidOperationException("Input bridge did not stop.");
            }
            lock (inputSync) CloseIfValid(ref inputThread);
        }

        private void RestoreOuterInputMode()
        {
            if (restoreOuterConsoleInputMode)
            {
                SetConsoleMode(outerConsoleInput, outerConsoleInputMode);
                restoreOuterConsoleInputMode = false;
            }
            outerConsoleInput = IntPtr.Zero;
        }

        private void BridgeOutput(IntPtr outerOutput)
        {
            var buffer = new byte[OutputBatchBytes];
            while (true)
            {
                uint read;
                if (!ReadFile(terminalOutput, buffer, (uint)buffer.Length, out read, IntPtr.Zero) || read == 0) break;
                var total = checked((int)read);
                var coalesce = Stopwatch.StartNew();
                while (total < buffer.Length && coalesce.ElapsedMilliseconds < OutputCoalesceMilliseconds)
                {
                    uint available;
                    if (!PeekNamedPipe(
                        terminalOutput, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
                    {
                        break;
                    }
                    if (available == 0)
                    {
                        Thread.SpinWait(64);
                        continue;
                    }
                    var requested = Math.Min(checked((int)available), buffer.Length - total);
                    var extra = new byte[requested];
                    uint extraRead;
                    if (!ReadFile(terminalOutput, extra, (uint)extra.Length, out extraRead, IntPtr.Zero) ||
                        extraRead == 0)
                    {
                        break;
                    }
                    Buffer.BlockCopy(extra, 0, buffer, total, checked((int)extraRead));
                    total += checked((int)extraRead);
                }
                var agentBytes = new byte[total];
                Buffer.BlockCopy(buffer, 0, agentBytes, 0, total);
                PublishAgentFrame(LocalFrameType.Output, agentBytes);
                if (!WriteAll(outerOutput, buffer, total))
                {
                    outputDisconnected = true;
                    break;
                }
            }
        }

        private void PollResize(IntPtr outerOutput, COORD previous)
        {
            while (!stopResize)
            {
                var current = QueryOuterSize(outerOutput, previous.X, previous.Y);
                if (current.X != previous.X || current.Y != previous.Y)
                {
                    if (ResizeInner(current)) previous = current;
                }
                Thread.Sleep(40);
            }
        }

        private bool ResizeInner(COORD size)
        {
            lock (resizeSync)
            {
                if (pseudoConsole == IntPtr.Zero) return false;
                var result = ResizePseudoConsole(pseudoConsole, size);
                if (result != 0) return false;
                innerSize = size;
            }
            PublishAgentFrame(LocalFrameType.Resize, LocalPayloadCodec.Resize(size.X, size.Y));
            return true;
        }

        private ulong PublishAgentFrame(LocalFrameType type, byte[] payload)
        {
            return agentPipe == null ? 0 : agentPipe.Publish(type, payload);
        }

        private void ClosePseudoConsoleForDrain()
        {
            CloseIfValid(ref pseudoConsoleInput);
            CloseIfValid(ref pseudoConsoleOutput);
            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
            }
        }

        private void CloseInput()
        {
            lock (inputSync) CloseIfValid(ref terminalInput);
        }

        private void TerminateProcessTree(uint exitCode)
        {
            if (IsValid(job))
            {
                CheckWin32(TerminateJobObject(job, exitCode), "TerminateJobObject");
            }
            else if (IsValid(processInfo.hProcess))
            {
                CheckWin32(TerminateProcess(processInfo.hProcess, exitCode), "TerminateProcess");
            }
        }

        private static COORD QueryOuterSize(IntPtr output, short fallbackColumns, short fallbackRows)
        {
            CONSOLE_SCREEN_BUFFER_INFO info;
            if (GetConsoleScreenBufferInfo(output, out info))
            {
                var columns = info.srWindow.Right - info.srWindow.Left + 1;
                var rows = info.srWindow.Bottom - info.srWindow.Top + 1;
                if (columns >= 2 && rows >= 2)
                {
                    return new COORD { X = checked((short)columns), Y = checked((short)rows) };
                }
            }
            return new COORD { X = fallbackColumns, Y = fallbackRows };
        }

        private static bool WriteAll(IntPtr handle, byte[] buffer, int length)
        {
            var offset = 0;
            while (offset < length)
            {
                var chunkLength = length - offset;
                var chunk = new byte[chunkLength];
                Buffer.BlockCopy(buffer, offset, chunk, 0, chunkLength);
                uint written;
                if (!WriteFile(handle, chunk, (uint)chunk.Length, out written, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorBrokenPipe || error == ErrorNoData) return false;
                    throw new Win32Exception(error, "WriteFile bridge failed.");
                }
                if (written == 0) return false;
                offset += checked((int)written);
            }
            return true;
        }

        private void CloseChildHandles()
        {
            CloseIfValid(ref processInfo.hThread);
            CloseIfValid(ref processInfo.hProcess);
        }

        private void ReleaseAttributeList()
        {
            if (attributeList == IntPtr.Zero) return;
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            attributeList = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stopInput = true;
            stopResize = true;
            CloseInput();
            lock (inputSync)
            {
                if (IsValid(inputThread)) CancelSynchronousIo(inputThread);
                CloseIfValid(ref inputThread);
            }
            RestoreOuterInputMode();
            if (agentPipe != null)
            {
                agentPipe.Dispose();
                agentPipe = null;
            }
            CloseIfValid(ref pseudoConsoleInput);
            CloseIfValid(ref pseudoConsoleOutput);
            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
            }
            CloseIfValid(ref terminalOutput);
            CloseChildHandles();
            CloseIfValid(ref job);
            ReleaseAttributeList();
        }

        private static bool IsValid(IntPtr handle)
        {
            return handle != IntPtr.Zero && handle != new IntPtr(-1);
        }

        private static void CheckHandle(IntPtr handle, string label)
        {
            if (!IsValid(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Invalid " + label + " handle.");
        }

        private static void CloseIfValid(ref IntPtr handle)
        {
            if (!IsValid(handle)) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private static void CheckWin32(bool success, string operation)
        {
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");
        }

        private static void CheckHResult(int result, string operation)
        {
            if (result != 0) throw new COMException(operation + " failed.", result);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public short wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct KEY_EVENT_RECORD
        {
            [MarshalAs(UnmanagedType.Bool)] public bool KeyDown;
            public ushort RepeatCount;
            public ushort VirtualKeyCode;
            public ushort VirtualScanCode;
            public char UnicodeChar;
            public uint ControlKeyState;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private enum JOBOBJECTINFOCLASS { JobObjectExtendedLimitInformation = 9 }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr console, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr console, uint mode);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleCP();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNumberOfConsoleInputEvents(IntPtr console, out uint events);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ReadConsoleInput(IntPtr console, [Out] INPUT_RECORD[] records, uint recordsToRead, out uint recordsRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelSynchronousIo(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr output, out CONSOLE_SCREEN_BUFFER_INFO info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, [Out] byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(
            IntPtr pipe,
            IntPtr buffer,
            uint bufferSize,
            IntPtr bytesRead,
            out uint totalBytesAvailable,
            IntPtr bytesLeftThisMessage);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr valueSize, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, [In] ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, JOBOBJECTINFOCLASS infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    }

    internal sealed class AgentPipeBridge : IDisposable
    {
        private readonly string pipeName;
        private readonly Guid sessionId;
        private readonly byte[] handshakeToken;
        private readonly Action<Guid, byte[]> inputHandler;
        private readonly Func<Guid, ulong, byte[], LocalRemoteInputResult> remoteInputHandler;
        private readonly Action<short, short> resizeHandler;
        private readonly int maximumBufferedBytes;
        private readonly object sync = new object();
        private readonly LinkedList<LocalFrame> pending = new LinkedList<LocalFrame>();
        private readonly AutoResetEvent sendPending = new AutoResetEvent(false);
        private NamedPipeClientStream pipe;
        private Task connectionTask;
        private ulong nextSequence;
        private ulong lastAcknowledgedSequence;
        private ulong lastAgentCommandSequence;
        private int pendingBytes;
        private volatile bool stop;
        private bool disposed;

        public AgentPipeBridge(
            string pipeName,
            Guid sessionId,
            byte[] handshakeToken,
            Action<Guid, byte[]> inputHandler,
            Func<Guid, ulong, byte[], LocalRemoteInputResult> remoteInputHandler,
            Action<short, short> resizeHandler,
            int maximumBufferedBytes)
        {
            this.pipeName = pipeName;
            this.sessionId = sessionId;
            this.handshakeToken = handshakeToken;
            this.inputHandler = inputHandler;
            this.remoteInputHandler = remoteInputHandler;
            this.resizeHandler = resizeHandler;
            this.maximumBufferedBytes = maximumBufferedBytes;
        }

        public ulong LastAcknowledgedSequence
        {
            get { lock (sync) return lastAcknowledgedSequence; }
        }

        public void Start()
        {
            lock (sync)
            {
                if (connectionTask != null) throw new InvalidOperationException("Agent pipe bridge already started.");
                connectionTask = Task.Run((Action)ConnectionLoop);
            }
        }

        public ulong Publish(LocalFrameType type, byte[] payload)
        {
            lock (sync)
            {
                if (disposed) return 0;
                var frame = new LocalFrame(type, sessionId, ++nextSequence, payload);
                AddPending(frame);
                sendPending.Set();
                return frame.Sequence;
            }
        }

        public bool WaitForAck(ulong sequence, int timeoutMilliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            lock (sync)
            {
                while (!disposed && lastAcknowledgedSequence < sequence)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return false;
                    Monitor.Wait(sync, remaining);
                }
                return lastAcknowledgedSequence >= sequence;
            }
        }

        private void ConnectionLoop()
        {
            while (!stop)
            {
                NamedPipeClientStream candidate = null;
                Task writer = null;
                try
                {
                    candidate = new NamedPipeClientStream(
                        ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    candidate.Connect(250);
                    Authenticate(candidate);
                    lock (sync)
                    {
                        if (stop) return;
                        pipe = candidate;
                    }
                    var activePipe = candidate;
                    writer = Task.Run(() => WritePending(activePipe));
                    sendPending.Set();
                    ReadAgentCommands(candidate);
                }
                catch (TimeoutException)
                {
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (InvalidDataException)
                {
                }
                finally
                {
                    lock (sync)
                    {
                        if (ReferenceEquals(pipe, candidate)) pipe = null;
                        Monitor.PulseAll(sync);
                    }
                    if (candidate != null) candidate.Dispose();
                    sendPending.Set();
                    if (writer != null) writer.Wait(2000);
                }
                if (!stop) Thread.Sleep(100);
            }
        }

        private void Authenticate(NamedPipeClientStream candidate)
        {
            uint serverProcessId;
            if (!GetNamedPipeServerProcessId(
                candidate.SafePipeHandle.DangerousGetHandle(), out serverProcessId) || serverProcessId == 0)
            {
                throw new InvalidDataException("Agent pipe server process identity is unavailable.");
            }
            LocalFrameCodec.Write(candidate, new LocalFrame(
                LocalFrameType.HostHello,
                sessionId,
                0,
                LocalPayloadCodec.Hello(Process.GetCurrentProcess().Id, handshakeToken)));
            var response = LocalFrameCodec.Read(candidate);
            if (response == null || response.Type != LocalFrameType.AgentHello ||
                response.SessionId != sessionId || response.Sequence != 0)
            {
                throw new InvalidDataException("Invalid Agent handshake response.");
            }
            var claimedProcessId = LocalPayloadCodec.ParseHello(response.Payload, handshakeToken);
            if (serverProcessId != claimedProcessId)
            {
                throw new InvalidDataException("Agent pipe server process identity mismatch.");
            }
        }

        private void ReadAgentCommands(NamedPipeClientStream candidate)
        {
            while (!stop && candidate.IsConnected)
            {
                var frame = LocalFrameCodec.Read(candidate);
                if (frame == null) return;
                if (frame.SessionId != sessionId)
                {
                    throw new InvalidDataException("Agent sent a frame for another session.");
                }
                if (frame.Type == LocalFrameType.Ack)
                {
                    if (frame.Payload.Length != 0) throw new InvalidDataException("ACK payload must be empty.");
                    Acknowledge(frame.Sequence);
                    continue;
                }
                if (frame.Sequence <= lastAgentCommandSequence)
                {
                    if (frame.Type == LocalFrameType.Input || frame.Type == LocalFrameType.RemoteInputV2)
                    {
                        Array.Clear(frame.Payload, 0, frame.Payload.Length);
                    }
                    continue;
                }
                lastAgentCommandSequence = frame.Sequence;
                switch (frame.Type)
                {
                    case LocalFrameType.Input:
                        byte[] bytes;
                        var inputId = LocalPayloadCodec.ParseInput(frame.Payload, out bytes);
                        try { inputHandler(inputId, bytes); }
                        finally
                        {
                            Array.Clear(bytes, 0, bytes.Length);
                            Array.Clear(frame.Payload, 0, frame.Payload.Length);
                        }
                        break;
                    case LocalFrameType.ResizeRequest:
                        short columns;
                        short rows;
                        LocalPayloadCodec.ParseResize(frame.Payload, out columns, out rows);
                        resizeHandler(columns, rows);
                        break;
                    case LocalFrameType.RemoteInputV2:
                        ulong localInputEpoch;
                        byte[] remoteBytes;
                        var remoteInputId = LocalPayloadCodec.ParseRemoteInput(frame.Payload, out localInputEpoch, out remoteBytes);
                        LocalRemoteInputResult remoteResult;
                        try { remoteResult = remoteInputHandler(remoteInputId, localInputEpoch, remoteBytes); }
                        finally
                        {
                            Array.Clear(remoteBytes, 0, remoteBytes.Length);
                            Array.Clear(frame.Payload, 0, frame.Payload.Length);
                        }
                        Publish(
                            LocalFrameType.RemoteInputResult,
                            LocalPayloadCodec.RemoteInputResult(remoteInputId, remoteResult));
                        break;
                    default:
                        throw new InvalidDataException("Agent sent an invalid command type: " + frame.Type + ".");
                }
            }
        }

        private void Acknowledge(ulong sequence)
        {
            lock (sync)
            {
                if (sequence > nextSequence) throw new InvalidDataException("Agent ACK exceeds published sequence.");
                if (sequence <= lastAcknowledgedSequence) return;
                lastAcknowledgedSequence = sequence;
                while (pending.First != null && pending.First.Value.Sequence <= sequence)
                {
                    pendingBytes -= StoredBytes(pending.First.Value);
                    pending.RemoveFirst();
                }
                Monitor.PulseAll(sync);
                sendPending.Set();
            }
        }

        private void WritePending(NamedPipeClientStream candidate)
        {
            ulong lastSent = 0;
            while (!stop)
            {
                LocalFrame next = null;
                lock (sync)
                {
                    if (!ReferenceEquals(pipe, candidate)) return;
                    var node = pending.First;
                    while (node != null && node.Value.Sequence <= lastSent) node = node.Next;
                    if (node != null) next = node.Value;
                }
                if (next == null)
                {
                    sendPending.WaitOne(100);
                    continue;
                }
                try
                {
                    LocalFrameCodec.Write(candidate, next);
                    lastSent = next.Sequence;
                }
                catch (IOException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private void AddPending(LocalFrame frame)
        {
            pending.AddLast(frame);
            pendingBytes += StoredBytes(frame);
            if (pendingBytes <= maximumBufferedBytes) return;

            var gapStart = lastAcknowledgedSequence + 1;
            var gapEnd = frame.Sequence;
            pending.Clear();
            pendingBytes = 0;
            var gap = new LocalFrame(
                LocalFrameType.Gap,
                sessionId,
                ++nextSequence,
                LocalPayloadCodec.Gap(gapStart, gapEnd));
            pending.AddLast(gap);
            pendingBytes = StoredBytes(gap);
            if (pipe != null)
            {
                pipe.Dispose();
                pipe = null;
            }
        }

        private static int StoredBytes(LocalFrame frame)
        {
            return checked(30 + frame.Payload.Length);
        }

        public void Dispose()
        {
            Task task;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                stop = true;
                if (pipe != null)
                {
                    pipe.Dispose();
                    pipe = null;
                }
                task = connectionTask;
                Monitor.PulseAll(sync);
            }
            if (task != null) task.Wait(2000);
            sendPending.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
    }
}
