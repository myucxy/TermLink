using System;
using System.IO;
using System.Text;

namespace TermLink.LocalIpc
{
    internal sealed class TerminalLocalProtocolException : IOException
    {
        public TerminalLocalProtocolException() : base("TERMINAL_LOCAL_PROTOCOL_MISMATCH: SessionHost and Agent require matching terminal semantics, width and resource profiles.") { }
    }

    internal sealed class TerminalHistoryProfileException : IOException
    {
        public string Code { get; private set; }
        public TerminalHistoryProfileException(string code)
            : base(code + ": Preserved terminal history cannot be replayed with the current session profile.")
        {
            Code = code;
        }
    }

    internal sealed class TerminalSemanticProfile
    {
        public static readonly TerminalSemanticProfile Legacy = new TerminalSemanticProfile(
            "terminal_state_2_21_v2", "terminal_state_2_21_v2;width=1;output=0;resources=1", false, 1);
        public static readonly TerminalSemanticProfile Graphics = new TerminalSemanticProfile(
            "terminal_state_2_22_v1", "terminal_state_2_22_v1;width=1;output=0;resources=2", true, 1);
        public static readonly TerminalSemanticProfile GraphicsUnicode16 = new TerminalSemanticProfile(
            "terminal_state_2_22_v2", "terminal_state_2_22_v2;width=2;output=0;resources=2", true, 2);

        private TerminalSemanticProfile(string stateContract, string descriptor, bool isGraphics, ushort widthProfile)
        {
            StateContract = stateContract;
            Descriptor = descriptor;
            IsGraphics = isGraphics;
            WidthProfile = widthProfile;
        }

        public string StateContract { get; }
        public string Descriptor { get; }
        public bool IsGraphics { get; }
        public ushort WidthProfile { get; }

        internal static TerminalSemanticProfile ParseDescriptor(string descriptor)
        {
            if (string.Equals(descriptor, Legacy.Descriptor, StringComparison.Ordinal)) return Legacy;
            if (string.Equals(descriptor, Graphics.Descriptor, StringComparison.Ordinal)) return Graphics;
            if (string.Equals(descriptor, GraphicsUnicode16.Descriptor, StringComparison.Ordinal)) return GraphicsUnicode16;
            return null;
        }
    }

    internal static class TerminalSessionProfile
    {
        public const string FileName = "terminal-profile.dpapi";
        public const int MaximumProtectedBytes = 4096;
        public const string Missing = "TERMINAL_HISTORY_PROFILE_MISSING";
        public const string Invalid = "TERMINAL_HISTORY_PROFILE_INVALID";
        public const string Mismatch = "TERMINAL_HISTORY_PROFILE_MISMATCH";

        public static byte[] Encode(Guid sessionId)
        {
            return Encode(sessionId, TerminalSemanticProfile.Legacy);
        }

        public static byte[] Encode(Guid sessionId, TerminalSemanticProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return Encoding.ASCII.GetBytes("TermLink terminal profile V1\n" +
                sessionId.ToString("N") + "\n" + profile.Descriptor);
        }

        public static void Validate(byte[] bytes, Guid sessionId)
        {
            ValidateExpected(bytes, sessionId, TerminalSemanticProfile.Legacy);
        }

        public static TerminalSemanticProfile ValidateExpected(byte[] bytes, Guid sessionId, TerminalSemanticProfile expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var actual = Read(bytes, sessionId);
            if (actual != expected) throw new TerminalHistoryProfileException(Mismatch);
            return actual;
        }

        public static TerminalSemanticProfile Read(byte[] bytes, Guid sessionId)
        {
            var prefix = Encoding.ASCII.GetBytes("TermLink terminal profile V1\n" + sessionId.ToString("N") + "\n");
            if (bytes == null || bytes.Length <= prefix.Length || bytes.Length > 512)
                throw new TerminalHistoryProfileException(Invalid);
            for (var index = 0; index < prefix.Length; index++)
                if (bytes[index] != prefix[index]) throw new TerminalHistoryProfileException(Invalid);
            for (var index = prefix.Length; index < bytes.Length; index++)
                if (bytes[index] < 32 || bytes[index] > 126) throw new TerminalHistoryProfileException(Invalid);
            var profile = TerminalSemanticProfile.ParseDescriptor(
                Encoding.ASCII.GetString(bytes, prefix.Length, bytes.Length - prefix.Length));
            if (profile == null) throw new TerminalHistoryProfileException(Mismatch);
            return profile;
        }
    }
    internal enum LocalResizeError : byte
    {
        OK, RESIZE_UNSUPPORTED, RESIZE_BUSY, LOCAL_INPUT_PRIORITY, RESIZE_EXPIRED,
        WINDOW_RESIZE_UNSUPPORTED, NO_DESKTOP_SIZE, SESSION_UNAVAILABLE, RESIZE_TIMEOUT
    }

    internal sealed class LocalResizeRequest
    {
        public Guid Id;
        public ulong LocalInputEpoch;
        public long ExpiresAtMs;
        public short Columns;
        public short Rows;
        public bool Restore;
    }

    internal sealed class LocalResizeResult
    {
        public Guid Id;
        public short Columns;
        public short Rows;
        public LocalResizeError Error;
    }

    internal sealed class LocalHostResizeResult
    {
        public ulong SourceSequence;
        public uint SourceOffsetEnd;
        public uint Ordinal;
        public ushort RequestedColumns;
        public ushort RequestedRows;
        public ushort ActualColumns;
        public ushort ActualRows;
        public LocalResizeError Error;

        public string Describe()
        {
            var sequence = RequestedColumns != 0 ? "<ESC>[" + RequestedColumns + "$|" : "<ESC>[" + RequestedRows + "*|";
            return "终端尺寸请求（规范化） / Terminal resize (normalized) " + sequence + ": " + Error +
                "; actual=" + ActualColumns + "x" + ActualRows + "; source=" + SourceSequence + ":" + SourceOffsetEnd + ":" + Ordinal;
        }
    }

    internal enum LocalFrameType : byte
    {
        HostHello = 1,
        AgentHello = 2,
        Output = 3,
        Resize = 4,
        Lifecycle = 5,
        Input = 6,
        ResizeRequest = 7,
        Ack = 8,
        Gap = 9,
        LocalInputObserved = 10,
        RemoteInputV2 = 11,
        RemoteInputResult = 12,
        Capabilities = 13,
        RemoteInputReject = 14,
        Title = 15,
        SessionRemoved = 16,
        RemoteAccessStopped = 17,
        ResizeResult = 18,
        HostGeometry = 19,
        HostTheme = 20,
        HostResizeResult = 21,
        ClipboardAuthorize = 22,
        ClipboardAuthorization = 23,
        BrokerOutput = 24,
        BrokerCaptureRoot = 25
    }

    internal enum LocalRemoteInputResult : byte
    {
        Accepted = 1,
        LocalInputSuperseded = 2,
        ConPtyClosed = 3,
        WriteFailed = 4,
        InputSequenceRejected = 5,
        ControlLeaseRejected = 6,
        SessionInputUnavailable = 7,
        InputPayloadRejected = 8
    }

    internal enum LocalLifecycleState : byte
    {
        Running = 1,
        Ended = 2,
        Interrupted = 3
    }

    internal sealed class LocalFrame
    {
        public LocalFrame(LocalFrameType type, Guid sessionId, ulong sequence, byte[] payload)
        {
            Type = type;
            SessionId = sessionId;
            Sequence = sequence;
            Payload = payload ?? new byte[0];
        }

        public LocalFrameType Type { get; private set; }
        public Guid SessionId { get; private set; }
        public ulong Sequence { get; private set; }
        public byte[] Payload { get; private set; }
    }

    internal static class LocalFrameCodec
    {
        public const byte ProtocolVersion = 1;
        public const int MaximumFrameBytes = 1024 * 1024;
        private const int PrefixBytes = 4;
        private const int HeaderBytes = 1 + 1 + 16 + 8;

        public static void Write(Stream stream, LocalFrame frame)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (frame == null) throw new ArgumentNullException("frame");
            var bodyLength = checked(HeaderBytes + frame.Payload.Length);
            if (bodyLength > MaximumFrameBytes)
            {
                throw new InvalidDataException("Local IPC frame exceeds 1 MiB.");
            }

            var buffer = new byte[PrefixBytes + bodyLength];
            WriteUInt32(buffer, 0, checked((uint)bodyLength));
            buffer[4] = ProtocolVersion;
            buffer[5] = (byte)frame.Type;
            Buffer.BlockCopy(frame.SessionId.ToByteArray(), 0, buffer, 6, 16);
            WriteUInt64(buffer, 22, frame.Sequence);
            Buffer.BlockCopy(frame.Payload, 0, buffer, 30, frame.Payload.Length);
            stream.Write(buffer, 0, buffer.Length);
            stream.Flush();
        }

        public static LocalFrame Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            var prefix = new byte[PrefixBytes];
            var first = stream.ReadByte();
            if (first < 0) return null;
            prefix[0] = (byte)first;
            ReadExact(stream, prefix, 1, PrefixBytes - 1);
            var bodyLength = checked((int)ReadUInt32(prefix, 0));
            if (bodyLength < HeaderBytes || bodyLength > MaximumFrameBytes)
            {
                throw new InvalidDataException("Invalid local IPC frame length: " + bodyLength + ".");
            }

            var body = new byte[bodyLength];
            ReadExact(stream, body, 0, body.Length);
            if (body[0] != ProtocolVersion)
            {
                throw new InvalidDataException("Unsupported local IPC version: " + body[0] + ".");
            }
            if (!Enum.IsDefined(typeof(LocalFrameType), body[1]))
            {
                throw new InvalidDataException("Unknown local IPC frame type: " + body[1] + ".");
            }
            var guidBytes = new byte[16];
            Buffer.BlockCopy(body, 2, guidBytes, 0, guidBytes.Length);
            var payload = new byte[bodyLength - HeaderBytes];
            Buffer.BlockCopy(body, HeaderBytes, payload, 0, payload.Length);
            return new LocalFrame(
                (LocalFrameType)body[1],
                new Guid(guidBytes),
                ReadUInt64(body, 18),
                payload);
        }

        public static byte[] Encode(LocalFrame frame)
        {
            using (var stream = new MemoryStream())
            {
                Write(stream, frame);
                return stream.ToArray();
            }
        }

        public static LocalFrame Decode(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                var frame = Read(stream);
                if (frame == null || stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Invalid persisted local IPC frame.");
                }
                return frame;
            }
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException("Local IPC frame ended early.");
                offset += read;
                count -= read;
            }
        }

        internal static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)(value >> 8);
            bytes[offset + 1] = (byte)value;
        }

        internal static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        }

        internal static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        internal static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) |
                bytes[offset + 3];
        }

        internal static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            for (var index = 7; index >= 0; index--)
            {
                bytes[offset + index] = (byte)value;
                value >>= 8;
            }
        }

        internal static ulong ReadUInt64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (var index = 0; index < 8; index++)
            {
                value = (value << 8) | bytes[offset + index];
            }
            return value;
        }
    }

    internal static class LocalPayloadCodec
    {
        public static byte[] BrokerCaptureRoot(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || absolutePath.IndexOf('\0') >= 0)
                throw new InvalidDataException("Broker capture root is empty or malformed.");
            var bytes = new UTF8Encoding(false, true).GetBytes(absolutePath);
            ParseBrokerCaptureRoot(bytes);
            return bytes;
        }

        public static string ParseBrokerCaptureRoot(byte[] payload)
        {
            if (payload == null || payload.Length < 1 || payload.Length > 32768)
                throw new InvalidDataException("Broker capture root payload is invalid.");
            string path;
            try { path = new UTF8Encoding(false, true).GetString(payload); }
            catch (DecoderFallbackException error)
            { throw new InvalidDataException("Broker capture root is not UTF-8.", error); }
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\' ||
                !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) ||
                path.IndexOf('\0') >= 0 || path.IndexOf(':', 2) >= 0 ||
                path.IndexOf('/') >= 0 || path.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new InvalidDataException("Broker capture root must be an absolute local drive path.");
            foreach (var character in path)
                if (character < ' ')
                    throw new InvalidDataException("Broker capture root contains a control character.");
            if (path.Length > 3)
                foreach (var segment in path.Substring(3).Split('\\'))
                    if (segment.Length == 0 || segment == "." || segment == ".." ||
                        segment.EndsWith(' ') || segment.EndsWith('.'))
                        throw new InvalidDataException("Broker capture root contains an invalid path segment.");
            return path;
        }

        // Immutable for this product session generation. Additive Snapshot
        // decodability alone is insufficient when parser replay rules differ.
        public const string TerminalStateContract = "terminal_state_2_21_v2";
        public const string TerminalProfileDescriptor = TerminalStateContract + ";width=1;output=0;resources=1";
        public const int HandshakeTokenBytes = 32;
        public const int MaximumTitleBytes = 4096;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Capabilities(bool remoteInputReject, bool remoteResize = false)
        {
            return new[] { (byte)1, (byte)((remoteInputReject ? 1 : 0) | (remoteResize ? 2 : 0)) };
        }

        public static bool ParseCapabilities(byte[] payload)
        {
            if (payload == null || payload.Length != 2 || payload[0] != 1 || (payload[1] & 0xfc) != 0)
            {
                throw new InvalidDataException("Invalid local IPC capabilities payload.");
            }
            return (payload[1] & 1) != 0;
        }

        public static byte[] RemoteResize(LocalResizeRequest request)
        {
            if (request.Id == Guid.Empty || request.ExpiresAtMs <= 0 ||
                (!request.Restore && (request.Columns < 2 || request.Columns > 1000 || request.Rows < 2 || request.Rows > 1000)))
                throw new InvalidDataException("Invalid remote resize request.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(request.Id.ToByteArray());
                writer.Write(request.LocalInputEpoch);
                writer.Write(request.ExpiresAtMs);
                writer.Write(request.Columns);
                writer.Write(request.Rows);
                writer.Write(request.Restore);
                return stream.ToArray();
            }
        }

        public static LocalResizeRequest ParseRemoteResize(byte[] payload)
        {
            if (payload == null || payload.Length != 37 || payload[36] > 1)
                throw new InvalidDataException("Invalid remote resize request length.");
            using (var reader = new BinaryReader(new MemoryStream(payload)))
            {
                var request = new LocalResizeRequest {
                    Id = new Guid(reader.ReadBytes(16)), LocalInputEpoch = reader.ReadUInt64(),
                    ExpiresAtMs = reader.ReadInt64(), Columns = reader.ReadInt16(), Rows = reader.ReadInt16(),
                    Restore = reader.ReadBoolean()
                };
                RemoteResize(request); // Same bounds at both IPC boundaries.
                return request;
            }
        }

        public static byte[] ResizeResult(LocalResizeResult result)
        {
            if (result.Id == Guid.Empty || !Enum.IsDefined(typeof(LocalResizeError), result.Error) ||
                (result.Error == LocalResizeError.OK && (result.Columns < 2 || result.Rows < 2)))
                throw new InvalidDataException("Invalid remote resize result.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(result.Id.ToByteArray());
                writer.Write(result.Columns);
                writer.Write(result.Rows);
                writer.Write((byte)result.Error);
                return stream.ToArray();
            }
        }

        public static LocalResizeResult ParseResizeResult(byte[] payload)
        {
            if (payload == null || payload.Length != 21 || !Enum.IsDefined(typeof(LocalResizeError), payload[20]))
                throw new InvalidDataException("Invalid remote resize result.");
            using (var reader = new BinaryReader(new MemoryStream(payload)))
            {
                var result = new LocalResizeResult { Id = new Guid(reader.ReadBytes(16)), Columns = reader.ReadInt16(),
                    Rows = reader.ReadInt16(), Error = (LocalResizeError)reader.ReadByte() };
                ResizeResult(result);
                return result;
            }
        }

        public static byte[] Hello(int processId, byte[] token)
        {
            RequireLength(token, HandshakeTokenBytes, "handshake token");
            var payload = new byte[4 + token.Length];
            LocalFrameCodec.WriteUInt32(payload, 0, checked((uint)processId));
            Buffer.BlockCopy(token, 0, payload, 4, token.Length);
            return payload;
        }

        public static byte[] HostResizeResult(LocalHostResizeResult result)
        {
            if (result == null || result.SourceSequence == 0 || result.SourceOffsetEnd == 0 || result.SourceOffsetEnd > 1024 * 1024 ||
                (result.RequestedColumns == 0) == (result.RequestedRows == 0) || result.RequestedColumns > 512 || result.RequestedRows > 512 ||
                result.ActualColumns > 512 || result.ActualRows > 512 || !Enum.IsDefined(typeof(LocalResizeError), result.Error) ||
                (result.Error == LocalResizeError.OK && (result.ActualColumns == 0 || result.ActualRows == 0 ||
                    (result.RequestedColumns != 0 && result.RequestedColumns != result.ActualColumns) ||
                    (result.RequestedRows != 0 && result.RequestedRows != result.ActualRows))))
                throw new InvalidDataException("Invalid ordered host resize result.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write((byte)result.Error); writer.Write((ushort)0);
                writer.Write(result.SourceSequence); writer.Write(result.SourceOffsetEnd); writer.Write(result.Ordinal);
                writer.Write(result.RequestedColumns); writer.Write(result.RequestedRows);
                writer.Write(result.ActualColumns); writer.Write(result.ActualRows);
                return stream.ToArray();
            }
        }

        public static LocalHostResizeResult ParseHostResizeResult(byte[] payload)
        {
            if (payload == null || payload.Length != 28 || payload[0] != 1 || payload[2] != 0 || payload[3] != 0)
                throw new InvalidDataException("Invalid ordered host resize result header.");
            using (var reader = new BinaryReader(new MemoryStream(payload)))
            {
                reader.ReadByte();
                var error = (LocalResizeError)reader.ReadByte(); reader.ReadUInt16();
                var result = new LocalHostResizeResult {
                    Error = error, SourceSequence = reader.ReadUInt64(), SourceOffsetEnd = reader.ReadUInt32(), Ordinal = reader.ReadUInt32(),
                    RequestedColumns = reader.ReadUInt16(), RequestedRows = reader.ReadUInt16(), ActualColumns = reader.ReadUInt16(), ActualRows = reader.ReadUInt16()
                };
                HostResizeResult(result);
                return result;
            }
        }

        public static byte[] TerminalHello(int processId, byte[] token)
        {
            return TerminalHello(processId, token, TerminalSemanticProfile.Legacy);
        }

        public static byte[] TerminalHello(int processId, byte[] token, TerminalSemanticProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var identity = Hello(processId, token);
            try
            {
                var descriptor = Encoding.ASCII.GetBytes(profile.Descriptor);
                var result = new byte[identity.Length + descriptor.Length];
                Buffer.BlockCopy(identity, 0, result, 0, identity.Length);
                Buffer.BlockCopy(descriptor, 0, result, identity.Length, descriptor.Length);
                return result;
            }
            finally { Array.Clear(identity, 0, identity.Length); }
        }

        public static int ParseTerminalHello(byte[] payload, byte[] expectedToken)
        {
            return ParseTerminalHello(payload, expectedToken, TerminalSemanticProfile.Legacy);
        }

        public static int ParseTerminalHello(byte[] payload, byte[] expectedToken, TerminalSemanticProfile expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var processId = ParseTerminalHello(payload, expectedToken, out TerminalSemanticProfile actual);
            if (actual != expected) throw new TerminalLocalProtocolException();
            return processId;
        }

        public static int ParseTerminalHello(byte[] payload, byte[] expectedToken, out TerminalSemanticProfile profile)
        {
            profile = null;
            if (payload == null || payload.Length < 36 || payload.Length > 256)
                throw new InvalidDataException("Invalid local IPC terminal hello length.");
            var identity = new byte[36];
            Buffer.BlockCopy(payload, 0, identity, 0, identity.Length);
            int processId;
            try { processId = ParseHello(identity, expectedToken); }
            finally { Array.Clear(identity, 0, identity.Length); }
            var actual = TerminalSemanticProfile.ParseDescriptor(Encoding.ASCII.GetString(payload, 36, payload.Length - 36));
            if (actual == null) throw new TerminalLocalProtocolException();
            profile = actual;
            return processId;
        }

        public static int ParseHello(byte[] payload, byte[] expectedToken)
        {
            if (payload == null || payload.Length != 4 + HandshakeTokenBytes)
            {
                throw new InvalidDataException("Invalid local IPC hello payload.");
            }
            RequireLength(expectedToken, HandshakeTokenBytes, "expected handshake token");
            var actualToken = new byte[HandshakeTokenBytes];
            Buffer.BlockCopy(payload, 4, actualToken, 0, actualToken.Length);
            if (!FixedTimeEquals(actualToken, expectedToken))
            {
                throw new InvalidDataException("Local IPC handshake token mismatch.");
            }
            var processId = LocalFrameCodec.ReadUInt32(payload, 0);
            if (processId == 0 || processId > int.MaxValue)
            {
                throw new InvalidDataException("Invalid local IPC process ID.");
            }
            return checked((int)processId);
        }

        public static byte[] Resize(short columns, short rows)
        {
            if (columns < 2 || rows < 2) throw new ArgumentOutOfRangeException("columns");
            var payload = new byte[4];
            LocalFrameCodec.WriteUInt16(payload, 0, checked((ushort)columns));
            LocalFrameCodec.WriteUInt16(payload, 2, checked((ushort)rows));
            return payload;
        }

        public static void ParseResize(byte[] payload, out short columns, out short rows)
        {
            if (payload == null || payload.Length != 4)
            {
                throw new InvalidDataException("Invalid local IPC resize payload.");
            }
            columns = checked((short)LocalFrameCodec.ReadUInt16(payload, 0));
            rows = checked((short)LocalFrameCodec.ReadUInt16(payload, 2));
            if (columns < 2 || rows < 2)
            {
                throw new InvalidDataException("Local IPC resize is outside the supported range.");
            }
        }

        public static byte[] Lifecycle(LocalLifecycleState state, int exitCode)
        {
            var payload = new byte[5];
            payload[0] = (byte)state;
            LocalFrameCodec.WriteUInt32(payload, 1, unchecked((uint)exitCode));
            return payload;
        }

        public static LocalLifecycleState ParseLifecycle(byte[] payload, out int exitCode)
        {
            if (payload == null || payload.Length != 5 ||
                !Enum.IsDefined(typeof(LocalLifecycleState), payload[0]))
            {
                throw new InvalidDataException("Invalid local IPC lifecycle payload.");
            }
            exitCode = unchecked((int)LocalFrameCodec.ReadUInt32(payload, 1));
            return (LocalLifecycleState)payload[0];
        }

        public static byte[] Title(string title)
        {
            title = NormalizeTitle(title);
            var payload = StrictUtf8.GetBytes(title);
            if (payload.Length > MaximumTitleBytes)
                throw new ArgumentException("terminal title exceeds the UTF-8 limit", "title");
            return payload;
        }

        public static string ParseTitle(byte[] payload)
        {
            if (payload == null || payload.Length > MaximumTitleBytes)
                throw new InvalidDataException("Invalid local IPC title payload.");
            string title;
            try { title = StrictUtf8.GetString(payload); }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("Local IPC title is not valid UTF-8.", error);
            }
            if (!string.Equals(title, NormalizeTitle(title), StringComparison.Ordinal))
                throw new InvalidDataException("Local IPC title contains unsupported control characters.");
            return title;
        }

        public static byte[] SessionRemoved()
        {
            return new byte[0];
        }

        public static void ParseSessionRemoved(byte[] payload)
        {
            if (payload == null || payload.Length != 0)
                throw new InvalidDataException("Invalid local IPC session-removed payload.");
        }

        public static byte[] RemoteAccessStopped()
        {
            return new byte[0];
        }

        public static void ParseRemoteAccessStopped(byte[] payload)
        {
            if (payload == null || payload.Length != 0)
                throw new InvalidDataException("Invalid local IPC remote-access-stopped payload.");
        }

        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (character == '\t' || (!char.IsControl(character) && character != '\u007f'))
                    builder.Append(character);
            }
            return builder.ToString().Trim();
        }

        public static byte[] Input(Guid inputId, byte[] bytes)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("input bytes are required", "bytes");
            if (bytes.Length > LocalFrameCodec.MaximumFrameBytes - 64)
            {
                throw new ArgumentException("input exceeds local IPC frame limit", "bytes");
            }
            var payload = new byte[16 + bytes.Length];
            Buffer.BlockCopy(inputId.ToByteArray(), 0, payload, 0, 16);
            Buffer.BlockCopy(bytes, 0, payload, 16, bytes.Length);
            return payload;
        }

        public static Guid ParseInput(byte[] payload, out byte[] bytes)
        {
            if (payload == null || payload.Length <= 16)
            {
                throw new InvalidDataException("Invalid local IPC input payload.");
            }
            var idBytes = new byte[16];
            Buffer.BlockCopy(payload, 0, idBytes, 0, 16);
            var inputId = new Guid(idBytes);
            if (inputId == Guid.Empty) throw new InvalidDataException("Input ID is empty.");
            bytes = new byte[payload.Length - 16];
            Buffer.BlockCopy(payload, 16, bytes, 0, bytes.Length);
            return inputId;
        }

        public static byte[] RemoteInput(Guid inputId, ulong localInputEpoch, byte[] bytes)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            if (bytes == null || bytes.Length == 0 || bytes.Length > LocalFrameCodec.MaximumFrameBytes - 72)
            {
                throw new ArgumentException("input bytes are invalid", "bytes");
            }
            var payload = new byte[24 + bytes.Length];
            Buffer.BlockCopy(inputId.ToByteArray(), 0, payload, 0, 16);
            LocalFrameCodec.WriteUInt64(payload, 16, localInputEpoch);
            Buffer.BlockCopy(bytes, 0, payload, 24, bytes.Length);
            return payload;
        }

        public static Guid ParseRemoteInput(byte[] payload, out ulong localInputEpoch, out byte[] bytes)
        {
            if (payload == null || payload.Length <= 24) throw new InvalidDataException("Invalid V2 remote input payload.");
            var idBytes = new byte[16];
            Buffer.BlockCopy(payload, 0, idBytes, 0, 16);
            var inputId = new Guid(idBytes);
            if (inputId == Guid.Empty) throw new InvalidDataException("Input ID is empty.");
            localInputEpoch = LocalFrameCodec.ReadUInt64(payload, 16);
            bytes = new byte[payload.Length - 24];
            Buffer.BlockCopy(payload, 24, bytes, 0, bytes.Length);
            return inputId;
        }

        public static byte[] RemoteInputResult(Guid inputId, LocalRemoteInputResult result)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            if (!Enum.IsDefined(typeof(LocalRemoteInputResult), result))
            {
                throw new ArgumentOutOfRangeException("result");
            }
            var payload = new byte[17];
            Buffer.BlockCopy(inputId.ToByteArray(), 0, payload, 0, 16);
            payload[16] = (byte)result;
            return payload;
        }

        public static Guid ParseRemoteInputResult(byte[] payload, out LocalRemoteInputResult result)
        {
            if (payload == null || payload.Length != 17 ||
                !Enum.IsDefined(typeof(LocalRemoteInputResult), payload[16]))
            {
                throw new InvalidDataException("Invalid remote input result payload.");
            }
            var idBytes = new byte[16];
            Buffer.BlockCopy(payload, 0, idBytes, 0, 16);
            var inputId = new Guid(idBytes);
            if (inputId == Guid.Empty) throw new InvalidDataException("Input result ID is empty.");
            result = (LocalRemoteInputResult)payload[16];
            return inputId;
        }

        public static byte[] LocalInputEpoch(ulong epoch)
        {
            if (epoch == 0) throw new ArgumentOutOfRangeException("epoch");
            var payload = new byte[8];
            LocalFrameCodec.WriteUInt64(payload, 0, epoch);
            return payload;
        }

        public static ulong ParseLocalInputEpoch(byte[] payload)
        {
            if (payload == null || payload.Length != 8) throw new InvalidDataException("Invalid local input epoch payload.");
            var epoch = LocalFrameCodec.ReadUInt64(payload, 0);
            if (epoch == 0) throw new InvalidDataException("Local input epoch is zero.");
            return epoch;
        }

        public static byte[] Gap(ulong startSequence, ulong endSequence)
        {
            if (startSequence == 0 || endSequence < startSequence)
            {
                throw new ArgumentOutOfRangeException("startSequence");
            }
            var payload = new byte[16];
            LocalFrameCodec.WriteUInt64(payload, 0, startSequence);
            LocalFrameCodec.WriteUInt64(payload, 8, endSequence);
            return payload;
        }

        public static void ParseGap(byte[] payload, out ulong startSequence, out ulong endSequence)
        {
            if (payload == null || payload.Length != 16)
            {
                throw new InvalidDataException("Invalid local IPC gap payload.");
            }
            startSequence = LocalFrameCodec.ReadUInt64(payload, 0);
            endSequence = LocalFrameCodec.ReadUInt64(payload, 8);
            if (startSequence == 0 || endSequence < startSequence)
            {
                throw new InvalidDataException("Invalid local IPC gap range.");
            }
        }

        public static byte[] ParseToken(string encoded)
        {
            byte[] token;
            try
            {
                token = Convert.FromBase64String(encoded);
            }
            catch (FormatException error)
            {
                throw new ArgumentException("handshake token is not valid Base64", error);
            }
            RequireLength(token, HandshakeTokenBytes, "handshake token");
            return token;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static void RequireLength(byte[] bytes, int expected, string label)
        {
            if (bytes == null || bytes.Length != expected)
            {
                throw new ArgumentException(label + " must be exactly " + expected + " bytes.");
            }
        }
    }
}
