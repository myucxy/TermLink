using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Validation;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

if (args.Contains("--print-snapshot-vector", StringComparer.Ordinal))
{
    Console.WriteLine(SnapshotVectorValidation.CreateVectorJson());
    return;
}

if (args.Contains("--print-terminal-core-snapshot-vector", StringComparer.Ordinal))
{
    Console.WriteLine(TerminalCoreSnapshotVectorValidation.CreateVectorJson());
    return;
}

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

var endedWithFailure = new SessionUpsertPayload { Lifecycle = "ENDED", ExitCode = 17 };
var endedSuccessfully = new SessionUpsertPayload { Lifecycle = "ENDED", ExitCode = 0 };
var legacyEnded = new SessionUpsertPayload { Lifecycle = "ENDED" };
Require(SessionUpsertPayload.Parser.ParseFrom(endedWithFailure.ToByteArray()).HasExitCode &&
    SessionUpsertPayload.Parser.ParseFrom(endedWithFailure.ToByteArray()).ExitCode == 17,
    "Session exit code 17 lost protobuf presence.");
Require(SessionUpsertPayload.Parser.ParseFrom(endedSuccessfully.ToByteArray()).HasExitCode &&
    SessionUpsertPayload.Parser.ParseFrom(endedSuccessfully.ToByteArray()).ExitCode == 0,
    "Session exit code 0 lost protobuf presence.");
Require(!SessionUpsertPayload.Parser.ParseFrom(legacyEnded.ToByteArray()).HasExitCode,
    "Legacy Session lifecycle unexpectedly acquired an exit code.");

var exactRelayEnvelopeBytes = ByteString.CopyFrom(Convert.FromHexString("080110141A03112233F80101"));
var directFrame = new DirectPathFrameV1
{
    ProtocolVersion = 1,
    ConnectionId = ByteString.CopyFrom(Enumerable.Repeat((byte)0xA5, 16).ToArray()),
    PathEpoch = 7,
    FrameSeq = 9,
    RealtimeEnvelope = exactRelayEnvelopeBytes
};
var parsedDirectFrame = DirectPathFrameV1.Parser.ParseFrom(directFrame.ToByteArray());
Require(parsedDirectFrame.BodyCase == DirectPathFrameV1.BodyOneofCase.RealtimeEnvelope &&
    parsedDirectFrame.RealtimeEnvelope.Equals(exactRelayEnvelopeBytes),
    "Direct transport did not preserve the byte-identical realtime Envelope.");
var directionalBarrier = SessionCutoverBarrierV1.Parser.ParseFrom(
    new SessionCutoverBarrierV1
    {
        SessionId = ByteString.CopyFrom(session),
        PcToAndroidFirstDirectSeq = 43
    }.ToByteArray());
Require(directionalBarrier.HasPcToAndroidFirstDirectSeq &&
    !directionalBarrier.HasAndroidToPcFirstDirectSeq &&
    directionalBarrier.PcToAndroidFirstDirectSeq == 43,
    "The direct cutover barrier lost per-direction presence semantics.");

const string synchronizedTitle = "构建完成 — PowerShell 🚀";
var titledSession = new SessionUpsertPayload
{
    ShellType = "powershell7",
    Lifecycle = "CHANGED",
    Title = synchronizedTitle,
    TitleCipher = ByteString.Empty
};
var clearedTitleSession = new SessionUpsertPayload { Lifecycle = "CHANGED", Title = string.Empty };
var legacyTitleSession = new SessionUpsertPayload { Lifecycle = "CHANGED" };
var parsedTitledSession = SessionUpsertPayload.Parser.ParseFrom(titledSession.ToByteArray());
var parsedClearedTitleSession = SessionUpsertPayload.Parser.ParseFrom(clearedTitleSession.ToByteArray());
Require(parsedTitledSession.HasTitle && parsedTitledSession.Title == synchronizedTitle &&
    parsedTitledSession.TitleCipher.IsEmpty,
    "Unicode session title lost presence or entered title_cipher.");
Require(parsedClearedTitleSession.HasTitle && parsedClearedTitleSession.Title.Length == 0,
    "Explicit empty session title did not retain protobuf presence.");
Require(!SessionUpsertPayload.Parser.ParseFrom(legacyTitleSession.ToByteArray()).HasTitle,
    "Legacy SessionUpsert unexpectedly acquired a title update.");
Require(LegacySessionUpsertCanIgnoreTitle(titledSession.ToByteArray()),
    "A legacy SessionUpsert reader could not skip optional title field 9.");

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
var titleEnvelope = envelope.Clone();
titleEnvelope.MessageType = MessageType.SessionUpsert;
titleEnvelope.Seq = 43;
titleEnvelope.Nonce = ByteString.CopyFrom(SessionCrypto.Nonce(new byte[] { 1, 2, 3, 4 }, titleEnvelope.Seq));
var titleAad = SessionCrypto.AssociatedData(titleEnvelope);
var sealedTitle = SessionCrypto.Encrypt(
    outputKey,
    titleEnvelope.Nonce.ToByteArray(),
    titleAad,
    titledSession.ToByteArray());
var openedTitle = SessionUpsertPayload.Parser.ParseFrom(
    SessionCrypto.Decrypt(outputKey, titleEnvelope.Nonce.ToByteArray(), titleAad, sealedTitle));
Require(openedTitle.HasTitle && openedTitle.Title == synchronizedTitle && openedTitle.TitleCipher.IsEmpty,
    "Unicode session title did not survive the recipient-specific E2EE payload round trip.");
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

Guid titleRecipient2Id = Guid.Parse("21324354-6576-8798-a9ba-cbdcedfe0f10");
byte[] titleRecipient2Pairwise = Enumerable.Range(64, 32).Select(value => checked((byte)value)).ToArray();
byte[] titleNonce1 = Convert.FromHexString("000102030405060708090A0B");
byte[] titleNonce2 = Convert.FromHexString("101112131415161718191A1B");
const string vectorTitle = "构建完成 — PowerShell 🚀";
var titleRecipients = new[]
{
    new SessionTitleRecipient(vectorAndroidId, 7, vectorPair, titleNonce1),
    new SessionTitleRecipient(titleRecipient2Id, 9, titleRecipient2Pairwise, titleNonce2)
};
byte[] titleCipher = SessionTitleCrypto.Seal(vectorTitle, vectorSessionId, vectorPcId, titleRecipients);
Require(SessionTitleCrypto.Open(vectorPair, titleCipher, vectorSessionId, vectorPcId, vectorAndroidId, 7) == vectorTitle,
    "The first session title recipient could not open its title.");
Require(SessionTitleCrypto.Open(titleRecipient2Pairwise, titleCipher, vectorSessionId, vectorPcId, titleRecipient2Id, 9) == vectorTitle,
    "The second session title recipient could not open its title.");
Require(!ContainsSequence(titleCipher, Encoding.UTF8.GetBytes(vectorTitle)),
    "The session title envelope leaked title plaintext.");
Require(SessionTitleCrypto.KeyId(vectorAndroidId, 7) == "title-v1:102132435465768798a9bacbdcedfe0f:00000007",
    "The independent title key ID changed.");
Require(Convert.ToHexString(titleCipher).Equals(
    "544c5449544c453100000001fedcba9876543210ffffeeeeeeeeeeee00112233445566778899aabbccddeeff00000002102132435465768798a9bacbdcedfe0f0000000000000007000102030405060708090a0b00000030182952144768033daa7e5ce9107cae203ea87a1e4e4c55c8e7e36f1f820a977448911969516fb3e821106d1d0f02daa12132435465768798a9bacbdcedfe0f100000000000000009101112131415161718191a1b0000003004d63f4639923e08cf5db7267b626151494e3388d725f9217a4c0d514d973a233271f288e5f697eed253fde73adccd46",
    StringComparison.OrdinalIgnoreCase), "Session title envelope vector changed.");

byte[] emptyTitleCipher = SessionTitleCrypto.Seal(
    string.Empty,
    vectorSessionId,
    vectorPcId,
    new[] { new SessionTitleRecipient(vectorAndroidId, 7, vectorPair, titleNonce1) });
Require(SessionTitleCrypto.Open(vectorPair, emptyTitleCipher, vectorSessionId, vectorPcId, vectorAndroidId, 7).Length == 0,
    "An explicit empty session title did not round trip.");
Require(Convert.ToHexString(emptyTitleCipher).Equals(
    "544c5449544c453100000001fedcba9876543210ffffeeeeeeeeeeee00112233445566778899aabbccddeeff00000001102132435465768798a9bacbdcedfe0f0000000000000007000102030405060708090a0b000000105fa75868560ae6e0c3fbbbbbbe33309c",
    StringComparison.OrdinalIgnoreCase), "Empty session title envelope vector changed.");

byte[] randomTitleCipher1 = SessionTitleCrypto.Seal("same", vectorSessionId, vectorPcId,
    new[] { new SessionTitleRecipient(vectorAndroidId, 7, vectorPair) });
byte[] randomTitleCipher2 = SessionTitleCrypto.Seal("same", vectorSessionId, vectorPcId,
    new[] { new SessionTitleRecipient(vectorAndroidId, 7, vectorPair) });
Require(!randomTitleCipher1.SequenceEqual(randomTitleCipher2), "Session title sealing reused a nonce.");

string maximumTitle = new('a', SessionTitleCrypto.MaxTitleUtf8Bytes);
byte[] maximumTitleCipher = SessionTitleCrypto.Seal(maximumTitle, vectorSessionId, vectorPcId,
    new[] { new SessionTitleRecipient(vectorAndroidId, 7, vectorPair, titleNonce1) });
Require(SessionTitleCrypto.Open(vectorPair, maximumTitleCipher, vectorSessionId, vectorPcId, vectorAndroidId, 7) == maximumTitle,
    "The maximum bounded session title did not round trip.");
ExpectFailure(() => SessionTitleCrypto.Seal(maximumTitle + "a", vectorSessionId, vectorPcId,
    new[] { new SessionTitleRecipient(vectorAndroidId, 7, vectorPair, titleNonce1) }),
    "An oversized session title was accepted.");

ExpectFailure(() => SessionTitleCrypto.Seal(vectorTitle, vectorSessionId, vectorPcId,
    new[]
    {
        new SessionTitleRecipient(vectorAndroidId, 7, vectorPair, titleNonce1),
        new SessionTitleRecipient(vectorAndroidId, 8, vectorPair, titleNonce2)
    }), "Duplicate outbound title recipients were accepted.");
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, titleCipher, vectorSessionId, vectorPcId, Guid.NewGuid(), 7),
    "A title entry was returned to the wrong recipient.");
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, titleCipher, vectorSessionId, vectorPcId, vectorAndroidId, 8),
    "A title entry was returned for the wrong key epoch.");
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, titleCipher, Guid.NewGuid(), vectorPcId, vectorAndroidId, 7),
    "A title entry was returned for the wrong session.");

byte[] tamperedTitleCipher = (byte[])titleCipher.Clone();
tamperedTitleCipher[48 + 40 + 5] ^= 0x40;
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, tamperedTitleCipher, vectorSessionId, vectorPcId, vectorAndroidId, 7),
    "A tampered title ciphertext was accepted.");
byte[] duplicateRecipientTitleCipher = (byte[])titleCipher.Clone();
int secondTitleRecipientOffset = 48 + 40 + Encoding.UTF8.GetByteCount(vectorTitle) + 16;
Array.Copy(duplicateRecipientTitleCipher, 48, duplicateRecipientTitleCipher, secondTitleRecipientOffset, 16);
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, duplicateRecipientTitleCipher, vectorSessionId, vectorPcId, vectorAndroidId, 7),
    "A duplicate parsed title recipient was accepted.");
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, titleCipher[..^1], vectorSessionId, vectorPcId, vectorAndroidId, 7),
    "A truncated title envelope was accepted.");
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, titleCipher.Concat(new byte[] { 0 }).ToArray(),
    vectorSessionId, vectorPcId, vectorAndroidId, 7), "A title envelope with trailing data was accepted.");
ExpectFailure(() => SessionTitleCrypto.Open(vectorPair, new byte[SessionTitleCrypto.MaxEnvelopeBytes + 1],
    vectorSessionId, vectorPcId, vectorAndroidId, 7), "An oversized title envelope was accepted.");
var tooManyTitleBytes = Enumerable.Range(0, 256)
    .Select(index => new SessionTitleRecipient(
        Guid.ParseExact($"{index + 1:x32}", "N"),
        7,
        vectorPair,
        titleNonce1))
    .ToArray();
ExpectFailure(() => SessionTitleCrypto.Seal(maximumTitle, vectorSessionId, vectorPcId, tooManyTitleBytes),
    "A title envelope beyond the total size limit was accepted.");

SnapshotVectorValidation.Run();
Console.WriteLine("TermLink terminal snapshot/transcript vector validation passed.");
TerminalCoreSnapshotVectorValidation.Run();
Console.WriteLine("TermLink terminal-core snapshot V2 vector validation passed.");

static bool LegacySessionUpsertCanIgnoreTitle(byte[] bytes)
{
    var shellType = string.Empty;
    var lifecycle = string.Empty;
    using var input = new CodedInputStream(bytes);
    while (true)
    {
        var tag = input.ReadTag();
        if (tag == 0) break;
        switch (tag)
        {
            case 10: shellType = input.ReadString(); break;
            case 18: input.ReadBytes(); break;
            case 26: lifecycle = input.ReadString(); break;
            case 32: input.ReadUInt32(); break;
            case 40: input.ReadUInt32(); break;
            case 50: input.ReadBytes(); break;
            case 56: input.ReadInt64(); break;
            case 64: input.ReadInt32(); break;
            default: input.SkipLastField(); break;
        }
    }
    return shellType == "powershell7" && lifecycle == "CHANGED";
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static void ExpectFailure(Action action, string message)
{
    try
    {
        action();
    }
    catch (Exception error) when (error is ArgumentException or InvalidDataException or CryptographicException or OverflowException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static bool ContainsSequence(byte[] value, byte[] candidate)
{
    if (candidate.Length == 0 || candidate.Length > value.Length) return false;
    for (var offset = 0; offset <= value.Length - candidate.Length; offset++)
        if (value.AsSpan(offset, candidate.Length).SequenceEqual(candidate)) return true;
    return false;
}
