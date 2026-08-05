using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Agent
{
    internal interface IRegistryRegistrationStore
    {
        IList<SessionRegistration> Load();
        void Upsert(SessionRegistration registration);
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

        public void Clear()
        {
        }
    }

    internal sealed class DpapiRegistryRegistrationStore : IRegistryRegistrationStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TLRI");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TermLink.Registry.Index.V1");
        private const byte Version = 2;
        private const int MaximumFileBytes = 1024 * 1024;
        private const int MaximumEntries = 1000;
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
            registrations.Clear();
            try
            {
                if (File.Exists(indexPath)) File.Delete(indexPath);
            }
            catch
            {
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
                        var registration = new SessionRegistration(
                            sessionId,
                            pipeName,
                            token,
                            processId,
                            (ManagedShellType)shellValue,
                            processStart,
                            registeredAt,
                            recoveryOnly);
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
