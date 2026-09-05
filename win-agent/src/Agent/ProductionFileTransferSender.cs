using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.FileTransfer.V2;

namespace TermLink.Agent
{
    internal sealed class FileTransferPeerKeyMaterial : IDisposable
    {
        internal FileTransferPeerKeyMaterial(Guid deviceId, long keyEpoch, byte[] pairwiseKey)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("Peer device ID is required.", nameof(deviceId));
            if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            if (pairwiseKey == null || pairwiseKey.Length != 32)
                throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
            DeviceId = deviceId;
            KeyEpoch = keyEpoch;
            PairwiseKey = (byte[])pairwiseKey.Clone();
        }

        internal Guid DeviceId { get; }
        internal long KeyEpoch { get; }
        internal byte[] PairwiseKey { get; private set; }

        public void Dispose()
        {
            if (PairwiseKey != null) CryptographicOperations.ZeroMemory(PairwiseKey);
            PairwiseKey = null;
        }
    }

    /// <summary>
    /// Sends one file over its own bounded E2EE WebSocket. The receiver's
    /// durable contiguous offset is authoritative after every reconnect, so
    /// neither a Relay reconnect nor an Agent restart restarts a completed
    /// prefix from byte zero.
    /// </summary>
    internal sealed class ProductionFileTransferSender : IFileTransferSender
    {
        internal delegate Task AuthenticateSocketAsync(
            ClientWebSocketOptions options,
            string path,
            CancellationToken token);

        internal delegate Task<FileTransferPeerKeyMaterial> ResolvePeerAsync(
            Guid requesterDeviceId,
            CancellationToken token);

        internal const int ChunkBytes = 256 * 1024;
        private const int MaximumWireBytes = 320 * 1024;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(15);
        private readonly string serverUrl;
        private readonly Guid localDeviceId;
        private readonly AuthenticateSocketAsync authenticate;
        private readonly ResolvePeerAsync resolvePeer;

        internal ProductionFileTransferSender(
            string serverUrl,
            Guid localDeviceId,
            AuthenticateSocketAsync authenticate,
            ResolvePeerAsync resolvePeer)
        {
            if (!Uri.TryCreate(serverUrl?.TrimEnd('/'), UriKind.Absolute, out var root) ||
                (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrWhiteSpace(root.Host) || !string.IsNullOrEmpty(root.Query) ||
                !string.IsNullOrEmpty(root.Fragment))
            {
                throw new ArgumentException("A valid TermLink server URL is required.", nameof(serverUrl));
            }
            if (localDeviceId == Guid.Empty)
                throw new ArgumentException("Local device ID is required.", nameof(localDeviceId));
            this.serverUrl = serverUrl.TrimEnd('/');
            this.localDeviceId = localDeviceId;
            this.authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
            this.resolvePeer = resolvePeer ?? throw new ArgumentNullException(nameof(resolvePeer));
        }

        public async Task SendAsync(
            FileOperationUploadTarget target,
            Stream content,
            CancellationToken token)
        {
            ValidateTarget(target, content);
            var delay = TimeSpan.FromMilliseconds(250);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var peer = await resolvePeer(target.RequesterDeviceId, token).ConfigureAwait(false);
                    if (peer.DeviceId != target.RequesterDeviceId)
                        throw new InvalidDataException("File-transfer peer identity changed.");
                    using var socket = await ConnectAsync(target.TransferId, token).ConfigureAwait(false);
                    await TransferAsync(socket, target, content, peer, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (FileOperationRemoteException error) when (error.Retryable)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(
                        MaximumRetryDelay.TotalMilliseconds,
                        Math.Max(250, delay.TotalMilliseconds * 2)));
                }
                catch (Exception error) when (IsRetryable(error))
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(
                        MaximumRetryDelay.TotalMilliseconds,
                        Math.Max(250, delay.TotalMilliseconds * 2)));
                }
                catch (FileOperationRemoteException) { throw; }
                catch (Exception error)
                {
                    throw new FileOperationRemoteException(
                        "Realtime file transfer was rejected / 实时文件传输被拒绝。",
                        error,
                        false);
                }
            }
        }

        private async Task<ClientWebSocket> ConnectAsync(Guid transferId, CancellationToken token)
        {
            var socket = new ClientWebSocket();
            try
            {
                socket.Options.AddSubProtocol("termlink.file-transfer.v2");
                ProductionTls.ConfigureWebSocket(socket.Options, serverUrl);
                await authenticate(socket.Options, "/v1/file-transfers", token).ConfigureAwait(false);
                var root = new Uri(serverUrl + "/");
                var builder = new UriBuilder(root)
                {
                    Scheme = root.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                    Path = "/v1/file-transfers",
                    Query = string.Empty,
                    Fragment = string.Empty
                };
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(HandshakeTimeout);
                await socket.ConnectAsync(builder.Uri, handshake.Token).ConfigureAwait(false);
                var hello = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    role = "pc",
                    transfer_id = transferId.ToString("D"),
                    protocol_versions = new[] { 2 },
                    capabilities = new[] { "e2ee", "resume_offset_v2" }
                });
                try
                {
                    await socket.SendAsync(
                        hello,
                        WebSocketMessageType.Text,
                        true,
                        handshake.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(hello); }
                var welcome = await ReceiveMessageAsync(socket, 4096, handshake.Token).ConfigureAwait(false);
                try
                {
                    if (welcome.Type != WebSocketMessageType.Text ||
                        !ValidWelcome(welcome.Bytes))
                    {
                        throw new InvalidDataException("File-transfer Relay welcome is invalid.");
                    }
                }
                finally { CryptographicOperations.ZeroMemory(welcome.Bytes); }
                return socket;
            }
            catch
            {
                socket.Abort();
                socket.Dispose();
                throw;
            }
        }

        private async Task TransferAsync(
            ClientWebSocket socket,
            FileOperationUploadTarget target,
            Stream content,
            FileTransferPeerKeyMaterial peer,
            CancellationToken token)
        {
            var keyId = KeyId(target.TransferId, peer.KeyEpoch);
            ulong outgoingSequence = 0;
            ulong incomingSequence = 0;
            try
            {
                var offer = new FileTransferPayloadV2
                {
                    Offer = new FileTransferOfferV2
                    {
                        Filename = target.Filename,
                        SizeBytes = checked((ulong)target.SizeBytes),
                        Sha256 = ByteString.CopyFrom(target.Sha256),
                        DirectoryZip = target.DirectoryZip,
                        ChunkBytes = ChunkBytes
                    }
                };
                await SendPayloadAsync(
                    socket, target.TransferId, peer, keyId,
                    FileTransferMessageTypeV2.Offer, ++outgoingSequence, offer, token).ConfigureAwait(false);
                var response = await ReceivePayloadAsync(
                    socket, target.TransferId, peer, keyId, incomingSequence, token).ConfigureAwait(false);
                incomingSequence = response.Sequence;
                ThrowIfTerminal(response);
                if (response.Payload.BodyCase != FileTransferPayloadV2.BodyOneofCase.Resume)
                    throw new InvalidDataException("File-transfer receiver did not provide a resume offset.");
                var offset = checked((long)response.Payload.Resume.CommittedOffset);
                if (offset < 0 || offset > target.SizeBytes)
                    throw new InvalidDataException("File-transfer receiver resume offset is invalid.");

                var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                try
                {
                    while (offset < target.SizeBytes)
                    {
                        token.ThrowIfCancellationRequested();
                        content.Position = offset;
                        var wanted = (int)Math.Min(ChunkBytes, target.SizeBytes - offset);
                        var count = 0;
                        while (count < wanted)
                        {
                            var read = await content.ReadAsync(
                                buffer.AsMemory(count, wanted - count), token).ConfigureAwait(false);
                            if (read == 0)
                                throw new InvalidDataException("File-transfer source ended before its declared size.");
                            count += read;
                        }
                        var chunk = new FileTransferPayloadV2
                        {
                            Chunk = new FileTransferChunkV2
                            {
                                Offset = checked((ulong)offset),
                                Data = ByteString.CopyFrom(buffer, 0, count)
                            }
                        };
                        await SendPayloadAsync(
                            socket, target.TransferId, peer, keyId,
                            FileTransferMessageTypeV2.Chunk, ++outgoingSequence, chunk, token).ConfigureAwait(false);
                        response = await ReceivePayloadAsync(
                            socket, target.TransferId, peer, keyId, incomingSequence, token).ConfigureAwait(false);
                        incomingSequence = response.Sequence;
                        ThrowIfTerminal(response);
                        long committed;
                        if (response.Payload.BodyCase == FileTransferPayloadV2.BodyOneofCase.Ack)
                            committed = checked((long)response.Payload.Ack.CommittedOffset);
                        else if (response.Payload.BodyCase == FileTransferPayloadV2.BodyOneofCase.Resume)
                            committed = checked((long)response.Payload.Resume.CommittedOffset);
                        else
                            throw new InvalidDataException("File-transfer receiver returned an invalid chunk acknowledgement.");
                        if (committed < 0 || committed > offset + count || committed > target.SizeBytes)
                            throw new InvalidDataException("File-transfer receiver committed an invalid offset.");
                        offset = committed;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer.AsSpan(0, ChunkBytes));
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var complete = new FileTransferPayloadV2
                {
                    Complete = new FileTransferCompleteV2
                    {
                        SizeBytes = checked((ulong)target.SizeBytes),
                        Sha256 = ByteString.CopyFrom(target.Sha256)
                    }
                };
                await SendPayloadAsync(
                    socket, target.TransferId, peer, keyId,
                    FileTransferMessageTypeV2.Complete, ++outgoingSequence, complete, token).ConfigureAwait(false);
                response = await ReceivePayloadAsync(
                    socket, target.TransferId, peer, keyId, incomingSequence, token).ConfigureAwait(false);
                ThrowIfTerminal(response);
                if (response.Payload.BodyCase != FileTransferPayloadV2.BodyOneofCase.CompleteAck ||
                    response.Payload.CompleteAck.CommittedOffset != checked((ulong)target.SizeBytes) ||
                    !CryptographicOperations.FixedTimeEquals(
                        response.Payload.CompleteAck.Sha256.Span,
                        target.Sha256))
                {
                    throw new InvalidDataException("File-transfer completion acknowledgement is invalid.");
                }
            }
            finally { CryptographicOperations.ZeroMemory(keyId); }
        }

        private async Task SendPayloadAsync(
            ClientWebSocket socket,
            Guid transferId,
            FileTransferPeerKeyMaterial peer,
            byte[] keyId,
            FileTransferMessageTypeV2 messageType,
            ulong sequence,
            FileTransferPayloadV2 payload,
            CancellationToken token)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var envelope = new FileTransferEnvelopeV2
            {
                ProtocolVersion = 2,
                TransferId = ByteString.CopyFrom(GuidBytes(transferId)),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(localDeviceId)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(peer.DeviceId)),
                MessageType = messageType,
                Sequence = sequence,
                KeyEpoch = checked((ulong)peer.KeyEpoch),
                KeyId = ByteString.CopyFrom(keyId),
                Nonce = ByteString.CopyFrom(nonce),
                Ciphertext = ByteString.Empty
            };
            var plaintext = payload.ToByteArray();
            byte[] key = null;
            byte[] wire = null;
            try
            {
                key = SessionCrypto.DeriveFileTransferKey(
                    peer.PairwiseKey,
                    transferId,
                    CryptoDirection.PcToAndroid,
                    peer.KeyEpoch,
                    keyId);
                envelope.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(
                    key,
                    nonce,
                    AssociatedData(envelope),
                    plaintext));
                wire = envelope.ToByteArray();
                if (wire.Length < 1 || wire.Length > MaximumWireBytes)
                    throw new InvalidDataException("File-transfer frame exceeds its bounded wire size.");
                await socket.SendAsync(wire, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(plaintext);
                if (key != null) CryptographicOperations.ZeroMemory(key);
                if (wire != null) CryptographicOperations.ZeroMemory(wire);
            }
        }

        private async Task<ReceivedPayload> ReceivePayloadAsync(
            ClientWebSocket socket,
            Guid transferId,
            FileTransferPeerKeyMaterial peer,
            byte[] expectedKeyId,
            ulong previousSequence,
            CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ResponseTimeout);
            ReceivedMessage message;
            try
            {
                message = await ReceiveMessageAsync(socket, MaximumWireBytes, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely,
                    "File-transfer peer response timed out.");
            }
            try
            {
                if (message.Type != WebSocketMessageType.Binary)
                    throw new InvalidDataException("File-transfer peer returned a non-binary frame.");
                var envelope = FileTransferEnvelopeV2.Parser.ParseFrom(message.Bytes);
                if (envelope.ProtocolVersion != 2 ||
                    !envelope.TransferId.Span.SequenceEqual(GuidBytes(transferId)) ||
                    !envelope.SenderDeviceId.Span.SequenceEqual(GuidBytes(peer.DeviceId)) ||
                    !envelope.RecipientDeviceId.Span.SequenceEqual(GuidBytes(localDeviceId)) ||
                    envelope.Sequence <= previousSequence || envelope.KeyEpoch != checked((ulong)peer.KeyEpoch) ||
                    !envelope.KeyId.Span.SequenceEqual(expectedKeyId) || envelope.Nonce.Length != 12 ||
                    envelope.Ciphertext.Length < 16)
                {
                    throw new InvalidDataException("File-transfer peer envelope metadata is invalid.");
                }
                var key = SessionCrypto.DeriveFileTransferKey(
                    peer.PairwiseKey,
                    transferId,
                    CryptoDirection.AndroidToPc,
                    peer.KeyEpoch,
                    expectedKeyId);
                byte[] plaintext = null;
                try
                {
                    plaintext = SessionCrypto.Decrypt(
                        key,
                        envelope.Nonce.ToByteArray(),
                        AssociatedData(envelope),
                        envelope.Ciphertext.ToByteArray());
                    var payload = FileTransferPayloadV2.Parser.ParseFrom(plaintext);
                    if (!MessageMatches(envelope.MessageType, payload.BodyCase))
                        throw new InvalidDataException("File-transfer payload does not match its authenticated type.");
                    return new ReceivedPayload(envelope.Sequence, payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally { CryptographicOperations.ZeroMemory(message.Bytes); }
        }

        private static void ThrowIfTerminal(ReceivedPayload response)
        {
            if (response.Payload.BodyCase == FileTransferPayloadV2.BodyOneofCase.Cancel)
                throw new FileOperationRemoteException(
                    "File transfer was cancelled by Android / 文件传输已被安卓端取消。",
                    false);
            if (response.Payload.BodyCase == FileTransferPayloadV2.BodyOneofCase.Error)
                throw new FileOperationRemoteException(
                    "Android rejected the file transfer / 安卓端拒绝了文件传输。",
                    response.Payload.Error.Retryable);
        }

        private static bool MessageMatches(
            FileTransferMessageTypeV2 type,
            FileTransferPayloadV2.BodyOneofCase body) =>
            type switch
            {
                FileTransferMessageTypeV2.Offer => body == FileTransferPayloadV2.BodyOneofCase.Offer,
                FileTransferMessageTypeV2.Resume => body == FileTransferPayloadV2.BodyOneofCase.Resume,
                FileTransferMessageTypeV2.Chunk => body == FileTransferPayloadV2.BodyOneofCase.Chunk,
                FileTransferMessageTypeV2.Ack => body == FileTransferPayloadV2.BodyOneofCase.Ack,
                FileTransferMessageTypeV2.Complete => body == FileTransferPayloadV2.BodyOneofCase.Complete,
                FileTransferMessageTypeV2.CompleteAck => body == FileTransferPayloadV2.BodyOneofCase.CompleteAck,
                FileTransferMessageTypeV2.Cancel => body == FileTransferPayloadV2.BodyOneofCase.Cancel,
                FileTransferMessageTypeV2.Error => body == FileTransferPayloadV2.BodyOneofCase.Error,
                _ => false
            };

        private static byte[] AssociatedData(FileTransferEnvelopeV2 envelope)
        {
            var metadata = envelope.Clone();
            metadata.Ciphertext = ByteString.Empty;
            return metadata.ToByteArray();
        }

        private static byte[] KeyId(Guid transferId, long keyEpoch) => Encoding.ASCII.GetBytes(
            "file-transfer-v2:" + transferId.ToString("N") + ":" +
            keyEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static bool ValidWelcome(byte[] value)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.TryGetProperty("type", out var type) &&
                    type.GetString() == "welcome" &&
                    document.RootElement.TryGetProperty("protocol_version", out var version) &&
                    version.GetInt32() == 2;
            }
            catch (JsonException) { return false; }
        }

        private static async Task<ReceivedMessage> ReceiveMessageAsync(
            ClientWebSocket socket,
            int maximumBytes,
            CancellationToken token)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(maximumBytes);
            try
            {
                var count = 0;
                WebSocketMessageType? type = null;
                while (true)
                {
                    if (count == maximumBytes)
                        throw new InvalidDataException("File-transfer WebSocket message is too large.");
                    var result = await socket.ReceiveAsync(
                        buffer.AsMemory(count, maximumBytes - count), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("File-transfer Relay closed the connection.");
                    if (type.HasValue && type.Value != result.MessageType)
                        throw new InvalidDataException("File-transfer WebSocket changed message type mid-frame.");
                    type = result.MessageType;
                    count += result.Count;
                    if (result.EndOfMessage)
                        return new ReceivedMessage(type.Value, buffer.AsSpan(0, count).ToArray());
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, maximumBytes));
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool IsRetryable(Exception error) =>
            error is WebSocketException || error is HttpRequestException || error is IOException ||
            error is TimeoutException || error is ProductionReauthenticationRequiredException ||
            error is OperationCanceledException;

        private static void ValidateTarget(FileOperationUploadTarget target, Stream content)
        {
            if (target == null || target.TransferId == Guid.Empty || target.RequesterDeviceId == Guid.Empty ||
                target.SizeBytes < 0 || target.SizeBytes > FileTransferPreparer.MaximumTransferBytes ||
                target.Sha256 == null || target.Sha256.Length != 32 || string.IsNullOrWhiteSpace(target.Filename) ||
                content == null || !content.CanRead || !content.CanSeek)
            {
                throw new ArgumentException("File-transfer target or source is invalid.");
            }
        }

        private readonly struct ReceivedMessage
        {
            internal ReceivedMessage(WebSocketMessageType type, byte[] bytes)
            {
                Type = type;
                Bytes = bytes;
            }
            internal WebSocketMessageType Type { get; }
            internal byte[] Bytes { get; }
        }

        private readonly struct ReceivedPayload
        {
            internal ReceivedPayload(ulong sequence, FileTransferPayloadV2 payload)
            {
                Sequence = sequence;
                Payload = payload;
            }
            internal ulong Sequence { get; }
            internal FileTransferPayloadV2 Payload { get; }
        }
    }
}
