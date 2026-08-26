using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.Tray
{
    internal sealed class ExternalAgentHost : IDisposable
    {
        private readonly string controlPipeName;
        private readonly string bufferRoot;
        private readonly string agentPath;
        private readonly object sync = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Task startup;
        private bool disposed;

        public ExternalAgentHost(string controlPipeName, string bufferRoot)
        {
            // Keep the Agent's DPAPI implementation assembly in development and
            // framework-dependent Tray layouts; release publishing also carries it.
            if (typeof(ProtectedData).Assembly == null)
                throw new InvalidOperationException("The DPAPI runtime dependency is unavailable.");
            if (string.IsNullOrWhiteSpace(controlPipeName))
                throw new ArgumentException("A control pipe is required.", "controlPipeName");
            if (string.IsNullOrWhiteSpace(bufferRoot))
                throw new ArgumentException("A buffer root is required.", "bufferRoot");
            this.controlPipeName = controlPipeName;
            this.bufferRoot = Path.GetFullPath(bufferRoot);
            var executableRoot = Path.GetDirectoryName(typeof(ExternalAgentHost).Assembly.Location);
            if (string.IsNullOrWhiteSpace(executableRoot)) executableRoot = AppContext.BaseDirectory;
            agentPath = Path.GetFullPath(Path.Combine(executableRoot, "TermLink.Agent.exe"));
        }

        public void BeginStart()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (startup == null || startup.IsFaulted || startup.IsCanceled)
                    startup = Task.Run(() => EnsureReady(lifetime.Token), lifetime.Token);
            }
        }

        public void Start()
        {
            BeginStart();
            Task active;
            lock (sync) active = startup;
            if (Task.WaitAny(new[] { active }, TimeSpan.FromSeconds(65)) < 0)
                throw new TimeoutException("The independent TermLink Agent did not become ready within 65 seconds.");
            active.GetAwaiter().GetResult();
        }

        public RegistryResponse Dispatch(RegistryRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            ThrowIfDisposed();
            try
            {
                return RegistryControlClient.Send(controlPipeName, request, 15_000);
            }
            catch (Exception error) when (IsConnectionFailure(error))
            {
                ResetStartup();
                Start();
                return RegistryControlClient.Send(controlPipeName, request, 15_000);
            }
        }

        public RegistryResponse Restart()
        {
            ThrowIfDisposed();
            var response = Dispatch(new RegistryRequest { Command = RegistryCommand.RestartAgent });
            if (!response.Success) return response;
            WaitForPipeToStop(DateTime.UtcNow.AddSeconds(20));
            ResetStartup();
            Start();
            return RegistryControlClient.Send(
                controlPipeName,
                new RegistryRequest { Command = RegistryCommand.List },
                15_000);
        }

        public void Shutdown()
        {
            ThrowIfDisposed();
            try
            {
                RegistryControlClient.Send(
                    controlPipeName,
                    new RegistryRequest { Command = RegistryCommand.Shutdown },
                    20_000);
            }
            catch (Exception error) when (IsConnectionFailure(error))
            {
                // No authenticated Agent is serving this installation.
            }
            WaitForPipeToStop(DateTime.UtcNow.AddSeconds(20));
            ResetStartup();
        }

        private void EnsureReady(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(bufferRoot);
            if (TryGetStatus(750, out _)) return;
            if (!File.Exists(agentPath))
                throw new FileNotFoundException("The independent TermLink Agent executable is missing.", agentPath);

            Process process;
            var startInfo = new ProcessStartInfo
            {
                FileName = agentPath,
                WorkingDirectory = Path.GetDirectoryName(agentPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--registry");
            startInfo.ArgumentList.Add("--control-pipe");
            startInfo.ArgumentList.Add(controlPipeName);
            startInfo.ArgumentList.Add("--buffer-root");
            startInfo.ArgumentList.Add(bufferRoot);
            process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("The independent TermLink Agent did not start.");
            using (process)
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                DateTime? exitedAt = null;
                int? exitCode = null;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryGetStatus(750, out _)) return;
                    if (process.HasExited && !exitedAt.HasValue)
                    {
                        exitedAt = DateTime.UtcNow;
                        exitCode = process.ExitCode;
                    }
                    if (exitedAt.HasValue && DateTime.UtcNow - exitedAt.Value > TimeSpan.FromSeconds(3))
                    {
                        throw new InvalidOperationException(
                            "The independent TermLink Agent exited before its authenticated control pipe became ready " +
                            "(exit " + exitCode + ").");
                    }
                    if (cancellationToken.WaitHandle.WaitOne(100))
                        cancellationToken.ThrowIfCancellationRequested();
                }
            }
            throw new TimeoutException("The independent TermLink Agent did not expose its authenticated control pipe.");
        }

        private bool TryGetStatus(int timeoutMilliseconds, out RegistryResponse response)
        {
            try
            {
                response = RegistryControlClient.Send(
                    controlPipeName,
                    new RegistryRequest { Command = RegistryCommand.List },
                    timeoutMilliseconds);
                return response != null && response.Success;
            }
            catch (Exception error) when (IsConnectionFailure(error))
            {
                response = null;
                return false;
            }
        }

        private void WaitForPipeToStop(DateTime deadlineUtc)
        {
            while (DateTime.UtcNow < deadlineUtc)
            {
                if (!TryGetStatus(250, out _)) return;
                Thread.Sleep(75);
            }
            throw new TimeoutException("The independent TermLink Agent did not release its control pipe.");
        }

        private void ResetStartup()
        {
            lock (sync)
            {
                if (startup == null || startup.IsCompleted) startup = null;
            }
        }

        private static bool IsConnectionFailure(Exception error)
        {
            return error is IOException ||
                error is TimeoutException ||
                error is UnauthorizedAccessException ||
                error is InvalidDataException ||
                error is CryptographicException ||
                error is Win32Exception;
        }

        private void ThrowIfDisposed()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ExternalAgentHost));
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                lifetime.Cancel();
            }
            // Disposing the UI supervisor intentionally does not stop the Agent.
            // Relay and SessionHost ownership remain in the independent process.
        }
    }
}
