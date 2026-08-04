using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TermLink.Agent
{
    public static class TermLinkControlPipe
    {
        public static string DefaultName()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null)
                {
                    throw new InvalidOperationException("Current Windows SID is unavailable.");
                }
                var sid = Encoding.UTF8.GetBytes(identity.User.Value);
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(sid);
                try
                {
                    var suffix = new StringBuilder(16);
                    for (var index = 0; index < 8; index++) suffix.Append(hash[index].ToString("x2"));
                    return "termlink-agent-control-" + suffix;
                }
                finally
                {
                    Array.Clear(sid, 0, sid.Length);
                    Array.Clear(hash, 0, hash.Length);
                }
            }
        }
    }

    public enum ManagedShellType : byte
    {
        CommandPrompt = 1,
        WindowsPowerShell = 2,
        PowerShell7 = 3,
        Wsl = 4,
        Custom = 5
    }

    public enum SessionAgentLifecycle : byte
    {
        Starting,
        Connected,
        Disconnected,
        Ended,
        RemoteStopped,
        HostLost,
        Faulted
    }

    public sealed class SessionRegistration
    {
        public const int HandshakeTokenBytes = 32;

        public SessionRegistration(
            Guid sessionId,
            string pipeName,
            byte[] handshakeToken,
            int expectedHostProcessId,
            ManagedShellType shellType)
            : this(
                sessionId,
                pipeName,
                handshakeToken,
                expectedHostProcessId,
                shellType,
                GetProcessStartTimeUtc(expectedHostProcessId),
                DateTime.UtcNow)
        {
        }

        public SessionRegistration(
            Guid sessionId,
            string pipeName,
            byte[] handshakeToken,
            int expectedHostProcessId,
            ManagedShellType shellType,
            DateTime expectedHostStartTimeUtc,
            DateTime registeredAtUtc)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("session ID is required", "sessionId");
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
                pipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("pipe name is invalid", "pipeName");
            }
            if (handshakeToken == null || handshakeToken.Length != HandshakeTokenBytes)
            {
                throw new ArgumentException("handshake token must contain 32 bytes", "handshakeToken");
            }
            if (expectedHostProcessId <= 0) throw new ArgumentOutOfRangeException("expectedHostProcessId");
            if (!Enum.IsDefined(typeof(ManagedShellType), shellType))
            {
                throw new ArgumentOutOfRangeException("shellType");
            }
            if (expectedHostStartTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("expected Host start time must be UTC", "expectedHostStartTimeUtc");
            }
            if (registeredAtUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("registration time must be UTC", "registeredAtUtc");
            }

            SessionId = sessionId;
            PipeName = pipeName;
            handshakeTokenBytes = (byte[])handshakeToken.Clone();
            ExpectedHostProcessId = expectedHostProcessId;
            ShellType = shellType;
            ExpectedHostStartTimeUtc = expectedHostStartTimeUtc;
            RegisteredAtUtc = registeredAtUtc;
        }

        private readonly byte[] handshakeTokenBytes;

        public Guid SessionId { get; private set; }
        public string PipeName { get; private set; }
        public byte[] HandshakeToken { get { return (byte[])handshakeTokenBytes.Clone(); } }
        public int ExpectedHostProcessId { get; private set; }
        public ManagedShellType ShellType { get; private set; }
        public DateTime ExpectedHostStartTimeUtc { get; private set; }
        public DateTime RegisteredAtUtc { get; private set; }

        public bool MatchesRunningHost()
        {
            try
            {
                return GetProcessStartTimeUtc(ExpectedHostProcessId).Ticks == ExpectedHostStartTimeUtc.Ticks;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static DateTime GetProcessStartTimeUtc(int processId)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException("processId");
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.StartTime.ToUniversalTime();
                }
            }
            catch (Exception error)
            {
                if (error is ArgumentException ||
                    error is InvalidOperationException ||
                    error is System.ComponentModel.Win32Exception)
                {
                    throw new ArgumentException("expected Host process is unavailable", "processId", error);
                }
                throw;
            }
        }
    }

    public sealed class SessionSummary
    {
        public Guid SessionId { get; set; }
        public ManagedShellType ShellType { get; set; }
        public SessionAgentLifecycle Lifecycle { get; set; }
        public int HostProcessId { get; set; }
        public ulong LastSequence { get; set; }
        public DateTime RegisteredAtUtc { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public int? ExitCode { get; set; }

        public string ToDiagnosticRecord()
        {
            return "session=" + SessionId.ToString("D") +
                ";shell=" + ShellType +
                ";state=" + Lifecycle +
                ";host_pid=" + HostProcessId +
                ";last_seq=" + LastSequence +
                ";registered_utc=" + RegisteredAtUtc.ToString("o") +
                ";last_activity_utc=" + LastActivityUtc.ToString("o") +
                ";exit=" + (ExitCode.HasValue ? ExitCode.Value.ToString() : "none");
        }
    }

    public enum RegistryCommand : byte
    {
        Register = 1,
        List = 2,
        StopSession = 3,
        StopAllRemoteAccess = 4,
        EnableRemoteAccess = 5,
        Shutdown = 6,
        RestartAgent = 7,
        RefreshProductionStatus = 8,
        CreatePairingChallenge = 9,
        RevokeDevice = 10
    }

    public sealed class RegistryRequest
    {
        public RegistryCommand Command { get; set; }
        public SessionRegistration Registration { get; set; }
        public Guid SessionId { get; set; }
        public Guid DeviceId { get; set; }
    }

    public enum ProductionRelayState : byte
    {
        NotConfigured = 0,
        Connecting = 1,
        Connected = 2,
        Reconnecting = 3,
        Failed = 4,
        Stopped = 5
    }

    public sealed class ProductionDeviceSummary
    {
        public Guid DeviceId { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string TrustState { get; set; }
    }

    public sealed class ProductionSessionSummary
    {
        public Guid SessionId { get; set; }
        public int ViewerCount { get; set; }
        public Guid? ControllerDeviceId { get; set; }
        public DateTime? LeaseExpiresAtUtc { get; set; }
    }

    public sealed class PairingChallengeSummary
    {
        public Guid ChallengeId { get; set; }
        public Guid TargetDeviceId { get; set; }
        public string TargetName { get; set; }
        public string Code { get; set; }
        public string ChallengeNonce { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    public sealed class ProductionStatus
    {
        public bool Configured { get; set; }
        public bool AccountLinked { get; set; }
        public string ServerHost { get; set; }
        public string PcName { get; set; }
        public ProductionRelayState RelayState { get; set; }
        public string LastErrorCode { get; set; }
        public DateTime? LastConnectedAtUtc { get; set; }
        public IList<ProductionDeviceSummary> Devices { get; set; }
        public IList<ProductionSessionSummary> Sessions { get; set; }
        public PairingChallengeSummary PairingChallenge { get; set; }
    }

    public sealed class RegistryResponse
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public bool RemoteAccessEnabled { get; set; }
        public IList<SessionSummary> Summaries { get; set; }
        public int AffectedSessions { get; set; }
        public int TimedOutSessions { get; set; }
        public ProductionStatus Production { get; set; }
    }

    public static class RegistryControlCodec
    {
        private const byte Version = 3;
        private const int MaximumBytes = 64 * 1024;

        public static void WriteRequest(Stream stream, RegistryRequest request)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (request == null) throw new ArgumentNullException("request");
            using (var body = new MemoryStream())
            using (var writer = new BinaryWriter(body, Encoding.UTF8))
            {
                writer.Write(Version);
                writer.Write((byte)request.Command);
                switch (request.Command)
                {
                    case RegistryCommand.Register:
                        if (request.Registration == null) throw new InvalidDataException("Registration is required.");
                        WriteGuid(writer, request.Registration.SessionId);
                        writer.Write(request.Registration.ExpectedHostProcessId);
                        writer.Write((byte)request.Registration.ShellType);
                        writer.Write(request.Registration.HandshakeToken);
                        WriteString(writer, request.Registration.PipeName);
                        break;
                    case RegistryCommand.StopSession:
                        WriteGuid(writer, request.SessionId);
                        break;
                    case RegistryCommand.CreatePairingChallenge:
                    case RegistryCommand.RevokeDevice:
                        WriteGuid(writer, request.DeviceId);
                        break;
                }
                writer.Flush();
                WriteFrame(stream, body.ToArray());
            }
        }

        public static RegistryRequest ReadRequest(Stream stream)
        {
            using (var body = new MemoryStream(ReadFrame(stream), false))
            using (var reader = new BinaryReader(body, Encoding.UTF8))
            {
                if (reader.ReadByte() != Version) throw new InvalidDataException("Unsupported Registry control version.");
                var commandValue = reader.ReadByte();
                if (!Enum.IsDefined(typeof(RegistryCommand), commandValue)) throw new InvalidDataException("Unknown Registry command.");
                var request = new RegistryRequest { Command = (RegistryCommand)commandValue };
                switch (request.Command)
                {
                    case RegistryCommand.Register:
                        var sessionId = ReadGuid(reader);
                        var pid = reader.ReadInt32();
                        var shellValue = reader.ReadByte();
                        if (!Enum.IsDefined(typeof(ManagedShellType), shellValue)) throw new InvalidDataException("Unknown shell type.");
                        var token = ReadExact(reader, SessionRegistration.HandshakeTokenBytes);
                        var pipeName = ReadString(reader);
                        request.Registration = new SessionRegistration(
                            sessionId, pipeName, token, pid, (ManagedShellType)shellValue);
                        break;
                    case RegistryCommand.StopSession:
                        request.SessionId = ReadGuid(reader);
                        break;
                    case RegistryCommand.CreatePairingChallenge:
                    case RegistryCommand.RevokeDevice:
                        request.DeviceId = ReadGuid(reader);
                        break;
                }
                if (body.Position != body.Length) throw new InvalidDataException("Registry request has trailing bytes.");
                return request;
            }
        }

        public static void WriteResponse(Stream stream, RegistryResponse response)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (response == null) throw new ArgumentNullException("response");
            using (var body = new MemoryStream())
            using (var writer = new BinaryWriter(body, Encoding.UTF8))
            {
                writer.Write(Version);
                writer.Write(response.Success);
                WriteString(writer, response.Code ?? string.Empty);
                writer.Write(response.RemoteAccessEnabled);
                writer.Write(response.AffectedSessions);
                writer.Write(response.TimedOutSessions);
                var summaries = response.Summaries ?? new List<SessionSummary>();
                writer.Write(summaries.Count);
                foreach (var summary in summaries)
                {
                    WriteGuid(writer, summary.SessionId);
                    writer.Write((byte)summary.ShellType);
                    writer.Write((byte)summary.Lifecycle);
                    writer.Write(summary.HostProcessId);
                    writer.Write(summary.LastSequence);
                    writer.Write(summary.RegisteredAtUtc.Ticks);
                    writer.Write(summary.LastActivityUtc.Ticks);
                    writer.Write(summary.ExitCode.HasValue);
                    if (summary.ExitCode.HasValue) writer.Write(summary.ExitCode.Value);
                }
                WriteProductionStatus(writer, response.Production);
                writer.Flush();
                WriteFrame(stream, body.ToArray());
            }
        }

        public static RegistryResponse ReadResponse(Stream stream)
        {
            using (var body = new MemoryStream(ReadFrame(stream), false))
            using (var reader = new BinaryReader(body, Encoding.UTF8))
            {
                if (reader.ReadByte() != Version) throw new InvalidDataException("Unsupported Registry response version.");
                var response = new RegistryResponse
                {
                    Success = reader.ReadBoolean(),
                    Code = ReadString(reader),
                    RemoteAccessEnabled = reader.ReadBoolean(),
                    AffectedSessions = reader.ReadInt32(),
                    TimedOutSessions = reader.ReadInt32()
                };
                var count = reader.ReadInt32();
                if (count < 0 || count > 1000) throw new InvalidDataException("Invalid Registry summary count.");
                var summaries = new List<SessionSummary>();
                for (var index = 0; index < count; index++)
                {
                    var sessionId = ReadGuid(reader);
                    var shellValue = reader.ReadByte();
                    var lifecycleValue = reader.ReadByte();
                    if (!Enum.IsDefined(typeof(ManagedShellType), shellValue) ||
                        !Enum.IsDefined(typeof(SessionAgentLifecycle), lifecycleValue))
                    {
                        throw new InvalidDataException("Registry response contains an invalid enum value.");
                    }
                    var summary = new SessionSummary
                    {
                        SessionId = sessionId,
                        ShellType = (ManagedShellType)shellValue,
                        Lifecycle = (SessionAgentLifecycle)lifecycleValue,
                        HostProcessId = reader.ReadInt32(),
                        LastSequence = reader.ReadUInt64(),
                        RegisteredAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                        LastActivityUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc)
                    };
                    if (reader.ReadBoolean()) summary.ExitCode = reader.ReadInt32();
                    summaries.Add(summary);
                }
                response.Production = ReadProductionStatus(reader);
                if (body.Position != body.Length) throw new InvalidDataException("Registry response has trailing bytes.");
                response.Summaries = summaries;
                return response;
            }
        }

        private static void WriteProductionStatus(BinaryWriter writer, ProductionStatus status)
        {
            status = status ?? new ProductionStatus
            {
                RelayState = ProductionRelayState.NotConfigured,
                Devices = new List<ProductionDeviceSummary>(),
                Sessions = new List<ProductionSessionSummary>()
            };
            writer.Write(status.Configured);
            writer.Write(status.AccountLinked);
            WriteString(writer, status.ServerHost ?? string.Empty);
            WriteString(writer, status.PcName ?? string.Empty);
            writer.Write((byte)status.RelayState);
            WriteString(writer, status.LastErrorCode ?? string.Empty);
            WriteOptionalDateTime(writer, status.LastConnectedAtUtc);
            var devices = status.Devices ?? new List<ProductionDeviceSummary>();
            if (devices.Count > 1000) throw new InvalidDataException("Too many production devices.");
            writer.Write(devices.Count);
            foreach (var device in devices)
            {
                WriteGuid(writer, device.DeviceId);
                WriteString(writer, device.Type ?? string.Empty);
                WriteString(writer, device.Name ?? string.Empty);
                WriteString(writer, device.TrustState ?? string.Empty);
            }
            var sessions = status.Sessions ?? new List<ProductionSessionSummary>();
            if (sessions.Count > 1000) throw new InvalidDataException("Too many production sessions.");
            writer.Write(sessions.Count);
            foreach (var session in sessions)
            {
                WriteGuid(writer, session.SessionId);
                writer.Write(session.ViewerCount);
                writer.Write(session.ControllerDeviceId.HasValue);
                if (session.ControllerDeviceId.HasValue) WriteGuid(writer, session.ControllerDeviceId.Value);
                WriteOptionalDateTime(writer, session.LeaseExpiresAtUtc);
            }
            writer.Write(status.PairingChallenge != null);
            if (status.PairingChallenge != null)
            {
                WriteGuid(writer, status.PairingChallenge.ChallengeId);
                WriteGuid(writer, status.PairingChallenge.TargetDeviceId);
                WriteString(writer, status.PairingChallenge.TargetName ?? string.Empty);
                WriteString(writer, status.PairingChallenge.Code ?? string.Empty);
                WriteString(writer, status.PairingChallenge.ChallengeNonce ?? string.Empty);
                writer.Write(status.PairingChallenge.ExpiresAtUtc.Ticks);
            }
        }

        private static ProductionStatus ReadProductionStatus(BinaryReader reader)
        {
            var status = new ProductionStatus
            {
                Configured = reader.ReadBoolean(),
                AccountLinked = reader.ReadBoolean(),
                ServerHost = ReadString(reader),
                PcName = ReadString(reader)
            };
            var relay = reader.ReadByte();
            if (!Enum.IsDefined(typeof(ProductionRelayState), relay)) throw new InvalidDataException("Invalid Relay state.");
            status.RelayState = (ProductionRelayState)relay;
            status.LastErrorCode = ReadString(reader);
            status.LastConnectedAtUtc = ReadOptionalDateTime(reader);
            var deviceCount = reader.ReadInt32();
            if (deviceCount < 0 || deviceCount > 1000) throw new InvalidDataException("Invalid production device count.");
            var devices = new List<ProductionDeviceSummary>();
            for (var index = 0; index < deviceCount; index++)
            {
                devices.Add(new ProductionDeviceSummary
                {
                    DeviceId = ReadGuid(reader),
                    Type = ReadString(reader),
                    Name = ReadString(reader),
                    TrustState = ReadString(reader)
                });
            }
            status.Devices = devices;
            var sessionCount = reader.ReadInt32();
            if (sessionCount < 0 || sessionCount > 1000) throw new InvalidDataException("Invalid production session count.");
            var sessions = new List<ProductionSessionSummary>();
            for (var index = 0; index < sessionCount; index++)
            {
                var session = new ProductionSessionSummary
                {
                    SessionId = ReadGuid(reader),
                    ViewerCount = reader.ReadInt32()
                };
                if (session.ViewerCount < 0 || session.ViewerCount > 1000) throw new InvalidDataException("Invalid viewer count.");
                if (reader.ReadBoolean()) session.ControllerDeviceId = ReadGuid(reader);
                session.LeaseExpiresAtUtc = ReadOptionalDateTime(reader);
                sessions.Add(session);
            }
            status.Sessions = sessions;
            if (reader.ReadBoolean())
            {
                var challenge = new PairingChallengeSummary
                {
                    ChallengeId = ReadGuid(reader),
                    TargetDeviceId = ReadGuid(reader),
                    TargetName = ReadString(reader),
                    Code = ReadString(reader),
                    ChallengeNonce = ReadString(reader),
                    ExpiresAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc)
                };
                status.PairingChallenge = challenge;
            }
            return status;
        }

        private static void WriteOptionalDateTime(BinaryWriter writer, DateTime? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                if (value.Value.Kind != DateTimeKind.Utc) throw new InvalidDataException("Production time must be UTC.");
                writer.Write(value.Value.Ticks);
            }
        }

        private static DateTime? ReadOptionalDateTime(BinaryReader reader)
        {
            return reader.ReadBoolean() ? new DateTime(reader.ReadInt64(), DateTimeKind.Utc) : (DateTime?)null;
        }

        private static void WriteFrame(Stream stream, byte[] body)
        {
            if (body.Length == 0 || body.Length > MaximumBytes) throw new InvalidDataException("Registry control frame is too large.");
            var prefix = new byte[4];
            WriteUInt32(prefix, checked((uint)body.Length));
            stream.Write(prefix, 0, prefix.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static byte[] ReadFrame(Stream stream)
        {
            var prefix = ReadExact(stream, 4);
            var length = checked((int)ReadUInt32(prefix));
            if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("Invalid Registry control frame length.");
            return ReadExact(stream, length);
        }

        private static void WriteGuid(BinaryWriter writer, Guid value)
        {
            if (value == Guid.Empty) throw new InvalidDataException("Registry session ID is empty.");
            writer.Write(value.ToByteArray());
        }

        private static Guid ReadGuid(BinaryReader reader)
        {
            var value = new Guid(ReadExact(reader, 16));
            if (value == Guid.Empty) throw new InvalidDataException("Registry session ID is empty.");
            return value;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > 4096) throw new InvalidDataException("Registry string is too long.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadUInt16();
            var value = new UTF8Encoding(false, true).GetString(ReadExact(reader, length));
            if (value.IndexOf('\0') >= 0) throw new InvalidDataException("Registry string contains NUL.");
            return value;
        }

        private static byte[] ReadExact(BinaryReader reader, int count)
        {
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("Registry control frame ended early.");
            return bytes;
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(bytes, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException("Registry control frame ended early.");
                offset += read;
            }
            return bytes;
        }

        private static void WriteUInt32(byte[] bytes, uint value)
        {
            bytes[0] = (byte)(value >> 24);
            bytes[1] = (byte)(value >> 16);
            bytes[2] = (byte)(value >> 8);
            bytes[3] = (byte)value;
        }

        private static uint ReadUInt32(byte[] bytes)
        {
            return ((uint)bytes[0] << 24) |
                ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) |
                bytes[3];
        }
    }

    public static class RegistryControlClient
    {
        public static RegistryResponse Send(string pipeName, RegistryRequest request, int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
                pipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("control pipe name is invalid", "pipeName");
            }
            if (request == null) throw new ArgumentNullException("request");
            if (timeoutMilliseconds < 1 || timeoutMilliseconds > 300000)
            {
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            NamedPipeClientStream pipe = null;
            while (pipe == null)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Registry control pipe was unavailable.");
                var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                try
                {
                    candidate.Connect(Math.Max(1, (int)remaining.TotalMilliseconds));
                    pipe = candidate;
                }
                catch (IOException)
                {
                    candidate.Dispose();
                    Thread.Sleep(10);
                }
                catch (TimeoutException)
                {
                    candidate.Dispose();
                    throw;
                }
            }
            using (pipe)
            {
                RegistryControlCodec.WriteRequest(pipe, request);
                return RegistryControlCodec.ReadResponse(pipe);
            }
        }
    }
}
