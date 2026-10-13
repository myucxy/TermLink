[CmdletBinding()]
param(
    [switch]$DefinitionsOnly,
    [switch]$WindowInputSelfCheck,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId,
    [uri]$AcceptanceOrigin,
    [string]$AgentControlPipe,
    [string]$SessionHostPath,
    [string]$NativeDisplayPath,
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$EmulatorSerial = 'emulator-5556',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$AndroidPackageName = 'dev.termlink.harness',
    [string]$AdbPath,
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$UiTestPackage = 'dev.termlink.terminal.nativecore.test',
    [string]$UiHierarchyScript = (Join-Path $PSScriptRoot 'Get-TerminalProductUiHierarchy.ps1')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Shell([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Get-SafeShellFailure([Management.Automation.ErrorRecord]$Failure) {
    $exception = $Failure.Exception
    $code = 'UNCLASSIFIED_FAILURE'
    $type = 'Exception'
    for ($depth = 0; $depth -lt 16 -and $null -ne $exception; $depth++) {
        $name = $exception.GetType().Name
        if ($name -cmatch '^[A-Za-z][A-Za-z0-9]{0,79}$') { $type = $name }
        if ($exception.Message -cmatch '^[A-Z][A-Z0-9_]{0,79}$') {
            $code = $exception.Message
            break
        }
        $exception = $exception.InnerException
    }
    $line = 0
    if ($null -ne $Failure.InvocationInfo) { $line = [int]$Failure.InvocationInfo.ScriptLineNumber }
    if ($line -lt 0) { $line = 0 }
    return [pscustomobject]@{ code = $code; type = $type; line = $line }
}

function Get-ShellPlan([string]$Id) {
    $stem = 'TLTF_' + $Id.Replace('-', '_').Replace('.', '_')
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Id))
    $live = 'TLTF_LIVE_' + [Convert]::ToHexString($hash, 0, 8).ToLowerInvariant()
    [pscustomobject]@{
        first_marker = $stem + '_0001'
        last_marker = $stem + '_5200'
        live_marker = $live
        generator_command = '1..5200|%{"' + $stem + '_{0:D4}"-f $_};"' + $live + '"'
        location_filler_command = '1..100|%{"H01_LOCATION_{0:D3}"-f $_}'
        android_commands = @('pwd', 'throw')
        directory_change_command = 'cd ..'
        shell_arguments = '-NoLogo -NoProfile'
        managed_shell = 'powershell-7'
        working_directory_leaf = '中文 空格'
        canonical_path_example_sha256 = Get-WindowsPathHash 'C:\Work\中文 空格\'
    }
}

function Get-WindowsPathHash([string]$Path) {
    Require-Shell ($Path -match '^[A-Za-z]:[\\/]' -and [IO.Path]::IsPathFullyQualified($Path)) 'SHELL_PATH_INVALID'
    $normalized = $Path.Replace('/', '\')
    if ($normalized.Length -gt 3) { $normalized = $normalized.TrimEnd('\') }
    $normalized = $normalized.ToLowerInvariant()
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

$plan = Get-ShellPlan ($RunId ?? 'shell-selftest')
if ($DefinitionsOnly) { $plan | ConvertTo-Json -Depth 3; return }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class ShellE2EWindowInput {
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public KeyInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    public static bool OwnsWindow(IntPtr window, int processId) =>
        window != IntPtr.Zero && IsWindow(window) && GetAncestor(window, 2) == window &&
        GetWindowThreadProcessId(window, out var actual) != 0 && actual == processId;
    public static IntPtr DesktopWindow() => GetDesktopWindow();
    public static int NativeInputSize() => Marshal.SizeOf<Input>();
    public static bool OwnsHit(IntPtr window, int processId, IntPtr hit) =>
        OwnsWindow(window, processId) && hit != IntPtr.Zero &&
        GetAncestor(hit, 2) == window &&
        GetWindowThreadProcessId(hit, out var actual) != 0 && actual == processId;

    private static bool HasFocus(IntPtr window, int processId) {
        if (!OwnsWindow(window, processId) || GetForegroundWindow() != window) return false;
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) && info.Focus != IntPtr.Zero &&
            GetAncestor(info.Focus, 2) == window &&
            GetWindowThreadProcessId(info.Focus, out var focusedProcess) != 0 && focusedProcess == processId;
    }

    public static void FocusWindow(IntPtr window, int processId) {
        if (!OwnsWindow(window, processId)) throw new InvalidOperationException("WINDOW_IDENTITY_CHANGED");
        if (IsIconic(window)) ShowWindow(window, 9);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        for (var attempt = 0; attempt < 20; attempt++) {
            if (HasFocus(window, processId)) return;
            Thread.Sleep(25);
        }
        if (!GetCursorPos(out var previousCursor))
            throw new InvalidOperationException("WINDOW_CURSOR_CAPTURE_FAILED");
        var wasTopmost = (GetWindowLong(window, -20) & 8) != 0;
        var raised = false;
        var clicked = false;
        try {
            if (!wasTopmost) {
                if (!SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x13))
                    throw new InvalidOperationException("WINDOW_RAISE_FAILED");
                raised = true;
            }
            BringWindowToTop(window);
            SetForegroundWindow(window);
            if (!GetClientRect(window, out var client) || client.Right <= client.Left || client.Bottom <= client.Top)
                throw new InvalidOperationException("WINDOW_CLIENT_INVALID");
            var point = new Point { X = (client.Left + client.Right) / 2, Y = (client.Top + client.Bottom) / 2 };
            if (!ClientToScreen(window, ref point) || !OwnsHit(window, processId, WindowFromPoint(point)))
                throw new InvalidOperationException("WINDOW_CLICK_TARGET_INVALID");
            if (!SetCursorPos(point.X, point.Y))
                throw new InvalidOperationException("WINDOW_CURSOR_MOVE_FAILED");
            Thread.Sleep(50);
            if (!OwnsHit(window, processId, WindowFromPoint(point)))
                throw new InvalidOperationException("WINDOW_CLICK_TARGET_CHANGED");
            var input = new[] {
                new Input { Type = 0, Value = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
                new Input { Type = 0, Value = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }
            };
            if (SendInput(2, input, NativeInputSize()) != 2)
                throw new InvalidOperationException("WINDOW_CLICK_FAILED");
            for (var attempt = 0; attempt < 80; attempt++) {
                if (HasFocus(window, processId)) { clicked = true; return; }
                Thread.Sleep(25);
            }
            throw new InvalidOperationException("WINDOW_FOCUS_NOT_READY");
        } finally {
            var cursorRestored = SetCursorPos(previousCursor.X, previousCursor.Y);
            var topmostRestored = !raised || !IsWindow(window) ||
                SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x13);
            if (clicked && (!cursorRestored || !topmostRestored))
                throw new InvalidOperationException("WINDOW_RESTORE_FAILED");
        }
    }

    private static void SendKey(IntPtr window, int processId, ushort key, ushort scan, uint flags) {
        if (!HasFocus(window, processId)) throw new InvalidOperationException("WINDOW_FOCUS_CHANGED");
        var input = new[] {
            new Input { Type = 1, Value = new InputUnion { Key = new KeyInput { VirtualKey = key, Scan = scan, Flags = flags } } },
            new Input { Type = 1, Value = new InputUnion { Key = new KeyInput { VirtualKey = key, Scan = scan, Flags = flags | 2 } } }
        };
        if (SendInput(2, input, Marshal.SizeOf<Input>()) != 2)
            throw new InvalidOperationException("WINDOW_INPUT_FAILED");
    }

    public static void SendText(IntPtr window, int processId, string text) {
        if (!HasFocus(window, processId)) throw new InvalidOperationException("WINDOW_FOCUS_CHANGED");
        foreach (var character in text) SendKey(window, processId, 0, character, 4);
        SendKey(window, processId, 13, 0x1c, 0);
    }

    public static void CancelInput(IntPtr window, int processId) {
        if (!HasFocus(window, processId)) throw new InvalidOperationException("WINDOW_FOCUS_CHANGED");
        var input = new[] {
            new Input { Type = 1, Value = new InputUnion { Key = new KeyInput { VirtualKey = 17, Scan = 0x1d } } },
            new Input { Type = 1, Value = new InputUnion { Key = new KeyInput { VirtualKey = 67, Scan = 0x2e } } },
            new Input { Type = 1, Value = new InputUnion { Key = new KeyInput { VirtualKey = 67, Scan = 0x2e, Flags = 2 } } },
            new Input { Type = 1, Value = new InputUnion { Key = new KeyInput { VirtualKey = 17, Scan = 0x1d, Flags = 2 } } }
        };
        if (SendInput(4, input, Marshal.SizeOf<Input>()) != 4)
            throw new InvalidOperationException("WINDOW_INPUT_FAILED");
    }
}
'@
if ($WindowInputSelfCheck) {
    Require-Shell ([ShellE2EWindowInput]::NativeInputSize() -eq
        $(if ([IntPtr]::Size -eq 8) { 40 } else { 28 })) 'WINDOW_INPUT_LAYOUT_INVALID'
    $foreignWindow = [ShellE2EWindowInput]::DesktopWindow()
    Require-Shell ($foreignWindow -ne [IntPtr]::Zero -and
        -not [ShellE2EWindowInput]::OwnsWindow($foreignWindow, $PID) -and
        -not [ShellE2EWindowInput]::OwnsHit($foreignWindow, $PID, $foreignWindow)) 'WINDOW_SELF_CHECK_FAILED'
    try { [ShellE2EWindowInput]::SendText($foreignWindow, $PID, 'x'); throw 'WINDOW_SELF_CHECK_FAILED' }
    catch {
        $safe = Get-SafeShellFailure $_
        Require-Shell ($safe.code -ceq 'WINDOW_FOCUS_CHANGED' -and
            $safe.type -ceq 'InvalidOperationException' -and $safe.line -gt 0) 'WINDOW_SELF_CHECK_FAILED'
    }
    try { [ShellE2EWindowInput]::CancelInput($foreignWindow, $PID); throw 'WINDOW_SELF_CHECK_FAILED' }
    catch {
        Require-Shell ((Get-SafeShellFailure $_).code -ceq 'WINDOW_FOCUS_CHANGED') 'WINDOW_SELF_CHECK_FAILED'
    }
    'Window input ownership self-check passed.'
    return
}
. (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1')

Require-Shell (-not [string]::IsNullOrWhiteSpace($RunId) -and
    $null -ne $AcceptanceOrigin -and $AcceptanceOrigin.IsAbsoluteUri -and
    $AcceptanceOrigin.Scheme -ceq 'https' -and $AcceptanceOrigin.AbsolutePath -ceq '/' -and
    [string]::IsNullOrEmpty($AcceptanceOrigin.UserInfo) -and
    [string]::IsNullOrEmpty($AcceptanceOrigin.Query) -and
    [string]::IsNullOrEmpty($AcceptanceOrigin.Fragment) -and
    -not [string]::IsNullOrWhiteSpace($AgentControlPipe) -and
    $AgentControlPipe -cmatch '^[A-Za-z0-9._-]{1,200}$' -and
    -not [string]::IsNullOrWhiteSpace($SessionHostPath) -and
    -not [string]::IsNullOrWhiteSpace($NativeDisplayPath)) 'RUN_CONTEXT_REQUIRED'
Require-Shell ((Test-Path -LiteralPath $SessionHostPath -PathType Leaf) -and
    (Test-Path -LiteralPath $NativeDisplayPath -PathType Leaf)) 'WINDOWS_BINARY_MISSING'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts/broker-product-e2e/' + $RunId)))
Require-Shell (-not (Test-Path -LiteralPath $runRoot)) 'RUN_ID_ALREADY_EXISTS'

function Invoke-Program([string]$File, [string[]]$Arguments, [int]$Timeout = 20) {
    $native = [Diagnostics.ProcessStartInfo]::new()
    $native.FileName = $File
    $native.UseShellExecute = $false
    $native.CreateNoWindow = $true
    $native.RedirectStandardOutput = $true
    $native.RedirectStandardError = $true
    foreach ($item in $Arguments) { $native.ArgumentList.Add($item) }
    $nativeProcess = [Diagnostics.Process]::new()
    $nativeProcess.StartInfo = $native
    $startedNative = $false
    try {
        $startedNative = $nativeProcess.Start()
        if (-not $startedNative) { throw [InvalidOperationException]::new('PROCESS_START_FAILED') }
        $stdoutTask = $nativeProcess.StandardOutput.ReadToEndAsync()
        $stderrTask = $nativeProcess.StandardError.ReadToEndAsync()
        if (-not $nativeProcess.WaitForExit($Timeout * 1000)) {
            throw [TimeoutException]::new('PROCESS_TIMEOUT')
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $nativeProcess.ExitCode
        return [pscustomobject]@{ exit_code = $exitCode; output = $stdout; error = $stderr }
    } finally {
        if ($startedNative -and -not $nativeProcess.HasExited) {
            try { $nativeProcess.Kill($true) } catch { }
        }
        $nativeProcess.Dispose()
    }
}

function Invoke-Adb([string[]]$Arguments, [int]$Timeout = 20) {
    Invoke-Program $script:adbPath (@('-s', $EmulatorSerial) + $Arguments) $Timeout
}

function Require-Adb([string[]]$Arguments, [string]$Code, [int]$Timeout = 20) {
    $result = Invoke-Adb $Arguments $Timeout
    Require-Shell ($result.exit_code -eq 0) $Code
    return $result.output
}

function Start-Hook([string]$Operation, [hashtable]$Arguments = @{}) {
    $requestId = [guid]::NewGuid().ToString('N')
    $null = Require-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $script:responsePath) 'HOOK_RESET_FAILED'
    if ($Operation -ceq 'open_session') {
        $null = Require-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $script:armPath) 'ARM_RESET_FAILED'
    }
    $broadcast = @('shell', 'am', 'broadcast', '-a', 'dev.termlink.product.debug.TAIL_FIRST_E2E',
        '-n', "$AndroidPackageName/dev.termlink.product.debug.TailFirstE2EReceiver",
        '--es', 'e2e_operation', $Operation, '--es', 'request_id', $requestId,
        '--es', 'run_id', $RunId)
    foreach ($key in @($Arguments.Keys | Sort-Object)) { $broadcast += @('--es', $key, [string]$Arguments[$key]) }
    $null = Require-Adb $broadcast 'HOOK_BROADCAST_FAILED'
    return $requestId
}

function Wait-Hook([string]$Path, [string]$RequestId, [int]$Timeout = 30,
    [switch]$ResponseRequired) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        $read = Invoke-Adb @('exec-out', 'run-as', $AndroidPackageName, 'cat', $Path) 10
        if ($read.exit_code -eq 0 -and $read.output.TrimStart().StartsWith('{')) {
            Require-Shell ($read.output.Length -le 1MB) 'HOOK_RESPONSE_TOO_LARGE'
            $response = $read.output | ConvertFrom-Json -ErrorAction Stop
            if ([string]$response.request_id -ceq $RequestId) {
                $script:lastHookResponse = $response
                if ($ResponseRequired) {
                    Require-Shell ([string]$response.operation -ceq $script:pendingOperation) 'HOOK_OPERATION_MISMATCH'
                    if ($response.ok -ne $true) {
                        $failure = $response.PSObject.Properties['failure_code']
                        throw [InvalidOperationException]::new($(if ($null -ne $failure -and
                            [string]$failure.Value -cmatch '^[A-Z0-9_]{1,80}$') { [string]$failure.Value } else { 'HOOK_FAILED' }))
                    }
                }
                return $response
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('HOOK_TIMEOUT')
}

function Invoke-Hook([string]$Operation, [hashtable]$Arguments = @{}, [int]$Timeout = 30) {
    $script:pendingOperation = $Operation
    Wait-Hook $script:responsePath (Start-Hook $Operation $Arguments) $Timeout -ResponseRequired
}

function Invoke-ProbeWithStartupRetry {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try { return Invoke-Hook 'probe' }
        catch {
            if ($_.Exception.Message -cne 'BRIDGE_UNAVAILABLE') { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('BRIDGE_STARTUP_TIMEOUT')
}

function Get-UiNodes {
    Require-Shell ([IO.Path]::IsPathFullyQualified($UiHierarchyScript) -and
        (Test-Path -LiteralPath $UiHierarchyScript -PathType Leaf)) 'UI_CAPTURE_SCRIPT_MISSING'
    $script:lastUiXml = & (Get-Process -Id $PID).Path -NoProfile -File $UiHierarchyScript -DeviceSerial $EmulatorSerial `
        -TargetPackage $AndroidPackageName -TestPackage $UiTestPackage `
        -AdbPath $AdbPath -PowerShellPath (Get-Process -Id $PID).Path
    Require-Shell ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($script:lastUiXml)) 'UI_DUMP_FAILED'
    $document = [xml]$script:lastUiXml
    foreach ($node in $document.GetElementsByTagName('node')) {
        if ([string]$node.GetAttribute('package') -ceq $AndroidPackageName) { $node }
    }
}

function Find-UiNode([string[]]$Names) {
    foreach ($node in @(Get-UiNodes)) {
        $label = [string]$node.GetAttribute('text') + ' ' + [string]$node.GetAttribute('content-desc')
        if ($Names | Where-Object { $label.Contains($_) }) { return $node }
    }
    return $null
}

function Tap-Ui([string[]]$Names, [string]$Code) {
    $node = Find-UiNode $Names
    Require-Shell ($null -ne $node) $Code
    $bounds = [string]$node.GetAttribute('bounds')
    Require-Shell ($bounds -match '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') 'UI_BOUNDS_INVALID'
    $x = [int][Math]::Floor(([int]$matches[1] + [int]$matches[3]) / 2)
    $y = [int][Math]::Floor(([int]$matches[2] + [int]$matches[4]) / 2)
    $null = Require-Adb @('shell', 'input', 'tap', "$x", "$y") 'UI_TAP_FAILED'
    Start-Sleep -Milliseconds 400
}

function Hide-Keyboard {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $state = Require-Adb @('shell', 'dumpsys', 'input_method') 'KEYBOARD_STATE_FAILED'
        if ($state -notmatch 'mInputShown=true') { return }
        $null = Require-Adb @('shell', 'input', 'keyevent', '4') 'KEYBOARD_HIDE_FAILED'
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [InvalidOperationException]::new('KEYBOARD_STILL_SHOWN')
}

function Open-Menu {
    Hide-Keyboard
    Tap-Ui @('控制中', 'Controlling', '只读', 'Read only') 'STATUS_MENU_NOT_FOUND'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        if ($null -ne (Find-UiNode @('命令定位', 'Command markers'))) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('STATUS_MENU_NOT_OPEN')
}

function Assert-ShellControl($State, [guid]$SessionId) {
    Require-Shell ([guid]$State.session_id -eq $SessionId) 'SESSION_ID_CHANGED'
    Require-Shell ($State.realtime_subscriber_ready -eq $true -and
        [string]$State.realtime_remote_state -ceq 'CONTROLLING') 'ANDROID_CONTROL_NOT_READY'
}

function Acquire-ShellControl([guid]$SessionId) {
    Open-Menu
    if ($null -ne (Find-UiNode @('获取控制权限', 'Request control'))) {
        Tap-Ui @('获取控制权限', 'Request control') 'ANDROID_REQUEST_CONTROL_FAILED'
    } else {
        Require-Shell ($null -ne (Find-UiNode @('释放控制权限', 'Release control'))) 'ANDROID_CONTROL_MENU_INVALID'
        $null = Require-Adb @('shell', 'input', 'keyevent', 'KEYCODE_BACK') 'CONTROL_MENU_CLOSE_FAILED'
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $state = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker }
        Require-Shell ([guid]$state.session_id -eq $SessionId) 'SESSION_ID_CHANGED'
        if ($state.realtime_subscriber_ready -eq $true -and
            [string]$state.realtime_remote_state -ceq 'CONTROLLING') { return $state }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('ANDROID_CONTROL_NOT_READY')
}

function Save-Screenshot([string]$Name) {
    $path = Join-Path $runRoot $Name
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:adbPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($item in @('-s', $EmulatorSerial, 'exec-out', 'screencap', '-p')) { $start.ArgumentList.Add($item) }
    $process = [Diagnostics.Process]::Start($start)
    Require-Shell ($null -ne $process) 'SCREENSHOT_START_FAILED'
    try {
        $file = [IO.File]::Create($path)
        try { $process.StandardOutput.BaseStream.CopyTo($file) } finally { $file.Dispose() }
        Require-Shell ($process.WaitForExit(20000) -and $process.ExitCode -eq 0) 'SCREENSHOT_FAILED'
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
    return [IO.Path]::GetFileName($path)
}

function Assert-AgentRelay([string]$AssemblyPath, [string]$Origin) {
    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $requestType = $assembly.GetType('TermLink.Agent.RegistryRequest', $true)
    $commandType = $assembly.GetType('TermLink.Agent.RegistryCommand', $true)
    $clientType = $assembly.GetType('TermLink.Agent.RegistryControlClient', $true)
    $request = [Activator]::CreateInstance($requestType)
    $request.Command = [Enum]::Parse($commandType, 'RefreshProductionStatus')
    $send = @($clientType.GetMethods() | Where-Object { $_.Name -eq 'Send' -and $_.GetParameters().Count -eq 3 })
    Require-Shell ($send.Count -eq 1) 'AGENT_CONTROL_API_INVALID'
    try {
        $response = $send[0].Invoke($null, [object[]]@($AgentControlPipe, $request, 10000))
    } catch {
        throw [InvalidOperationException]::new('AGENT_RELAY_QUERY_FAILED')
    }
    Require-Shell ($null -ne $response) 'AGENT_RELAY_RESPONSE_INVALID'
    Require-Shell ($response.Success -and $response.RemoteAccessEnabled -and
        $response.Production.Configured -and $response.Production.AccountLinked -and
        [string]$response.Production.RelayState -ceq 'Connected' -and
        [string]$response.Production.ServerOrigin -ceq $Origin) 'AGENT_RELAY_NOT_READY'
}

function Find-OwnedDisplay([Diagnostics.Process]$Owner, [string]$ExpectedPath) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Require-Shell (-not $Owner.HasExited) 'HOST_EXITED_BEFORE_DISPLAY'
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Owner.Id)" |
            Where-Object { $_.ExecutablePath -and
                [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $ExpectedPath,
                    [StringComparison]::OrdinalIgnoreCase) })
        Require-Shell ($children.Count -le 1) 'DISPLAY_IDENTITY_AMBIGUOUS'
        if ($children.Count -eq 1) {
            try {
                $display = [Diagnostics.Process]::GetProcessById([int]$children[0].ProcessId)
                $display.Refresh()
                if (-not $display.HasExited -and $display.StartTime -ge $Owner.StartTime -and
                    [ShellE2EWindowInput]::OwnsWindow($display.MainWindowHandle, $display.Id)) { return $display }
                $display.Dispose()
            } catch [ArgumentException] { }
        }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('OWNED_DISPLAY_NOT_READY')
}

function Test-OwnedDisplayIdentity($Display, $Identity, [int]$OwnerId,
    [string]$ExpectedPath, [DateTime]$ExpectedStartTime) {
    return ($null -ne $Display -and $null -ne $Identity -and -not $Display.HasExited -and
        $Display.StartTime -eq $ExpectedStartTime -and
        [int]$Identity.ParentProcessId -eq $OwnerId -and $Identity.ExecutablePath -and
        [string]::Equals([IO.Path]::GetFullPath([string]$Identity.ExecutablePath), $ExpectedPath,
            [StringComparison]::OrdinalIgnoreCase))
}

function Stop-OwnedDisplay([Diagnostics.Process]$Owner, [Diagnostics.Process]$Display,
    [string]$ExpectedPath, [DateTime]$ExpectedStartTime) {
    Require-Shell ($null -ne $Owner -and $Owner.HasExited) 'HOST_MUST_EXIT_BEFORE_DISPLAY_CLEANUP'
    if ($null -eq $Display) { return $true }
    try {
        $Display.Refresh()
        $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($Display.Id)"
        if ($Display.HasExited) { return $true }
        if (-not (Test-OwnedDisplayIdentity $Display $identity $Owner.Id $ExpectedPath $ExpectedStartTime)) {
            return $false
        }
        $null = $Display.CloseMainWindow()
        if ($Display.WaitForExit(5000)) { return $true }
        $Display.Refresh()
        $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($Display.Id)"
        if ($Display.HasExited) { return $true }
        if (-not (Test-OwnedDisplayIdentity $Display $identity $Owner.Id $ExpectedPath $ExpectedStartTime)) {
            return $false
        }
        $Display.Kill($true)
        return $Display.WaitForExit(5000)
    } catch [ArgumentException] {
        return $true
    }
}

function Send-WindowText([Diagnostics.Process]$Owner, [Diagnostics.Process]$Display,
    [IntPtr]$Window, [string]$Command, [string]$ExpectedPath, [DateTime]$DisplayStartTime,
    [switch]$CancelPending) {
    Require-Shell ($Command.Length -in 1..240 -and $Command -cmatch '^[\x20-\x7E]+$') 'WINDOW_INPUT_INVALID'
    Require-Shell (-not $Owner.HasExited) 'HOST_EXITED_BEFORE_INPUT'
    $Display.Refresh()
    $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($Display.Id)"
    Require-Shell (-not $Display.HasExited -and $Display.StartTime -eq $DisplayStartTime -and
        $Display.StartTime -ge $Owner.StartTime -and $Display.MainWindowHandle -eq $Window -and
        $null -ne $identity -and [int]$identity.ParentProcessId -eq $Owner.Id -and
        $identity.ExecutablePath -and
        [string]::Equals([IO.Path]::GetFullPath($identity.ExecutablePath), $ExpectedPath,
            [StringComparison]::OrdinalIgnoreCase) -and
        [ShellE2EWindowInput]::OwnsWindow($Window, $Display.Id)) 'WINDOW_IDENTITY_CHANGED'
    [ShellE2EWindowInput]::FocusWindow($Window, $Display.Id)
    if ($CancelPending) {
        # Cancel an unfinished PS7 command before appending the fixture's exit command.
        [ShellE2EWindowInput]::CancelInput($Window, $Display.Id)
        Start-Sleep -Milliseconds 200
    }
    [ShellE2EWindowInput]::SendText($Window, $Display.Id, $Command)
}

function Assert-CompletedShellCommand($Inspection, [string]$ExpectedPathHash,
    [long]$AfterCommandId, [int]$ExpectedExitCode, [string]$CommandPathHash = '') {
    if ($CommandPathHash -ceq '') { $CommandPathHash = $ExpectedPathHash }
    $shell = $Inspection.shell_semantics
    Require-Shell ($null -ne $shell -and $null -ne $Inspection.terminal_semantics -and
        [string]$shell.cwd_path_sha256 -ceq $ExpectedPathHash -and
        [long]$shell.command_count -ge 1 -and [long]$shell.next_command_id -gt 1) 'SHELL_SEMANTICS_MISSING'
    $completed = @($shell.commands | Where-Object {
        [long]$_.id -gt $AfterCommandId -and [int]$_.phase -eq 3 -and
        $null -ne $_.exit_code -and [int]$_.exit_code -eq $ExpectedExitCode
    } | Sort-Object { [long]$_.id })
    if ($completed.Count -eq 0) { return $null }
    Require-Shell ($completed.Count -eq 1 -and [long]$completed[0].id -eq $AfterCommandId + 1) 'SHELL_COMMAND_ID_NOT_ADJACENT'
    $command = $completed[-1]
    Require-Shell ([string]$command.cwd_path_sha256 -ceq $CommandPathHash -and
        [long]$shell.next_command_id -gt [long]$command.id) 'SHELL_COMMAND_CWD_OR_ID_INVALID'
    $points = @($command.prompt, $command.input, $command.output, $command.end)
    Require-Shell (@($points | Where-Object { $null -eq $_ }).Count -eq 0) 'SHELL_COMMAND_ANCHOR_MISSING'
    $previousSequence = -1L
    $sourceNext = [long]$Inspection.terminal_semantics.next_source_sequence
    foreach ($point in $points) {
        Require-Shell ([long]$point.sequence -ge $previousSequence -and
            [long]$point.sequence -lt $sourceNext -and [long]$point.line_id -ge 0 -and
            [long]$point.cell_offset -ge 0 -and [int]$point.page -in 1..6 -and
            $point.alternate -is [bool]) 'SHELL_COMMAND_ANCHOR_INVALID'
        $previousSequence = [long]$point.sequence
    }
    return $command
}

function Test-RetryableShellObservation([string]$Code) {
    # These fields can lag one inspect snapshot while the terminal event is published.
    return $Code -cin @('SHELL_SEMANTICS_MISSING', 'SHELL_COMMAND_ANCHOR_MISSING')
}

function Wait-AndroidAck([long]$Before, [string]$BeforeHash, [guid]$SessionId,
    [string]$ExpectedPathHash, [long]$PreviousCommandId, [int]$ExpectedExitCode,
    [string]$CommandPathHash) {
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        $current = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker }
        Require-Shell ([guid]$current.session_id -eq $SessionId) 'SESSION_ID_CHANGED'
        $completed = $null
        try {
            $completed = Assert-CompletedShellCommand $current $ExpectedPathHash $PreviousCommandId $ExpectedExitCode $CommandPathHash
        }
        catch [InvalidOperationException] {
            if (-not (Test-RetryableShellObservation ((Get-SafeShellFailure $_).code))) { throw }
            $script:lastAnchorInspection = $current
        }
        if ([long]$current.live_ack -gt $Before -and
            [string]$current.visible_text_sha256 -cne $BeforeHash -and
            $current.realtime_subscriber_ready -eq $true -and
            $null -ne $completed) {
            return $current
        }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -ne $script:lastAnchorInspection) {
        [IO.File]::WriteAllText((Join-Path $runRoot 'command-anchor-invalid-inspect.json'),
            ($script:lastAnchorInspection | ConvertTo-Json -Depth 12) + "`n")
    }
    [IO.File]::WriteAllText((Join-Path $runRoot 'command-timeout-inspect.json'),
        ($current | ConvertTo-Json -Depth 12) + "`n")
    $evidence.screenshots += Save-Screenshot 'command-timeout.png'
    throw [TimeoutException]::new('ANDROID_COMMAND_ACK_TIMEOUT')
}

function Send-AndroidCommand([string]$Command, $Before, [guid]$SessionId,
    [string]$ExpectedPathHash, [long]$PreviousCommandId) {
    Require-Shell ($Command -cin @('pwd', 'throw', 'cd ..')) 'ANDROID_COMMAND_NOT_ALLOWED'
    Assert-ShellControl $Before $SessionId
    $ime = Invoke-Hook 'show_ime'
    Require-Shell ([guid]$ime.session_id -eq $SessionId -and $ime.ime_visible -eq $true) 'ANDROID_IME_NOT_READY'
    $inputText = if ($Command -ceq 'cd ..') { 'cd%s..' } else { $Command }
    $null = Require-Adb @('shell', 'input', 'text', $inputText) 'ANDROID_INPUT_FAILED'
    $null = Require-Adb @('shell', 'input', 'keyevent', 'KEYCODE_ENTER') 'ANDROID_ENTER_FAILED'
    $exitCode = if ($Command -ceq 'throw') { 1 } else { 0 }
    return Wait-AndroidAck ([long]$Before.live_ack) ([string]$Before.visible_text_sha256) $SessionId $ExpectedPathHash $PreviousCommandId $exitCode ([string]$Before.shell_semantics.cwd_path_sha256)
}

$script:responsePath = 'no_backup/tail-first-e2e/response.json'
$script:armPath = 'no_backup/tail-first-e2e/session-arm.json'
$script:pendingOperation = ''
$script:lastHookResponse = $null
$script:lastUiXml = $null
$script:lastAnchorInspection = $null
$arm = $null
$hostProcess = $null
$displayProcess = $null
$hostStdout = $null
$hostStderr = $null
$evidence = [ordered]@{
    schema_version = 1; run_id = $RunId; fixture = 'interactive-powershell7-real-shell-integration'
    status = 'running'; stage = 'preflight'; session_id = $null; host_pid = $null
    display_pid = $null; origin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority)
    screenshots = @(); command_ack = @(); host_exit_code = $null; host_forced_stop = $false
    shell_semantics_verified = $false; command_location_verified = $false
    directory_checks = @()
}

try {
    $null = New-Item -ItemType Directory -Path (Join-Path $runRoot $plan.working_directory_leaf) -Force
    $workDir = Join-Path $runRoot $plan.working_directory_leaf
    $expectedCwdHash = Get-WindowsPathHash ([IO.Path]::GetFullPath($workDir))
    $parentCwdHash = Get-WindowsPathHash $runRoot
    $sentinel = 'H01_' + $RunId.Replace('-', '_').Replace('.', '_') + '.txt'
    $parentSentinel = 'H01_PARENT_' + $RunId.Replace('-', '_').Replace('.', '_') + '.txt'
    [IO.File]::WriteAllText((Join-Path $workDir $sentinel), 'TermLink isolated shell fixture')
    [IO.File]::WriteAllText((Join-Path $runRoot $parentSentinel), 'TermLink parent shell fixture')
    if ([string]::IsNullOrWhiteSpace($AdbPath)) {
        $manifest = Get-Content -LiteralPath 'D:\Dev\DEV_ENV_MANIFEST.json' -Raw | ConvertFrom-Json
        $sdk = @($manifest.tools | Where-Object { $_.id -eq 'android-sdk' })
        Require-Shell ($sdk.Count -eq 1) 'ANDROID_SDK_MANIFEST_MISSING'
        $AdbPath = [IO.Path]::GetFullPath((Join-Path $manifest.roots.root $sdk[0].probes[0].relativePath))
    }
    $script:adbPath = [IO.Path]::GetFullPath($AdbPath)
    Require-Shell (Test-Path -LiteralPath $script:adbPath -PathType Leaf) 'ADB_MISSING'
    $resolvedHost = [IO.Path]::GetFullPath($SessionHostPath)
    $resolvedDisplay = [IO.Path]::GetFullPath($NativeDisplayPath)
    $controlAssembly = Join-Path (Split-Path -Parent $resolvedHost) 'TermLink.AgentControl.dll'
    Require-Shell (Test-Path -LiteralPath $controlAssembly -PathType Leaf) 'AGENT_CONTROL_ASSEMBLY_MISSING'
    Require-Shell ((Require-Adb @('get-state') 'EMULATOR_NOT_RUNNING').Trim() -ceq 'device') 'EMULATOR_NOT_RUNNING'
    Assert-AgentRelay $controlAssembly $evidence.origin
    $null = Require-Adb @('shell', 'am', 'start', '-n',
        "$AndroidPackageName/dev.termlink.product.ProductActivity") 'ANDROID_ACTIVITY_UNAVAILABLE'
    $probe = Invoke-ProbeWithStartupRetry
    Require-Shell ($probe.account_ready -eq $true -and
        ([uri]$probe.server_origin).GetLeftPart([UriPartial]::Authority) -ceq $evidence.origin) 'SERVER_OR_ACCOUNT_MISMATCH'

    $evidence.stage = 'arm'
    $openId = Start-Hook 'open_session' @{
        first_marker = $plan.first_marker; last_marker = $plan.last_marker
        live_marker = $plan.live_marker
        marker_count = '5200'; minimum_tail = '0'; history_bootstrap_mode = 'SnapshotTranscript'
    }
    $script:pendingOperation = 'open_session'
    $arm = Wait-Hook $script:armPath $openId 15
    $armAge = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [long]$arm.armed_at_epoch_millis
    Require-Shell ($arm.armed -eq $true -and [string]$arm.operation -ceq 'open_session' -and
        [string]$arm.run_id -ceq $RunId -and $armAge -ge 0 -and $armAge -le 1800000) 'ARM_MISMATCH'

    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    Require-Shell (Test-Path -LiteralPath $pwsh -PathType Leaf) 'POWERSHELL_7_MISSING'
    $commandLine = '"' + $pwsh + '" ' + $plan.shell_arguments
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $resolvedHost
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($item in @('--command-base64', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($commandLine)),
        '--managed-shell', $plan.managed_shell, '--registry-pipe', $AgentControlPipe,
        '--working-directory-base64', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($workDir)),
        '--native-display', $resolvedDisplay, '--terminal-state', 'terminal_state_2_22_v2',
        '--timeout-ms', '1800000')) { $start.ArgumentList.Add($item) }
    $evidence.stage = 'interactive-host'
    $hostProcess = [Diagnostics.Process]::Start($start)
    Require-Shell ($null -ne $hostProcess) 'SESSION_HOST_START_FAILED'
    $evidence.host_pid = $hostProcess.Id
    $hostStdout = $hostProcess.StandardOutput.ReadToEndAsync()
    $hostStderr = $hostProcess.StandardError.ReadToEndAsync()
    $displayProcess = Find-OwnedDisplay $hostProcess $resolvedDisplay
    $evidence.display_pid = $displayProcess.Id
    $window = $displayProcess.MainWindowHandle
    $displayStartTime = $displayProcess.StartTime
    Start-Sleep -Milliseconds 1500
    Send-WindowText $hostProcess $displayProcess $window $plan.generator_command $resolvedDisplay $displayStartTime

    $evidence.stage = 'android-open'
    $opened = Wait-Hook $script:responsePath $openId 180 -ResponseRequired
    Require-Shell ([guid]$opened.session_id -ne [guid]::Empty -and
        $opened.last_marker_visible -eq $true -and $opened.realtime_subscriber_ready -eq $true -and
        [string]$opened.session_id -cnotin @($arm.baseline_session_ids)) 'SESSION_NOT_ISOLATED'
    $sessionId = [guid]$opened.session_id
    $evidence.session_id = "$sessionId"
    $before = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker }
    [IO.File]::WriteAllText((Join-Path $runRoot 'startup-inspect.json'),
        ($before | ConvertTo-Json -Depth 12) + "`n")
    $evidence.screenshots += Save-Screenshot 'startup.png'
    Require-Shell ([guid]$before.session_id -eq $sessionId -and
        $before.live_marker_visible -eq $true) 'STARTUP_MARKER_NOT_VISIBLE'
    $startup = Assert-CompletedShellCommand $before $expectedCwdHash 0 0
    Require-Shell ($null -ne $startup) 'STARTUP_SHELL_COMMAND_MISSING'
    $previousCommandId = [long]$startup.id
    $pwdCommandId = 0L
    $before = Acquire-ShellControl $sessionId
    Assert-ShellControl $before $sessionId
    [IO.File]::WriteAllText((Join-Path $runRoot 'control-inspect.json'),
        ($before | ConvertTo-Json -Depth 12) + "`n")

    foreach ($command in @($plan.android_commands)) {
        $evidence.stage = 'android-' + $command
        $after = Send-AndroidCommand $command $before $sessionId $expectedCwdHash $previousCommandId
        $completed = Assert-CompletedShellCommand $after $expectedCwdHash $previousCommandId $(if ($command -ceq 'throw') { 1 } else { 0 })
        Require-Shell ($null -ne $completed -and [long]$completed.id -gt $previousCommandId) 'SHELL_COMMAND_NOT_COMPLETED'
        $evidence.command_ack += [ordered]@{ command = $command; before = [long]$before.live_ack
            after = [long]$after.live_ack; command_id = [long]$completed.id; exit_code = [int]$completed.exit_code }
        if ($command -ceq 'pwd') { $pwdCommandId = [long]$completed.id }
        $previousCommandId = [long]$completed.id
        $before = $after
    }
    Require-Shell ($pwdCommandId -gt 0) 'PWD_COMMAND_ID_MISSING'
    Send-WindowText $hostProcess $displayProcess $window $plan.location_filler_command $resolvedDisplay $displayStartTime
    $before = Wait-AndroidAck ([long]$before.live_ack) ([string]$before.visible_text_sha256) $sessionId $expectedCwdHash $previousCommandId 0 $expectedCwdHash
    $filler = Assert-CompletedShellCommand $before $expectedCwdHash $previousCommandId 0
    Require-Shell ($null -ne $filler) 'LOCATION_FILLER_MISSING'
    $previousCommandId = [long]$filler.id
    $evidence.shell_semantics_verified = $true
    $evidence.stage = 'shell-ui'
    Open-Menu
    Tap-Ui @('命令定位', 'Command markers') 'COMMAND_MENU_MISSING'
    Require-Shell ($null -ne (Find-UiNode @('退出码 0', 'Exit 0')) -and
        $null -ne (Find-UiNode @('退出码 1', 'Exit 1')) -and
        $null -ne (Find-UiNode @('中文 空格'))) 'SHELL_COMMAND_PHASE_OR_CWD_MISSING'
    $evidence.screenshots += Save-Screenshot 'shell-command-markers.png'
    $locationBefore = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker }
    Require-Shell (-not (Test-TerminalSemanticMarker $locationBefore.terminal_semantics 'pwd')) 'PWD_ALREADY_VISIBLE'
    Tap-Ui @("#$pwdCommandId ·") 'SHELL_COMMAND_ROW_MISSING'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $located = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker }
        Require-Shell ([guid]$located.session_id -eq $sessionId) 'SESSION_ID_CHANGED'
        if ([string]$located.visible_text_sha256 -cne [string]$locationBefore.visible_text_sha256 -and
            [string]$located.viewport_signature -cne [string]$locationBefore.viewport_signature -and
            (Test-TerminalSemanticMarker $located.terminal_semantics 'pwd')) { break }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-Shell ([string]$located.visible_text_sha256 -cne [string]$locationBefore.visible_text_sha256 -and
        [string]$located.viewport_signature -cne [string]$locationBefore.viewport_signature -and
        (Test-TerminalSemanticMarker $located.terminal_semantics 'pwd')) 'SHELL_COMMAND_LOCATION_FAILED'
    $evidence.command_location_verified = $true
    $evidence.screenshots += Save-Screenshot 'shell-command-located.png'
    Open-Menu
    Tap-Ui @('打开当前目录', 'Open current directory') 'OPEN_DIRECTORY_MISSING'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($null -ne (Find-UiNode @($sentinel)) -and
            $null -ne (Find-UiNode @('中文 空格'))) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-Shell ($null -ne (Find-UiNode @($sentinel)) -and
        $null -ne (Find-UiNode @('中文 空格'))) 'SHELL_DIRECTORY_NOT_OPENED'
    $evidence.screenshots += Save-Screenshot 'shell-directory.png'
    $evidence.directory_checks += [ordered]@{ cwd_path_sha256 = $expectedCwdHash; sentinel = $sentinel }

    Hide-Keyboard
    Tap-Ui @('CLI') 'CLI_NAV_MISSING'
    $before = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker } 60
    Require-Shell ([guid]$before.session_id -eq $sessionId -and
        [string]$before.shell_semantics.cwd_path_sha256 -ceq $expectedCwdHash) 'CLI_RETURN_MISMATCH'
    $before = Acquire-ShellControl $sessionId
    $evidence.stage = 'android-cd-parent'
    $after = Send-AndroidCommand $plan.directory_change_command $before $sessionId $parentCwdHash $previousCommandId
    $completed = Assert-CompletedShellCommand $after $parentCwdHash $previousCommandId 0 $expectedCwdHash
    Require-Shell ($null -ne $completed -and [long]$completed.id -gt $previousCommandId) 'SHELL_DIRECTORY_CHANGE_MISSING'
    $evidence.command_ack += [ordered]@{ command = $plan.directory_change_command; before = [long]$before.live_ack
        after = [long]$after.live_ack; command_id = [long]$completed.id; exit_code = [int]$completed.exit_code }
    $evidence.stage = 'shell-parent-directory'
    Open-Menu
    Tap-Ui @('打开当前目录', 'Open current directory') 'OPEN_PARENT_DIRECTORY_MISSING'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($null -ne (Find-UiNode @($parentSentinel)) -and
            $null -ne (Find-UiNode @('中文 空格'))) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-Shell ($null -ne (Find-UiNode @($parentSentinel)) -and
        $null -ne (Find-UiNode @('中文 空格'))) 'SHELL_PARENT_DIRECTORY_NOT_OPENED'
    $evidence.screenshots += Save-Screenshot 'shell-parent-directory.png'
    $evidence.directory_checks += [ordered]@{ cwd_path_sha256 = $parentCwdHash; sentinel = $parentSentinel }
    $evidence.status = 'passed'
    $evidence.stage = 'complete'
} catch {
    $evidence.status = 'failed'
    $safe = Get-SafeShellFailure $_
    $evidence.failure_code = $safe.code
    $evidence.failure_type = $safe.type
    $evidence.failure_line = $safe.line
    if ($evidence.stage -ceq 'android-open' -and $null -ne $script:lastHookResponse) {
        [IO.File]::WriteAllText((Join-Path $runRoot 'open-failure-response.json'),
            ($script:lastHookResponse | ConvertTo-Json -Depth 12) + "`n")
    }
    if ($null -ne $hostProcess -and $null -ne $arm) {
        try {
            $failedState = Invoke-Hook 'inspect' @{ live_marker = $plan.live_marker } 20
            Require-Shell ([guid]$failedState.session_id -ne [guid]::Empty -and
                [string]$failedState.session_id -cnotin @($arm.baseline_session_ids)) 'FAILURE_SESSION_NOT_ISOLATED'
            if ($null -ne $evidence.session_id) {
                Require-Shell ([guid]$failedState.session_id -eq [guid]$evidence.session_id) 'FAILURE_SESSION_CHANGED'
            }
            [IO.File]::WriteAllText((Join-Path $runRoot 'failure-inspect.json'),
                ($failedState | ConvertTo-Json -Depth 12) + "`n")
            if ($null -ne $script:lastUiXml) {
                [IO.File]::WriteAllText((Join-Path $runRoot 'failure-ui.xml'), $script:lastUiXml)
            }
            $evidence.screenshots += Save-Screenshot 'failure.png'
        } catch { $evidence.failure_capture_code = (Get-SafeShellFailure $_).code }
    }
} finally {
    if ($null -ne $hostProcess) {
        try {
            if (-not $hostProcess.HasExited -and $null -ne $displayProcess) {
                try { Send-WindowText $hostProcess $displayProcess $window 'exit 0' $resolvedDisplay $displayStartTime -CancelPending }
                catch { $evidence.cleanup_error_code = 'OWNED_WINDOW_INPUT_SKIPPED' }
            }
            if (-not $hostProcess.WaitForExit(15000)) {
                $evidence.host_forced_stop = $true
                $hostProcess.Kill($true)
                $null = $hostProcess.WaitForExit(5000)
            }
            if ($hostProcess.HasExited) {
                $evidence.host_exit_code = $hostProcess.ExitCode
            }
        } catch {
            $evidence.cleanup_error_code = 'OWN_HOST_CLEANUP_FAILED'
            $evidence.status = 'failed'
        }
        $hostEnded = $hostProcess.HasExited
        if ($hostEnded -and $null -ne $displayProcess) {
            try {
                if (-not (Stop-OwnedDisplay $hostProcess $displayProcess $resolvedDisplay $displayStartTime)) {
                    $evidence.cleanup_error_code = 'OWNED_DISPLAY_IDENTITY_CHANGED'
                    $evidence.status = 'failed'
                }
            } catch {
                $evidence.cleanup_error_code = 'OWNED_DISPLAY_CLEANUP_FAILED'
                $evidence.status = 'failed'
            }
        }
        $hostProcess.Dispose()
    }
    if ($null -ne $displayProcess) { $displayProcess.Dispose() }
    if ($evidence.host_forced_stop -or $evidence.host_exit_code -ne 0) {
        $evidence.status = 'failed'
        if (-not $evidence.Contains('failure_code')) { $evidence.failure_code = 'HOST_EXIT_NOT_CLEAN' }
    }
    [IO.File]::WriteAllText((Join-Path $runRoot 'shell-integration-evidence.json'),
        (($evidence | ConvertTo-Json -Depth 6) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}
if ($evidence.status -eq 'failed') { throw [InvalidOperationException]::new([string]$evidence.failure_code) }
$evidence | ConvertTo-Json -Depth 6
