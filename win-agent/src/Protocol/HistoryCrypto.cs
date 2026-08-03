using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Protocol;

public static class HistoryCrypto
{
    public const uint FormatVersion = 1;
    private static readonly byte[] AadPrefix = "TERMLINK-HISTORY-OBJECT-V1\0"u8.ToArray();

    public static string KeyId(Guid recipientDeviceId, long keyEpoch)
    {
        if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
        return "history-v1:" + recipientDeviceId.ToString("N") + ":" + keyEpoch.ToString("D8");
    }

    public static byte[] Seal(
        byte[] pairwiseKey,
        Guid sessionId,
        Guid pcDeviceId,
        Guid recipientDeviceId,
        long keyEpoch,
        IEnumerable<HistoryEventV1> events,
        byte[] nonce = null)
    {
        var values = events?.Select(value => value?.Clone() ?? throw new ArgumentException("History event is null.", nameof(events))).ToList()
            ?? throw new ArgumentNullException(nameof(events));
        ValidateEvents(values);
        var keyId = KeyId(recipientDeviceId, keyEpoch);
        var nonceBytes = nonce is null ? RandomNumberGenerator.GetBytes(12) : (byte[])nonce.Clone();
        if (nonceBytes.Length != 12) throw new ArgumentException("History nonce must be 12 bytes.", nameof(nonce));
        var container = new HistoryCiphertextV1
        {
            FormatVersion = FormatVersion,
            SessionId = ByteString.CopyFrom(UuidBytes(sessionId)),
            PcDeviceId = ByteString.CopyFrom(UuidBytes(pcDeviceId)),
            RecipientDeviceId = ByteString.CopyFrom(UuidBytes(recipientDeviceId)),
            KeyEpoch = keyEpoch,
            KeyId = keyId,
            StartSeq = values[0].Seq,
            EndSeq = values[^1].Seq,
            Nonce = ByteString.CopyFrom(nonceBytes)
        };
        var chunk = new HistoryChunkV1();
        chunk.Events.AddRange(values);
        var plaintext = chunk.ToByteArray();
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        var key = SessionCrypto.DeriveHistoryKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyIdBytes);
        var aad = AssociatedData(container);
        try
        {
            container.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, nonceBytes, aad, plaintext));
            return container.ToByteArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(keyIdBytes);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    public static HistoryChunkV1 Open(
        byte[] pairwiseKey,
        byte[] sealedObject,
        Guid expectedSessionId,
        Guid expectedPcDeviceId,
        Guid expectedRecipientDeviceId,
        long expectedKeyEpoch,
        string expectedKeyId,
        ulong expectedStartSequence,
        ulong expectedEndSequence)
    {
        ArgumentNullException.ThrowIfNull(sealedObject);
        var container = HistoryCiphertextV1.Parser.ParseFrom(sealedObject);
        ValidateContainer(container, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch,
            expectedKeyId, expectedStartSequence, expectedEndSequence);
        var keyIdBytes = Encoding.UTF8.GetBytes(container.KeyId);
        var key = SessionCrypto.DeriveHistoryKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, keyIdBytes);
        var aad = AssociatedData(container);
        byte[] plaintext = null;
        try
        {
            plaintext = SessionCrypto.Decrypt(key, container.Nonce.ToByteArray(), aad, container.Ciphertext.ToByteArray());
            var chunk = HistoryChunkV1.Parser.ParseFrom(plaintext);
            ValidateEvents(chunk.Events);
            if (chunk.Events[0].Seq != container.StartSeq || chunk.Events[^1].Seq != container.EndSeq)
                throw new InvalidDataException("History event range does not match its authenticated container.");
            return chunk;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyIdBytes);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static byte[] AssociatedData(HistoryCiphertextV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var keyId = Encoding.UTF8.GetBytes(value.KeyId ?? string.Empty);
        if (keyId.Length is < 8 or > 128) throw new InvalidDataException("History key ID length is invalid.");
        if (value.Nonce.Length != 12) throw new InvalidDataException("History nonce length is invalid.");
        using var stream = new MemoryStream();
        stream.Write(AadPrefix);
        stream.Write(value.SessionId.Span);
        stream.Write(value.PcDeviceId.Span);
        stream.Write(value.RecipientDeviceId.Span);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, value.KeyEpoch); stream.Write(number);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)keyId.Length)); stream.Write(length);
        stream.Write(keyId);
        BinaryPrimitives.WriteUInt64BigEndian(number, value.StartSeq); stream.Write(number);
        BinaryPrimitives.WriteUInt64BigEndian(number, value.EndSeq); stream.Write(number);
        stream.Write(value.Nonce.Span);
        CryptographicOperations.ZeroMemory(keyId);
        return stream.ToArray();
    }

    private static void ValidateContainer(HistoryCiphertextV1 value, Guid sessionId, Guid pcDeviceId, Guid recipientDeviceId,
        long keyEpoch, string keyId, ulong start, ulong end)
    {
        if (value.FormatVersion != FormatVersion || value.KeyEpoch != keyEpoch || value.KeyId != keyId ||
            value.StartSeq != start || value.EndSeq != end || start == 0 || end < start || value.Nonce.Length != 12 ||
            value.Ciphertext.Length < 16 || !value.SessionId.Span.SequenceEqual(UuidBytes(sessionId)) ||
            !value.PcDeviceId.Span.SequenceEqual(UuidBytes(pcDeviceId)) ||
            !value.RecipientDeviceId.Span.SequenceEqual(UuidBytes(recipientDeviceId)))
            throw new InvalidDataException("History object metadata does not match the requested object.");
    }

    private static void ValidateEvents(IList<HistoryEventV1> values)
    {
        if (values.Count == 0) throw new InvalidDataException("History chunk must contain at least one event.");
        ulong expected = values[0].Seq;
        if (expected == 0) throw new InvalidDataException("History event sequence must be positive.");
        foreach (var value in values)
        {
            if (value.Seq != expected || value.MessageType == MessageType.Unspecified)
                throw new InvalidDataException("History events must be contiguous and typed.");
            if (expected == ulong.MaxValue && !ReferenceEquals(value, values[^1]))
                throw new InvalidDataException("History event sequence overflow.");
            expected++;
        }
    }

    private static byte[] UuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
}
