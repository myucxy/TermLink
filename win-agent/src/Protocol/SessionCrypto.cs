using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Google.Protobuf;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Protocol;

public enum CryptoDirection : byte
{
    PcToAndroid = 1,
    AndroidToPc = 2
}

public sealed class DeviceExchangeKeyPair : IDisposable
{
    private byte[] privateKey;
    public byte[] PublicKey { get; }

    public DeviceExchangeKeyPair(byte[] privateKey)
    {
        if (privateKey is null || privateKey.Length != 32) throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        this.privateKey = (byte[])privateKey.Clone();
        PublicKey = new X25519PrivateKeyParameters(this.privateKey, 0).GeneratePublicKey().GetEncoded();
    }

    public static DeviceExchangeKeyPair Generate() => new(new X25519PrivateKeyParameters(new SecureRandom()).GetEncoded());
    internal byte[] ExportPrivate() => privateKey is null ? throw new ObjectDisposedException(nameof(DeviceExchangeKeyPair)) : (byte[])privateKey.Clone();
    public void Dispose() { if (privateKey is not null) { CryptographicOperations.ZeroMemory(privateKey); privateKey = null; } }
}

public static class SessionCrypto
{
    private static readonly byte[] Salt = SHA256.HashData("TERMLINK-E2EE-V1"u8);

    public static byte[] DerivePairwiseKey(DeviceExchangeKeyPair localKey, byte[] peerPublicKey, Guid pcDeviceId, Guid androidDeviceId, long keyEpoch)
    {
        ArgumentNullException.ThrowIfNull(localKey);
        if (peerPublicKey is null || peerPublicKey.Length != 32) throw new ArgumentException("X25519 public key must be 32 bytes.", nameof(peerPublicKey));
        byte[] privateBytes = localKey.ExportPrivate();
        byte[] shared = new byte[32];
        try
        {
            var agreement = new X25519Agreement();
            agreement.Init(new X25519PrivateKeyParameters(privateBytes, 0));
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey, 0), shared, 0);
            byte[] info = Concat("TERMLINK-PAIRWISE-V1"u8.ToArray(), new byte[] { 0 }, UuidBytes(pcDeviceId), UuidBytes(androidDeviceId), LongBytes(keyEpoch));
            return Hkdf(shared, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public static byte[] DeriveSessionKey(byte[] pairwiseKey, Guid sessionId, CryptoDirection direction, byte[] keyId)
    {
        if (pairwiseKey is null || pairwiseKey.Length != 32) throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
        if (keyId is null || keyId.Length is < 8 or > 128) throw new ArgumentException("Key ID length is invalid.", nameof(keyId));
        return Hkdf(pairwiseKey, Concat("TERMLINK-SESSION-KEY-V1"u8.ToArray(), new byte[] { 0 }, UuidBytes(sessionId), new[] { (byte)direction }, keyId));
    }

    public static byte[] DeriveHistoryKey(byte[] pairwiseKey, Guid sessionId, Guid recipientDeviceId, long keyEpoch, byte[] keyId)
    {
        if (pairwiseKey is null || pairwiseKey.Length != 32) throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
        if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
        if (keyId is null || keyId.Length is < 8 or > 128) throw new ArgumentException("Key ID length is invalid.", nameof(keyId));
        return Hkdf(pairwiseKey, Concat(
            "TERMLINK-HISTORY-KEY-V1"u8.ToArray(),
            new byte[] { 0 },
            UuidBytes(sessionId),
            UuidBytes(recipientDeviceId),
            LongBytes(keyEpoch),
            keyId));
    }

    public static byte[] DeriveFileTransferKey(
        byte[] pairwiseKey,
        Guid transferId,
        CryptoDirection direction,
        long keyEpoch,
        byte[] keyId)
    {
        if (pairwiseKey is null || pairwiseKey.Length != 32)
            throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
        if (transferId == Guid.Empty)
            throw new ArgumentException("Transfer ID is required.", nameof(transferId));
        if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
        if (keyId is null || keyId.Length is < 8 or > 128)
            throw new ArgumentException("Key ID length is invalid.", nameof(keyId));
        return Hkdf(pairwiseKey, Concat(
            "TERMLINK-FILE-TRANSFER-KEY-V2"u8.ToArray(),
            new byte[] { 0 },
            UuidBytes(transferId),
            new[] { (byte)direction },
            LongBytes(keyEpoch),
            keyId));
    }

    public static byte[] Nonce(byte[] prefix, ulong sequence)
    {
        if (prefix is null || prefix.Length != 4 || sequence == 0) throw new ArgumentException("Nonce requires a four-byte prefix and non-zero sequence.");
        byte[] result = new byte[12];
        prefix.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(4), sequence);
        return result;
    }

    public static byte[] AssociatedData(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Envelope metadata = envelope.Clone();
        metadata.Ciphertext = ByteString.Empty;
        return metadata.ToByteArray();
    }

    public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] associatedData, byte[] plaintext) => Process(true, key, nonce, associatedData, plaintext);
    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] associatedData, byte[] ciphertext) => Process(false, key, nonce, associatedData, ciphertext);

    private static byte[] Process(bool encrypt, byte[] key, byte[] nonce, byte[] associatedData, byte[] input)
    {
        if (key is null || key.Length != 32) throw new ArgumentException("Session key must be 32 bytes.", nameof(key));
        if (nonce is null || nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes.", nameof(nonce));
        ArgumentNullException.ThrowIfNull(associatedData);
        ArgumentNullException.ThrowIfNull(input);
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, nonce, associatedData));
        byte[] output = new byte[cipher.GetOutputSize(input.Length)];
        int count = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        try
        {
            count += cipher.DoFinal(output, count);
        }
        catch (InvalidCipherTextException error)
        {
            CryptographicOperations.ZeroMemory(output);
            throw new CryptographicException("E2EE authentication failed.", error);
        }
        return output.AsSpan(0, count).ToArray();
    }

    private static byte[] Hkdf(byte[] input, byte[] info)
    {
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(input, Salt, info));
        byte[] result = new byte[32];
        generator.GenerateBytes(result, 0, result.Length);
        return result;
    }

    private static byte[] UuidBytes(Guid value)
    {
        string compact = value.ToString("N");
        return Convert.FromHexString(compact);
    }

    private static byte[] LongBytes(long value) { byte[] result = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(result, value); return result; }
    private static byte[] Concat(params byte[][] values) { using var stream = new MemoryStream(); foreach (byte[] value in values) stream.Write(value); return stream.ToArray(); }
}
