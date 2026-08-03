using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
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
            try
            {
                var options = TrayOptions.Parse(args);
                var controller = new AgentController(options);
                if (!string.IsNullOrEmpty(options.StatusJsonPath))
                {
                    var response = controller.EnsureAgent();
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
                    var response = controller.Send(RegistryCommand.Shutdown);
                    return response.Success ? 0 : 1;
                }
                if (options.RestartAgent)
                {
                    var response = controller.RestartAgent();
                    return response.Success ? 0 : 1;
                }

                bool ownsTrayMutex;
                using (var trayMutex = new Mutex(
                    true,
                    "Local\\" + options.ControlPipeName + "-tray",
                    out ownsTrayMutex))
                {
                    if (!ownsTrayMutex) return 0;
                    Application.SetHighDpiMode(HighDpiMode.SystemAware);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApplicationContext(controller));
                    return 0;
                }
            }
            catch
            {
                return 125;
            }
        }
    }

    internal sealed class TrayOptions
    {
        public string ControlPipeName { get; private set; }
        public string BufferRoot { get; private set; }
        public string AgentExecutable { get; private set; }
        public string StatusJsonPath { get; private set; }
        public bool ShutdownAgent { get; private set; }
        public bool RestartAgent { get; private set; }

        public static TrayOptions Parse(string[] args)
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var bufferRoot = Path.Combine(localData, "TermLink", "Agent");
            var agentExecutable = Path.Combine(AppContext.BaseDirectory, "TermLink.Agent.exe");
            var pipeName = DefaultControlPipeName();
            string statusJson = null;
            var shutdown = false;
            var restart = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--control-pipe": pipeName = Required(args, ref index, "--control-pipe"); break;
                    case "--buffer-root": bufferRoot = Required(args, ref index, "--buffer-root"); break;
                    case "--agent-exe": agentExecutable = Required(args, ref index, "--agent-exe"); break;
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
                AgentExecutable = Path.GetFullPath(agentExecutable),
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

        private static string DefaultControlPipeName()
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity.User == null) throw new InvalidOperationException("Current Windows SID is unavailable.");
            var sid = Encoding.UTF8.GetBytes(identity.User.Value);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(sid);
            var suffix = new StringBuilder(16);
            for (var index = 0; index < 8; index++) suffix.Append(hash[index].ToString("x2"));
            return "termlink-agent-control-" + suffix;
        }
    }

    internal sealed class AgentController
    {
        private readonly TrayOptions options;

        public AgentController(TrayOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public RegistryResponse EnsureAgent()
        {
            RegistryResponse response;
            if (TryList(250, out response)) return response;
            if (!File.Exists(options.AgentExecutable))
            {
                throw new FileNotFoundException("TermLink Agent executable was not found.", options.AgentExecutable);
            }
            Directory.CreateDirectory(options.BufferRoot);
            var start = new ProcessStartInfo
            {
                FileName = options.AgentExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--registry");
            start.ArgumentList.Add("--control-pipe");
            start.ArgumentList.Add(options.ControlPipeName);
            start.ArgumentList.Add("--buffer-root");
            start.ArgumentList.Add(options.BufferRoot);
            var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("TermLink Agent could not be started.");
            process.Dispose();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (TryList(500, out response)) return response;
                Thread.Sleep(50);
            }
            throw new TimeoutException("TermLink Agent did not open its control pipe.");
        }

        public RegistryResponse Send(RegistryCommand command)
        {
            return RegistryControlClient.Send(
                options.ControlPipeName,
                new RegistryRequest { Command = command },
                5000);
        }

        public RegistryResponse StopSession(Guid sessionId)
        {
            return RegistryControlClient.Send(
                options.ControlPipeName,
                new RegistryRequest { Command = RegistryCommand.StopSession, SessionId = sessionId },
                7000);
        }

        public RegistryResponse RefreshProductionStatus()
        {
            return RegistryControlClient.Send(
                options.ControlPipeName,
                new RegistryRequest { Command = RegistryCommand.RefreshProductionStatus },
                7000);
        }

        public RegistryResponse CreatePairingChallenge(Guid deviceId)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
            return RegistryControlClient.Send(
                options.ControlPipeName,
                new RegistryRequest
                {
                    Command = RegistryCommand.CreatePairingChallenge,
                    DeviceId = deviceId
                },
                10000);
        }

        public RegistryResponse RevokeDevice(Guid deviceId)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
            return RegistryControlClient.Send(
                options.ControlPipeName,
                new RegistryRequest
                {
                    Command = RegistryCommand.RevokeDevice,
                    DeviceId = deviceId
                },
                10000);
        }

        public RegistryResponse RestartAgent()
        {
            var response = Send(RegistryCommand.RestartAgent);
            if (!response.Success) return response;
            Thread.Sleep(100);
            return EnsureAgent();
        }

        private bool TryList(int timeoutMilliseconds, out RegistryResponse response)
        {
            try
            {
                response = RegistryControlClient.Send(
                    options.ControlPipeName,
                    new RegistryRequest { Command = RegistryCommand.List },
                    timeoutMilliseconds);
                return response.Success;
            }
            catch (IOException)
            {
            }
            catch (TimeoutException)
            {
            }
            response = null;
            return false;
        }
    }
}
