using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Protocol.Validation;

internal static class SnapshotVectorValidation
{
    private const string VectorFileName = "terminal-snapshot-v1.json";

    private static readonly Guid PcDeviceId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid AndroidDeviceId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid SessionId = Guid.Parse("fedcba98-7654-3210-ffff-eeeeeeeeeeee");
    private const long KeyEpoch = 7;

    public static void Run()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ResolveVectorPath()));
        var vector = document.RootElement;
        Require(ReadString(vector, "format") == "termlink.terminal-snapshot-v1", "Snapshot vector format changed.");
        Require(ReadInt64(vector, "format_version") == 1, "Snapshot vector version changed.");
        Require(Guid.Parse(ReadString(vector, "pc_device_id")) == PcDeviceId, "Snapshot vector PC ID changed.");
        Require(Guid.Parse(ReadString(vector, "android_device_id")) == AndroidDeviceId, "Snapshot vector recipient ID changed.");
        Require(Guid.Parse(ReadString(vector, "session_id")) == SessionId, "Snapshot vector session ID changed.");
        Require(ReadInt64(vector, "key_epoch") == KeyEpoch, "Snapshot vector key epoch changed.");

        byte[] pairwiseKey = ReadHex(vector, "pairwise_key");
        byte[] snapshotNonce = ReadHex(vector, "snapshot_nonce");
        byte[] transcriptNonce = ReadHex(vector, "transcript_nonce");
        var snapshot = CreateSnapshot();
        var transcript = CreateTranscript();
        byte[] snapshotPlaintext = snapshot.ToByteArray();
        byte[] transcriptPlaintext = transcript.ToByteArray();

        string snapshotKeyId = SnapshotCrypto.SnapshotKeyId(AndroidDeviceId, KeyEpoch);
        string transcriptKeyId = SnapshotCrypto.TranscriptKeyId(AndroidDeviceId, KeyEpoch);
        Require(ReadString(vector, "snapshot_key_id") == snapshotKeyId, "Snapshot key ID vector changed.");
        Require(ReadString(vector, "transcript_key_id") == transcriptKeyId, "Transcript key ID vector changed.");
        RequireHex(vector, "snapshot_plaintext", snapshotPlaintext);
        RequireHex(vector, "transcript_plaintext", transcriptPlaintext);

        byte[] snapshotKey = SnapshotCrypto.DeriveSnapshotKey(pairwiseKey, SessionId, AndroidDeviceId, KeyEpoch, snapshotKeyId);
        byte[] transcriptKey = SnapshotCrypto.DeriveTranscriptKey(pairwiseKey, SessionId, AndroidDeviceId, KeyEpoch, transcriptKeyId);
        RequireHex(vector, "snapshot_key", snapshotKey);
        RequireHex(vector, "transcript_key", transcriptKey);

        byte[] snapshotSealed = SnapshotCrypto.SealSnapshot(
            pairwiseKey, SessionId, PcDeviceId, AndroidDeviceId, KeyEpoch, snapshot, snapshotNonce);
        byte[] transcriptSealed = SnapshotCrypto.SealTranscript(
            pairwiseKey, SessionId, PcDeviceId, AndroidDeviceId, KeyEpoch, transcript, 101, transcriptNonce);
        RequireHex(vector, "snapshot_sealed_object", snapshotSealed);
        RequireHex(vector, "transcript_sealed_object", transcriptSealed);

        var snapshotContainer = TerminalSnapshotCiphertextV1.Parser.ParseFrom(snapshotSealed);
        var transcriptContainer = TerminalTranscriptCiphertextV1.Parser.ParseFrom(transcriptSealed);
        RequireHex(vector, "snapshot_associated_data", SnapshotCrypto.SnapshotAssociatedData(snapshotContainer));
        RequireHex(vector, "transcript_associated_data", SnapshotCrypto.TranscriptAssociatedData(transcriptContainer));

        var openedSnapshot = OpenSnapshot(pairwiseKey, snapshotSealed, AndroidDeviceId);
        Require(openedSnapshot.AlternateScreen.VisibleRows.All(row => row.LineId == 0),
            "Legacy alternate line IDs leaked through snapshot import.");
        openedSnapshot.AlternateScreen = snapshot.AlternateScreen.Clone();
        Require(openedSnapshot.ToByteArray().SequenceEqual(snapshotPlaintext),
            "Snapshot fixed-vector fields other than ignored alternate IDs changed.");
        var openedTranscript = OpenTranscript(pairwiseKey, transcriptSealed);
        Require(openedTranscript.ToByteArray().SequenceEqual(transcriptPlaintext), "Transcript fixed-vector round trip changed.");

        byte[] tamperedSnapshot = (byte[])snapshotSealed.Clone();
        tamperedSnapshot[^1] ^= 0x01;
        ExpectThrows<CryptographicException>(
            () => OpenSnapshot(pairwiseKey, tamperedSnapshot, AndroidDeviceId),
            "Tampered snapshot ciphertext was accepted.");

        var wrongRecipient = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        ExpectThrows<InvalidDataException>(
            () => OpenSnapshot(pairwiseKey, snapshotSealed, wrongRecipient),
            "Snapshot selected for the wrong recipient was accepted.");

        ExpectThrows<InvalidDataException>(
            () => OpenSnapshot(pairwiseKey, new byte[SnapshotCrypto.MaxSnapshotObjectBytes + 1], AndroidDeviceId),
            "Oversized snapshot object was accepted.");

        var sizeMismatch = snapshotContainer.Clone();
        sizeMismatch.UncompressedBytes++;
        byte[] sizeMismatchAad = SnapshotCrypto.SnapshotAssociatedData(sizeMismatch);
        sizeMismatch.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(
            snapshotKey, sizeMismatch.Nonce.ToByteArray(), sizeMismatchAad, snapshotPlaintext));
        ExpectThrows<InvalidDataException>(
            () => OpenSnapshot(pairwiseKey, sizeMismatch.ToByteArray(), AndroidDeviceId),
            "Authenticated snapshot plaintext-size mismatch was accepted.");

        var zstd = snapshotContainer.Clone();
        zstd.Compression = SnapshotCompressionV1.Zstd;
        ExpectThrows<InvalidDataException>(
            () => OpenSnapshot(pairwiseKey, zstd.ToByteArray(), AndroidDeviceId),
            "Unsupported snapshot compression was accepted.");

        var unknownCompression = snapshotContainer.Clone();
        unknownCompression.Compression = (SnapshotCompressionV1)99;
        ExpectThrows<InvalidDataException>(
            () => OpenSnapshot(pairwiseKey, unknownCompression.ToByteArray(), AndroidDeviceId),
            "Unknown snapshot compression enum was accepted.");

        ValidateStrictRejections(snapshot, transcript);

        byte[] tamperedTranscript = (byte[])transcriptSealed.Clone();
        tamperedTranscript[^1] ^= 0x01;
        ExpectThrows<CryptographicException>(
            () => OpenTranscript(pairwiseKey, tamperedTranscript),
            "Tampered transcript ciphertext was accepted.");

        CryptographicOperations.ZeroMemory(pairwiseKey);
        CryptographicOperations.ZeroMemory(snapshotKey);
        CryptographicOperations.ZeroMemory(transcriptKey);
        CryptographicOperations.ZeroMemory(snapshotPlaintext);
        CryptographicOperations.ZeroMemory(transcriptPlaintext);
        CryptographicOperations.ZeroMemory(sizeMismatchAad);
    }

    public static string CreateVectorJson()
    {
        byte[] pairwiseKey = Convert.FromHexString(
            "b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773");
        byte[] snapshotNonce = Convert.FromHexString("000102030405060708090a0b");
        byte[] transcriptNonce = Convert.FromHexString("0c0d0e0f1011121314151617");
        var snapshot = CreateSnapshot();
        var transcript = CreateTranscript();
        string snapshotKeyId = SnapshotCrypto.SnapshotKeyId(AndroidDeviceId, KeyEpoch);
        string transcriptKeyId = SnapshotCrypto.TranscriptKeyId(AndroidDeviceId, KeyEpoch);
        byte[] snapshotPlaintext = snapshot.ToByteArray();
        byte[] transcriptPlaintext = transcript.ToByteArray();
        byte[] snapshotKey = SnapshotCrypto.DeriveSnapshotKey(pairwiseKey, SessionId, AndroidDeviceId, KeyEpoch, snapshotKeyId);
        byte[] transcriptKey = SnapshotCrypto.DeriveTranscriptKey(pairwiseKey, SessionId, AndroidDeviceId, KeyEpoch, transcriptKeyId);
        byte[] snapshotSealed = SnapshotCrypto.SealSnapshot(
            pairwiseKey, SessionId, PcDeviceId, AndroidDeviceId, KeyEpoch, snapshot, snapshotNonce);
        byte[] transcriptSealed = SnapshotCrypto.SealTranscript(
            pairwiseKey, SessionId, PcDeviceId, AndroidDeviceId, KeyEpoch, transcript, 101, transcriptNonce);

        var values = new Dictionary<string, object>
        {
            ["format"] = "termlink.terminal-snapshot-v1",
            ["format_version"] = 1,
            ["pairwise_key"] = Hex(pairwiseKey),
            ["pc_device_id"] = PcDeviceId.ToString("D"),
            ["android_device_id"] = AndroidDeviceId.ToString("D"),
            ["session_id"] = SessionId.ToString("D"),
            ["key_epoch"] = KeyEpoch,
            ["snapshot_key_id"] = snapshotKeyId,
            ["snapshot_nonce"] = Hex(snapshotNonce),
            ["snapshot_plaintext"] = Hex(snapshotPlaintext),
            ["snapshot_key"] = Hex(snapshotKey),
            ["snapshot_associated_data"] = Hex(SnapshotCrypto.SnapshotAssociatedData(
                TerminalSnapshotCiphertextV1.Parser.ParseFrom(snapshotSealed))),
            ["snapshot_sealed_object"] = Hex(snapshotSealed),
            ["transcript_key_id"] = transcriptKeyId,
            ["transcript_nonce"] = Hex(transcriptNonce),
            ["transcript_plaintext"] = Hex(transcriptPlaintext),
            ["transcript_key"] = Hex(transcriptKey),
            ["transcript_associated_data"] = Hex(SnapshotCrypto.TranscriptAssociatedData(
                TerminalTranscriptCiphertextV1.Parser.ParseFrom(transcriptSealed))),
            ["transcript_sealed_object"] = Hex(transcriptSealed)
        };
        return JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
    }

    private static TerminalSnapshotV1 OpenSnapshot(byte[] pairwiseKey, byte[] sealedObject, Guid recipientDeviceId) =>
        SnapshotCrypto.OpenSnapshot(
            pairwiseKey,
            sealedObject,
            SessionId,
            PcDeviceId,
            recipientDeviceId,
            KeyEpoch,
            SnapshotCrypto.SnapshotKeyId(recipientDeviceId, KeyEpoch),
            42,
            6,
            2);

    private static TerminalTranscriptPageV1 OpenTranscript(byte[] pairwiseKey, byte[] sealedObject) =>
        SnapshotCrypto.OpenTranscript(
            pairwiseKey,
            sealedObject,
            SessionId,
            PcDeviceId,
            AndroidDeviceId,
            KeyEpoch,
            SnapshotCrypto.TranscriptKeyId(AndroidDeviceId, KeyEpoch),
            42,
            90,
            91,
            101);

    private static TerminalSnapshotV1 CreateSnapshot()
    {
        var snapshot = new TerminalSnapshotV1
        {
            FormatVersion = SnapshotCrypto.FormatVersion,
            SnapshotSeq = 42,
            Columns = 6,
            Rows = 2,
            ActiveScreen = TerminalScreenKindV1.Primary,
            PrimaryScreen = CreateScreen(primary: true),
            AlternateScreen = CreateScreen(primary: false),
            Modes = new TerminalModesV1
            {
                AutoWrap = true,
                BracketedPaste = true,
                MouseTracking = TerminalMouseTrackingV1.None,
                MouseEncoding = TerminalMouseEncodingV1.Default
            },
            Palette = CreatePalette(),
            NextTranscriptLineId = 103,
            Title = "TermLink \u6f14\u793a",
            Projection = new TerminalSessionProjectionV1
            {
                Lifecycle = TerminalLifecycleV1.Running,
                ObservedLocalInputEpoch = 9
            },
            ParserState = SnapshotParserStateV1.Ground
        };
        snapshot.TabStops.Add(4);
        snapshot.StyleTable.Add(CreateStyles());
        snapshot.HyperlinkTable.Add(new TerminalHyperlinkV1
        {
            Id = "docs",
            Uri = "https://example.invalid/termlink",
            Params = "vector=1"
        });
        return snapshot;
    }

    private static TerminalTranscriptPageV1 CreateTranscript()
    {
        var page = new TerminalTranscriptPageV1
        {
            FormatVersion = SnapshotCrypto.FormatVersion,
            AsOfSnapshotSeq = 42,
            FirstLineId = 90,
            LastLineId = 91,
            HasMoreBefore = true,
            NextBeforeLineId = 90,
            Palette = CreatePalette()
        };
        page.StyleTable.Add(CreateStyles());
        page.HyperlinkTable.Add(new TerminalHyperlinkV1
        {
            Id = "docs",
            Uri = "https://example.invalid/termlink",
            Params = "vector=1"
        });
        page.Rows.Add(CreateTextRow(90, 40, "old", 3));
        page.Rows.Add(CreateWideRow(91, 41, "\u754c", 4));
        return page;
    }

    private static void ValidateStrictRejections(TerminalSnapshotV1 snapshot, TerminalTranscriptPageV1 transcript)
    {
        Require(SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot) == 101,
            "Initial transcript cursor is not the first visible primary line ID.");
        SnapshotCrypto.ValidateInitialTranscriptPage(snapshot, transcript);
        ExpectThrows<InvalidDataException>(
            () => SnapshotCrypto.ValidateSnapshotForProducer(snapshot),
            "Legacy alternate IDs were accepted as producer-canonical.");
        var canonicalSnapshot = snapshot.Clone();
        foreach (var row in canonicalSnapshot.AlternateScreen.VisibleRows) row.LineId = 0;
        SnapshotCrypto.ValidateSnapshotForProducer(canonicalSnapshot);

        var invalidSnapshot = snapshot.Clone();
        invalidSnapshot.ActiveScreen = (TerminalScreenKindV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown active-screen enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.ParserState = (SnapshotParserStateV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown parser-state enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.Cursor.Style = (TerminalCursorStyleV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown cursor-style enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.Cursor.Charset.G0 = (TerminalCharsetV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown charset enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.Modes.MouseTracking = (TerminalMouseTrackingV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown mouse-tracking enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.Modes.MouseEncoding = (TerminalMouseEncodingV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown mouse-encoding enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.Projection.Lifecycle = (TerminalLifecycleV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown lifecycle enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.StyleTable[0].UnderlineStyle = (TerminalUnderlineStyleV1)99;
        ExpectInvalidSnapshot(invalidSnapshot, "Unknown underline-style enum was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.Columns = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned snapshot column overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.ScrollTop = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned screen-bound overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.CurrentStyleIndex = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned current-style overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.Cursor.Column = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned cursor-column overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.Cursor.RenditionStyleIndex = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned cursor-style-index overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[0].StyleIndex = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned cell-style-index overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[0].Repeat = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned repeat overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.StyleTable[0].HyperlinkIndexPlusOne = uint.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned hyperlink-index overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.StyleTable[0].Foreground = new TerminalColorRefV1 { PaletteIndex = uint.MaxValue };
        ExpectInvalidSnapshot(invalidSnapshot, "Unsigned palette-index overflow was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].LineId = 0;
        ExpectInvalidSnapshot(invalidSnapshot, "Zero primary line ID was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[1].LineId = 101;
        ExpectInvalidSnapshot(invalidSnapshot, "Duplicate primary line ID was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.NextTranscriptLineId = 104;
        ExpectInvalidSnapshot(invalidSnapshot, "Inconsistent next transcript line ID was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[1].LineId = (ulong)long.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Primary line ID without a signed successor was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.SnapshotSeq = ulong.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Snapshot sequence above Int64.MaxValue was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.AlternateScreen.VisibleRows[0].LineId = ulong.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Alternate line ID above Int64.MaxValue was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].SealedAtSeq = ulong.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Snapshot row sequence above Int64.MaxValue was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.ScrollbackFirstLineId = 1;
        ExpectInvalidSnapshot(invalidSnapshot, "Non-zero empty-scrollback sentinel was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.Projection.ObservedLocalInputEpoch = ulong.MaxValue;
        ExpectInvalidSnapshot(invalidSnapshot, "Local input epoch above Int64.MaxValue was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[0].Repeat = 2;
        ExpectInvalidSnapshot(invalidSnapshot, "Repeated non-empty grapheme was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[1].Grapheme = string.Empty;
        ExpectInvalidSnapshot(invalidSnapshot, "Empty width-two lead was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[1].Repeat = 2;
        ExpectInvalidSnapshot(invalidSnapshot, "Repeated width-two lead was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[2].StyleIndex = 0;
        ExpectInvalidSnapshot(invalidSnapshot, "Wide continuation with a different style was accepted.");

        invalidSnapshot = snapshot.Clone();
        invalidSnapshot.PrimaryScreen.VisibleRows[0].Runs[2].Repeat = 2;
        ExpectInvalidSnapshot(invalidSnapshot, "Repeated wide continuation was accepted.");

        var invalidTranscript = transcript.Clone();
        invalidTranscript.StyleTable[0].UnderlineStyle = (TerminalUnderlineStyleV1)99;
        ExpectInvalidTranscript(invalidTranscript, "Transcript accepted an unknown shared style enum.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.Rows[0].Runs[0].StyleIndex = uint.MaxValue;
        ExpectInvalidTranscript(invalidTranscript, "Transcript accepted an unsigned shared style index.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.AsOfSnapshotSeq = ulong.MaxValue;
        ExpectInvalidTranscript(invalidTranscript, "Transcript anchor above Int64.MaxValue was accepted.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.Rows[1].LineId = ulong.MaxValue;
        invalidTranscript.LastLineId = ulong.MaxValue;
        ExpectInvalidTranscript(invalidTranscript, "Transcript line ID above Int64.MaxValue was accepted.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.Rows[0].SealedAtSeq = ulong.MaxValue;
        ExpectInvalidTranscript(invalidTranscript, "Transcript row sequence above Int64.MaxValue was accepted.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.Rows[1].LineId = invalidTranscript.Rows[0].LineId;
        ExpectInvalidTranscript(invalidTranscript, "Duplicate transcript line ID was accepted.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.HasMoreBefore = false;
        ExpectInvalidTranscript(invalidTranscript, "A final transcript page retained a non-zero cursor.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.NextBeforeLineId = 89;
        ExpectInvalidTranscript(invalidTranscript, "A transcript continuation cursor differed from first_line_id.");

        invalidTranscript = transcript.Clone();
        invalidTranscript.Rows.Clear();
        for (ulong lineId = 1; lineId <= 201; lineId++)
        {
            var row = transcript.Rows[0].Clone();
            row.LineId = lineId;
            row.SealedAtSeq = 1;
            invalidTranscript.Rows.Add(row);
        }
        invalidTranscript.FirstLineId = 1;
        invalidTranscript.LastLineId = 201;
        invalidTranscript.NextBeforeLineId = 1;
        ExpectInvalidTranscript(invalidTranscript, "A transcript page with more than 200 rows was accepted.");

        ExpectThrows<InvalidDataException>(
            () => SnapshotCrypto.ValidateTranscriptForRequest(transcript, 42, 91),
            "Transcript row at the exclusive cursor was accepted.");
        ExpectThrows<InvalidDataException>(
            () => SnapshotCrypto.ValidateTranscriptForRequest(transcript, 43, 101),
            "Transcript page changed its fixed snapshot anchor.");
    }

    private static TerminalScreenV1 CreateScreen(bool primary)
    {
        var charset = CreateCharset();
        var screen = new TerminalScreenV1
        {
            Cursor = CreateCursor(primary ? 3u : 0u, primary ? 1u : 0u),
            SavedCursor = CreateCursor(0, 0),
            ScrollTop = 0,
            ScrollBottom = 1,
            MarginLeft = 0,
            MarginRight = 5,
            CurrentStyleIndex = 0,
            PendingWrap = false,
            LastPrintedGrapheme = primary ? "!" : string.Empty,
            Charset = charset
        };
        if (primary)
        {
            screen.VisibleRows.Add(CreateWideRow(101, 42, "\u5bbd", 3, "T"));
            screen.VisibleRows.Add(CreateBlankRow(102, 42));
        }
        else
        {
            screen.VisibleRows.Add(CreateBlankRow(201, 42));
            screen.VisibleRows.Add(CreateBlankRow(202, 42));
        }
        return screen;
    }

    private static TerminalCursorStateV1 CreateCursor(uint column, uint row) => new()
    {
        Column = column,
        Row = row,
        Visible = true,
        Style = TerminalCursorStyleV1.Block,
        Blink = true,
        RenditionStyleIndex = 0,
        Charset = CreateCharset()
    };

    private static TerminalCharsetStateV1 CreateCharset() => new()
    {
        G0 = TerminalCharsetV1.Ascii,
        G1 = TerminalCharsetV1.DecSpecialGraphics,
        G2 = TerminalCharsetV1.Ascii,
        G3 = TerminalCharsetV1.Latin1,
        Gl = 0,
        Gr = 1
    };

    private static TerminalRowV1 CreateBlankRow(ulong lineId, ulong sealedAt) => new()
    {
        LineId = lineId,
        SealedAtSeq = sealedAt,
        Runs = { new TerminalCellRunV1 { Repeat = 6, DisplayWidth = 1, StyleIndex = 0 } }
    };

    private static TerminalRowV1 CreateTextRow(ulong lineId, ulong sealedAt, string text, uint trailingBlanks)
    {
        var row = new TerminalRowV1 { LineId = lineId, SealedAtSeq = sealedAt };
        foreach (char value in text)
            row.Runs.Add(new TerminalCellRunV1 { Repeat = 1, Grapheme = value.ToString(), DisplayWidth = 1, StyleIndex = 0 });
        row.Runs.Add(new TerminalCellRunV1 { Repeat = trailingBlanks, DisplayWidth = 1, StyleIndex = 0 });
        return row;
    }

    private static TerminalRowV1 CreateWideRow(
        ulong lineId,
        ulong sealedAt,
        string grapheme,
        uint trailingBlanks,
        string leading = "")
    {
        var row = new TerminalRowV1 { LineId = lineId, SealedAtSeq = sealedAt };
        if (leading.Length > 0)
            row.Runs.Add(new TerminalCellRunV1 { Repeat = 1, Grapheme = leading, DisplayWidth = 1, StyleIndex = 0 });
        row.Runs.Add(new TerminalCellRunV1 { Repeat = 1, Grapheme = grapheme, DisplayWidth = 2, StyleIndex = 1 });
        row.Runs.Add(new TerminalCellRunV1 { Repeat = 1, DisplayWidth = 0, StyleIndex = 1, WideContinuation = true });
        row.Runs.Add(new TerminalCellRunV1 { Repeat = trailingBlanks, DisplayWidth = 1, StyleIndex = 0 });
        return row;
    }

    private static IEnumerable<TerminalStyleV1> CreateStyles()
    {
        yield return new TerminalStyleV1
        {
            Foreground = DefaultColor(),
            Background = DefaultColor(),
            UnderlineColor = DefaultColor(),
            UnderlineStyle = TerminalUnderlineStyleV1.None
        };
        yield return new TerminalStyleV1
        {
            Foreground = new TerminalColorRefV1 { Rgb = 0x00_33_cc },
            Background = new TerminalColorRefV1 { PaletteIndex = 4 },
            UnderlineColor = DefaultColor(),
            Attributes = 0x09,
            UnderlineStyle = TerminalUnderlineStyleV1.Single,
            HyperlinkIndexPlusOne = 1
        };
    }

    private static TerminalColorRefV1 DefaultColor() => new() { DefaultColor = true };

    private static TerminalPaletteV1 CreatePalette()
    {
        var palette = new TerminalPaletteV1
        {
            DefaultForegroundRgb = 0x00_e5_e5_e5,
            DefaultBackgroundRgb = 0x00_10_10_10,
            DefaultCursorRgb = 0x00_ff_ff_ff
        };
        palette.ColorsRgb.Add(Enumerable.Range(0, 256).Select(value => checked((uint)value * 0x01_01_01u)));
        return palette;
    }

    private static string ResolveVectorPath()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                string candidate = Path.Combine(current.FullName, "protocol", "test-vectors", VectorFileName);
                if (File.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        throw new FileNotFoundException("Unable to locate the shared terminal snapshot vector.", VectorFileName);
    }

    private static string ReadString(JsonElement vector, string name) =>
        vector.GetProperty(name).GetString() ?? throw new InvalidDataException("Vector field is null: " + name);

    private static long ReadInt64(JsonElement vector, string name) => vector.GetProperty(name).GetInt64();
    private static byte[] ReadHex(JsonElement vector, string name) => Convert.FromHexString(ReadString(vector, name));
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private static void RequireHex(JsonElement vector, string name, byte[] actual) =>
        Require(ReadHex(vector, name).SequenceEqual(actual), name + " vector changed.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalidSnapshot(TerminalSnapshotV1 snapshot, string message) =>
        ExpectThrows<InvalidDataException>(
            () => SnapshotCrypto.ValidateSnapshot(TerminalSnapshotV1.Parser.ParseFrom(snapshot.ToByteArray())),
            message);

    private static void ExpectInvalidTranscript(TerminalTranscriptPageV1 transcript, string message) =>
        ExpectThrows<InvalidDataException>(
            () => SnapshotCrypto.ValidateTranscript(TerminalTranscriptPageV1.Parser.ParseFrom(transcript.ToByteArray())),
            message);

    private static void ExpectThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
