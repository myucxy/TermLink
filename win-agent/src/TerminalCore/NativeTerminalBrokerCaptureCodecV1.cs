using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace TermLink.TerminalCore
{
    /// <summary>Owns the decoded path-free captures for one Output.</summary>
    public sealed class NativeTerminalBrokerCaptureGroupV1 : IDisposable
    {
        private readonly NativeTerminalBrokerRecordV1[] records;
        private readonly ReadOnlyCollection<NativeTerminalBrokerRecordV1> view;
        private bool disposed;

        internal NativeTerminalBrokerCaptureGroupV1(ulong sourceSequence,
            NativeTerminalBrokerRecordV1[] records)
        {
            SourceSequence = sourceSequence;
            this.records = records;
            view = Array.AsReadOnly(records);
        }

        public ulong SourceSequence { get; }

        public IReadOnlyList<NativeTerminalBrokerRecordV1> Records
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalBrokerCaptureGroupV1));
                return view;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var record in records) record.Dispose();
        }
    }

    /// <summary>TLCAPV1 path-free, little-endian capture serialization.</summary>
    public static class NativeTerminalBrokerCaptureCodecV1
    {
        private static readonly byte[] Magic = { (byte)'T', (byte)'L', (byte)'C', (byte)'A',
            (byte)'P', (byte)'V', (byte)'1', 0 };
        private const int HeaderBytes = 32;
        private const int RecordHeaderBytes = 104;

        public static byte[] Encode(ulong sourceSequence,
            IReadOnlyList<NativeTerminalBrokerRecordV1> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (sourceSequence == 0 || sourceSequence >= (ulong)long.MaxValue ||
                records.Count == 0 || records.Count > 64)
                throw new ArgumentOutOfRangeException(nameof(sourceSequence));
            var stable = new NativeTerminalBrokerRecordV1[records.Count];
            var total = HeaderBytes;
            var captureTotal = 0;
            for (var index = 0; index < stable.Length; index++)
            {
                stable[index] = records[index] ?? throw new ArgumentException("Null broker record.", nameof(records));
                var length = stable[index].CaptureLength;
                NativeTerminalBroker.ValidateOutcome(stable[index].Outcome, length);
                var identity = stable[index].ExportRedactedIdentityV1();
                try { ValidateRecordIdentity(identity, sourceSequence, index); }
                finally { CryptographicOperations.ZeroMemory(identity); }
                if (length > NativeTerminalBroker.MaximumOutputCaptureBytes - captureTotal ||
                    RecordHeaderBytes + length > NativeTerminalBroker.MaximumOutputCaptureBytes - total)
                    throw new ArgumentOutOfRangeException(nameof(records), "Broker Output exceeds 32 MiB.");
                captureTotal += length;
                total += RecordHeaderBytes + length;
            }
            var output = new byte[total];
            try
            {
                Magic.CopyTo(output, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), HeaderBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), (uint)stable.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(16, 8), sourceSequence);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), (uint)total);
                var cursor = HeaderBytes;
                foreach (var record in stable)
                {
                    var length = record.CaptureLength;
                    record.CopyTo(output.AsSpan(cursor, 96), output.AsSpan(cursor + 104, length));
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 96, 4), record.Outcome);
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 100, 4), (uint)length);
                    cursor += RecordHeaderBytes + length;
                }
                return output;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(output);
                throw;
            }
        }

        public static NativeTerminalBrokerCaptureGroupV1 Decode(byte[] encoded)
        {
            if (encoded == null) throw new ArgumentNullException(nameof(encoded));
            if (encoded.Length < HeaderBytes + RecordHeaderBytes ||
                encoded.Length > NativeTerminalBroker.MaximumOutputCaptureBytes ||
                !encoded.AsSpan(0, 8).SequenceEqual(Magic) ||
                BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(8, 4)) != HeaderBytes ||
                BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(24, 4)) != (uint)encoded.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(28, 4)) != 0)
                throw new ArgumentException("Invalid TLCAPV1 header.", nameof(encoded));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(12, 4));
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(16, 8));
            if (count == 0 || count > 64 || sequence == 0 || sequence >= (ulong)long.MaxValue)
                throw new ArgumentException("Invalid TLCAPV1 header.", nameof(encoded));

            // Scan the complete structure and byte budget before allocating capture copies.
            var cursor = HeaderBytes;
            var captureTotal = 0;
            for (var index = 0; index < (int)count; index++)
            {
                if (encoded.Length - cursor < RecordHeaderBytes)
                    throw new ArgumentException("Truncated TLCAPV1 record.", nameof(encoded));
                ValidateRecordIdentity(encoded.AsSpan(cursor, 96), sequence, index);
                var outcome = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor + 96, 4));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor + 100, 4));
                if (length > NativeTerminalBroker.MaximumCaptureBytes ||
                    length > encoded.Length - cursor - RecordHeaderBytes ||
                    length > NativeTerminalBroker.MaximumOutputCaptureBytes - captureTotal)
                    throw new ArgumentException("Invalid TLCAPV1 capture length.", nameof(encoded));
                NativeTerminalBroker.ValidateOutcome(outcome, (int)length);
                captureTotal += (int)length;
                cursor += RecordHeaderBytes + (int)length;
            }
            if (cursor != encoded.Length)
                throw new ArgumentException("TLCAPV1 contains trailing bytes.", nameof(encoded));

            var records = new NativeTerminalBrokerRecordV1[checked((int)count)];
            cursor = HeaderBytes;
            try
            {
                for (var index = 0; index < records.Length; index++)
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor + 100, 4));
                    records[index] = NativeTerminalBrokerRecordV1.FromScanned(
                        encoded.AsSpan(cursor, 96),
                        BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor + 96, 4)),
                        encoded.AsSpan(cursor + RecordHeaderBytes, length));
                    cursor += RecordHeaderBytes + length;
                }
                return new NativeTerminalBrokerCaptureGroupV1(sequence, records);
            }
            catch
            {
                foreach (var record in records) record?.Dispose();
                throw;
            }
        }

        internal static void ValidateRecordIdentity(ReadOnlySpan<byte> identity,
            ulong sourceSequence, int ordinal)
        {
            NativeTerminalBroker.ValidateRedactedIdentity(identity);
            if (BinaryPrimitives.ReadUInt64LittleEndian(identity.Slice(16, 8)) != sourceSequence ||
                BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(28, 4)) != (uint)ordinal)
                throw new ArgumentException("TLCAPV1 record sequence or ordinal mismatch.");
        }
    }
}
