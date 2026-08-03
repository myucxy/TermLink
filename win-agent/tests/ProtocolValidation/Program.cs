using System;
using System.Linq;
using System.Text;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

byte[] session = Enumerable.Repeat((byte)0x11, 16).ToArray();
byte[] sender = Enumerable.Repeat((byte)0x22, 16).ToArray();
var envelope = new Envelope
{
    ProtocolVersion = 1,
    MessageType = MessageType.OutputChunk,
    SessionId = ByteString.CopyFrom(session),
    SenderDeviceId = ByteString.CopyFrom(sender),
    Direction = Direction.PcToAndroid,
    Seq = 42,
    Ack = 7,
    SentAtMs = 1_800_000_000_123,
    KeyId = ByteString.CopyFromUtf8("epoch-01"),
    Nonce = ByteString.CopyFrom(Enumerable.Repeat((byte)0x33, 12).ToArray()),
    Ciphertext = ByteString.CopyFrom(Enumerable.Repeat((byte)0x44, 32).ToArray()),
};
const string expected = "080110141a10111111111111111111111111111111112210222222222222222222222222222222222801302a380740fba0f1c2b1344a0865706f63682d3031520c3333333333333333333333335a204444444444444444444444444444444444444444444444444444444444444444";
Require(Convert.ToHexString(envelope.ToByteArray()).Equals(expected, StringComparison.OrdinalIgnoreCase), "Protobuf Envelope vector changed.");

using var pc = DeviceExchangeKeyPair.Generate();
using var android = DeviceExchangeKeyPair.Generate();
Guid pcId = Guid.NewGuid(), androidId = Guid.NewGuid(), sessionId = Guid.NewGuid();
byte[] pcPair = SessionCrypto.DerivePairwiseKey(pc, android.PublicKey, pcId, androidId, 1);
byte[] androidPair = SessionCrypto.DerivePairwiseKey(android, pc.PublicKey, pcId, androidId, 1);
Require(pcPair.SequenceEqual(androidPair), "X25519 pairwise keys differ.");
byte[] keyId = Encoding.UTF8.GetBytes("epoch-00000001");
byte[] outputKey = SessionCrypto.DeriveSessionKey(pcPair, sessionId, CryptoDirection.PcToAndroid, keyId);
byte[] inputKey = SessionCrypto.DeriveSessionKey(pcPair, sessionId, CryptoDirection.AndroidToPc, keyId);
Require(!outputKey.SequenceEqual(inputKey), "Direction keys were not separated.");
byte[] nonce = SessionCrypto.Nonce(new byte[] { 1, 2, 3, 4 }, 9);
byte[] plaintext = Encoding.UTF8.GetBytes("\u001b[31mTermLink\u001b[0m");
byte[] aad = SessionCrypto.AssociatedData(envelope);
Require(SessionCrypto.Decrypt(outputKey, nonce, aad, SessionCrypto.Encrypt(outputKey, nonce, aad, plaintext)).SequenceEqual(plaintext), "AEAD round trip failed.");
Console.WriteLine("TermLink production Protocol/E2EE validation passed.");

using var vectorPc = new DeviceExchangeKeyPair(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
using var vectorAndroid = new DeviceExchangeKeyPair(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray());
Guid vectorPcId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
Guid vectorAndroidId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
Guid vectorSessionId = Guid.Parse("fedcba98-7654-3210-ffff-eeeeeeeeeeee");
byte[] vectorPair = SessionCrypto.DerivePairwiseKey(vectorPc, vectorAndroid.PublicKey, vectorPcId, vectorAndroidId, 7);
byte[] vectorKeyId = Encoding.UTF8.GetBytes("epoch-00000007");
byte[] vectorKey = SessionCrypto.DeriveSessionKey(vectorPair, vectorSessionId, CryptoDirection.PcToAndroid, vectorKeyId);
byte[] vectorNonce = SessionCrypto.Nonce(Convert.FromHexString("AABBCCDD"), 42);
var vectorEnvelope = new Envelope
{
    ProtocolVersion = 1, MessageType = MessageType.OutputChunk,
    SessionId = ByteString.CopyFrom(Convert.FromHexString(vectorSessionId.ToString("N"))),
    SenderDeviceId = ByteString.CopyFrom(Convert.FromHexString(vectorPcId.ToString("N"))),
    Direction = Direction.PcToAndroid, Seq = 42, Ack = 7, SentAtMs = 1_800_000_000_123,
    KeyId = ByteString.CopyFrom(vectorKeyId), Nonce = ByteString.CopyFrom(vectorNonce),
};
byte[] vectorAad = SessionCrypto.AssociatedData(vectorEnvelope);
byte[] vectorPlaintext = Convert.FromHexString("1B5B33316D5465726D4C696E6B1B5B306D0D0A");
Require(Convert.ToHexString(vectorPc.PublicKey).Equals("8f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f", StringComparison.OrdinalIgnoreCase), "PC public vector changed.");
Require(Convert.ToHexString(vectorAndroid.PublicKey).Equals("358072d6365880d1aeea329adf9121383851ed21a28e3b75e965d0d2cd166254", StringComparison.OrdinalIgnoreCase), "Android public vector changed.");
Require(Convert.ToHexString(vectorPair).Equals("b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773", StringComparison.OrdinalIgnoreCase), "Pairwise vector changed.");
Require(Convert.ToHexString(vectorKey).Equals("e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c", StringComparison.OrdinalIgnoreCase), "Session key vector changed.");
Require(Convert.ToHexString(vectorAad).Equals("080110141a10fedcba9876543210ffffeeeeeeeeeeee221000112233445566778899aabbccddeeff2801302a380740fba0f1c2b1344a0e65706f63682d3030303030303037520caabbccdd000000000000002a", StringComparison.OrdinalIgnoreCase), "AAD vector changed.");
Require(Convert.ToHexString(SessionCrypto.Encrypt(vectorKey, vectorNonce, vectorAad, vectorPlaintext)).Equals("6a97c75f0d8c984923467129d973a325134d69c9bd792587797651196a7498a68e3c44", StringComparison.OrdinalIgnoreCase), "Ciphertext vector changed.");

var historyEvents = new[]
{
    new HistoryEventV1
    {
        Seq = 41,
        MessageType = MessageType.OutputChunk,
        Payload = ByteString.CopyFrom(new OutputChunkPayload { Output = ByteString.CopyFromUtf8("history\r\n") }.ToByteArray())
    },
    new HistoryEventV1
    {
        Seq = 42,
        MessageType = MessageType.ResizeApplied,
        Payload = ByteString.CopyFrom(new ResizePayload { Columns = 120, Rows = 40 }.ToByteArray())
    }
};
byte[] historyObject = HistoryCrypto.Seal(
    vectorPair,
    vectorSessionId,
    vectorPcId,
    vectorAndroidId,
    7,
    historyEvents,
    Convert.FromHexString("000102030405060708090a0b"));
var openedHistory = HistoryCrypto.Open(
    vectorPair,
    historyObject,
    vectorSessionId,
    vectorPcId,
    vectorAndroidId,
    7,
    HistoryCrypto.KeyId(vectorAndroidId, 7),
    41,
    42);
Require(openedHistory.Events.Count == 2 && openedHistory.Events[0].Seq == 41 && openedHistory.Events[1].Seq == 42,
    "History object round trip failed.");
Require(Convert.ToHexString(historyObject).Equals(
    "08011210fedcba9876543210ffffeeeeeeeeeeee1a1000112233445566778899aabbccddeeff2210102132435465768798a9bacbdcedfe0f28073234686973746f72792d76313a31303231333234333534363537363837393861396261636264636564666530663a30303030303030373829402a4a0c000102030405060708090a0b522f7795cb6643e7868534311f82aff8381fcf651b52f354c62b6929e248b47358486a0bba113af757d4dd62eede2d3df4",
    StringComparison.OrdinalIgnoreCase), "History ciphertext vector changed.");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
