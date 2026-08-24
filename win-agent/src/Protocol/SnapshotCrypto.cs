using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Protocol;

public static class SnapshotCrypto
{
    public const uint ContainerVersion = 1;
    public const uint FormatVersion = 1;
    public const int MaxSnapshotObjectBytes = 2 * 1024 * 1024;
    public const int MaxSnapshotPlaintextBytes = 8 * 1024 * 1024;
    public const int MaxTranscriptObjectBytes = 512 * 1024;
    public const int MaxTranscriptPlaintextBytes = 2 * 1024 * 1024;
    public const string SnapshotFormatName = "TERMLINK_TERMINAL_SNAPSHOT";
    public const string TranscriptFormatName = "TERMLINK_TERMINAL_TRANSCRIPT";

    private const int MaxColumns = 512;
    private const int MaxRows = 256;
    private const int MaxExpandedSnapshotCells = 262_144;
    private const int MaxTranscriptRows = 200;
    private const int MaxStyles = 1_024;
    private const int MaxHyperlinks = 1_024;
    private const int MaxTitleBytes = 4_096;
    private const int MaxGraphemeBytes = 64;
    private const int MaxHyperlinkIdBytes = 256;
    private const int MaxHyperlinkUriBytes = 4_096;
    private const int MaxHyperlinkParamsBytes = 1_024;
    private const uint SupportedStyleAttributes = 0x1ff;
    private const ulong MaxSupportedUInt64 = long.MaxValue;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] SnapshotKeyPrefix = "TERMLINK-TERMINAL-SNAPSHOT-KEY-V1\0"u8.ToArray();
    private static readonly byte[] TranscriptKeyPrefix = "TERMLINK-TERMINAL-TRANSCRIPT-KEY-V1\0"u8.ToArray();
    private static readonly byte[] SnapshotAadPrefix = "TERMLINK-TERMINAL-SNAPSHOT-OBJECT-V1\0"u8.ToArray();
    private static readonly byte[] TranscriptAadPrefix = "TERMLINK-TERMINAL-TRANSCRIPT-OBJECT-V1\0"u8.ToArray();

    public static string SnapshotKeyId(Guid recipientDeviceId, long keyEpoch) =>
        KeyId("snapshot-v1:", recipientDeviceId, keyEpoch);

    public static string TranscriptKeyId(Guid recipientDeviceId, long keyEpoch) =>
        KeyId("transcript-v1:", recipientDeviceId, keyEpoch);

    public static byte[] SealSnapshot(
        byte[] pairwiseKey,
        Guid sessionId,
        Guid pcDeviceId,
        Guid recipientDeviceId,
        long keyEpoch,
        TerminalSnapshotV1 snapshot,
        byte[] nonce = null)
    {
        ValidatePairwiseAndIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch);
        ValidateSnapshot(snapshot);
        var plaintext = snapshot.ToByteArray();
        var nonceBytes = CopyNonce(nonce);
        try
        {
            if (plaintext.Length > MaxSnapshotPlaintextBytes)
                throw new InvalidDataException("Snapshot plaintext exceeds the Phase 1 bound.");
            var keyId = SnapshotKeyId(recipientDeviceId, keyEpoch);
            var container = new TerminalSnapshotCiphertextV1
            {
                ContainerVersion = ContainerVersion,
                SessionId = ByteString.CopyFrom(UuidBytes(sessionId)),
                PcDeviceId = ByteString.CopyFrom(UuidBytes(pcDeviceId)),
                RecipientDeviceId = ByteString.CopyFrom(UuidBytes(recipientDeviceId)),
                KeyEpoch = keyEpoch,
                KeyId = keyId,
                SnapshotSeq = snapshot.SnapshotSeq,
                Columns = snapshot.Columns,
                Rows = snapshot.Rows,
                FormatName = SnapshotFormatName,
                FormatVersion = FormatVersion,
                Compression = SnapshotCompressionV1.None,
                UncompressedBytes = checked((ulong)plaintext.Length),
                Nonce = ByteString.CopyFrom(nonceBytes)
            };
            var key = DeriveSnapshotKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyId);
            var aad = SnapshotAssociatedData(container);
            try
            {
                container.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, nonceBytes, aad, plaintext));
                var result = container.ToByteArray();
                if (result.Length > MaxSnapshotObjectBytes)
                    throw new InvalidDataException("Sealed snapshot exceeds the Phase 1 bound.");
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(aad);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    public static TerminalSnapshotV1 OpenSnapshot(
        byte[] pairwiseKey,
        byte[] sealedObject,
        Guid expectedSessionId,
        Guid expectedPcDeviceId,
        Guid expectedRecipientDeviceId,
        long expectedKeyEpoch,
        string expectedKeyId,
        ulong expectedSnapshotSequence,
        uint expectedColumns,
        uint expectedRows)
    {
        ValidatePairwiseAndIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch);
        if (sealedObject is null || sealedObject.Length == 0 || sealedObject.Length > MaxSnapshotObjectBytes)
            throw new InvalidDataException("Sealed snapshot size is invalid.");
        var container = ParseSnapshotContainer(sealedObject);
        ValidateSnapshotContainer(container, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId,
            expectedKeyEpoch, expectedKeyId, expectedSnapshotSequence, expectedColumns, expectedRows);
        var key = DeriveSnapshotKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedKeyId);
        var aad = SnapshotAssociatedData(container);
        byte[] plaintext = null;
        try
        {
            plaintext = SessionCrypto.Decrypt(key, container.Nonce.ToByteArray(), aad, container.Ciphertext.ToByteArray());
            if ((ulong)plaintext.Length != container.UncompressedBytes)
                throw new InvalidDataException("Snapshot plaintext size does not match authenticated metadata.");
            TerminalSnapshotV1 snapshot;
            try { snapshot = TerminalSnapshotV1.Parser.ParseFrom(plaintext); }
            catch (InvalidProtocolBufferException error) { throw new InvalidDataException("Snapshot plaintext is invalid protobuf.", error); }
            ValidateSnapshot(snapshot);
            if (snapshot.SnapshotSeq != container.SnapshotSeq || snapshot.Columns != container.Columns || snapshot.Rows != container.Rows)
                throw new InvalidDataException("Snapshot plaintext does not match authenticated metadata.");
            return NormalizeSnapshotForImport(snapshot);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static byte[] SealTranscript(
        byte[] pairwiseKey,
        Guid sessionId,
        Guid pcDeviceId,
        Guid recipientDeviceId,
        long keyEpoch,
        TerminalTranscriptPageV1 page,
        ulong exclusiveBeforeLineId,
        byte[] nonce = null)
    {
        ValidatePairwiseAndIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch);
        ValidateTranscriptForRequest(page, page.AsOfSnapshotSeq, exclusiveBeforeLineId);
        var plaintext = page.ToByteArray();
        var nonceBytes = CopyNonce(nonce);
        try
        {
            if (plaintext.Length > MaxTranscriptPlaintextBytes)
                throw new InvalidDataException("Transcript plaintext exceeds the Phase 1 bound.");
            var keyId = TranscriptKeyId(recipientDeviceId, keyEpoch);
            var container = new TerminalTranscriptCiphertextV1
            {
                ContainerVersion = ContainerVersion,
                SessionId = ByteString.CopyFrom(UuidBytes(sessionId)),
                PcDeviceId = ByteString.CopyFrom(UuidBytes(pcDeviceId)),
                RecipientDeviceId = ByteString.CopyFrom(UuidBytes(recipientDeviceId)),
                KeyEpoch = keyEpoch,
                KeyId = keyId,
                AsOfSnapshotSeq = page.AsOfSnapshotSeq,
                FirstLineId = page.FirstLineId,
                LastLineId = page.LastLineId,
                FormatName = TranscriptFormatName,
                FormatVersion = FormatVersion,
                Compression = SnapshotCompressionV1.None,
                UncompressedBytes = checked((ulong)plaintext.Length),
                Nonce = ByteString.CopyFrom(nonceBytes)
            };
            var key = DeriveTranscriptKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyId);
            var aad = TranscriptAssociatedData(container);
            try
            {
                container.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, nonceBytes, aad, plaintext));
                var result = container.ToByteArray();
                if (result.Length > MaxTranscriptObjectBytes)
                    throw new InvalidDataException("Sealed transcript exceeds the Phase 1 bound.");
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(aad);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonceBytes);
        }
    }

    public static TerminalTranscriptPageV1 OpenTranscript(
        byte[] pairwiseKey,
        byte[] sealedObject,
        Guid expectedSessionId,
        Guid expectedPcDeviceId,
        Guid expectedRecipientDeviceId,
        long expectedKeyEpoch,
        string expectedKeyId,
        ulong expectedAsOfSnapshotSequence,
        ulong expectedFirstLineId,
        ulong expectedLastLineId,
        ulong expectedExclusiveBeforeLineId)
    {
        ValidatePairwiseAndIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch);
        if (sealedObject is null || sealedObject.Length == 0 || sealedObject.Length > MaxTranscriptObjectBytes)
            throw new InvalidDataException("Sealed transcript size is invalid.");
        var container = ParseTranscriptContainer(sealedObject);
        ValidateTranscriptContainer(container, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId,
            expectedKeyEpoch, expectedKeyId, expectedAsOfSnapshotSequence, expectedFirstLineId, expectedLastLineId);
        var key = DeriveTranscriptKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedKeyId);
        var aad = TranscriptAssociatedData(container);
        byte[] plaintext = null;
        try
        {
            plaintext = SessionCrypto.Decrypt(key, container.Nonce.ToByteArray(), aad, container.Ciphertext.ToByteArray());
            if ((ulong)plaintext.Length != container.UncompressedBytes)
                throw new InvalidDataException("Transcript plaintext size does not match authenticated metadata.");
            TerminalTranscriptPageV1 page;
            try { page = TerminalTranscriptPageV1.Parser.ParseFrom(plaintext); }
            catch (InvalidProtocolBufferException error) { throw new InvalidDataException("Transcript plaintext is invalid protobuf.", error); }
            ValidateTranscriptForRequest(page, expectedAsOfSnapshotSequence, expectedExclusiveBeforeLineId);
            if (page.AsOfSnapshotSeq != container.AsOfSnapshotSeq || page.FirstLineId != container.FirstLineId ||
                page.LastLineId != container.LastLineId)
                throw new InvalidDataException("Transcript plaintext does not match authenticated metadata.");
            return page;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static byte[] DeriveSnapshotKey(byte[] pairwiseKey, Guid sessionId, Guid recipientDeviceId, long keyEpoch, string keyId) =>
        DeriveObjectKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyId, SnapshotKeyPrefix, SnapshotKeyId(recipientDeviceId, keyEpoch));

    public static byte[] DeriveTranscriptKey(byte[] pairwiseKey, Guid sessionId, Guid recipientDeviceId, long keyEpoch, string keyId) =>
        DeriveObjectKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyId, TranscriptKeyPrefix, TranscriptKeyId(recipientDeviceId, keyEpoch));

    public static byte[] SnapshotAssociatedData(TerminalSnapshotCiphertextV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCommonContainerMetadata(value.SessionId, value.PcDeviceId, value.RecipientDeviceId, value.KeyEpoch,
            value.KeyId, value.Nonce, value.ContainerVersion, value.FormatVersion, value.FormatName,
            value.Compression, value.UncompressedBytes, MaxSnapshotPlaintextBytes, SnapshotFormatName);
        if (value.SnapshotSeq is 0 or > MaxSupportedUInt64)
            throw new InvalidDataException("Snapshot sequence exceeds the supported signed range.");
        using var stream = new MemoryStream();
        stream.Write(SnapshotAadPrefix);
        WriteUInt32(stream, value.ContainerVersion);
        stream.Write(value.SessionId.Span);
        stream.Write(value.PcDeviceId.Span);
        stream.Write(value.RecipientDeviceId.Span);
        WriteInt64(stream, value.KeyEpoch);
        WriteString(stream, value.KeyId, "snapshot key ID");
        WriteUInt64(stream, value.SnapshotSeq);
        WriteUInt32(stream, value.Columns);
        WriteUInt32(stream, value.Rows);
        WriteString(stream, value.FormatName, "snapshot format name");
        WriteUInt32(stream, value.FormatVersion);
        WriteUInt32(stream, checked((uint)value.Compression));
        WriteUInt64(stream, value.UncompressedBytes);
        stream.Write(value.Nonce.Span);
        return stream.ToArray();
    }

    public static byte[] TranscriptAssociatedData(TerminalTranscriptCiphertextV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCommonContainerMetadata(value.SessionId, value.PcDeviceId, value.RecipientDeviceId, value.KeyEpoch,
            value.KeyId, value.Nonce, value.ContainerVersion, value.FormatVersion, value.FormatName,
            value.Compression, value.UncompressedBytes, MaxTranscriptPlaintextBytes, TranscriptFormatName);
        if (value.AsOfSnapshotSeq is 0 or > MaxSupportedUInt64 || value.FirstLineId is 0 or > MaxSupportedUInt64 ||
            value.LastLineId < value.FirstLineId || value.LastLineId > MaxSupportedUInt64)
            throw new InvalidDataException("Transcript sequence metadata exceeds the supported signed range.");
        using var stream = new MemoryStream();
        stream.Write(TranscriptAadPrefix);
        WriteUInt32(stream, value.ContainerVersion);
        stream.Write(value.SessionId.Span);
        stream.Write(value.PcDeviceId.Span);
        stream.Write(value.RecipientDeviceId.Span);
        WriteInt64(stream, value.KeyEpoch);
        WriteString(stream, value.KeyId, "transcript key ID");
        WriteUInt64(stream, value.AsOfSnapshotSeq);
        WriteUInt64(stream, value.FirstLineId);
        WriteUInt64(stream, value.LastLineId);
        WriteString(stream, value.FormatName, "transcript format name");
        WriteUInt32(stream, value.FormatVersion);
        WriteUInt32(stream, checked((uint)value.Compression));
        WriteUInt64(stream, value.UncompressedBytes);
        stream.Write(value.Nonce.Span);
        return stream.ToArray();
    }

    public static void ValidateSnapshot(TerminalSnapshotV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.FormatVersion != FormatVersion || value.SnapshotSeq is 0 or > MaxSupportedUInt64 ||
            value.Columns is < 1 or > MaxColumns || value.Rows is < 1 or > MaxRows ||
            value.ActiveScreen is not (TerminalScreenKindV1.Primary or TerminalScreenKindV1.Alternate) ||
            value.ParserState != SnapshotParserStateV1.Ground || value.PrimaryScreen is null ||
            value.AlternateScreen is null || value.Modes is null || value.Palette is null || value.Projection is null)
            throw new InvalidDataException("Snapshot root metadata is invalid or unsupported.");
        if (value.ScrollbackRows.Count != 0 || value.ScrollbackFirstLineId != 0)
            throw new InvalidDataException("Phase 1 snapshot scrollback and its sentinel must be empty.");
        ValidateStyles(value.StyleTable, value.HyperlinkTable);
        ValidatePalette(value.Palette);
        uint previousTab = 0;
        for (var index = 0; index < value.TabStops.Count; index++)
        {
            var tab = value.TabStops[index];
            if (tab >= value.Columns || index > 0 && tab <= previousTab)
                throw new InvalidDataException("Snapshot tab stops are invalid.");
            previousTab = tab;
        }
        ValidateModes(value.Modes);
        var cells = ValidateScreen(value.PrimaryScreen, value.Columns, value.Rows, value.SnapshotSeq,
            value.StyleTable.Count, value.HyperlinkTable.Count);
        cells += ValidateScreen(value.AlternateScreen, value.Columns, value.Rows, value.SnapshotSeq,
            value.StyleTable.Count, value.HyperlinkTable.Count);
        ulong previousPrimaryLineId = 0;
        foreach (var row in value.PrimaryScreen.VisibleRows)
        {
            if (row.LineId == 0 || row.LineId <= previousPrimaryLineId || row.LineId > MaxSupportedUInt64)
                throw new InvalidDataException("Primary line IDs must be positive and strictly increasing.");
            previousPrimaryLineId = row.LineId;
        }
        if (previousPrimaryLineId == MaxSupportedUInt64 || value.NextTranscriptLineId != previousPrimaryLineId + 1)
            throw new InvalidDataException("Snapshot next transcript line ID is inconsistent with the primary screen.");
        foreach (var row in value.AlternateScreen.VisibleRows)
            if (row.LineId > MaxSupportedUInt64)
                throw new InvalidDataException("Alternate line ID exceeds the supported signed range.");
        if (cells > MaxExpandedSnapshotCells)
            throw new InvalidDataException("Snapshot expanded cell count exceeds the Phase 1 bound.");
        if (Utf8Length(value.Title, "snapshot title") > MaxTitleBytes)
            throw new InvalidDataException("Snapshot title exceeds the Phase 1 bound.");
        ValidateProjection(value.Projection);
    }

    // Existing V1 objects may carry ignored alternate IDs; new producers emit zero.
    public static void ValidateSnapshotForProducer(TerminalSnapshotV1 value)
    {
        ValidateSnapshot(value);
        foreach (var row in value.AlternateScreen.VisibleRows)
            if (row.LineId != 0)
                throw new InvalidDataException("Phase 1 producers must emit zero alternate line IDs.");
    }

    public static TerminalSnapshotV1 NormalizeSnapshotForImport(TerminalSnapshotV1 value)
    {
        ValidateSnapshot(value);
        if (value.AlternateScreen.VisibleRows.All(row => row.LineId == 0)) return value;
        var normalized = value.Clone();
        foreach (var row in normalized.AlternateScreen.VisibleRows) row.LineId = 0;
        return normalized;
    }

    public static ulong InitialTranscriptBeforeLineId(TerminalSnapshotV1 value)
    {
        ValidateSnapshot(value);
        return value.PrimaryScreen.VisibleRows[0].LineId;
    }

    public static void ValidateInitialTranscriptPage(TerminalSnapshotV1 snapshot, TerminalTranscriptPageV1 page) =>
        ValidateTranscriptForRequest(page, snapshot.SnapshotSeq, InitialTranscriptBeforeLineId(snapshot));

    public static void ValidateTranscript(TerminalTranscriptPageV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.FormatVersion != FormatVersion || value.AsOfSnapshotSeq is 0 or > MaxSupportedUInt64 ||
            value.Rows.Count is < 1 or > MaxTranscriptRows || value.Palette is null)
            throw new InvalidDataException("Transcript root metadata is invalid.");
        ValidateStyles(value.StyleTable, value.HyperlinkTable);
        ValidatePalette(value.Palette);
        if (value.FirstLineId is 0 or > MaxSupportedUInt64 || value.LastLineId < value.FirstLineId ||
            value.LastLineId > MaxSupportedUInt64 ||
            value.Rows[0].LineId != value.FirstLineId || value.Rows[^1].LineId != value.LastLineId ||
            value.HasMoreBefore && value.NextBeforeLineId != value.FirstLineId ||
            !value.HasMoreBefore && value.NextBeforeLineId != 0)
            throw new InvalidDataException("Transcript cursor metadata is invalid.");
        ulong previousLine = 0;
        long totalCells = 0;
        foreach (var row in value.Rows)
        {
            if (row.LineId == 0 || row.LineId <= previousLine || row.LineId > MaxSupportedUInt64 ||
                row.SealedAtSeq == 0 || row.SealedAtSeq > value.AsOfSnapshotSeq)
                throw new InvalidDataException("Transcript row sequence metadata is invalid.");
            var rowCells = ValidateRow(row, null, value.StyleTable.Count);
            if (rowCells is < 1 or > MaxColumns)
                throw new InvalidDataException("Transcript row width is invalid.");
            totalCells += rowCells;
            previousLine = row.LineId;
        }
        if (totalCells > MaxTranscriptRows * MaxColumns)
            throw new InvalidDataException("Transcript expanded cell count exceeds the Phase 1 bound.");
    }

    public static void ValidateTranscriptForRequest(
        TerminalTranscriptPageV1 value,
        ulong expectedAsOfSnapshotSequence,
        ulong exclusiveBeforeLineId)
    {
        ValidateTranscript(value);
        if (expectedAsOfSnapshotSequence is 0 or > MaxSupportedUInt64 ||
            exclusiveBeforeLineId is 0 or > MaxSupportedUInt64 ||
            value.AsOfSnapshotSeq != expectedAsOfSnapshotSequence || value.LastLineId >= exclusiveBeforeLineId)
            throw new InvalidDataException("Transcript page does not match its fixed snapshot anchor and exclusive cursor.");
    }

    private static TerminalSnapshotCiphertextV1 ParseSnapshotContainer(byte[] value)
    {
        try { return TerminalSnapshotCiphertextV1.Parser.ParseFrom(value); }
        catch (InvalidProtocolBufferException error) { throw new InvalidDataException("Snapshot container is invalid protobuf.", error); }
    }

    private static TerminalTranscriptCiphertextV1 ParseTranscriptContainer(byte[] value)
    {
        try { return TerminalTranscriptCiphertextV1.Parser.ParseFrom(value); }
        catch (InvalidProtocolBufferException error) { throw new InvalidDataException("Transcript container is invalid protobuf.", error); }
    }

    private static void ValidateSnapshotContainer(TerminalSnapshotCiphertextV1 value, Guid sessionId, Guid pcDeviceId,
        Guid recipientDeviceId, long keyEpoch, string keyId, ulong sequence, uint columns, uint rows)
    {
        ValidateCommonContainerMetadata(value.SessionId, value.PcDeviceId, value.RecipientDeviceId, value.KeyEpoch,
            value.KeyId, value.Nonce, value.ContainerVersion, value.FormatVersion, value.FormatName,
            value.Compression, value.UncompressedBytes, MaxSnapshotPlaintextBytes, SnapshotFormatName);
        if (!value.SessionId.Span.SequenceEqual(UuidBytes(sessionId)) || !value.PcDeviceId.Span.SequenceEqual(UuidBytes(pcDeviceId)) ||
            !value.RecipientDeviceId.Span.SequenceEqual(UuidBytes(recipientDeviceId)) || value.KeyEpoch != keyEpoch ||
            value.KeyId != keyId || value.KeyId != SnapshotKeyId(recipientDeviceId, keyEpoch) || value.SnapshotSeq != sequence ||
            value.SnapshotSeq is 0 or > MaxSupportedUInt64 || sequence is 0 or > MaxSupportedUInt64 ||
            value.Columns != columns || value.Rows != rows || columns is < 1 or > MaxColumns ||
            rows is < 1 or > MaxRows || value.Ciphertext.Length < 16 || value.Ciphertext.Length > MaxSnapshotObjectBytes)
            throw new InvalidDataException("Snapshot object metadata does not match the requested object.");
    }

    private static void ValidateTranscriptContainer(TerminalTranscriptCiphertextV1 value, Guid sessionId, Guid pcDeviceId,
        Guid recipientDeviceId, long keyEpoch, string keyId, ulong asOf, ulong first, ulong last)
    {
        ValidateCommonContainerMetadata(value.SessionId, value.PcDeviceId, value.RecipientDeviceId, value.KeyEpoch,
            value.KeyId, value.Nonce, value.ContainerVersion, value.FormatVersion, value.FormatName,
            value.Compression, value.UncompressedBytes, MaxTranscriptPlaintextBytes, TranscriptFormatName);
        if (!value.SessionId.Span.SequenceEqual(UuidBytes(sessionId)) || !value.PcDeviceId.Span.SequenceEqual(UuidBytes(pcDeviceId)) ||
            !value.RecipientDeviceId.Span.SequenceEqual(UuidBytes(recipientDeviceId)) || value.KeyEpoch != keyEpoch ||
            value.KeyId != keyId || value.KeyId != TranscriptKeyId(recipientDeviceId, keyEpoch) ||
            value.AsOfSnapshotSeq != asOf || value.FirstLineId != first || value.LastLineId != last ||
            asOf is 0 or > MaxSupportedUInt64 || first is 0 or > MaxSupportedUInt64 ||
            last < first || last > MaxSupportedUInt64 || value.Ciphertext.Length < 16 ||
            value.Ciphertext.Length > MaxTranscriptObjectBytes)
            throw new InvalidDataException("Transcript object metadata does not match the requested object.");
    }

    private static void ValidateCommonContainerMetadata(ByteString sessionId, ByteString pcDeviceId, ByteString recipientDeviceId,
        long keyEpoch, string keyId, ByteString nonce, uint containerVersion, uint formatVersion, string formatName,
        SnapshotCompressionV1 compression, ulong uncompressedBytes, int maxPlaintextBytes, string expectedFormatName)
    {
        if (containerVersion != ContainerVersion || sessionId.Length != 16 || pcDeviceId.Length != 16 ||
            recipientDeviceId.Length != 16 || keyEpoch < 1 || Utf8Length(keyId, "object key ID") is < 8 or > 128 ||
            nonce.Length != 12 || formatVersion != FormatVersion || formatName != expectedFormatName ||
            compression != SnapshotCompressionV1.None || uncompressedBytes == 0 || uncompressedBytes > (ulong)maxPlaintextBytes)
            throw new InvalidDataException("Snapshot object cryptographic metadata is invalid or unsupported.");
    }

    private static long ValidateScreen(TerminalScreenV1 screen, uint columns, uint rows, ulong snapshotSequence,
        int styleCount, int hyperlinkCount)
    {
        if ((uint)screen.VisibleRows.Count != rows || screen.Cursor is null || screen.SavedCursor is null || screen.Charset is null ||
            screen.ScrollTop > screen.ScrollBottom || screen.ScrollBottom >= rows || screen.MarginLeft > screen.MarginRight ||
            screen.MarginRight >= columns || screen.CurrentStyleIndex >= (uint)styleCount || screen.PendingWrap != screen.Cursor.PendingWrap)
            throw new InvalidDataException("Snapshot screen metadata is invalid.");
        ValidateCursor(screen.Cursor, columns, rows, styleCount);
        ValidateCursor(screen.SavedCursor, columns, rows, styleCount);
        ValidateCharset(screen.Charset);
        if (Utf8Length(screen.LastPrintedGrapheme, "last printed grapheme") > MaxGraphemeBytes)
            throw new InvalidDataException("Last printed grapheme exceeds the Phase 1 bound.");
        long cells = 0;
        foreach (var row in screen.VisibleRows)
        {
            if (row.SealedAtSeq > snapshotSequence)
                throw new InvalidDataException("Snapshot row sequence exceeds snapshot_seq.");
            cells += ValidateRow(row, columns, styleCount);
        }
        return cells;
    }

    private static int ValidateRow(TerminalRowV1 row, uint? expectedCells, int styleCount)
    {
        if (row is null || row.Runs.Count == 0) throw new InvalidDataException("Terminal row has no cells.");
        var expanded = 0;
        var expectsWideContinuation = false;
        uint wideLeadStyleIndex = 0;
        foreach (var run in row.Runs)
        {
            if (run.Repeat == 0 || run.Repeat > (uint)MaxColumns || run.StyleIndex >= (uint)styleCount)
                throw new InvalidDataException("Terminal cell run metadata is invalid.");
            if (Utf8Length(run.Grapheme, "terminal grapheme") > MaxGraphemeBytes)
                throw new InvalidDataException("Terminal grapheme exceeds the Phase 1 bound.");
            if (run.WideContinuation)
            {
                if (!expectsWideContinuation || run.Repeat != 1 || run.DisplayWidth != 0 || run.Grapheme.Length != 0 ||
                    run.StyleIndex != wideLeadStyleIndex)
                    throw new InvalidDataException("Wide-cell continuation is invalid.");
                expectsWideContinuation = false;
                wideLeadStyleIndex = 0;
            }
            else
            {
                if (expectsWideContinuation || run.DisplayWidth is not (1 or 2) ||
                    (run.Grapheme.Length > 0 && run.Repeat != 1) || (run.DisplayWidth == 2 && run.Grapheme.Length == 0))
                    throw new InvalidDataException("Terminal cell run width is invalid.");
                expectsWideContinuation = run.DisplayWidth == 2;
                if (expectsWideContinuation) wideLeadStyleIndex = run.StyleIndex;
            }
            expanded = checked(expanded + (int)run.Repeat);
            if (expanded > MaxColumns) throw new InvalidDataException("Terminal row exceeds the Phase 1 width bound.");
        }
        if (expectsWideContinuation || (expectedCells.HasValue && (uint)expanded != expectedCells.Value))
            throw new InvalidDataException("Terminal row does not fill the declared width.");
        return expanded;
    }

    private static void ValidateCursor(TerminalCursorStateV1 cursor, uint columns, uint rows, int styleCount)
    {
        if (cursor.Column >= columns || cursor.Row >= rows ||
            cursor.Style is not (TerminalCursorStyleV1.Block or TerminalCursorStyleV1.Underline or TerminalCursorStyleV1.Bar) ||
            cursor.RenditionStyleIndex >= (uint)styleCount || cursor.Charset is null)
            throw new InvalidDataException("Terminal cursor metadata is invalid.");
        ValidateCharset(cursor.Charset);
    }

    private static void ValidateCharset(TerminalCharsetStateV1 charset)
    {
        if (!IsSupportedCharset(charset.G0) || !IsSupportedCharset(charset.G1) ||
            !IsSupportedCharset(charset.G2) || !IsSupportedCharset(charset.G3) ||
            charset.Gl > 3 || charset.Gr > 3)
            throw new InvalidDataException("Terminal charset state is invalid or unsupported.");
    }

    private static void ValidateModes(TerminalModesV1 modes)
    {
        if (modes.MouseTracking is not (TerminalMouseTrackingV1.None or TerminalMouseTrackingV1.X10 or
                TerminalMouseTrackingV1.Normal or TerminalMouseTrackingV1.ButtonEvent or TerminalMouseTrackingV1.AnyEvent) ||
            modes.MouseEncoding is not (TerminalMouseEncodingV1.Default or TerminalMouseEncodingV1.Utf8 or
                TerminalMouseEncodingV1.Sgr or TerminalMouseEncodingV1.Urxvt or TerminalMouseEncodingV1.SgrPixels))
            throw new InvalidDataException("Terminal modes are invalid or unsupported.");
    }

    private static void ValidateProjection(TerminalSessionProjectionV1 projection)
    {
        if (projection.Lifecycle is not (TerminalLifecycleV1.Running or TerminalLifecycleV1.Interrupted or TerminalLifecycleV1.Ended) ||
            projection.Lifecycle == TerminalLifecycleV1.Ended != projection.HasExitCode ||
            projection.ObservedLocalInputEpoch > MaxSupportedUInt64)
            throw new InvalidDataException("Terminal session projection is invalid.");
    }

    private static void ValidateStyles(IList<TerminalStyleV1> styles, IList<TerminalHyperlinkV1> hyperlinks)
    {
        if (styles.Count is < 1 or > MaxStyles || hyperlinks.Count > MaxHyperlinks)
            throw new InvalidDataException("Terminal style table size is invalid.");
        foreach (var hyperlink in hyperlinks)
        {
            if (Utf8Length(hyperlink.Id, "hyperlink ID") > MaxHyperlinkIdBytes ||
                Utf8Length(hyperlink.Uri, "hyperlink URI") is < 1 or > MaxHyperlinkUriBytes ||
                Utf8Length(hyperlink.Params, "hyperlink parameters") > MaxHyperlinkParamsBytes)
                throw new InvalidDataException("Terminal hyperlink exceeds the Phase 1 bound.");
        }
        foreach (var style in styles)
        {
            if (style is null || style.Foreground is null || style.Background is null || style.UnderlineColor is null ||
                style.UnderlineStyle is not (TerminalUnderlineStyleV1.None or TerminalUnderlineStyleV1.Single or
                    TerminalUnderlineStyleV1.Double or TerminalUnderlineStyleV1.Curly or TerminalUnderlineStyleV1.Dotted or
                    TerminalUnderlineStyleV1.Dashed) || (style.Attributes & ~SupportedStyleAttributes) != 0 ||
                style.HyperlinkIndexPlusOne > (uint)hyperlinks.Count)
                throw new InvalidDataException("Terminal style metadata is invalid.");
            ValidateColor(style.Foreground);
            ValidateColor(style.Background);
            ValidateColor(style.UnderlineColor);
        }
    }

    private static void ValidateColor(TerminalColorRefV1 color)
    {
        var valid = color.ValueCase switch
        {
            TerminalColorRefV1.ValueOneofCase.DefaultColor => color.DefaultColor,
            TerminalColorRefV1.ValueOneofCase.PaletteIndex => color.PaletteIndex <= 255,
            TerminalColorRefV1.ValueOneofCase.Rgb => color.Rgb <= 0x00ff_ffff,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Terminal color reference is invalid.");
    }

    private static void ValidatePalette(TerminalPaletteV1 palette)
    {
        if (palette.ColorsRgb.Count != 256 || palette.DefaultForegroundRgb > 0x00ff_ffff ||
            palette.DefaultBackgroundRgb > 0x00ff_ffff || palette.DefaultCursorRgb > 0x00ff_ffff)
            throw new InvalidDataException("Terminal palette is invalid.");
        foreach (var color in palette.ColorsRgb)
            if (color > 0x00ff_ffff) throw new InvalidDataException("Terminal palette color is invalid.");
    }

    private static bool IsSupportedCharset(TerminalCharsetV1 value) =>
        value is TerminalCharsetV1.Ascii or TerminalCharsetV1.DecSpecialGraphics or TerminalCharsetV1.Uk or TerminalCharsetV1.Latin1;

    private static byte[] DeriveObjectKey(byte[] pairwiseKey, Guid sessionId, Guid recipientDeviceId, long keyEpoch,
        string keyId, byte[] prefix, string expectedKeyId)
    {
        if (pairwiseKey is null || pairwiseKey.Length != 32) throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
        if (sessionId == Guid.Empty || recipientDeviceId == Guid.Empty || keyEpoch < 1 || keyId != expectedKeyId)
            throw new InvalidDataException("Snapshot key metadata is invalid.");
        var keyIdBytes = StrictUtf8.GetBytes(keyId);
        if (keyIdBytes.Length is < 8 or > 128) throw new InvalidDataException("Snapshot key ID length is invalid.");
        using var info = new MemoryStream();
        info.Write(prefix);
        info.Write(UuidBytes(recipientDeviceId));
        WriteInt64(info, keyEpoch);
        WriteUInt16(info, checked((ushort)keyIdBytes.Length));
        info.Write(keyIdBytes);
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(pairwiseKey, UuidBytes(sessionId), info.ToArray()));
        var result = new byte[32];
        generator.GenerateBytes(result, 0, result.Length);
        CryptographicOperations.ZeroMemory(keyIdBytes);
        return result;
    }

    private static void ValidatePairwiseAndIdentity(byte[] pairwiseKey, Guid sessionId, Guid pcDeviceId,
        Guid recipientDeviceId, long keyEpoch)
    {
        if (pairwiseKey is null || pairwiseKey.Length != 32) throw new ArgumentException("Pairwise key must be 32 bytes.", nameof(pairwiseKey));
        if (sessionId == Guid.Empty || pcDeviceId == Guid.Empty || recipientDeviceId == Guid.Empty ||
            pcDeviceId == recipientDeviceId || keyEpoch < 1)
            throw new InvalidDataException("Snapshot identity metadata is invalid.");
    }

    private static string KeyId(string prefix, Guid recipientDeviceId, long keyEpoch)
    {
        if (recipientDeviceId == Guid.Empty) throw new ArgumentException("Recipient device ID is required.", nameof(recipientDeviceId));
        if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
        return prefix + recipientDeviceId.ToString("N") + ":" + keyEpoch.ToString("D8");
    }

    private static byte[] CopyNonce(byte[] nonce)
    {
        var value = nonce is null ? RandomNumberGenerator.GetBytes(12) : (byte[])nonce.Clone();
        if (value.Length != 12) throw new ArgumentException("Snapshot nonce must be 12 bytes.", nameof(nonce));
        return value;
    }

    private static int Utf8Length(string value, string label)
    {
        try { return StrictUtf8.GetByteCount(value ?? string.Empty); }
        catch (EncoderFallbackException error) { throw new InvalidDataException(label + " is not valid Unicode.", error); }
    }

    private static void WriteString(Stream stream, string value, string label)
    {
        byte[] encoded;
        try { encoded = StrictUtf8.GetBytes(value ?? string.Empty); }
        catch (EncoderFallbackException error) { throw new InvalidDataException(label + " is not valid Unicode.", error); }
        if (encoded.Length > ushort.MaxValue) throw new InvalidDataException(label + " is too long.");
        WriteUInt16(stream, checked((ushort)encoded.Length));
        stream.Write(encoded);
        CryptographicOperations.ZeroMemory(encoded);
    }

    private static byte[] UuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
    private static void WriteUInt16(Stream output, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); output.Write(bytes); }
    private static void WriteUInt32(Stream output, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); output.Write(bytes); }
    private static void WriteUInt64(Stream output, ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); output.Write(bytes); }
    private static void WriteInt64(Stream output, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); output.Write(bytes); }
}
