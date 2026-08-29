using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Protocol.Validation;

internal static class TerminalCoreSnapshotVectorValidation
{
    private const string VectorFileName = "terminal-core-snapshot-v2.json";
    private static readonly Guid PcDeviceId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid AndroidDeviceId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid SessionId = Guid.Parse("fedcba98-7654-3210-ffff-eeeeeeeeeeee");
    private const long KeyEpoch = 7;
    private const ulong SnapshotSequence = 5200;
    private const uint Columns = 132;
    private const uint Rows = 43;
    private const ulong InitialTranscriptBeforeLineId = 4097;
    private const ulong ObservedLocalInputEpoch = 11;

    public static void Run()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ResolveVectorPath()));
        var vector = document.RootElement;
        Require(ReadString(vector, "format") == "termlink.terminal-core-snapshot-v2",
            "Terminal-core snapshot vector format changed.");
        Require(ReadInt64(vector, "format_version") == 2,
            "Terminal-core snapshot vector version changed.");
        Require(Guid.Parse(ReadString(vector, "pc_device_id")) == PcDeviceId,
            "Terminal-core snapshot vector PC ID changed.");
        Require(Guid.Parse(ReadString(vector, "android_device_id")) == AndroidDeviceId,
            "Terminal-core snapshot vector recipient ID changed.");
        Require(Guid.Parse(ReadString(vector, "session_id")) == SessionId,
            "Terminal-core snapshot vector session ID changed.");
        Require(ReadInt64(vector, "key_epoch") == KeyEpoch,
            "Terminal-core snapshot vector key epoch changed.");
        Require(ReadUInt64(vector, "snapshot_sequence") == SnapshotSequence &&
            ReadUInt64(vector, "columns") == Columns && ReadUInt64(vector, "rows") == Rows &&
            ReadUInt64(vector, "initial_transcript_before_line_id") == InitialTranscriptBeforeLineId &&
            ReadUInt64(vector, "observed_local_input_epoch") == ObservedLocalInputEpoch,
            "Terminal-core snapshot authenticated metadata changed.");

        var pairwiseKey = ReadHex(vector, "pairwise_key");
        var nonce = ReadHex(vector, "nonce");
        var checkpoint = ReadHex(vector, "checkpoint");
        var keyId = SnapshotCrypto.TerminalCoreSnapshotKeyId(AndroidDeviceId, KeyEpoch);
        Require(ReadString(vector, "key_id") == keyId, "Terminal-core snapshot key ID vector changed.");
        var key = SnapshotCrypto.DeriveTerminalCoreSnapshotKey(
            pairwiseKey, SessionId, AndroidDeviceId, KeyEpoch, keyId);
        RequireHex(vector, "key", key);
        var sealedObject = SnapshotCrypto.SealTerminalCoreSnapshot(
            pairwiseKey,
            SessionId,
            PcDeviceId,
            AndroidDeviceId,
            KeyEpoch,
            SnapshotSequence,
            Columns,
            Rows,
            InitialTranscriptBeforeLineId,
            ObservedLocalInputEpoch,
            checkpoint,
            nonce);
        RequireHex(vector, "sealed_object", sealedObject);
        var container = TerminalCoreSnapshotCiphertextV2.Parser.ParseFrom(sealedObject);
        RequireHex(vector, "associated_data", SnapshotCrypto.TerminalCoreSnapshotAssociatedData(container));
        var opened = SnapshotCrypto.OpenTerminalCoreSnapshot(
            pairwiseKey,
            sealedObject,
            SessionId,
            PcDeviceId,
            AndroidDeviceId,
            KeyEpoch,
            keyId,
            SnapshotSequence,
            Columns,
            Rows,
            InitialTranscriptBeforeLineId,
            out var observedLocalInputEpoch);
        try
        {
            Require(opened.SequenceEqual(checkpoint), "Terminal-core snapshot fixed-vector checkpoint changed.");
            Require(observedLocalInputEpoch == ObservedLocalInputEpoch,
                "Terminal-core snapshot fixed-vector input epoch changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(opened);
            CryptographicOperations.ZeroMemory(pairwiseKey);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(checkpoint);
        }
    }

    public static string CreateVectorJson()
    {
        var pairwiseKey = Convert.FromHexString(
            "b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773");
        var nonce = Convert.FromHexString("18191a1b1c1d1e1f20212223");
        var checkpoint = Enumerable.Range(0, 64).Select(value => checked((byte)value)).ToArray();
        var keyId = SnapshotCrypto.TerminalCoreSnapshotKeyId(AndroidDeviceId, KeyEpoch);
        var key = SnapshotCrypto.DeriveTerminalCoreSnapshotKey(
            pairwiseKey, SessionId, AndroidDeviceId, KeyEpoch, keyId);
        var sealedObject = SnapshotCrypto.SealTerminalCoreSnapshot(
            pairwiseKey,
            SessionId,
            PcDeviceId,
            AndroidDeviceId,
            KeyEpoch,
            SnapshotSequence,
            Columns,
            Rows,
            InitialTranscriptBeforeLineId,
            ObservedLocalInputEpoch,
            checkpoint,
            nonce);
        var values = new Dictionary<string, object>
        {
            ["format"] = "termlink.terminal-core-snapshot-v2",
            ["format_version"] = 2,
            ["pairwise_key"] = Hex(pairwiseKey),
            ["pc_device_id"] = PcDeviceId.ToString("D"),
            ["android_device_id"] = AndroidDeviceId.ToString("D"),
            ["session_id"] = SessionId.ToString("D"),
            ["key_epoch"] = KeyEpoch,
            ["key_id"] = keyId,
            ["nonce"] = Hex(nonce),
            ["snapshot_sequence"] = SnapshotSequence,
            ["columns"] = Columns,
            ["rows"] = Rows,
            ["initial_transcript_before_line_id"] = InitialTranscriptBeforeLineId,
            ["observed_local_input_epoch"] = ObservedLocalInputEpoch,
            ["checkpoint"] = Hex(checkpoint),
            ["key"] = Hex(key),
            ["associated_data"] = Hex(SnapshotCrypto.TerminalCoreSnapshotAssociatedData(
                TerminalCoreSnapshotCiphertextV2.Parser.ParseFrom(sealedObject))),
            ["sealed_object"] = Hex(sealedObject)
        };
        return JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ResolveVectorPath()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "protocol", "test-vectors", VectorFileName);
                if (File.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        throw new FileNotFoundException("Unable to locate the shared terminal-core snapshot vector.", VectorFileName);
    }

    private static string ReadString(JsonElement vector, string name) =>
        vector.GetProperty(name).GetString() ?? throw new InvalidDataException("Vector field is null: " + name);

    private static long ReadInt64(JsonElement vector, string name) => vector.GetProperty(name).GetInt64();
    private static ulong ReadUInt64(JsonElement vector, string name) => vector.GetProperty(name).GetUInt64();
    private static byte[] ReadHex(JsonElement vector, string name) => Convert.FromHexString(ReadString(vector, name));
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static void RequireHex(JsonElement vector, string name, byte[] actual) =>
        Require(ReadHex(vector, name).SequenceEqual(actual), name + " vector changed.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
