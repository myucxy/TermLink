using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Protocol;

public sealed record SessionTitleRecipient(
    Guid RecipientDeviceId,
    long KeyEpoch,
    byte[] PairwiseKey,
    byte[] Nonce = null);

public static class SessionTitleCrypto
{
    public const uint FormatVersion = 1;
    public const int MaxTitleUtf8Bytes = 4_096;
    public const int MaxEnvelopeBytes = 1_048_576;
    public const int MaxRecipients = 4_096;

    private const int TagBytes = 16;
    private const int NonceBytes = 12;
    private const int HeaderBytes = 8 + 4 + 16 + 16 + 4;
    private const int EntryHeaderBytes = 16 + 8 + NonceBytes + 4;
    private static readonly byte[] Magic = "TLTITLE1"u8.ToArray();
    private static readonly byte[] AadPrefix = "TERMLINK-SESSION-TITLE-AAD-V1\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string KeyId(Guid recipientDeviceId, long keyEpoch)
    {
        ValidateRecipientMetadata(recipientDeviceId, keyEpoch);
        return "title-v1:" + recipientDeviceId.ToString("N") + ":" + keyEpoch.ToString("D8");
    }

    public static byte[] Seal(
        string title,
        Guid sessionId,
        Guid pcDeviceId,
        IEnumerable<SessionTitleRecipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(title);
        ValidateRootMetadata(sessionId, pcDeviceId);
        var recipientList = recipients?.ToList() ?? throw new ArgumentNullException(nameof(recipients));
        if (recipientList.Count is < 1 or > MaxRecipients)
            throw new ArgumentException("Session title recipient count is invalid.", nameof(recipients));

        var recipientIds = new HashSet<Guid>();
        foreach (var recipient in recipientList)
        {
            if (recipient is null) throw new ArgumentException("Session title recipient is null.", nameof(recipients));
            ValidateRecipientMetadata(recipient.RecipientDeviceId, recipient.KeyEpoch);
            if (recipient.RecipientDeviceId == pcDeviceId)
                throw new ArgumentException("The PC device cannot be a session title recipient.", nameof(recipients));
            if (recipient.PairwiseKey is null || recipient.PairwiseKey.Length != 32)
                throw new ArgumentException("Session title pairwise keys must be 32 bytes.", nameof(recipients));
            if (recipient.Nonce is not null && recipient.Nonce.Length != NonceBytes)
                throw new ArgumentException("Session title nonces must be 12 bytes.", nameof(recipients));
            if (!recipientIds.Add(recipient.RecipientDeviceId))
                throw new ArgumentException("Session title recipients must be unique.", nameof(recipients));
        }

        byte[] plaintext = StrictUtf8.GetBytes(title);
        try
        {
            if (plaintext.Length > MaxTitleUtf8Bytes)
                throw new ArgumentException("Session title exceeds the UTF-8 size limit.", nameof(title));
            var envelopeSize = checked(HeaderBytes + recipientList.Count * checked(EntryHeaderBytes + plaintext.Length + TagBytes));
            if (envelopeSize > MaxEnvelopeBytes)
                throw new ArgumentException("Session title envelope exceeds the size limit.", nameof(recipients));

            using var output = new MemoryStream(envelopeSize);
            output.Write(Magic);
            WriteUInt32(output, FormatVersion);
            output.Write(UuidBytes(sessionId));
            output.Write(UuidBytes(pcDeviceId));
            WriteUInt32(output, checked((uint)recipientList.Count));

            for (var index = 0; index < recipientList.Count; index++)
            {
                var recipient = recipientList[index];
                byte[] nonce = recipient.Nonce is null
                    ? RandomNumberGenerator.GetBytes(NonceBytes)
                    : (byte[])recipient.Nonce.Clone();
                byte[] keyId = StrictUtf8.GetBytes(KeyId(recipient.RecipientDeviceId, recipient.KeyEpoch));
                byte[] key = null;
                byte[] aad = null;
                byte[] ciphertext = null;
                try
                {
                    key = SessionCrypto.DeriveHistoryKey(
                        recipient.PairwiseKey,
                        sessionId,
                        recipient.RecipientDeviceId,
                        recipient.KeyEpoch,
                        keyId);
                    aad = AssociatedData(
                        sessionId,
                        pcDeviceId,
                        checked((uint)recipientList.Count),
                        checked((uint)index),
                        recipient.RecipientDeviceId,
                        recipient.KeyEpoch,
                        keyId,
                        nonce,
                        checked((uint)plaintext.Length));
                    ciphertext = SessionCrypto.Encrypt(key, nonce, aad, plaintext);

                    output.Write(UuidBytes(recipient.RecipientDeviceId));
                    WriteInt64(output, recipient.KeyEpoch);
                    output.Write(nonce);
                    WriteUInt32(output, checked((uint)ciphertext.Length));
                    output.Write(ciphertext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(keyId);
                    if (key is not null) CryptographicOperations.ZeroMemory(key);
                    if (aad is not null) CryptographicOperations.ZeroMemory(aad);
                    if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
                }
            }

            var result = output.ToArray();
            if (result.Length != envelopeSize)
                throw new InvalidDataException("Session title envelope length is inconsistent.");
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static string Open(
        byte[] pairwiseKey,
        byte[] sealedEnvelope,
        Guid expectedSessionId,
        Guid expectedPcDeviceId,
        Guid expectedRecipientDeviceId,
        long expectedKeyEpoch)
    {
        if (pairwiseKey is null || pairwiseKey.Length != 32)
            throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
        ValidateRootMetadata(expectedSessionId, expectedPcDeviceId);
        ValidateRecipientMetadata(expectedRecipientDeviceId, expectedKeyEpoch);
        if (expectedRecipientDeviceId == expectedPcDeviceId)
            throw new ArgumentException("The PC device cannot be a session title recipient.", nameof(expectedRecipientDeviceId));
        if (sealedEnvelope is null || sealedEnvelope.Length < HeaderBytes + EntryHeaderBytes + TagBytes ||
            sealedEnvelope.Length > MaxEnvelopeBytes)
            throw new InvalidDataException("Session title envelope size is invalid.");

        var reader = new EnvelopeReader(sealedEnvelope);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Session title envelope magic is invalid.");
        if (reader.ReadUInt32() != FormatVersion)
            throw new InvalidDataException("Session title envelope version is unsupported.");
        var sessionId = ReadGuid(reader.ReadBytes(16));
        var pcDeviceId = ReadGuid(reader.ReadBytes(16));
        if (sessionId != expectedSessionId || pcDeviceId != expectedPcDeviceId)
            throw new InvalidDataException("Session title envelope identity does not match the request.");
        var recipientCount = reader.ReadUInt32();
        if (recipientCount is 0 or > MaxRecipients)
            throw new InvalidDataException("Session title recipient count is invalid.");

        var recipientIds = new HashSet<Guid>();
        byte[] matchingNonce = null;
        byte[] matchingCiphertext = null;
        uint matchingIndex = 0;
        long matchingEpoch = 0;
        int? commonPlaintextLength = null;
        try
        {
            for (uint index = 0; index < recipientCount; index++)
            {
                var recipientId = ReadGuid(reader.ReadBytes(16));
                var keyEpoch = reader.ReadInt64();
                var nonce = reader.ReadBytes(NonceBytes);
                var ciphertextLength = reader.ReadUInt32();
                if (recipientId == Guid.Empty || recipientId == pcDeviceId || keyEpoch < 1 ||
                    ciphertextLength < TagBytes || ciphertextLength > MaxTitleUtf8Bytes + TagBytes)
                    throw new InvalidDataException("Session title recipient metadata is invalid.");
                if (!recipientIds.Add(recipientId))
                    throw new InvalidDataException("Session title envelope contains a duplicate recipient.");
                var plaintextLength = checked((int)ciphertextLength - TagBytes);
                if (commonPlaintextLength.HasValue && commonPlaintextLength.Value != plaintextLength)
                    throw new InvalidDataException("Session title entries have inconsistent plaintext lengths.");
                commonPlaintextLength = plaintextLength;
                var ciphertext = reader.ReadBytes(checked((int)ciphertextLength));

                if (recipientId == expectedRecipientDeviceId)
                {
                    matchingIndex = index;
                    matchingEpoch = keyEpoch;
                    matchingNonce = nonce.ToArray();
                    matchingCiphertext = ciphertext.ToArray();
                }
            }
            if (!reader.AtEnd)
                throw new InvalidDataException("Session title envelope has trailing data.");
            if (matchingCiphertext is null)
                throw new InvalidDataException("Session title envelope has no entry for this recipient.");
            if (matchingEpoch != expectedKeyEpoch)
                throw new InvalidDataException("Session title key epoch does not match the request.");

            byte[] keyId = StrictUtf8.GetBytes(KeyId(expectedRecipientDeviceId, expectedKeyEpoch));
            byte[] key = null;
            byte[] aad = null;
            byte[] plaintext = null;
            try
            {
                key = SessionCrypto.DeriveHistoryKey(
                    pairwiseKey,
                    expectedSessionId,
                    expectedRecipientDeviceId,
                    expectedKeyEpoch,
                    keyId);
                aad = AssociatedData(
                    expectedSessionId,
                    expectedPcDeviceId,
                    recipientCount,
                    matchingIndex,
                    expectedRecipientDeviceId,
                    expectedKeyEpoch,
                    keyId,
                    matchingNonce,
                    checked((uint)(matchingCiphertext.Length - TagBytes)));
                plaintext = SessionCrypto.Decrypt(key, matchingNonce, aad, matchingCiphertext);
                if (plaintext.Length > MaxTitleUtf8Bytes)
                    throw new InvalidDataException("Session title plaintext exceeds the size limit.");
                try
                {
                    return StrictUtf8.GetString(plaintext);
                }
                catch (DecoderFallbackException error)
                {
                    throw new InvalidDataException("Session title plaintext is not valid UTF-8.", error);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyId);
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (aad is not null) CryptographicOperations.ZeroMemory(aad);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            if (matchingNonce is not null) CryptographicOperations.ZeroMemory(matchingNonce);
            if (matchingCiphertext is not null) CryptographicOperations.ZeroMemory(matchingCiphertext);
        }
    }

    private static byte[] AssociatedData(
        Guid sessionId,
        Guid pcDeviceId,
        uint recipientCount,
        uint recipientIndex,
        Guid recipientDeviceId,
        long keyEpoch,
        byte[] keyId,
        byte[] nonce,
        uint plaintextLength)
    {
        using var output = new MemoryStream();
        output.Write(AadPrefix);
        WriteUInt32(output, FormatVersion);
        output.Write(UuidBytes(sessionId));
        output.Write(UuidBytes(pcDeviceId));
        WriteUInt32(output, recipientCount);
        WriteUInt32(output, recipientIndex);
        output.Write(UuidBytes(recipientDeviceId));
        WriteInt64(output, keyEpoch);
        WriteUInt16(output, checked((ushort)keyId.Length));
        output.Write(keyId);
        output.Write(nonce);
        WriteUInt32(output, plaintextLength);
        return output.ToArray();
    }

    private static void ValidateRootMetadata(Guid sessionId, Guid pcDeviceId)
    {
        if (sessionId == Guid.Empty || pcDeviceId == Guid.Empty)
            throw new ArgumentException("Session title identity metadata is invalid.");
    }

    private static void ValidateRecipientMetadata(Guid recipientDeviceId, long keyEpoch)
    {
        if (recipientDeviceId == Guid.Empty || keyEpoch < 1)
            throw new ArgumentException("Session title recipient metadata is invalid.");
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> value) => new(value, bigEndian: true);
    private static byte[] UuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt64(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        output.Write(bytes);
    }

    private ref struct EnvelopeReader
    {
        private readonly ReadOnlySpan<byte> value;
        private int offset;

        public EnvelopeReader(ReadOnlySpan<byte> value)
        {
            this.value = value;
            offset = 0;
        }

        public bool AtEnd => offset == value.Length;

        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length < 0 || offset > value.Length - length)
                throw new InvalidDataException("Session title envelope is truncated.");
            var result = value.Slice(offset, length);
            offset += length;
            return result;
        }

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));
    }
}
