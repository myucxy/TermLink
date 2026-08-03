using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace TermLink.Protocol;

public sealed class DeviceIdentity : IDisposable
{
    private byte[] signingPrivateKey;
    public byte[] SigningPublicKey { get; }
    public DeviceExchangeKeyPair Exchange { get; }

    public DeviceIdentity(byte[] signingPrivateKey, byte[] exchangePrivateKey)
    {
        if (signingPrivateKey is null || signingPrivateKey.Length != 32) throw new ArgumentException("Ed25519 private key must be 32 bytes.", nameof(signingPrivateKey));
        this.signingPrivateKey = (byte[])signingPrivateKey.Clone();
        SigningPublicKey = new Ed25519PrivateKeyParameters(this.signingPrivateKey, 0).GeneratePublicKey().GetEncoded();
        Exchange = new DeviceExchangeKeyPair(exchangePrivateKey);
    }

    public static DeviceIdentity Generate()
    {
        var random = new SecureRandom();
        return new DeviceIdentity(new Ed25519PrivateKeyParameters(random).GetEncoded(), new X25519PrivateKeyParameters(random).GetEncoded());
    }

    public byte[] Sign(byte[] message)
    {
        if (signingPrivateKey is null) throw new ObjectDisposedException(nameof(DeviceIdentity));
        ArgumentNullException.ThrowIfNull(message);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(signingPrivateKey, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public byte[] ExportPrivateMaterial()
    {
        if (signingPrivateKey is null) throw new ObjectDisposedException(nameof(DeviceIdentity));
        byte[] exchange = Exchange.ExportPrivate();
        byte[] result = new byte[64];
        signingPrivateKey.CopyTo(result, 0);
        exchange.CopyTo(result, 32);
        CryptographicOperations.ZeroMemory(exchange);
        return result;
    }

    public void Dispose()
    {
        if (signingPrivateKey is not null) { CryptographicOperations.ZeroMemory(signingPrivateKey); signingPrivateKey = null; }
        Exchange.Dispose();
    }
}
