using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Agent
{
    internal interface IRegistryRegistrationStore
    {
        IList<SessionRegistration> Load();
        void Upsert(SessionRegistration registration);
        void Retire(Guid sessionId);
        void Remove(Guid sessionId);
        void Clear();
    }

    internal sealed class NullRegistryRegistrationStore : IRegistryRegistrationStore
    {
        public IList<SessionRegistration> Load()
        {
            return new List<SessionRegistration>();
        }

        public void Upsert(SessionRegistration registration)
        {
        }

        public void Remove(Guid sessionId)
        {
        }

        public void Retire(Guid sessionId)
        {
        }

        public void Clear()
        {
        }
    }

    internal sealed class StoppedHostIdentity
    {
        public StoppedHostIdentity(Guid sessionId, int processId, DateTime processStartTimeUtc)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("session ID is required", "sessionId");
            if (processId <= 0) throw new ArgumentOutOfRangeException("processId");
            if (processStartTimeUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("process start time must be UTC", "processStartTimeUtc");
            SessionId = sessionId;
            ProcessId = processId;
            ProcessStartTimeUtc = processStartTimeUtc;
        }

        public Guid SessionId { get; private set; }
        public int ProcessId { get; private set; }
        public DateTime ProcessStartTimeUtc { get; private set; }

        public static StoppedHostIdentity FromRegistration(SessionRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException("registration");
            return new StoppedHostIdentity(
                registration.SessionId,
                registration.ExpectedHostProcessId,
                registration.ExpectedHostStartTimeUtc);
        }

        public bool MatchesRunningHost()
        {
            try
            {
                using (var process = Process.GetProcessById(ProcessId))
                {
                    return process.StartTime.ToUniversalTime().Ticks == ProcessStartTimeUtc.Ticks;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
    }

    internal sealed class RemoteAccessPolicySnapshot
    {
        public RemoteAccessPolicySnapshot(
            bool remoteAccessEnabled,
            IList<StoppedHostIdentity> stoppedHosts,
            bool valid = true)
        {
            RemoteAccessEnabled = remoteAccessEnabled;
            StoppedHosts = stoppedHosts ?? new List<StoppedHostIdentity>();
            Valid = valid;
        }

        public bool RemoteAccessEnabled { get; private set; }
        public IList<StoppedHostIdentity> StoppedHosts { get; private set; }
        public bool Valid { get; private set; }
    }

    internal interface IRemoteAccessPolicyStore
    {
        RemoteAccessPolicySnapshot Load();
        void Save(RemoteAccessPolicySnapshot snapshot);
    }

    internal sealed class NullRemoteAccessPolicyStore : IRemoteAccessPolicyStore
    {
        public RemoteAccessPolicySnapshot Load()
        {
            return new RemoteAccessPolicySnapshot(true, new List<StoppedHostIdentity>());
        }

        public void Save(RemoteAccessPolicySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
        }
    }

    internal sealed class DpapiRemoteAccessPolicyStore : IRemoteAccessPolicyStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TLRP");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TermLink.RemoteAccess.Policy.V1");
        private const byte Version = 1;
        private const int MaximumFileBytes = 256 * 1024;
        private const int MaximumEntries = 1000;
        private readonly string policyPath;

        public DpapiRemoteAccessPolicyStore(string policyPath)
        {
            if (string.IsNullOrWhiteSpace(policyPath)) throw new ArgumentException("policy path is required", "policyPath");
            this.policyPath = Path.GetFullPath(policyPath);
            var directory = Path.GetDirectoryName(this.policyPath);
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("policy path has no directory", "policyPath");
            Directory.CreateDirectory(directory);
        }

        public RemoteAccessPolicySnapshot Load()
        {
            try
            {
                return ReadExisting();
            }
            catch (FileNotFoundException)
            {
                return new RemoteAccessPolicySnapshot(true, new List<StoppedHostIdentity>());
            }
            catch (DirectoryNotFoundException)
            {
                return new RemoteAccessPolicySnapshot(true, new List<StoppedHostIdentity>());
            }
            catch (Exception error)
            {
                if (error is IOException ||
                    error is UnauthorizedAccessException ||
                    error is InvalidDataException ||
                    error is CryptographicException ||
                    error is ArgumentException ||
                    error is OverflowException)
                {
                    // A damaged or inaccessible safety policy must never turn
                    // remote access back on. Explicit Enable rewrites it.
                    return new RemoteAccessPolicySnapshot(
                        false,
                        new List<StoppedHostIdentity>(),
                        false);
                }
                throw;
            }
        }

        public void Save(RemoteAccessPolicySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (snapshot.StoppedHosts.Count > MaximumEntries)
                throw new InvalidDataException("Remote-access policy has too many stopped Hosts.");
            var observed = new HashSet<Guid>();
            byte[] plain;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(snapshot.RemoteAccessEnabled);
                writer.Write(snapshot.StoppedHosts.Count);
                foreach (var stopped in snapshot.StoppedHosts)
                {
                    if (stopped == null || !observed.Add(stopped.SessionId))
                        throw new InvalidDataException("Remote-access policy contains an invalid or duplicate stopped Host.");
                    writer.Write(stopped.SessionId.ToByteArray());
                    writer.Write(stopped.ProcessId);
                    writer.Write(stopped.ProcessStartTimeUtc.Ticks);
                }
                writer.Flush();
                plain = stream.ToArray();
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
            if (protectedBytes.Length + Magic.Length + 1 > MaximumFileBytes)
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                throw new InvalidDataException("Remote-access policy exceeds the maximum size.");
            }

            var encoded = new byte[Magic.Length + 1 + protectedBytes.Length];
            Buffer.BlockCopy(Magic, 0, encoded, 0, Magic.Length);
            encoded[Magic.Length] = Version;
            Buffer.BlockCopy(protectedBytes, 0, encoded, Magic.Length + 1, protectedBytes.Length);
            var temporaryPath = policyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encoded, 0, encoded.Length);
                    stream.Flush(true);
                }
                if (File.Exists(policyPath)) File.Replace(temporaryPath, policyPath, null, true);
                else File.Move(temporaryPath, policyPath);
            }
            finally
            {
                Array.Clear(encoded, 0, encoded.Length);
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private RemoteAccessPolicySnapshot ReadExisting()
        {
            var encoded = File.ReadAllBytes(policyPath);
            if (encoded.Length < Magic.Length + 2 || encoded.Length > MaximumFileBytes)
                throw new InvalidDataException("Remote-access policy has an invalid size.");
            for (var index = 0; index < Magic.Length; index++)
            {
                if (encoded[index] != Magic[index])
                    throw new InvalidDataException("Remote-access policy has invalid magic.");
            }
            if (encoded[Magic.Length] != Version)
                throw new InvalidDataException("Remote-access policy version is unsupported.");
            var protectedBytes = new byte[encoded.Length - Magic.Length - 1];
            Buffer.BlockCopy(encoded, Magic.Length + 1, protectedBytes, 0, protectedBytes.Length);
            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            finally { Array.Clear(protectedBytes, 0, protectedBytes.Length); }
            try
            {
                using (var stream = new MemoryStream(plain, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    var enabled = reader.ReadBoolean();
                    var count = reader.ReadInt32();
                    if (count < 0 || count > MaximumEntries)
                        throw new InvalidDataException("Remote-access policy entry count is invalid.");
                    var stopped = new List<StoppedHostIdentity>(count);
                    var observed = new HashSet<Guid>();
                    for (var index = 0; index < count; index++)
                    {
                        var sessionId = new Guid(ReadExact(reader, 16));
                        var processId = reader.ReadInt32();
                        var startTime = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                        if (!observed.Add(sessionId))
                            throw new InvalidDataException("Remote-access policy contains a duplicate Session ID.");
                        stopped.Add(new StoppedHostIdentity(sessionId, processId, startTime));
                    }
                    if (stream.Position != stream.Length)
                        throw new InvalidDataException("Remote-access policy contains trailing bytes.");
                    return new RemoteAccessPolicySnapshot(enabled, stopped);
                }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        private static byte[] ReadExact(BinaryReader reader, int count)
        {
            var value = reader.ReadBytes(count);
            if (value.Length != count) throw new EndOfStreamException("Remote-access policy ended early.");
            return value;
        }
    }

    internal sealed class DpapiRegistryRegistrationStore : IRegistryRegistrationStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TLRI");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TermLink.Registry.Index.V1");
        private const byte Version = 3;
        private const int MaximumFileBytes = 64 * 1024 * 1024;
        private const int MaximumEntries = 100000;
        private readonly string indexPath;
        private readonly Dictionary<Guid, SessionRegistration> registrations =
            new Dictionary<Guid, SessionRegistration>();

        public DpapiRegistryRegistrationStore(string indexPath)
        {
            if (string.IsNullOrWhiteSpace(indexPath)) throw new ArgumentException("index path is required", "indexPath");
            this.indexPath = Path.GetFullPath(indexPath);
            var directory = Path.GetDirectoryName(this.indexPath);
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("index path has no directory", "indexPath");
            Directory.CreateDirectory(directory);
            ReadExisting();
        }

        public IList<SessionRegistration> Load()
        {
            return new List<SessionRegistration>(registrations.Values);
        }

        public void Upsert(SessionRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException("registration");
            SessionRegistration previous;
            var replaced = registrations.TryGetValue(registration.SessionId, out previous);
            if (replaced && !previous.HasSameHostIdentity(registration))
                throw new InvalidDataException("A Session ID cannot be rebound to another Host identity.");
            if (replaced && previous.Retired && !registration.Retired)
                throw new InvalidDataException("A retired Session ID cannot be reactivated.");
            registrations[registration.SessionId] = registration;
            try
            {
                Save();
            }
            catch
            {
                if (replaced) registrations[registration.SessionId] = previous;
                else registrations.Remove(registration.SessionId);
                throw;
            }
        }

        public void Retire(Guid sessionId)
        {
            SessionRegistration previous;
            if (!registrations.TryGetValue(sessionId, out previous) || previous.Retired) return;
            registrations[sessionId] = previous.AsRetired();
            try
            {
                Save();
            }
            catch
            {
                registrations[sessionId] = previous;
                throw;
            }
        }

        public void Remove(Guid sessionId)
        {
            SessionRegistration previous;
            if (!registrations.TryGetValue(sessionId, out previous)) return;
            registrations.Remove(sessionId);
            try
            {
                Save();
            }
            catch
            {
                registrations[sessionId] = previous;
                throw;
            }
        }

        public void Clear()
        {
            var previous = new Dictionary<Guid, SessionRegistration>(registrations);
            foreach (var item in previous)
            {
                registrations[item.Key] = item.Value.AsRecoveryOnly();
            }
            try
            {
                Save();
            }
            catch
            {
                registrations.Clear();
                foreach (var item in previous) registrations[item.Key] = item.Value;
                throw;
            }
        }

        private void ReadExisting()
        {
            if (!File.Exists(indexPath)) return;
            var encoded = File.ReadAllBytes(indexPath);
            if (encoded.Length < Magic.Length + 2 || encoded.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Registry registration index has an invalid size.");
            }
            for (var index = 0; index < Magic.Length; index++)
            {
                if (encoded[index] != Magic[index]) throw new InvalidDataException("Registry registration index has invalid magic.");
            }
            var storedVersion = encoded[Magic.Length];
            if (storedVersion < 1 || storedVersion > Version)
            {
                throw new InvalidDataException("Registry registration index version is unsupported.");
            }
            var protectedBytes = new byte[encoded.Length - Magic.Length - 1];
            Buffer.BlockCopy(encoded, Magic.Length + 1, protectedBytes, 0, protectedBytes.Length);
            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException error)
            {
                throw new InvalidDataException("Registry registration index could not be decrypted for the current user.", error);
            }

            try
            {
                using (var stream = new MemoryStream(plain, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    var count = reader.ReadInt32();
                    if (count < 0 || count > MaximumEntries)
                    {
                        throw new InvalidDataException("Registry registration index entry count is invalid.");
                    }
                    for (var index = 0; index < count; index++)
                    {
                        var sessionId = new Guid(ReadExact(reader, 16));
                        var processId = reader.ReadInt32();
                        var shellValue = reader.ReadByte();
                        if (!Enum.IsDefined(typeof(ManagedShellType), shellValue))
                        {
                            throw new InvalidDataException("Registry registration index contains an invalid shell type.");
                        }
                        var processStart = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                        var registeredAt = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                        var token = ReadExact(reader, SessionRegistration.HandshakeTokenBytes);
                        var pipeName = ReadString(reader);
                        var recoveryOnly = storedVersion >= 2 && reader.ReadBoolean();
                        var retired = storedVersion >= 3 && reader.ReadBoolean();
                        var registration = new SessionRegistration(
                            sessionId,
                            pipeName,
                            token,
                            processId,
                            (ManagedShellType)shellValue,
                            processStart,
                            registeredAt,
                            recoveryOnly,
                            retired);
                        if (registrations.ContainsKey(sessionId))
                        {
                            throw new InvalidDataException("Registry registration index contains a duplicate session ID.");
                        }
                        registrations.Add(sessionId, registration);
                    }
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("Registry registration index contains trailing bytes.");
                    }
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        private void Save()
        {
            if (registrations.Count == 0)
            {
                if (File.Exists(indexPath)) File.Delete(indexPath);
                return;
            }

            byte[] plain;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(registrations.Count);
                foreach (var registration in registrations.Values)
                {
                    writer.Write(registration.SessionId.ToByteArray());
                    writer.Write(registration.ExpectedHostProcessId);
                    writer.Write((byte)registration.ShellType);
                    writer.Write(registration.ExpectedHostStartTimeUtc.Ticks);
                    writer.Write(registration.RegisteredAtUtc.Ticks);
                    writer.Write(registration.HandshakeToken);
                    WriteString(writer, registration.PipeName);
                    writer.Write(registration.RecoveryOnly);
                    writer.Write(registration.Retired);
                }
                writer.Flush();
                plain = stream.ToArray();
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
            if (protectedBytes.Length + Magic.Length + 1 > MaximumFileBytes)
            {
                throw new InvalidDataException("Registry registration index exceeds the maximum size.");
            }

            var encoded = new byte[Magic.Length + 1 + protectedBytes.Length];
            Buffer.BlockCopy(Magic, 0, encoded, 0, Magic.Length);
            encoded[Magic.Length] = Version;
            Buffer.BlockCopy(protectedBytes, 0, encoded, Magic.Length + 1, protectedBytes.Length);
            var temporaryPath = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encoded, 0, encoded.Length);
                    stream.Flush(true);
                }
                if (File.Exists(indexPath)) File.Replace(temporaryPath, indexPath, null, true);
                else File.Move(temporaryPath, indexPath);
            }
            finally
            {
                Array.Clear(encoded, 0, encoded.Length);
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length == 0 || bytes.Length > 4096)
            {
                throw new InvalidDataException("Registry registration index string length is invalid.");
            }
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadUInt16();
            if (length == 0 || length > 4096)
            {
                throw new InvalidDataException("Registry registration index string length is invalid.");
            }
            var value = new UTF8Encoding(false, true).GetString(ReadExact(reader, length));
            if (value.IndexOf('\0') >= 0) throw new InvalidDataException("Registry registration index contains NUL.");
            return value;
        }

        private static byte[] ReadExact(BinaryReader reader, int count)
        {
            var value = reader.ReadBytes(count);
            if (value.Length != count) throw new EndOfStreamException("Registry registration index ended early.");
            return value;
        }
    }
}
