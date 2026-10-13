using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Protocol;

public static class FrozenHistoryCrypto
{
    public const uint ContainerVersion = 1;
    public const uint PageFormatV1 = 1;
    public const uint PageFormatV2 = 2;
    public const int MaxPageV1Bytes = 16 * 1024 * 1024;
    public const int MaxPageV2Bytes = 64 * 1024 * 1024;
    public const int ObjectOverheadBytes = 4096;

    private static readonly byte[] KeyPrefixLegacy = "TERMLINK-FROZEN-HISTORY-KEY-V1\0"u8.ToArray();
    private static readonly byte[] KeyPrefixV1 = "TERMLINK-FROZEN-HISTORY-KEY-V1\0"u8.ToArray();
    private static readonly byte[] KeyPrefixV2 = "TERMLINK-FROZEN-HISTORY-KEY-V2\0"u8.ToArray();
    private static readonly byte[] AadPrefixLegacy = "TERMLINK-FROZEN-HISTORY-OBJECT-V1\0"u8.ToArray();
    private static readonly byte[] AadPrefixV1 = "TERMLINK-FROZEN-HISTORY-OBJECT-V1\0"u8.ToArray();
    private static readonly byte[] AadPrefixV2 = "TERMLINK-FROZEN-HISTORY-OBJECT-V2\0"u8.ToArray();
    public const string StateContractLegacy = "terminal_state_2_21_v2";
    public const string StateContractV1 = "terminal_state_2_22_v1";
    public const string StateContractV2 = "terminal_state_2_22_v2";

    public static string KeyId(Guid recipientDeviceId, long keyEpoch, string stateContract = StateContractV1)
    {
        if (recipientDeviceId == Guid.Empty || keyEpoch < 1) throw new ArgumentException("Frozen history key identity is invalid.");
        var normalized = NormalizeStateContract(stateContract);
        var variant = normalized == StateContractV2 ? "v2" : "v1";
        return "frozen-history-" + variant + ":" + recipientDeviceId.ToString("N") + ":" + keyEpoch.ToString("D8");
    }

    public static byte[] Seal(byte[] pairwiseKey, Guid sessionId, Guid pcDeviceId, Guid recipientDeviceId,
        long keyEpoch, byte[] token, ulong captureId, ulong captureRevision, ulong snapshotSeq,
        ulong requestBeforeLineId, uint maxLines, uint maxCells, uint maxUtf8Bytes, uint pageFormatVersion,
        string stateContract, byte[] page, byte[] nonce = null)
    {
        ValidateIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch);
        ValidateRequest(token, captureId, captureRevision, snapshotSeq, requestBeforeLineId,
            maxLines, maxCells, maxUtf8Bytes, pageFormatVersion);
        ValidateStateContract(stateContract, pageFormatVersion);
        if (page is null || page.Length is < 96 || page.Length > PageLimit(pageFormatVersion))
            throw new InvalidDataException("Frozen history page size is invalid.");
        var bounds = ReadPage(page, captureId, captureRevision, requestBeforeLineId, maxLines, maxCells,
            maxUtf8Bytes, pageFormatVersion);
        if (nonce is not null && nonce.Length != 12) throw new ArgumentException("Frozen history nonce must be 12 bytes.", nameof(nonce));
        var nonceBytes = nonce is null ? RandomNumberGenerator.GetBytes(12) : (byte[])nonce.Clone();
        var container = new FrozenHistoryCiphertextV1
        {
            ContainerVersion = ContainerVersion,
            SessionId = ByteString.CopyFrom(UuidBytes(sessionId)),
            PcDeviceId = ByteString.CopyFrom(UuidBytes(pcDeviceId)),
            RecipientDeviceId = ByteString.CopyFrom(UuidBytes(recipientDeviceId)),
            KeyEpoch = keyEpoch,
            KeyId = KeyId(recipientDeviceId, keyEpoch, stateContract),
            Token = ByteString.CopyFrom(token),
            CaptureId = captureId,
            CaptureRevision = captureRevision,
            SnapshotSeq = snapshotSeq,
            RequestBeforeLineId = requestBeforeLineId,
            MaxLines = maxLines,
            MaxCells = maxCells,
            MaxUtf8Bytes = maxUtf8Bytes,
            PageFormatVersion = pageFormatVersion,
            PlaintextBytes = (ulong)page.Length,
            Nonce = ByteString.CopyFrom(nonceBytes),
            FirstLineId = bounds.First,
            LastLineId = bounds.Last,
            HasMoreBefore = bounds.HasMore,
            NextBeforeLineId = bounds.Next
        };
        var key = DeriveKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, stateContract);
        var aad = AssociatedData(container, stateContract);
        try
        {
            var ciphertext = new byte[page.Length];
            var tag = new byte[16];
            try
            {
                using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonceBytes, page, ciphertext, tag, aad);
                var sealedBytes = new byte[ciphertext.Length + tag.Length];
                Buffer.BlockCopy(ciphertext, 0, sealedBytes, 0, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, sealedBytes, ciphertext.Length, tag.Length);
                container.Ciphertext = ByteString.CopyFrom(sealedBytes);
                CryptographicOperations.ZeroMemory(sealedBytes);
                var result = container.ToByteArray();
                if (result.Length > PageLimit(pageFormatVersion) + ObjectOverheadBytes)
                    throw new InvalidDataException("Frozen history sealed object exceeds its bound.");
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    /// <summary>The caller owns and must clear the returned plaintext byte array after decoding.</summary>
    public static byte[] Open(byte[] pairwiseKey, byte[] sealedObject, Guid expectedSessionId,
        Guid expectedPcDeviceId, Guid expectedRecipientDeviceId, long expectedKeyEpoch, byte[] expectedToken,
        ulong expectedCaptureId, ulong expectedCaptureRevision, ulong expectedSnapshotSeq,
        ulong expectedRequestBeforeLineId, uint expectedMaxLines, uint expectedMaxCells,
        uint expectedMaxUtf8Bytes, uint expectedPageFormatVersion, string expectedStateContract)
    {
        ValidateIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch);
        ValidateRequest(expectedToken, expectedCaptureId, expectedCaptureRevision, expectedSnapshotSeq,
            expectedRequestBeforeLineId, expectedMaxLines, expectedMaxCells, expectedMaxUtf8Bytes, expectedPageFormatVersion);
        ValidateStateContract(expectedStateContract, expectedPageFormatVersion);
        if (sealedObject is null || sealedObject.Length is < 1 ||
            sealedObject.Length > PageLimit(expectedPageFormatVersion) + ObjectOverheadBytes)
            throw new InvalidDataException("Frozen history sealed object size is invalid.");
        FrozenHistoryCiphertextV1 value;
        try { value = FrozenHistoryCiphertextV1.Parser.ParseFrom(sealedObject); }
        catch (InvalidProtocolBufferException error) { throw new InvalidDataException("Frozen history container is invalid protobuf.", error); }
        if (value.ContainerVersion != ContainerVersion ||
            !value.SessionId.Span.SequenceEqual(UuidBytes(expectedSessionId)) ||
            !value.PcDeviceId.Span.SequenceEqual(UuidBytes(expectedPcDeviceId)) ||
            !value.RecipientDeviceId.Span.SequenceEqual(UuidBytes(expectedRecipientDeviceId)) ||
            value.KeyEpoch != expectedKeyEpoch || value.KeyId != KeyId(expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract) ||
            !value.Token.Span.SequenceEqual(expectedToken) || value.CaptureId != expectedCaptureId ||
            value.CaptureRevision != expectedCaptureRevision || value.SnapshotSeq != expectedSnapshotSeq ||
            value.RequestBeforeLineId != expectedRequestBeforeLineId || value.MaxLines != expectedMaxLines ||
            value.MaxCells != expectedMaxCells || value.MaxUtf8Bytes != expectedMaxUtf8Bytes ||
            value.PageFormatVersion != expectedPageFormatVersion ||
            value.PlaintextBytes is < 96 or > MaxPageV2Bytes ||
            value.PlaintextBytes > (ulong)PageLimit(expectedPageFormatVersion) ||
            value.Nonce.Length != 12 || (ulong)value.Ciphertext.Length != value.PlaintextBytes + 16)
            throw new InvalidDataException("Frozen history object metadata does not match the request.");
        var key = DeriveKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract);
        var aad = AssociatedData(value, expectedStateContract);
        var plaintext = new byte[checked((int)value.PlaintextBytes)];
        try
        {
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(value.Nonce.Span, value.Ciphertext.Span[..^16], value.Ciphertext.Span[^16..], plaintext, aad);
            var bounds = ReadPage(plaintext, expectedCaptureId, expectedCaptureRevision,
                expectedRequestBeforeLineId, expectedMaxLines, expectedMaxCells, expectedMaxUtf8Bytes,
                expectedPageFormatVersion);
            if (value.FirstLineId != bounds.First || value.LastLineId != bounds.Last ||
                value.HasMoreBefore != bounds.HasMore || value.NextBeforeLineId != bounds.Next)
                throw new InvalidDataException("Frozen history page boundaries do not match its authenticated container.");
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public static byte[] AssociatedData(FrozenHistoryCiphertextV1 value, string stateContract)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateStateContract(stateContract, value.PageFormatVersion);
        if (value.SessionId.Length != 16 || value.PcDeviceId.Length != 16 || value.RecipientDeviceId.Length != 16 ||
            value.Token.Length != 32 || value.Nonce.Length != 12 || value.KeyId.Length > 128)
            throw new InvalidDataException("Frozen history authenticated metadata is invalid.");
        var keyId = Encoding.UTF8.GetBytes(value.KeyId);
        var stateBytes = Encoding.UTF8.GetBytes(stateContract);
        try
        {
            using var output = new MemoryStream();
            output.Write(AadPrefix(stateContract));
            Write32(output, value.ContainerVersion);
            output.Write(value.SessionId.Span);
            output.Write(value.PcDeviceId.Span);
            output.Write(value.RecipientDeviceId.Span);
            Write64(output, checked((ulong)value.KeyEpoch));
            Write16(output, checked((ushort)keyId.Length));
            output.Write(keyId);
            output.Write(value.Token.Span);
            foreach (var number in new[] { value.CaptureId, value.CaptureRevision, value.SnapshotSeq, value.RequestBeforeLineId }) Write64(output, number);
            foreach (var number in new[] { value.MaxLines, value.MaxCells, value.MaxUtf8Bytes, value.PageFormatVersion }) Write32(output, number);
            Write16(output, checked((ushort)stateBytes.Length));
            output.Write(stateBytes);
            Write64(output, value.PlaintextBytes);
            Write64(output, value.FirstLineId);
            Write64(output, value.LastLineId);
            output.WriteByte(value.HasMoreBefore ? (byte)1 : (byte)0);
            Write64(output, value.NextBeforeLineId);
            output.Write(value.Nonce.Span);
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(keyId); CryptographicOperations.ZeroMemory(stateBytes); }
    }

    private static byte[] DeriveKey(byte[] pairwiseKey, Guid sessionId, Guid recipientDeviceId, long keyEpoch,
        string stateContract)
    {
        var keyId = Encoding.UTF8.GetBytes(KeyId(recipientDeviceId, keyEpoch, stateContract));
        using var info = new MemoryStream();
        info.Write(KeyPrefix(stateContract));
        info.Write(UuidBytes(recipientDeviceId));
        Write64(info, checked((ulong)keyEpoch));
        Write16(info, checked((ushort)keyId.Length));
        info.Write(keyId);
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(pairwiseKey, UuidBytes(sessionId), info.ToArray()));
        var result = new byte[32];
        generator.GenerateBytes(result, 0, result.Length);
        CryptographicOperations.ZeroMemory(keyId);
        return result;
    }

    private static (ulong First, ulong Last, bool HasMore, ulong Next) ReadPage(byte[] page,
        ulong captureId, ulong revision, ulong anchor, uint maxLines, uint maxCells, uint maxUtf8,
        uint format)
    {
        var p = page.AsSpan();
        static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
        var magic = format == PageFormatV1 ? "TLHPV1\0\0"u8 : "TLHPV2\0\0"u8;
        var lines = U32(p, 48);
        var flags = U32(p, 68);
        if (!p[..8].SequenceEqual(magic) || BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(8, 2)) != format ||
            BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(10, 2)) != 96 || U32(p, 12) != page.Length ||
            U64(p, 16) != captureId || U64(p, 24) != revision || U64(p, 32) != anchor ||
            U32(p, 72) != format - 1 || !p.Slice(76, 16).SequenceEqual(new byte[16]) ||
            lines > maxLines || lines > 128 || U32(p, 52) > U32(p, 60) ||
            U32(p, 56) is < 1 or > 512 || U32(p, 60) > maxCells || U32(p, 64) > maxUtf8 ||
            flags > 3 || page.Length < 96L + 40L * lines)
            throw new InvalidDataException("Frozen history TLHP page header is invalid or does not match the request.");
        uint crc = uint.MaxValue;
        for (int index = 0; index < page.Length; index++)
        {
            if (index is >= 92 and <= 95) continue;
            crc = (crc >> 8) ^ CrcTable[(crc ^ page[index]) & 255];
        }
        if (~crc != U32(p, 92)) throw new InvalidDataException("Frozen history TLHP CRC is invalid.");
        ulong first = lines == 0 ? 0 : U64(p, 96);
        ulong last = lines == 0 ? 0 : U64(p, 96 + checked((int)(lines - 1) * 40));
        ulong next = U64(p, 40);
        if (next != (lines == 0 ? anchor : first) ||
            (lines == 0 && (flags != 0 || U32(p, 52) != 0 || U32(p, 60) != 0 || U32(p, 64) != 0)))
            throw new InvalidDataException("Frozen history TLHP page boundaries are invalid.");
        return (first, last, (flags & 1) != 0, next);
    }

    private static void ValidateIdentity(byte[] key, Guid session, Guid pc, Guid recipient, long epoch)
    {
        if (key is null || key.Length != 32 || session == Guid.Empty || pc == Guid.Empty ||
            recipient == Guid.Empty || pc == recipient || epoch < 1)
            throw new InvalidDataException("Frozen history identity or key is invalid.");
    }

    private static void ValidateRequest(byte[] token, ulong captureId, ulong revision, ulong snapshotSeq,
        ulong anchor, uint maxLines, uint maxCells, uint maxUtf8, uint format)
    {
        if (token is null || token.Length != 32 || captureId is 0 or > long.MaxValue || snapshotSeq == 0 ||
            snapshotSeq >= long.MaxValue || revision != snapshotSeq + 1 || anchor == 0 ||
            maxLines is < 1 or > 128 || maxCells is < 1 or > 65_536 ||
            maxUtf8 is < 1 or > 4 * 1024 * 1024 || format is not (PageFormatV1 or PageFormatV2))
            throw new InvalidDataException("Frozen history request metadata is invalid.");
    }

    private static int PageLimit(uint format) => format switch
    {
        PageFormatV1 => MaxPageV1Bytes,
        PageFormatV2 => MaxPageV2Bytes,
        _ => throw new InvalidDataException("Frozen history page format is unsupported.")
    };
    private static void ValidateStateContract(string stateContract, uint pageFormatVersion)
    {
        if (pageFormatVersion is not (PageFormatV1 or PageFormatV2) ||
            !IsAllowedStateContract(stateContract, pageFormatVersion))
            throw new InvalidDataException("Frozen history state contract does not match its page format.");
    }
    private static bool IsAllowedStateContract(string stateContract, uint pageFormatVersion) =>
        pageFormatVersion == PageFormatV1 ? stateContract == StateContractLegacy :
            stateContract is StateContractV1 or StateContractV2;
    private static string NormalizeStateContract(string value) => value switch
    {
        StateContractLegacy => StateContractLegacy,
        StateContractV1 => StateContractV1,
        StateContractV2 => StateContractV2,
        _ => throw new InvalidDataException("Frozen history state contract is unsupported.")
    };
    private static byte[] KeyPrefix(string stateContract) => NormalizeStateContract(stateContract) switch
    {
        StateContractLegacy => KeyPrefixLegacy,
        StateContractV1 => KeyPrefixV1,
        _ => KeyPrefixV2
    };
    private static byte[] AadPrefix(string stateContract) => NormalizeStateContract(stateContract) switch
    {
        StateContractLegacy => AadPrefixLegacy,
        StateContractV1 => AadPrefixV1,
        _ => AadPrefixV2
    };
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (uint)-(int)(crc & 1));
            table[i] = crc;
        }
        return table;
    }
    private static byte[] UuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
    private static void Write16(Stream stream, ushort number) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, number); stream.Write(bytes); }
    private static void Write32(Stream stream, uint number) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, number); stream.Write(bytes); }
    private static void Write64(Stream stream, ulong number) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, number); stream.Write(bytes); }
}
