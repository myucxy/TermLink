using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using TermLink.Agent;

namespace TermLink.Tray
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // DPI awareness must be selected before Agent startup, argument
            // diagnostics, or any failure MessageBox can create a UI handle.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            try
            {
                var options = TrayOptions.Parse(args);
                if (!string.IsNullOrEmpty(options.StatusJsonPath))
                {
                    var response = ExternalAgentDiagnostics.Send(
                        options,
                        new RegistryRequest { Command = RegistryCommand.List },
                        10000);
                    // Pairing challenges contain a short-lived code and nonce. They
                    // are available only inside the interactive pairing surface and
                    // must never be written to diagnostic JSON.
                    if (response.Production != null)
                    {
                        response.Production.PairingChallenge = null;
                    }
                    var destination = Path.GetFullPath(options.StatusJsonPath);
                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(
                        destination,
                        JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }) +
                            Environment.NewLine,
                        new UTF8Encoding(false));
                    return response.Success ? 0 : 1;
                }
                if (options.ShutdownAgent)
                {
                    var response = ExternalAgentDiagnostics.Send(
                        options,
                        new RegistryRequest { Command = RegistryCommand.Shutdown },
                        20000);
                    return response.Success ? 0 : 1;
                }
                if (options.RestartAgent)
                {
                    var response = ExternalAgentDiagnostics.Send(
                        options,
                        new RegistryRequest { Command = RegistryCommand.RestartAgent },
                        20000);
                    return response.Success ? 0 : 1;
                }

                using var controller = new AgentController(options);
                bool ownsTrayMutex;
                using (var trayMutex = new Mutex(
                    true,
                    "Local\\" + options.ControlPipeName + "-tray",
                    out ownsTrayMutex))
                {
                    if (!ownsTrayMutex) return 0;
                    // Start or attach to the independent Agent without blocking
                    // the WinForms message loop on account/session recovery.
                    controller.StartInteractive();
                    // The shell is sized in device-independent pixels and is safe to
                    // move between monitors with different scale factors.
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApplicationContext(controller));
                    return 0;
                }
            }
            catch (Exception error)
            {
                WriteStartupFailure(error);
                if (Array.IndexOf(args ?? Array.Empty<string>(), "--status-json") < 0 &&
                    Array.IndexOf(args ?? Array.Empty<string>(), "--shutdown-agent") < 0 &&
                    Array.IndexOf(args ?? Array.Empty<string>(), "--restart-agent") < 0)
                {
                    MessageBox.Show(
                        TrayText.Pick(
                            "TermLink 启动失败：" + error.Message + "\n\n诊断日志已写入本机 TermLink 日志目录。",
                            "TermLink could not start: " + error.Message + "\n\nA diagnostic was written to the local TermLink log directory."),
                        "TermLink",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return 125;
            }
        }

        private static void WriteStartupFailure(Exception error)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink", "logs");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "TermLink-startup.log"),
                    DateTime.UtcNow.ToString("O") + Environment.NewLine + error + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never replace the original startup failure.
            }
        }
    }

    internal static class ExternalAgentDiagnostics
    {
        public static RegistryResponse Send(
            TrayOptions options,
            RegistryRequest request,
            int timeoutMilliseconds)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return RegistryControlClient.Send(
                options.ControlPipeName,
                request,
                timeoutMilliseconds);
        }
    }

    internal sealed class TrayOptions
    {
        public string ControlPipeName { get; private set; }
        public string BufferRoot { get; private set; }
        public string ServerUrl { get; private set; }
        public string StatusJsonPath { get; private set; }
        public bool ShutdownAgent { get; private set; }
        public bool RestartAgent { get; private set; }

        public static TrayOptions Parse(string[] args)
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var bufferRoot = Path.Combine(localData, "TermLink", "Agent");
            var pipeName = TermLinkControlPipe.DefaultName();
            var serverUrl = ProductionEnrollment.PublicServerUrl;
            string statusJson = null;
            var shutdown = false;
            var restart = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--control-pipe": pipeName = Required(args, ref index, "--control-pipe"); break;
                    case "--buffer-root": bufferRoot = Required(args, ref index, "--buffer-root"); break;
                    case "--server-url": serverUrl = Required(args, ref index, "--server-url"); break;
                    case "--status-json": statusJson = Required(args, ref index, "--status-json"); break;
                    case "--shutdown-agent": shutdown = true; break;
                    case "--restart-agent": restart = true; break;
                    default: throw new ArgumentException("Unknown Tray argument: " + args[index]);
                }
            }
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
                pipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("control pipe name is invalid");
            }
            if (!TrayInputValidation.TryNormalizeServerOrigin(serverUrl, out var serverOrigin))
            {
                throw new ArgumentException("server URL must be an HTTPS origin");
            }
            var commandCount = (shutdown ? 1 : 0) + (restart ? 1 : 0) +
                (!string.IsNullOrEmpty(statusJson) ? 1 : 0);
            if (commandCount > 1)
            {
                throw new ArgumentException("Tray command-line actions cannot be combined.");
            }
            return new TrayOptions
            {
                ControlPipeName = pipeName,
                BufferRoot = Path.GetFullPath(bufferRoot),
                ServerUrl = serverOrigin,
                StatusJsonPath = statusJson,
                ShutdownAgent = shutdown,
                RestartAgent = restart
            };
        }

        private static string Required(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException(option + " requires a value.");
            }
            return args[index];
        }

    }

    internal sealed class AgentController : IDisposable
    {
        private readonly TrayOptions options;
        private readonly object lifecycleSync = new object();
        private readonly ExternalAgentHost externalHost;
        private bool disposed;

        public AgentController(TrayOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            externalHost = new ExternalAgentHost(options.ControlPipeName, options.BufferRoot);
        }

        public string ServerUrl { get { return options.ServerUrl; } }

        public void StartInteractive()
        {
            lock (lifecycleSync)
            {
                ThrowIfDisposed();
                Directory.CreateDirectory(options.BufferRoot);
                externalHost.BeginStart();
            }
        }

        public RegistryResponse EnsureAgent()
        {
            lock (lifecycleSync) return EnsureAgentCore();
        }

        private RegistryResponse EnsureAgentCore()
        {
            ThrowIfDisposed();
            Directory.CreateDirectory(options.BufferRoot);
            externalHost.Start();
            return externalHost.Dispatch(new RegistryRequest { Command = RegistryCommand.List });
        }

        public RegistryResponse Send(RegistryCommand command)
        {
            return SendRequest(new RegistryRequest { Command = command });
        }

        private RegistryResponse SendRequest(RegistryRequest request)
        {
            lock (lifecycleSync)
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                ThrowIfDisposed();
                Directory.CreateDirectory(options.BufferRoot);
                return externalHost.Dispatch(request);
            }
        }

        /// <summary>
        /// Stops the registry Agent and every managed SessionHost left from this
        /// installation. The tray exit action must not leave a background relay
        /// connection alive after its UI has disappeared, so a graceful registry
        /// shutdown is followed by a path-scoped process sweep and a last-resort
        /// tree kill for processes that did not terminate in time.
        /// </summary>
        public void ShutdownAndWait()
        {
            lock (lifecycleSync) ShutdownAndWaitCore();
        }

        private void ShutdownAndWaitCore()
        {
            externalHost.Shutdown();

            WaitForOwnedProcesses(DateTime.UtcNow.AddSeconds(10));
            foreach (var process in GetOwnedProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between enumeration and Kill.
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Do not broaden the kill scope when a process cannot be inspected.
                    }
                    try { process.WaitForExit(5000); }
                    catch (InvalidOperationException) { }
                }
            }
            WaitForOwnedProcesses(DateTime.UtcNow.AddSeconds(5));
        }

        public RegistryResponse StopSession(Guid sessionId)
        {
            return SendRequest(
                new RegistryRequest { Command = RegistryCommand.StopSession, SessionId = sessionId });
        }

        public RegistryResponse RefreshProductionStatus()
        {
            return Send(RegistryCommand.RefreshProductionStatus);
        }

        public RegistryResponse RetryProductionConnection()
        {
            return Send(RegistryCommand.RetryProductionConnection);
        }

        public RegistryResponse CreatePairingChallenge(Guid deviceId, string permission)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
            permission = (permission ?? string.Empty).Trim().ToUpperInvariant();
            if (permission != "VIEW" && permission != "CONTROL" && permission != "MANAGE")
                throw new ArgumentException("A supported pairing permission is required.", nameof(permission));
            return SendRequest(
                new RegistryRequest
                {
                    Command = RegistryCommand.CreatePairingChallenge,
                    DeviceId = deviceId,
                    PairingPermission = permission
                });
        }

        public RegistryResponse GrantDevice(Guid deviceId, string permission)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
            permission = ProductionDeviceGrantContract.NormalizePermission(permission);
            return SendRequest(
                new RegistryRequest
                {
                    Command = RegistryCommand.GrantDevice,
                    DeviceId = deviceId,
                    GrantPermission = permission
                });
        }

        public RegistryResponse RevokeDevice(Guid deviceId)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
            return SendRequest(
                new RegistryRequest
                {
                    Command = RegistryCommand.RevokeDevice,
                    DeviceId = deviceId
                });
        }

        public RegistryResponse RestartAgent()
        {
            lock (lifecycleSync)
            {
                ThrowIfDisposed();
                return externalHost.Restart();
            }
        }

        /// <summary>
        /// Ends the in-memory account session before removing the two known
        /// production account files, then starts a fresh unconfigured Agent.
        /// This is deliberately separate from tray exit, which preserves the
        /// local account for the next launch.
        /// </summary>
        public RegistryResponse ForgetLocalAccountAndRestart()
        {
            lock (lifecycleSync)
            {
                ShutdownAndWaitCore();
                Exception forgetError = null;
                try
                {
                    ProductionEnrollment.ForgetLocalAccount();
                }
                catch (Exception error)
                {
                    forgetError = error;
                }

                RegistryResponse response = null;
                Exception restartError = null;
                try
                {
                    response = EnsureAgentCore();
                    if (response == null || !response.Success ||
                        (response.Production != null &&
                            (response.Production.Configured || response.Production.AccountLinked)))
                    {
                        throw new InvalidOperationException("The restarted Agent is not in a signed-out state.");
                    }
                }
                catch (Exception error)
                {
                    restartError = error;
                }
                if (forgetError != null || restartError != null)
                {
                    var errors = new List<Exception>();
                    if (forgetError != null) errors.Add(forgetError);
                    if (restartError != null) errors.Add(restartError);
                    throw new AggregateException(
                        "The local TermLink account could not be forgotten and restarted safely.",
                        errors);
                }
                return response;
            }
        }

        public void LinkAccount(string pcName)
        {
            if (!TrayInputValidation.TryNormalizePcName(pcName, out var normalizedPcName))
                throw new ArgumentException("A valid PC name is required.", nameof(pcName));
            lock (lifecycleSync)
            {
                ProductionEnrollment.LinkAccount(options.ServerUrl, normalizedPcName);
            }
        }

        public void LinkAccount(string serverUrl, string pcName)
        {
            if (!TrayInputValidation.TryNormalizeServerOrigin(serverUrl, out var serverOrigin) ||
                !string.Equals(serverOrigin, options.ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The account-link server must match the Tray server URL.", nameof(serverUrl));
            }
            LinkAccount(pcName);
        }

        public void LoginAccount(string username, string password, string pcName)
        {
            lock (lifecycleSync)
            {
                ProductionEnrollment.LoginDirect(options.ServerUrl, pcName, username, password);
            }
        }

        public void RegisterAccount(string username, string email, string password, string confirm, string pcName)
        {
            lock (lifecycleSync)
            {
                ProductionEnrollment.RegisterDirect(options.ServerUrl, pcName, username, email, password, confirm);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(AgentController));
        }

        private void WaitForOwnedProcesses(DateTime deadlineUtc)
        {
            while (DateTime.UtcNow < deadlineUtc)
            {
                var processes = GetOwnedProcesses();
                var hasProcesses = processes.Count != 0;
                foreach (var process in processes) process.Dispose();
                if (!hasProcesses) return;
                Thread.Sleep(50);
            }
        }

        private List<Process> GetOwnedProcesses()
        {
            var result = new List<Process>();
            var installRoot = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(installRoot)) return result;
            installRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            foreach (var name in new[] { "TermLink.Agent", "TermLink.SessionHost" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = process.MainModule == null ? null : process.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            Path.GetFullPath(path).StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(process);
                            continue;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                    process.Dispose();
                }
            }
            return result;
        }

        public void Dispose()
        {
            lock (lifecycleSync)
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    externalHost.Dispose();
                }
                catch (Exception error)
                {
                    // Exiting the UI must not be reclassified as a startup
                    // failure merely because the already-stopping background
                    // runtime reported a late network/account cleanup error.
                    Debug.WriteLine("TermLink external Agent supervisor cleanup failed: " + error);
                }
            }
        }
    }
}
