using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

var session = Guid.Parse("fedcba98-7654-3210-ffff-eeeeeeeeeeee");
var pc = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
var android = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
var key = Convert.FromHexString("B1632A5A77CAFEC55F885FC4C3588F303ED3CA9856D5C464636E0B5FEAEFA773");
var token = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
var nonce = Enumerable.Range(0, 12).Select(i => (byte)(0xa0 + i)).ToArray();
const string stateV1 = "terminal_state_2_21_v2";
const string stateV2 = "terminal_state_2_22_v1";
var page = EmptyPage(1);
byte[] Seal(byte[] bytes, uint version = 1) => FrozenHistoryCrypto.Seal(key, session, pc, android, 7,
    token, 47, 5201, 5200, ulong.MaxValue, 128, 65536, 4 * 1024 * 1024, version,
    version == 1 ? stateV1 : stateV2, bytes, nonce);
byte[] Open(byte[] bytes, byte[] expectedToken = null, Guid? expectedSession = null, uint version = 1,
    string expectedStateContract = null) =>
    FrozenHistoryCrypto.Open(key, bytes, expectedSession ?? session, pc, android, 7, expectedToken ?? token,
        47, 5201, 5200, ulong.MaxValue, 128, 65536, 4 * 1024 * 1024, version,
        expectedStateContract ?? (version == 1 ? stateV1 : stateV2));
var sealedObject = Seal(page);
var opened = Open(sealedObject);
Require(opened.AsSpan().SequenceEqual(page), "C# cannot open its sealed page.");
CryptographicOperations.ZeroMemory(opened);
var container = FrozenHistoryCiphertextV1.Parser.ParseFrom(sealedObject);
Require(container.FirstLineId == 0 && container.LastLineId == 0 && !container.HasMoreBefore &&
    container.NextBeforeLineId == ulong.MaxValue, "Empty page boundary fields are wrong.");
ExpectFailure(() => Open(sealedObject, token.Select(value => (byte)(value ^ 1)).ToArray()));
ExpectFailure(() => Open(sealedObject, expectedSession: Guid.NewGuid()));
ExpectFailure(() => Open(sealedObject, expectedStateContract: stateV2));
foreach (Action<FrozenHistoryCiphertextV1> mutate in new Action<FrozenHistoryCiphertextV1>[]
{
    value => value.Token = ByteString.CopyFrom(new byte[32]),
    value => value.CaptureId++,
    value => value.CaptureRevision++,
    value => value.SnapshotSeq++,
    value => value.RequestBeforeLineId = 7,
    value => value.MaxLines--,
    value => value.MaxCells--,
    value => value.MaxUtf8Bytes--,
    value => value.FirstLineId++,
    value => value.LastLineId++,
    value => value.HasMoreBefore = true,
    value => value.NextBeforeLineId = 5,
    value => value.PlaintextBytes--,
    value => value.Ciphertext = ByteString.CopyFrom(value.Ciphertext.ToByteArray().Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray()),
})
{
    var changed = container.Clone();
    mutate(changed);
    ExpectFailure(() => Open(changed.ToByteArray()));
}
ExpectFailure(() => Open(new byte[FrozenHistoryCrypto.MaxPageV1Bytes + FrozenHistoryCrypto.ObjectOverheadBytes + 1]));
var v2Page = EmptyPage(2);
var v2Sealed = Seal(v2Page, 2);
opened = Open(v2Sealed, version: 2);
Require(opened.AsSpan().SequenceEqual(v2Page), "TLHPV2 empty page did not round trip.");
CryptographicOperations.ZeroMemory(opened);
ExpectFailure(() => Open(v2Sealed));

var vector = new
{
    format = "termlink.frozen-history-v1",
    session_id = session.ToString(), pc_device_id = pc.ToString(), recipient_device_id = android.ToString(),
    key_epoch = 7, pairwise_key = Hex(key), token = Hex(token), capture_id = 47,
    capture_revision = 5201, snapshot_seq = 5200, request_before_line_id = ulong.MaxValue.ToString(),
    max_lines = 128, max_cells = 65536, max_utf8_bytes = 4 * 1024 * 1024,
    page_format_version = 1, state_contract = stateV1, nonce = Hex(nonce), page = Hex(page),
    associated_data = Hex(FrozenHistoryCrypto.AssociatedData(container, stateV1)), sealed_object = Hex(sealedObject)
};
var json = JsonSerializer.Serialize(vector, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 2 && args[0] == "--write-vector") File.WriteAllText(args[1], json + Environment.NewLine);
else
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "protocol/test-vectors/frozen-history-v1.json")))
        directory = directory.Parent;
    Require(directory is not null, "Frozen history shared vector is missing.");
    var committed = File.ReadAllText(Path.Combine(directory.FullName, "protocol/test-vectors/frozen-history-v1.json"));
    Require(committed.Trim() == json, "Frozen history shared vector changed.");
    Console.WriteLine("FrozenHistoryCrypto C# validation passed.");
}

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ExpectFailure(Action action) { try { action(); } catch (Exception error) when (error is InvalidDataException or CryptographicException) { return; } throw new Exception("Tampered frozen history object was accepted."); }
static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
static byte[] EmptyPage(int version)
{
    var bytes = new byte[96];
    System.Text.Encoding.ASCII.GetBytes($"TLHPV{version}\0\0").CopyTo(bytes, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), checked((ushort)version));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 96);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 96);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 47);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 5201);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), ulong.MaxValue);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), ulong.MaxValue);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 80);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), checked((uint)(version - 1)));
    uint crc = uint.MaxValue;
    for (int i = 0; i < bytes.Length; i++)
    {
        if (i is >= 92 and <= 95) continue;
        crc ^= bytes[i];
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (uint)-(int)(crc & 1));
    }
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), ~crc);
    return bytes;
}
