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
var android = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
var pairwise = Convert.FromHexString("B1632A5A77CAFEC55F885FC4C3588F303ED3CA9856D5C464636E0B5FEAEFA773");
const string state = "terminal_state_2_22_v1";
var context = DeltaCrypto.DeriveContextId(session, pc, state);
var baseHash = SHA256.HashData("base canonical snapshot"u8);
var targetHash = SHA256.HashData("target canonical snapshot"u8);
var nonce = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAAB");
var native = new byte[256];
"TLDELTA1"u8.CopyTo(native);
BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(8), 1);
BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(10), 256);
BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(12), 4096);
BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(16), 1);
BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(20), 1);
context.CopyTo(native, 24);
BinaryPrimitives.WriteUInt64LittleEndian(native.AsSpan(56), 5201);
BinaryPrimitives.WriteUInt64LittleEndian(native.AsSpan(64), 5203);
BinaryPrimitives.WriteUInt64LittleEndian(native.AsSpan(72), 8192);
BinaryPrimitives.WriteUInt64LittleEndian(native.AsSpan(80), 9000);
baseHash.CopyTo(native, 88);
targetHash.CopyTo(native, 120);
BinaryPrimitives.WriteUInt64LittleEndian(native.AsSpan(192), (ulong)native.Length);
byte[] Seal(byte[] delta, byte[] contextOverride = null) => DeltaCrypto.Seal(pairwise, session, pc, android, 7,
    state, contextOverride ?? context, 1,
    5201, 5203, 8192, 9000, baseHash, targetHash, 17, 1, delta, nonce);
byte[] Open(byte[] sealedObject, Guid? expectedSession = null, byte[] expectedContext = null,
    ulong expectedTargetSequence = 5203, byte[] expectedBaseHash = null,
    string expectedState = null, ulong minimumEpoch = 10) => DeltaCrypto.Open(pairwise,
    sealedObject, expectedSession ?? session, pc, android, 7, expectedState ?? state, expectedContext ?? context, 1,
    5201, expectedTargetSequence, 8192, 9000, expectedBaseHash ?? baseHash, targetHash,
    minimumEpoch).Delta;
var sealedObject = Seal(native);
var opened = Open(sealedObject);
Require(opened.AsSpan().SequenceEqual(native), "Delta plaintext did not round trip.");
CryptographicOperations.ZeroMemory(opened);
Require(sealedObject.Length == DeltaCrypto.HeaderBytes + native.Length + 16, "Delta object length is invalid.");
Require(DeltaCrypto.KeyId(android, 7) == "delta-v1:102132435465768798a9bacbdcedfe0f:7", "Delta key ID changed.");
var openedMetadata = DeltaCrypto.Open(pairwise, sealedObject, session, pc, android, 7, state, context,
    1, 5201, 5203, 8192, 9000, baseHash, targetHash, 10);
Require(openedMetadata.TargetObservedLocalInputEpoch == 17 &&
    openedMetadata.TargetInitialTranscriptBeforeLineId == 1, "Delta target metadata was lost.");
CryptographicOperations.ZeroMemory(openedMetadata.Delta);
ExpectFailure(() => Open(sealedObject, expectedSession: Guid.NewGuid()));
ExpectFailure(() => Open(sealedObject, expectedState: "terminal_state_2_21_v2"));
ExpectFailure(() => Open(sealedObject, expectedContext: new byte[32]));
var wrongContext = Enumerable.Repeat((byte)1, 32).ToArray();
ExpectFailure(() => Open(sealedObject, expectedContext: wrongContext));
ExpectFailure(() => Seal(native, wrongContext));
ExpectFailure(() => Open(sealedObject, expectedTargetSequence: 5204));
ExpectFailure(() => Open(sealedObject, expectedBaseHash: new byte[32]));
ExpectFailure(() => Open(sealedObject, minimumEpoch: 18));
foreach (int index in new[] { 0, 8, 10, 12, 28, 44, 60, 68, 133, 158, 190, 194, 202, 210, 218,
    226, 258, 290, 294, 306, 314, 322, DeltaCrypto.HeaderBytes, sealedObject.Length - 1 })
{
    var tampered = (byte[])sealedObject.Clone();
    tampered[index] ^= 1;
    ExpectFailure(() => Open(tampered));
}
ExpectFailure(() => Open(new byte[DeltaCrypto.MaximumObjectBytes + 1]));
ExpectFailure(() => Seal(new byte[DeltaCrypto.MaximumPlaintextBytes + 1]));
var wrongNative = (byte[])native.Clone();
wrongNative[64] ^= 1;
ExpectFailure(() => Seal(wrongNative));

var vector = new
{
    format = "termlink.state-delta-sealed-v1",
    format_name = DeltaCrypto.FormatName,
    format_version = DeltaCrypto.FormatVersion,
    note = "AEAD wire fixture; native Delta operations/digest are intentionally absent and must be validated by terminal-core.",
    session_id = session.ToString(), pc_device_id = pc.ToString(), recipient_device_id = android.ToString(),
    key_epoch = 7, key_id = DeltaCrypto.KeyId(android, 7), state_contract = state,
    context_id = Hex(context), profile = 1, base_next_sequence = 5201, target_next_sequence = 5203,
    base_snapshot_bytes = 8192, target_snapshot_bytes = 9000,
    base_sha256 = Hex(baseHash), target_sha256 = Hex(targetHash), pairwise_key = Hex(pairwise),
    nonce = Hex(nonce), target_observed_local_input_epoch = 17,
    target_initial_transcript_before_line_id = 1, native_delta = Hex(native),
    associated_data = Hex(Encoding.UTF8.GetBytes("TERMLINK-STATE-DELTA-OBJECT-V1\0").Concat(sealedObject.Take(DeltaCrypto.HeaderBytes)).ToArray()),
    sealed_object = Hex(sealedObject)
};
var json = JsonSerializer.Serialize(vector, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 2 && args[0] == "--write-vector") File.WriteAllText(args[1], json + Environment.NewLine);
else
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "protocol/test-vectors/state-delta-sealed-v1.json")))
        directory = directory.Parent;
    Require(directory is not null && File.ReadAllText(Path.Combine(directory.FullName,
        "protocol/test-vectors/state-delta-sealed-v1.json")).Trim() == json, "Delta shared vector changed.");
    Console.WriteLine("DeltaCrypto C# validation passed.");
}

static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
static void ExpectFailure(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or CryptographicException) { return; }
    throw new Exception("Invalid or tampered Delta was accepted.");
}
