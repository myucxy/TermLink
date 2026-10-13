using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace TermLink.Protocol;

/// <summary>One authenticated native TLDELTA1 object. The caller clears returned plaintext.</summary>
public sealed record OpenedDelta(byte[] Delta, ulong TargetObservedLocalInputEpoch,
    ulong TargetInitialTranscriptBeforeLineId);

public static class DeltaCrypto
{
    public const int HeaderBytes = 336;
    public const int MaximumObjectBytes = 8 * 1024 * 1024;
    public const int MaximumPlaintextBytes = MaximumObjectBytes - HeaderBytes - 16;
    public const string FormatName = "TERMLINK_STATE_DELTA_V1";
    public const uint FormatVersion = 1;
    public const string StateContractV1 = "terminal_state_2_22_v1";
    public const string StateContractV2 = "terminal_state_2_22_v2";
    private static readonly byte[] KeyPrefixV1 = "TERMLINK-STATE-DELTA-KEY-V1\0"u8.ToArray();
    private static readonly byte[] KeyPrefixV2 = "TERMLINK-STATE-DELTA-KEY-V2\0"u8.ToArray();
    private static readonly byte[] AadPrefixV1 = "TERMLINK-STATE-DELTA-OBJECT-V1\0"u8.ToArray();
    private static readonly byte[] AadPrefixV2 = "TERMLINK-STATE-DELTA-OBJECT-V2\0"u8.ToArray();
    private static readonly byte[] ContextPrefixV1 = "TermLink state delta context V1\n"u8.ToArray();
    private static readonly byte[] ContextPrefixV2 = "TermLink state delta context V2\n"u8.ToArray();

    public static byte[] DeriveContextId(Guid sessionId, Guid pcDeviceId, string stateContract)
    {
        var normalized = NormalizeStateContract(stateContract);
        if (sessionId == Guid.Empty || pcDeviceId == Guid.Empty)
            throw new InvalidDataException("Delta context identity is invalid.");
        var state = Encoding.UTF8.GetBytes(stateContract);
        var prefix = normalized == StateContractV1 ? ContextPrefixV1 : ContextPrefixV2;
        var material = new byte[prefix.Length + 32 + state.Length];
        try
        {
            prefix.CopyTo(material, 0);
            UuidBytes(sessionId).CopyTo(material, prefix.Length);
            UuidBytes(pcDeviceId).CopyTo(material, prefix.Length + 16);
            state.CopyTo(material, prefix.Length + 32);
            return SHA256.HashData(material);
        }
        finally { CryptographicOperations.ZeroMemory(state); CryptographicOperations.ZeroMemory(material); }
    }

    public static string KeyId(Guid recipientDeviceId, long keyEpoch, string stateContract = StateContractV1)
    {
        if (recipientDeviceId == Guid.Empty || keyEpoch < 1) throw new ArgumentException("Delta key identity is invalid.");
        var variant = NormalizeStateContract(stateContract) == StateContractV2 ? "v2" : "v1";
        return "delta-" + variant + ":" + recipientDeviceId.ToString("N") + ":" + keyEpoch.ToString("x");
    }

    /// <summary>Production callers omit nonce; explicit nonce is only for fixed cross-platform test vectors.</summary>
    public static byte[] Seal(byte[] pairwiseKey, Guid sessionId, Guid pcDeviceId, Guid recipientDeviceId,
        long keyEpoch, string stateContract, byte[] contextId, uint profile,
        ulong baseNextSequence, ulong targetNextSequence, ulong baseSnapshotBytes, ulong targetSnapshotBytes,
        byte[] baseSha256, byte[] targetSha256, ulong targetObservedLocalInputEpoch,
        ulong targetInitialTranscriptBeforeLineId, byte[] delta, byte[] nonce = null)
    {
        ValidateExpected(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch, stateContract,
            contextId, profile, baseNextSequence, targetNextSequence, baseSnapshotBytes, targetSnapshotBytes,
            baseSha256, targetSha256);
        ValidateTargetMetadata(targetObservedLocalInputEpoch, targetInitialTranscriptBeforeLineId, 0);
        if (delta is null || delta.Length is < 256 or > MaximumPlaintextBytes)
            throw new InvalidDataException("Delta plaintext size is invalid.");
        ValidateNative(delta, contextId, profile, baseNextSequence, targetNextSequence,
            baseSnapshotBytes, targetSnapshotBytes, baseSha256, targetSha256);
        if (nonce is not null && nonce.Length != 12) throw new ArgumentException("Delta nonce must be 12 bytes.", nameof(nonce));
        var nonceBytes = nonce is null ? RandomNumberGenerator.GetBytes(12) : (byte[])nonce.Clone();
        var header = WriteHeader(sessionId, pcDeviceId, recipientDeviceId, keyEpoch, stateContract, contextId,
            profile, baseNextSequence, targetNextSequence, baseSnapshotBytes, targetSnapshotBytes,
            baseSha256, targetSha256, targetObservedLocalInputEpoch, targetInitialTranscriptBeforeLineId,
            delta.Length, nonceBytes);
        var aad = AssociatedData(header, stateContract);
        var key = DeriveKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, stateContract);
        var result = new byte[HeaderBytes + delta.Length + 16];
        try
        {
            header.CopyTo(result, 0);
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonceBytes, delta, result.AsSpan(HeaderBytes, delta.Length),
                    result.AsSpan(HeaderBytes + delta.Length, 16), aad);
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(nonceBytes);
            CryptographicOperations.ZeroMemory(header);
        }
    }

    /// <summary>The caller owns and must clear the returned TLDELTA1 plaintext after native apply.</summary>
    public static OpenedDelta Open(byte[] pairwiseKey, byte[] sealedObject, Guid expectedSessionId,
        Guid expectedPcDeviceId, Guid expectedRecipientDeviceId, long expectedKeyEpoch,
        string expectedStateContract, byte[] expectedContextId, uint expectedProfile,
        ulong expectedBaseNextSequence, ulong expectedTargetNextSequence,
        ulong expectedBaseSnapshotBytes, ulong expectedTargetSnapshotBytes,
        byte[] expectedBaseSha256, byte[] expectedTargetSha256, ulong minimumObservedLocalInputEpoch)
    {
        ValidateExpected(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId,
            expectedKeyEpoch, expectedStateContract, expectedContextId, expectedProfile,
            expectedBaseNextSequence, expectedTargetNextSequence, expectedBaseSnapshotBytes,
            expectedTargetSnapshotBytes, expectedBaseSha256, expectedTargetSha256);
        if (minimumObservedLocalInputEpoch > long.MaxValue)
            throw new InvalidDataException("Delta minimum local input epoch is invalid.");
        if (sealedObject is null || sealedObject.Length is < HeaderBytes + 256 + 16 or > MaximumObjectBytes)
            throw new InvalidDataException("Sealed Delta object size is invalid.");
        var header = sealedObject.AsSpan(0, HeaderBytes);
        ValidateHeader(header, sealedObject.Length, expectedSessionId, expectedPcDeviceId,
            expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract, expectedContextId,
            expectedProfile, expectedBaseNextSequence, expectedTargetNextSequence,
            expectedBaseSnapshotBytes, expectedTargetSnapshotBytes, expectedBaseSha256, expectedTargetSha256);
        var targetEpoch = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(306, 8));
        var targetAnchor = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(314, 8));
        ValidateTargetMetadata(targetEpoch, targetAnchor, minimumObservedLocalInputEpoch);
        var key = DeriveKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract);
        var aad = AssociatedData(header, expectedStateContract);
        var plaintext = new byte[sealedObject.Length - HeaderBytes - 16];
        try
        {
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(header.Slice(294, 12),
                    sealedObject.AsSpan(HeaderBytes, plaintext.Length),
                    sealedObject.AsSpan(HeaderBytes + plaintext.Length, 16), plaintext, aad);
            ValidateNative(plaintext, expectedContextId, expectedProfile,
                expectedBaseNextSequence, expectedTargetNextSequence, expectedBaseSnapshotBytes,
                expectedTargetSnapshotBytes, expectedBaseSha256, expectedTargetSha256);
            return new OpenedDelta(plaintext, targetEpoch, targetAnchor);
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    private static byte[] WriteHeader(Guid session, Guid pc, Guid recipient, long epoch, string state,
        byte[] context, uint profile, ulong baseSeq, ulong targetSeq, ulong baseBytes,
        ulong targetBytes, byte[] baseHash, byte[] targetHash, ulong targetEpoch, ulong targetAnchor,
        int plaintextBytes, byte[] nonce)
    {
        var result = new byte[HeaderBytes];
        "TLDSEAL1"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), HeaderBytes);
        UuidBytes(session).CopyTo(result, 12);
        UuidBytes(pc).CopyTo(result, 28);
        UuidBytes(recipient).CopyTo(result, 44);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(60), epoch);
        WriteAscii(KeyId(recipient, epoch, state), result, 68, 64);
        WriteAscii(state, result, 133, 24);
        context.CopyTo(result, 158);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(190), profile);
        foreach (var (offset, number) in new[] { (194, baseSeq), (202, targetSeq), (210, baseBytes), (218, targetBytes) })
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(offset), number);
        baseHash.CopyTo(result, 226);
        targetHash.CopyTo(result, 258);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(290), (uint)plaintextBytes);
        nonce.CopyTo(result, 294);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(306), targetEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(314), targetAnchor);
        return result;
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header, int objectBytes, Guid session, Guid pc,
        Guid recipient, long epoch, string state, byte[] context, uint profile, ulong baseSeq,
        ulong targetSeq, ulong baseBytes, ulong targetBytes, byte[] baseHash, byte[] targetHash)
    {
        if (!header[..8].SequenceEqual("TLDSEAL1"u8) ||
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(8, 2)) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(10, 2)) != HeaderBytes ||
            !header.Slice(12, 16).SequenceEqual(UuidBytes(session)) ||
            !header.Slice(28, 16).SequenceEqual(UuidBytes(pc)) ||
            !header.Slice(44, 16).SequenceEqual(UuidBytes(recipient)) ||
            BinaryPrimitives.ReadInt64BigEndian(header.Slice(60, 8)) != epoch ||
            !MatchesAscii(header, 68, 64, KeyId(recipient, epoch, state)) ||
            !MatchesAscii(header, 133, 24, state) ||
            !header.Slice(158, 32).SequenceEqual(context) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(190, 4)) != profile ||
            BinaryPrimitives.ReadUInt64BigEndian(header.Slice(194, 8)) != baseSeq ||
            BinaryPrimitives.ReadUInt64BigEndian(header.Slice(202, 8)) != targetSeq ||
            BinaryPrimitives.ReadUInt64BigEndian(header.Slice(210, 8)) != baseBytes ||
            BinaryPrimitives.ReadUInt64BigEndian(header.Slice(218, 8)) != targetBytes ||
            !header.Slice(226, 32).SequenceEqual(baseHash) ||
            !header.Slice(258, 32).SequenceEqual(targetHash) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(290, 4)) != objectBytes - HeaderBytes - 16 ||
            header.Slice(322, 14).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Delta object metadata does not match the requested transition.");
    }

    private static void ValidateNative(ReadOnlySpan<byte> delta, byte[] context, uint profile,
        ulong baseSeq, ulong targetSeq, ulong baseBytes, ulong targetBytes, byte[] baseHash, byte[] targetHash)
    {
        if (delta.Length is < 256 or > MaximumPlaintextBytes ||
            !delta[..8].SequenceEqual("TLDELTA1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(delta.Slice(8, 2)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(delta.Slice(10, 2)) != 256 ||
            BinaryPrimitives.ReadUInt32LittleEndian(delta.Slice(12, 4)) != 4096 ||
            BinaryPrimitives.ReadUInt32LittleEndian(delta.Slice(16, 4)) != profile ||
            BinaryPrimitives.ReadUInt32LittleEndian(delta.Slice(20, 4)) != 1 ||
            !delta.Slice(24, 32).SequenceEqual(context) ||
            BinaryPrimitives.ReadUInt64LittleEndian(delta.Slice(56, 8)) != baseSeq ||
            BinaryPrimitives.ReadUInt64LittleEndian(delta.Slice(64, 8)) != targetSeq ||
            BinaryPrimitives.ReadUInt64LittleEndian(delta.Slice(72, 8)) != baseBytes ||
            BinaryPrimitives.ReadUInt64LittleEndian(delta.Slice(80, 8)) != targetBytes ||
            !delta.Slice(88, 32).SequenceEqual(baseHash) ||
            !delta.Slice(120, 32).SequenceEqual(targetHash) ||
            BinaryPrimitives.ReadUInt64LittleEndian(delta.Slice(192, 8)) != (ulong)delta.Length ||
            targetBytes <= (ulong)delta.Length)
            throw new InvalidDataException("Native Delta header does not match the authenticated transition.");
    }

    private static void ValidateExpected(byte[] key, Guid session, Guid pc, Guid recipient, long epoch,
        string state, byte[] context, uint profile, ulong baseSeq, ulong targetSeq, ulong baseBytes,
        ulong targetBytes, byte[] baseHash, byte[] targetHash)
    {
        if (key is null || key.Length != 32 || session == Guid.Empty || pc == Guid.Empty ||
            recipient == Guid.Empty || pc == recipient || epoch < 1 || NormalizeStateContract(state) != state ||
            context is null || context.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(context, DeriveContextId(session, pc, state)) ||
            profile is < 1 or > 4 ||
            (state == StateContractV1 ? profile is not (1u or 3u) : profile is not (2u or 4u)) ||
            baseSeq == 0 || targetSeq <= baseSeq ||
            baseBytes is < 104 or > 128UL * 1024 * 1024 ||
            targetBytes is < 104 or > 128UL * 1024 * 1024 ||
            baseHash is null || baseHash.Length != 32 || targetHash is null || targetHash.Length != 32)
            throw new InvalidDataException("Delta transition identity is invalid.");
    }

    private static void ValidateTargetMetadata(ulong targetEpoch, ulong targetAnchor, ulong minimumEpoch)
    {
        if (targetEpoch > long.MaxValue || targetEpoch < minimumEpoch || targetAnchor != 1)
            throw new InvalidDataException("Delta target local input epoch or history anchor is invalid.");
    }

    private static byte[] DeriveKey(byte[] pairwiseKey, Guid session, Guid recipient, long epoch,
        string stateContract)
    {
        var keyId = Encoding.ASCII.GetBytes(KeyId(recipient, epoch, stateContract));
        using var info = new MemoryStream();
        info.Write(KeyPrefix(NormalizeStateContract(stateContract)));
        info.Write(UuidBytes(recipient));
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, epoch);
        info.Write(number);
        info.WriteByte(checked((byte)keyId.Length));
        info.Write(keyId);
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(pairwiseKey, UuidBytes(session), info.ToArray()));
        var result = new byte[32];
        generator.GenerateBytes(result, 0, result.Length);
        CryptographicOperations.ZeroMemory(keyId);
        return result;
    }

    internal static byte[] AssociatedData(ReadOnlySpan<byte> header) =>
        AssociatedData(header, StateContractV1);

    internal static byte[] AssociatedData(ReadOnlySpan<byte> header, string stateContract)
    {
        if (header.Length != HeaderBytes) throw new InvalidDataException("Delta header size is invalid.");
        var prefix = AadPrefix(NormalizeStateContract(stateContract));
        var result = new byte[prefix.Length + HeaderBytes];
        prefix.CopyTo(result, 0);
        header.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    private static string NormalizeStateContract(string value) => value switch
    {
        StateContractV1 => StateContractV1,
        StateContractV2 => StateContractV2,
        _ => throw new InvalidDataException("Delta state contract is unsupported.")
    };
    private static byte[] KeyPrefix(string value) => value == StateContractV1 ? KeyPrefixV1 : KeyPrefixV2;
    private static byte[] AadPrefix(string value) => value == StateContractV1 ? AadPrefixV1 : AadPrefixV2;

    private static void WriteAscii(string value, byte[] output, int lengthAt, int width)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        if (encoded.Length > width) throw new InvalidDataException("Delta header string is too long.");
        output[lengthAt] = checked((byte)encoded.Length);
        encoded.CopyTo(output, lengthAt + 1);
    }
    private static bool MatchesAscii(ReadOnlySpan<byte> source, int lengthAt, int width, string expected)
    {
        var encoded = Encoding.ASCII.GetBytes(expected);
        return encoded.Length <= width && source[lengthAt] == encoded.Length &&
            source.Slice(lengthAt + 1, encoded.Length).SequenceEqual(encoded) &&
            source.Slice(lengthAt + 1 + encoded.Length, width - encoded.Length).IndexOfAnyExcept((byte)0) < 0;
    }
    private static byte[] UuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
}
