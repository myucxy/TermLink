using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Protocol;

var session = Guid.Parse("fedcba98-7654-3210-ffff-eeeeeeeeeeee");
var pc = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
var recipient = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
var objectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
var pairwise = Convert.FromHexString("B1632A5A77CAFEC55F885FC4C3588F303ED3CA9856D5C464636E0B5FEAEFA773");
var nonce = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAAB");
const ulong sequence = 9;
const long epoch = 7;
var canonical = new byte[136];
"TLCAPV1\0"u8.CopyTo(canonical);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(8), 32);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(12), 1);
BinaryPrimitives.WriteUInt64LittleEndian(canonical.AsSpan(16), sequence);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(24), (uint)canonical.Length);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(32), 1);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(36), 96);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(40), 1);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(44), 'f');
BinaryPrimitives.WriteUInt64LittleEndian(canonical.AsSpan(48), sequence);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(56), 1);
BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(128), 1); // Denied, zero capture bytes.
var digest = SHA256.HashData(canonical);
byte[] Seal(byte[] body, byte[]? fixedNonce = null) => BrokerCaptureCrypto.Seal(pairwise, session, pc,
    recipient, objectId, sequence, epoch, body, fixedNonce ?? nonce);
byte[] Open(byte[] value, Guid? expectedSession = null, Guid? expectedObject = null,
    ulong expectedSequence = sequence, uint expectedLength = 136, byte[]? expectedDigest = null) =>
    BrokerCaptureCrypto.Open(pairwise, value, expectedSession ?? session, pc, recipient,
        expectedObject ?? objectId, expectedSequence, epoch, expectedLength, expectedDigest ?? digest);
var sealedObject = Seal(canonical);
var opened = Open(sealedObject);
Require(opened.AsSpan().SequenceEqual(canonical), "Broker capture round trip failed.");
CryptographicOperations.ZeroMemory(opened);
Require(BrokerCaptureCrypto.KeyId(recipient, epoch) ==
    "broker_capture_v1:102132435465768798a9bacbdcedfe0f:7", "Key ID changed.");
Require(sealedObject.Length == canonical.Length + 208, "Sealed object length changed.");
ExpectFailure(() => Open(sealedObject, expectedSession: Guid.NewGuid()));
ExpectFailure(() => Open(sealedObject, expectedObject: Guid.NewGuid()));
ExpectFailure(() => Open(sealedObject, expectedSequence: 10));
ExpectFailure(() => Open(sealedObject, expectedLength: 137));
ExpectFailure(() => Open(sealedObject, expectedDigest: new byte[32]));
foreach (var index in new[] { 0, 8, 10, 12, 28, 44, 60, 76, 84, 92, 96, 128, 140, 162, 172,
    BrokerCaptureCrypto.HeaderBytes, sealedObject.Length - 1 })
{
    var tampered = (byte[])sealedObject.Clone();
    tampered[index] ^= 1;
    ExpectFailure(() => Open(tampered));
}
ExpectFailure(() => Open(new byte[BrokerCaptureCrypto.MaximumObjectBytes + 1]));
ExpectFailure(() => Seal(new byte[BrokerCaptureCrypto.MaximumCanonicalBytes + 1]));
var wrongCanonical = (byte[])canonical.Clone();
wrongCanonical[16] ^= 1;
ExpectFailure(() => Seal(wrongCanonical));
var vector = new
{
    format = "termlink.broker-capture-sealed-v1",
    note = "AEAD wire fixture; full TLCAPV1 record decoding belongs to the native codec.",
    session_id = session.ToString(), pc_device_id = pc.ToString(),
    recipient_device_id = recipient.ToString(), object_id = objectId.ToString(),
    source_sequence = sequence, key_epoch = epoch,
    key_id = BrokerCaptureCrypto.KeyId(recipient, epoch),
    canonical_length = canonical.Length, canonical_sha256 = Hex(digest),
    pairwise_key = Hex(pairwise), nonce = Hex(nonce), canonical = Hex(canonical),
    associated_data = Hex(Encoding.UTF8.GetBytes("TermLink broker capture AAD V1\n")
        .Concat(sealedObject.Take(BrokerCaptureCrypto.HeaderBytes)).ToArray()),
    sealed_object = Hex(sealedObject)
};
var json = JsonSerializer.Serialize(vector, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 2 && args[0] == "--write-vector") File.WriteAllText(args[1], json + Environment.NewLine);
else
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName,
        "protocol/test-vectors/broker-capture-sealed-v1.json"))) dir = dir.Parent;
    Require(dir is not null && File.ReadAllText(Path.Combine(dir.FullName,
        "protocol/test-vectors/broker-capture-sealed-v1.json")).Trim() == json,
        "Broker capture shared vector changed.");
    Console.WriteLine("BrokerCaptureCrypto C# validation passed.");
}

static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
static void ExpectFailure(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or CryptographicException or ArgumentException)
    { return; }
    throw new Exception("Invalid broker capture was accepted.");
}
