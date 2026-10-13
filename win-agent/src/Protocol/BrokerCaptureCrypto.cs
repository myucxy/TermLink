using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace TermLink.Protocol;

/// <summary>Encrypts one path-free TLCAPV1 capture. The caller owns and clears decrypted bytes.</summary>
public static class BrokerCaptureCrypto
{
    public const int HeaderBytes = 192;
    public const int MaximumCanonicalBytes = 32 * 1024 * 1024;
    public const int MaximumObjectBytes = MaximumCanonicalBytes + HeaderBytes + 16;
    private const int MinimumCanonicalBytes = 136;
    private static readonly byte[] Magic = "TLBCSEA1"u8.ToArray();
    private static readonly byte[] NativeMagic = "TLCAPV1\0"u8.ToArray();
    public const string StateContractV1 = "terminal_state_2_22_v1";
    public const string StateContractV2 = "terminal_state_2_22_v2";
    private static readonly byte[] StateContractV1Bytes = Encoding.ASCII.GetBytes(StateContractV1);
    private static readonly byte[] StateContractV2Bytes = Encoding.ASCII.GetBytes(StateContractV2);
    private static readonly byte[] AadPrefixV1 = "TermLink broker capture AAD V1\n"u8.ToArray();
    private static readonly byte[] AadPrefixV2 = "TermLink broker capture AAD V2\n"u8.ToArray();
    private static readonly byte[] KeyPrefixV1 = "TermLink broker capture key V1\n"u8.ToArray();
    private static readonly byte[] KeyPrefixV2 = "TermLink broker capture key V2\n"u8.ToArray();

    public static string KeyId(Guid recipientDeviceId, long keyEpoch, string stateContract = StateContractV1)
    {
        if (recipientDeviceId == Guid.Empty || keyEpoch <= 0)
            throw new InvalidDataException("Broker capture key identity is invalid.");
        var variant = StateContract(stateContract) == StateContractV2 ? "v2" : "v1";
        return "broker_capture_" + variant + ":" + recipientDeviceId.ToString("N") + ":" +
            keyEpoch.ToString(CultureInfo.InvariantCulture);
    }

    public static byte[] Seal(byte[] pairwiseKey, Guid sessionId, Guid pcDeviceId,
        Guid recipientDeviceId, Guid objectId, ulong sourceSequence, long keyEpoch,
        byte[] canonical, byte[] nonce = null, string stateContract = StateContractV1)
    {
        ValidateIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId,
            objectId, sourceSequence, keyEpoch);
        if (canonical is null || canonical.Length is < MinimumCanonicalBytes or > MaximumCanonicalBytes)
            throw new InvalidDataException("Broker capture canonical length is invalid.");
        ValidateCanonical(canonical, sourceSequence);
        if (nonce is not null && nonce.Length != 12)
            throw new ArgumentException("Broker capture nonce must be 12 bytes.", nameof(nonce));
        var nonceBytes = nonce is null ? RandomNumberGenerator.GetBytes(12) : (byte[])nonce.Clone();
        var header = new byte[HeaderBytes];
        var digest = SHA256.HashData(canonical);
        byte[] key = null;
        byte[] aad = null;
        try
        {
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), HeaderBytes);
            UuidBytes(sessionId).CopyTo(header, 12);
            UuidBytes(pcDeviceId).CopyTo(header, 28);
            UuidBytes(recipientDeviceId).CopyTo(header, 44);
            UuidBytes(objectId).CopyTo(header, 60);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(76, 8), sourceSequence);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(84, 8), keyEpoch);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(92, 4), (uint)canonical.Length);
            digest.CopyTo(header, 96);
            nonceBytes.CopyTo(header, 128);
            var state = StateContractBytesFor(stateContract);
            state.CopyTo(header, 140);
            key = DeriveKey(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch, stateContract);
            aad = AssociatedData(header, stateContract);
            var result = new byte[HeaderBytes + canonical.Length + 16];
            try
            {
                header.CopyTo(result, 0);
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(nonceBytes, canonical, result.AsSpan(HeaderBytes, canonical.Length),
                    result.AsSpan(HeaderBytes + canonical.Length, 16), aad);
                return result;
            }
            catch { CryptographicOperations.ZeroMemory(result); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(nonceBytes);
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (aad != null) CryptographicOperations.ZeroMemory(aad);
        }
    }

    public static byte[] Open(byte[] pairwiseKey, byte[] sealedObject, Guid expectedSessionId,
        Guid expectedPcDeviceId, Guid expectedRecipientDeviceId, Guid expectedObjectId,
        ulong expectedSourceSequence, long expectedKeyEpoch, uint expectedCanonicalLength,
        byte[] expectedCanonicalSha256, string expectedStateContract = StateContractV1)
    {
        ValidateIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId,
            expectedRecipientDeviceId, expectedObjectId, expectedSourceSequence, expectedKeyEpoch);
        if (expectedCanonicalLength is < MinimumCanonicalBytes or > MaximumCanonicalBytes ||
            expectedCanonicalSha256 is null || expectedCanonicalSha256.Length != 32 ||
            sealedObject is null || sealedObject.Length < HeaderBytes + MinimumCanonicalBytes + 16 ||
            sealedObject.Length > MaximumObjectBytes ||
            sealedObject.Length != (long)HeaderBytes + expectedCanonicalLength + 16)
            throw new InvalidDataException("Broker capture expected object size or digest is invalid.");
        var header = sealedObject.AsSpan(0, HeaderBytes);
        StateContract(expectedStateContract);
        if (!header[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(8, 2)) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(10, 2)) != HeaderBytes ||
            !header.Slice(12, 16).SequenceEqual(UuidBytes(expectedSessionId)) ||
            !header.Slice(28, 16).SequenceEqual(UuidBytes(expectedPcDeviceId)) ||
            !header.Slice(44, 16).SequenceEqual(UuidBytes(expectedRecipientDeviceId)) ||
            !header.Slice(60, 16).SequenceEqual(UuidBytes(expectedObjectId)) ||
            BinaryPrimitives.ReadUInt64BigEndian(header.Slice(76, 8)) != expectedSourceSequence ||
            BinaryPrimitives.ReadInt64BigEndian(header.Slice(84, 8)) != expectedKeyEpoch ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(92, 4)) != expectedCanonicalLength ||
            !CryptographicOperations.FixedTimeEquals(header.Slice(96, 32), expectedCanonicalSha256) ||
            !header.Slice(140, StateContractBytesFor(expectedStateContract).Length).SequenceEqual(StateContractBytesFor(expectedStateContract)) ||
            header.Slice(140 + StateContractBytesFor(expectedStateContract).Length).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Broker capture metadata does not match expected identity.");
        var key = DeriveKey(pairwiseKey, expectedSessionId, expectedPcDeviceId,
            expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract);
        var aad = AssociatedData(header, expectedStateContract);
        var canonical = new byte[expectedCanonicalLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(header.Slice(128, 12), sealedObject.AsSpan(HeaderBytes, (int)expectedCanonicalLength),
                sealedObject.AsSpan(HeaderBytes + (int)expectedCanonicalLength, 16), canonical, aad);
            var digest = SHA256.HashData(canonical);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(digest, expectedCanonicalSha256))
                    throw new InvalidDataException("Broker capture canonical digest differs from its container.");
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
            ValidateCanonical(canonical, expectedSourceSequence);
            return canonical;
        }
        catch { CryptographicOperations.ZeroMemory(canonical); throw; }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(aad); }
    }

    internal static byte[] AssociatedData(ReadOnlySpan<byte> header) =>
        AssociatedData(header, StateContractV1);

    internal static byte[] AssociatedData(ReadOnlySpan<byte> header, string stateContract)
    {
        if (header.Length != HeaderBytes) throw new InvalidDataException("Broker capture header size is invalid.");
        var prefix = AadPrefix(stateContract);
        var aad = new byte[prefix.Length + HeaderBytes];
        prefix.CopyTo(aad, 0);
        header.CopyTo(aad.AsSpan(prefix.Length));
        return aad;
    }

    private static void ValidateIdentity(byte[] key, Guid session, Guid pc, Guid recipient,
        Guid objectId, ulong sequence, long epoch)
    {
        if (key is null || key.Length != 32 || session == Guid.Empty || pc == Guid.Empty ||
            recipient == Guid.Empty || recipient == pc || objectId == Guid.Empty ||
            sequence == 0 || sequence >= long.MaxValue || epoch <= 0)
            throw new InvalidDataException("Broker capture identity is invalid.");
    }

    private static void ValidateCanonical(ReadOnlySpan<byte> canonical, ulong sequence)
    {
        if (canonical.Length is < MinimumCanonicalBytes or > MaximumCanonicalBytes ||
            !canonical[..8].SequenceEqual(NativeMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(canonical.Slice(8, 4)) != 32 ||
            BinaryPrimitives.ReadUInt32LittleEndian(canonical.Slice(12, 4)) is < 1 or > 64 ||
            canonical.Length < 32 + 104 * BinaryPrimitives.ReadUInt32LittleEndian(canonical.Slice(12, 4)) ||
            BinaryPrimitives.ReadUInt64LittleEndian(canonical.Slice(16, 8)) != sequence ||
            BinaryPrimitives.ReadUInt32LittleEndian(canonical.Slice(24, 4)) != canonical.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(canonical.Slice(28, 4)) != 0)
            throw new InvalidDataException("Broker capture TLCAPV1 header is invalid.");
    }

    private static byte[] DeriveKey(byte[] pairwiseKey, Guid session, Guid pc, Guid recipient, long epoch,
        string stateContract)
    {
        var prefix = KeyPrefix(stateContract);
        var info = new byte[prefix.Length + 16 + 16 + 8];
        try
        {
            prefix.CopyTo(info, 0);
            UuidBytes(pc).CopyTo(info, prefix.Length);
            UuidBytes(recipient).CopyTo(info, prefix.Length + 16);
            BinaryPrimitives.WriteInt64BigEndian(info.AsSpan(prefix.Length + 32), epoch);
            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(pairwiseKey, UuidBytes(session), info));
            var result = new byte[32];
            hkdf.GenerateBytes(result, 0, result.Length);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(info); }
    }

    private static string StateContract(string stateContract) => stateContract switch
    {
        StateContractV1 => StateContractV1,
        StateContractV2 => StateContractV2,
        _ => throw new InvalidDataException("Broker capture state contract is unsupported.")
    };
    private static byte[] StateContractBytesFor(string stateContract) =>
        StateContract(stateContract) == StateContractV1 ? StateContractV1Bytes : StateContractV2Bytes;
    private static byte[] AadPrefix(string stateContract) => stateContract switch
    {
        StateContractV1 => AadPrefixV1,
        StateContractV2 => AadPrefixV2,
        _ => throw new InvalidDataException("Broker capture state contract is unsupported.")
    };
    private static byte[] KeyPrefix(string stateContract) => stateContract switch
    {
        StateContractV1 => KeyPrefixV1,
        StateContractV2 => KeyPrefixV2,
        _ => throw new InvalidDataException("Broker capture state contract is unsupported.")
    };

    private static byte[] UuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
}
