using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.TerminalCore
{
    public enum NativeTerminalBrokerFailure : uint
    {
        Denied = 1,
        Unavailable = 2,
        UnsupportedMedium = 3,
        OverLimit = 4
    }

    public sealed unsafe class NativeTerminalBrokerRequest
    {
        private readonly byte[] redactedIdentity;

        internal NativeTerminalBrokerRequest(in TlTerminalBrokerRequestV1 identity, string reference)
        {
            Medium = checked((byte)identity.Medium);
            Reference = reference;
            ReadOffset = identity.ReadOffset;
            ReadLength = identity.HasReadLength == 0 ? (uint?)null : identity.ReadLength;
            SourceSequence = identity.SourceSequence;
            SourceOffsetEnd = identity.SourceOffsetEnd;
            Ordinal = identity.Ordinal;
            var digest = new byte[32];
            var identityCopy = identity;
            for (var index = 0; index < digest.Length; index++)
                digest[index] = identityCopy.ControlDigest[index];
            ControlDigest = Array.AsReadOnly(digest);
            redactedIdentity = NativeTerminalBroker.EncodeRedactedIdentity(in identity);
        }

        public byte Medium { get; }
        // Trusted Windows broker input only. Never send this reference to Android or telemetry.
        public string Reference { get; }
        public ulong ReadOffset { get; }
        public uint? ReadLength { get; }
        public ulong SourceSequence { get; }
        public uint SourceOffsetEnd { get; }
        public uint Ordinal { get; }
        public IReadOnlyList<byte> ControlDigest { get; }

        /// <summary>The exact path-free 96-byte parser request identity for durable replay.</summary>
        public byte[] ExportRedactedIdentityV1() => (byte[])redactedIdentity.Clone();
    }

    public sealed class NativeTerminalBrokerCapture : IDisposable
    {
        private readonly object sync = new object();
        private byte[] bytes;
        private bool disposed;

        private NativeTerminalBrokerCapture(byte[] bytes, NativeTerminalBrokerFailure? failure)
        {
            this.bytes = bytes;
            Failure = failure;
        }

        public NativeTerminalBrokerFailure? Failure { get; }

        internal int ByteLength
        {
            get { lock (sync) { ThrowIfDisposed(); return bytes?.Length ?? 0; } }
        }

        internal byte[] CopyBytesForRecord()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return bytes == null ? null : (byte[])bytes.Clone();
            }
        }

        internal static NativeTerminalBrokerCapture FromOwnedBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length > NativeTerminalBroker.MaximumCaptureBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            return new NativeTerminalBrokerCapture(bytes, null);
        }

        internal byte[] ConsumeBytes()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                var captured = bytes;
                bytes = null;
                return captured;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
                bytes = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalBrokerCapture));
        }

        public static NativeTerminalBrokerCapture FromBytes(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > NativeTerminalBroker.MaximumCaptureBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            return new NativeTerminalBrokerCapture((byte[])bytes.Clone(), null);
        }

        public static NativeTerminalBrokerCapture FromFailure(NativeTerminalBrokerFailure failure)
        {
            if (!Enum.IsDefined(typeof(NativeTerminalBrokerFailure), failure))
                throw new ArgumentOutOfRangeException(nameof(failure));
            return new NativeTerminalBrokerCapture(null, failure);
        }
    }

    /// <summary>Owns one path-free capture; Dispose clears its copied identity and bytes.</summary>
    public sealed class NativeTerminalBrokerRecordV1 : IDisposable
    {
        private readonly object sync = new object();
        private byte[] identity;
        private byte[] data;

        private NativeTerminalBrokerRecordV1(byte[] ownedIdentity, uint outcome, byte[] ownedData)
        {
            identity = ownedIdentity;
            Outcome = outcome;
            data = ownedData;
        }

        public uint Outcome { get; }

        public static NativeTerminalBrokerRecordV1 FromStored(byte[] redactedIdentity,
            uint outcome, byte[] captureBytes)
        {
            if (redactedIdentity == null) throw new ArgumentNullException(nameof(redactedIdentity));
            if (captureBytes == null) throw new ArgumentNullException(nameof(captureBytes));
            return FromScanned(redactedIdentity, outcome, captureBytes);
        }

        internal static NativeTerminalBrokerRecordV1 FromScanned(ReadOnlySpan<byte> redactedIdentity,
            uint outcome, ReadOnlySpan<byte> captureBytes)
        {
            NativeTerminalBroker.ValidateRedactedIdentity(redactedIdentity);
            NativeTerminalBroker.ValidateOutcome(outcome, captureBytes.Length);
            var ownedIdentity = redactedIdentity.ToArray();
            try
            {
                return new NativeTerminalBrokerRecordV1(ownedIdentity, outcome,
                    captureBytes.ToArray());
            }
            catch
            {
                CryptographicOperations.ZeroMemory(ownedIdentity);
                throw;
            }
        }

        internal static NativeTerminalBrokerRecordV1 FromCapture(NativeTerminalBrokerRequest request,
            NativeTerminalBrokerCapture capture)
        {
            var outcome = capture.Failure.HasValue ? (uint)capture.Failure.Value : 0;
            var length = capture.ByteLength;
            NativeTerminalBroker.ValidateOutcome(outcome, length);
            var identity = request.ExportRedactedIdentityV1();
            try
            {
                return new NativeTerminalBrokerRecordV1(identity, outcome,
                    capture.CopyBytesForRecord() ?? Array.Empty<byte>());
            }
            catch
            {
                CryptographicOperations.ZeroMemory(identity);
                throw;
            }
        }

        public byte[] ExportRedactedIdentityV1()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return (byte[])identity.Clone();
            }
        }

        public byte[] CopyCaptureBytes()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return (byte[])data.Clone();
            }
        }

        internal int CaptureLength
        {
            get { lock (sync) { ThrowIfDisposed(); return data.Length; } }
        }

        internal void CopyTo(Span<byte> identityDestination, Span<byte> dataDestination)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                identity.AsSpan().CopyTo(identityDestination);
                data.AsSpan().CopyTo(dataDestination);
            }
        }

        internal NativeTerminalBrokerRecordV1 CloneOwned()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                var copiedIdentity = (byte[])identity.Clone();
                try
                {
                    return new NativeTerminalBrokerRecordV1(copiedIdentity, Outcome,
                        (byte[])data.Clone());
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(copiedIdentity);
                    throw;
                }
            }
        }

        internal bool Matches(NativeTerminalBrokerRequest request)
        {
            var actual = request.ExportRedactedIdentityV1();
            try
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    return CryptographicOperations.FixedTimeEquals(identity, actual);
                }
            }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }

        internal NativeTerminalBrokerCapture ToCapture()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return Outcome == 0
                    ? NativeTerminalBrokerCapture.FromOwnedBytes((byte[])data.Clone())
                    : NativeTerminalBrokerCapture.FromFailure((NativeTerminalBrokerFailure)Outcome);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (identity == null) return;
                CryptographicOperations.ZeroMemory(identity);
                CryptographicOperations.ZeroMemory(data);
                identity = null;
                data = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (identity == null) throw new ObjectDisposedException(nameof(NativeTerminalBrokerRecordV1));
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    internal unsafe struct TlTerminalBrokerRequestV1
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint HasRequest;
        [FieldOffset(12)] internal uint Medium;
        [FieldOffset(16)] internal ulong SourceSequence;
        [FieldOffset(24)] internal uint SourceOffsetEnd;
        [FieldOffset(28)] internal uint Ordinal;
        [FieldOffset(32)] internal fixed byte ControlDigest[32];
        [FieldOffset(64)] internal ulong ReadOffset;
        [FieldOffset(72)] internal uint ReadLength;
        [FieldOffset(76)] internal uint HasReadLength;
        [FieldOffset(80)] internal ulong Reserved0;
        [FieldOffset(88)] internal ulong Reserved1;
    }

    internal static class NativeTerminalBroker
    {
        internal const int MaximumCaptureBytes = 16 * 1024 * 1024;
        internal const int MaximumOutputCaptureBytes = 32 * 1024 * 1024;
        internal const int MaximumReferenceBytes = 1024 * 1024;
        internal const int MaximumDiagnosticBytes = 8192;

        internal static TlTerminalBrokerRequestV1 EmptyRequest() => new TlTerminalBrokerRequestV1
        {
            AbiVersion = NativeTerminalCoreV2.AbiVersion,
            StructSize = 96
        };

        internal static unsafe byte[] EncodeRedactedIdentity(in TlTerminalBrokerRequestV1 source)
        {
            var identity = source;
            var bytes = new byte[96];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), identity.AbiVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), identity.StructSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), identity.HasRequest);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), identity.Medium);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16, 8), identity.SourceSequence);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), identity.SourceOffsetEnd);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), identity.Ordinal);
            for (var index = 0; index < 32; index++) bytes[32 + index] = identity.ControlDigest[index];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64, 8), identity.ReadOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72, 4), identity.ReadLength);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), identity.HasReadLength);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80, 8), identity.Reserved0);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88, 8), identity.Reserved1);
            return bytes;
        }

        internal static void ValidateRedactedIdentity(ReadOnlySpan<byte> identity)
        {
            if (identity.Length != 96 ||
                BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(0, 4)) != NativeTerminalCoreV2.AbiVersion ||
                BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(4, 4)) != 96 ||
                BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(8, 4)) != 1)
                throw new ArgumentException("Invalid redacted broker identity.");
            var medium = BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(12, 4));
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(identity.Slice(16, 8));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(24, 4));
            var ordinal = BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(28, 4));
            var readLength = BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(72, 4));
            var hasReadLength = BinaryPrimitives.ReadUInt32LittleEndian(identity.Slice(76, 4));
            if ((medium != (uint)'f' && medium != (uint)'t' && medium != (uint)'s') ||
                sequence == 0 || sequence >= (ulong)long.MaxValue || offset == 0 || ordinal >= 64 ||
                hasReadLength > 1 || readLength > MaximumCaptureBytes ||
                (hasReadLength == 0 && readLength != 0) ||
                BinaryPrimitives.ReadUInt64LittleEndian(identity.Slice(80, 8)) != 0 ||
                BinaryPrimitives.ReadUInt64LittleEndian(identity.Slice(88, 8)) != 0)
                throw new ArgumentException("Invalid redacted broker identity.");
        }

        internal static void ValidateOutcome(uint outcome, int length)
        {
            if (outcome > 4 || length < 0 || length > MaximumCaptureBytes ||
                (outcome != 0 && length != 0))
                throw new ArgumentException("Invalid broker capture outcome or length.");
        }

        internal static byte[] CopyAndFree(ref TlTerminalBuffer buffer, int maximum, bool required)
        {
            try
            {
                if (buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize)
                    throw new NativeTerminalAbiException("Invalid native broker buffer metadata.");
                if (buffer.Data == IntPtr.Zero && buffer.Length == 0 && !required) return null;
                if (buffer.Data == IntPtr.Zero || buffer.Length > (ulong)maximum ||
                    buffer.Length > int.MaxValue)
                    throw new NativeTerminalAbiException("Invalid native broker buffer metadata.");
                var bytes = new byte[checked((int)buffer.Length)];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero)
                {
                    var status = NativeTerminalInterop.TerminalBufferFree(ref buffer);
                    NativeTerminalInterop.ThrowIfFailed(status,
                        nameof(NativeTerminalInterop.TerminalBufferFree));
                }
            }
        }

        internal static string ReadDiagnostic(ref TlTerminalBuffer buffer)
        {
            var bytes = CopyAndFree(ref buffer, MaximumDiagnosticBytes, false);
            if (bytes == null) return null;
            var text = new UTF8Encoding(false, true).GetString(bytes);
            foreach (var character in text)
                if (character != '\n' && character != '\r' && character < ' ') return null;
            return text;
        }
    }

    public sealed partial class NativeTerminal
    {
        /// <summary>
        /// Captures path-free records for a durable Output group. The records are borrowed by
        /// publishDurable and are cleared when this method exits; serialize them in the callback.
        /// The callback must persist, not execute PTY replies or OS effects.
        /// </summary>
        public NativeTerminalHostEffectFrameV2 ApplyOutputWithCapturedBrokerV1(
            ulong sourceSequence, byte[] output,
            Func<NativeTerminalBrokerRequest, NativeTerminalBrokerCapture> resolve,
            Action<NativeTerminalHostEffectFrameV2,
                IReadOnlyList<NativeTerminalBrokerRecordV1>> publishDurable)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            if (publishDurable == null) throw new ArgumentNullException(nameof(publishDurable));
            var records = new List<NativeTerminalBrokerRecordV1>();
            var totalBytes = 0;
            try
            {
                return ApplyOutputWithBrokerV1(sourceSequence, output, request =>
                {
                    if (records.Count == 64)
                        throw new ArgumentOutOfRangeException(nameof(output), "Broker request count exceeds 64.");
                    var capture = resolve(request);
                    if (capture == null) return null;
                    try
                    {
                        var length = capture.ByteLength;
                        if (length > NativeTerminalBroker.MaximumCaptureBytes ||
                            length > NativeTerminalBroker.MaximumOutputCaptureBytes - totalBytes)
                            throw new ArgumentOutOfRangeException(nameof(output), "Broker captures exceed 32 MiB.");
                        var record = NativeTerminalBrokerRecordV1.FromCapture(request, capture);
                        try { records.Add(record); }
                        catch { record.Dispose(); throw; }
                        totalBytes += length;
                        return capture;
                    }
                    catch
                    {
                        capture.Dispose();
                        throw;
                    }
                }, frame => publishDurable(frame, records.AsReadOnly()));
            }
            finally
            {
                foreach (var record in records) record.Dispose();
            }
        }

        /// <summary>
        /// Replays captured bytes only when every stored identity matches the current parser
        /// request in order. The supplied records are borrowed and never disposed by this method.
        /// publishDurable follows the same persist-before-commit rule as the live broker API.
        /// </summary>
        public NativeTerminalHostEffectFrameV2 ApplyOutputWithBrokerReplayV1(
            ulong sourceSequence, byte[] output,
            IReadOnlyList<NativeTerminalBrokerRecordV1> captures,
            Action<NativeTerminalHostEffectFrameV2> publishDurable)
        {
            if (captures == null) throw new ArgumentNullException(nameof(captures));
            if (publishDurable == null) throw new ArgumentNullException(nameof(publishDurable));
            if (sourceSequence == 0 || sourceSequence >= (ulong)long.MaxValue ||
                captures.Count == 0 || captures.Count > 64)
                throw new ArgumentOutOfRangeException(nameof(captures));
            var stable = new NativeTerminalBrokerRecordV1[captures.Count];
            var totalBytes = 0;
            try
            {
                // Validate the complete budget and all identities before copying capture data.
                for (var index = 0; index < stable.Length; index++)
                {
                    var record = captures[index] ?? throw new ArgumentException("Null broker capture.", nameof(captures));
                    var length = record.CaptureLength;
                    NativeTerminalBroker.ValidateOutcome(record.Outcome, length);
                    var identity = record.ExportRedactedIdentityV1();
                    try { NativeTerminalBrokerCaptureCodecV1.ValidateRecordIdentity(identity, sourceSequence, index); }
                    finally { CryptographicOperations.ZeroMemory(identity); }
                    if (length > NativeTerminalBroker.MaximumOutputCaptureBytes - totalBytes)
                        throw new ArgumentOutOfRangeException(nameof(captures), "Broker captures exceed 32 MiB.");
                    totalBytes += length;
                }
                for (var index = 0; index < stable.Length; index++) stable[index] = captures[index].CloneOwned();
                var cursor = 0;
                return ApplyOutputWithBrokerV1(sourceSequence, output, request =>
                {
                    if (cursor >= stable.Length)
                        throw new InvalidOperationException("Broker replay is missing a parser request.");
                    var capture = stable[cursor++];
                    if (!capture.Matches(request))
                        throw new InvalidOperationException("Broker replay request identity mismatch.");
                    return capture.ToCapture();
                }, frame =>
                {
                    if (cursor != stable.Length)
                        throw new InvalidOperationException("Broker replay has extra captures.");
                    publishDurable(frame);
                });
            }
            finally
            {
                foreach (var record in stable) record?.Dispose();
            }
        }

        /// <summary>
        /// Runs one bounded Output with parser-bound Windows captures. Returning a capture
        /// transfers its owned byte copy to this method, which clears it on every exit path.
        /// publishDurable must
        /// persist the replay group and preview frame before native commit. It must not send
        /// PTY replies or perform OS effects; callers perform those only after this method
        /// returns a committed frame. If commit fails, keep the durable group for recovery.
        /// </summary>
        public unsafe NativeTerminalHostEffectFrameV2 ApplyOutputWithBrokerV1(
            ulong sourceSequence, byte[] output,
            Func<NativeTerminalBrokerRequest, NativeTerminalBrokerCapture> resolve,
            Action<NativeTerminalHostEffectFrameV2> publishDurable)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (output.Length == 0 || output.Length > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(output));
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            if (publishDurable == null) throw new ArgumentNullException(nameof(publishDurable));
            lock (sync)
            {
                ThrowIfUnavailable();
                var owner = handle.DangerousGetHandle();
                var originalOwner = owner;
                var transaction = IntPtr.Zero;
                var diagnostic = NativeTerminalInterop.CreateEmptyBuffer();
                NativeTerminalStatus status;
                fixed (byte* outputPointer = output)
                {
                    var terminalEvent = NativeTerminalInterop.CreateOutputEvent(sourceSequence,
                        outputPointer, checked((ulong)output.Length));
                    status = NativeTerminalInterop.TerminalBrokerBeginV1(ref owner,
                        in terminalEvent, ref transaction, ref diagnostic);
                }
                var ownershipTransferred = owner == IntPtr.Zero;
                try
                {
                    if (ownershipTransferred)
                    {
                        handle.SetHandleAsInvalid();
                        handle.Dispose();
                        handle = null;
                        brokerActive = true;
                    }
                    if (status != NativeTerminalStatus.Ok)
                    {
                        if (ownershipTransferred || owner != originalOwner || transaction != IntPtr.Zero)
                            throw new NativeTerminalAbiException("Broker begin changed ownership on failure.");
                        var beginDiagnostic = NativeTerminalBroker.ReadDiagnostic(ref diagnostic);
                        throw new NativeTerminalException(nameof(NativeTerminalInterop.TerminalBrokerBeginV1),
                            status, beginDiagnostic);
                    }
                    if (!ownershipTransferred || transaction == IntPtr.Zero)
                        throw new NativeTerminalAbiException("Broker begin returned inconsistent ownership.");
                    if (NativeTerminalBroker.ReadDiagnostic(ref diagnostic) != null)
                        throw new NativeTerminalAbiException("Broker begin published an unexpected diagnostic.");
                    NativeTerminalHostEffectFrameV2 preview = null;
                    for (var count = 0; count <= 64; count++)
                    {
                        var identity = NativeTerminalBroker.EmptyRequest();
                        var reference = NativeTerminalInterop.CreateEmptyBuffer();
                        status = NativeTerminalInterop.TerminalBrokerRequestV1(transaction,
                            ref identity, ref reference);
                        var referenceBytes = NativeTerminalBroker.CopyAndFree(ref reference,
                            NativeTerminalBroker.MaximumReferenceBytes, identity.HasRequest == 1);
                        NativeTerminalInterop.ThrowIfFailed(status,
                            nameof(NativeTerminalInterop.TerminalBrokerRequestV1));
                        if (identity.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                            identity.StructSize != 96 || identity.HasRequest > 1 ||
                            identity.Reserved0 != 0 || identity.Reserved1 != 0)
                            throw new NativeTerminalAbiException("Invalid native broker request metadata.");
                        if (identity.HasRequest == 0)
                        {
                            if (referenceBytes != null)
                                throw new NativeTerminalAbiException("Ready broker request exposed a reference.");
                            var previewBuffer = NativeTerminalInterop.CreateEmptyBuffer();
                            status = NativeTerminalInterop.TerminalBrokerPreviewV1(transaction,
                                ref previewBuffer);
                            if (status != NativeTerminalStatus.Ok)
                            {
                                _ = NativeTerminalBroker.CopyAndFree(ref previewBuffer,
                                    NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes, false);
                                NativeTerminalInterop.ThrowIfFailed(status,
                                    nameof(NativeTerminalInterop.TerminalBrokerPreviewV1));
                            }
                            var previewBytes = NativeTerminalBroker.CopyAndFree(ref previewBuffer,
                                NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes, true);
                            preview = NativeTerminalHostEffectFrameV2Decoder.Decode(previewBytes);
                            break;
                        }
                        if (count == 64) throw new NativeTerminalAbiException("Broker request count exceeded 64.");
                        if (identity.Medium != (uint)'f' && identity.Medium != (uint)'t' &&
                            identity.Medium != (uint)'s')
                            throw new NativeTerminalAbiException("Broker medium is invalid.");
                        var path = new UTF8Encoding(false, true).GetString(referenceBytes);
                        if (path.Length == 0 || path.IndexOf('\0') >= 0 ||
                            identity.HasReadLength > 1 || identity.ReadLength >
                            NativeTerminalBroker.MaximumCaptureBytes)
                            throw new NativeTerminalAbiException("Broker reference or range is invalid.");
                        var capture = resolve(new NativeTerminalBrokerRequest(in identity, path));
                        if (capture == null)
                            throw new InvalidOperationException("Broker resolver returned no capture.");
                        try
                        {
                            var captureBytes = capture.ConsumeBytes();
                            try
                            {
                                fixed (byte* capturePointer = captureBytes)
                                {
                                    status = NativeTerminalInterop.TerminalBrokerSupplyV1(transaction,
                                        in identity, capture.Failure.HasValue ? (uint)capture.Failure.Value : 0,
                                        captureBytes == null ? IntPtr.Zero : (IntPtr)capturePointer,
                                        checked((ulong)(captureBytes?.Length ?? 0)));
                                }
                            }
                            finally { if (captureBytes != null) CryptographicOperations.ZeroMemory(captureBytes); }
                        }
                        finally { capture.Dispose(); }
                        if (status != NativeTerminalStatus.Ok)
                        {
                            var failureBuffer = NativeTerminalInterop.CreateEmptyBuffer();
                            _ = NativeTerminalInterop.TerminalBrokerDiagnosticV1(transaction,
                                ref failureBuffer);
                            throw new NativeTerminalException(
                                nameof(NativeTerminalInterop.TerminalBrokerSupplyV1), status,
                                NativeTerminalBroker.ReadDiagnostic(ref failureBuffer));
                        }
                    }
                    if (preview == null) throw new NativeTerminalAbiException("Broker Output was not resolved.");
                    publishDurable(preview);
                    var committed = NativeTerminalInterop.CreateEmptyBuffer();
                    status = NativeTerminalInterop.TerminalBrokerCommitV1(ref transaction,
                        ref owner, ref committed);
                    if (status != NativeTerminalStatus.Ok)
                    {
                        _ = NativeTerminalBroker.CopyAndFree(ref committed,
                            NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes, false);
                        NativeTerminalInterop.ThrowIfFailed(status,
                            nameof(NativeTerminalInterop.TerminalBrokerCommitV1));
                    }
                    // Native state has committed. A malformed returned frame must retire the wrapper.
                    try
                    {
                        var committedBytes = NativeTerminalBroker.CopyAndFree(ref committed,
                            NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes, true);
                        var frame = NativeTerminalHostEffectFrameV2Decoder.Decode(committedBytes);
                        if (!BytesEqual(preview.RawBytes, frame.RawBytes))
                            throw new NativeTerminalAbiException("Broker effect frame changed between preview and commit.");
                        return frame;
                    }
                    catch
                    {
                        failedClosed = true;
                        throw;
                    }
                }
                finally
                {
                    try
                    {
                        if (transaction != IntPtr.Zero)
                        {
                            // CANCEL requires an empty output slot even if BEGIN violated its contract.
                            var recovered = IntPtr.Zero;
                            var cancel = NativeTerminalInterop.TerminalBrokerCancelV1(
                                ref transaction, ref recovered);
                            if (cancel == NativeTerminalStatus.Ok && owner == IntPtr.Zero)
                                owner = recovered;
                            else
                                failedClosed = true;
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (diagnostic.Data != IntPtr.Zero)
                                _ = NativeTerminalBroker.CopyAndFree(ref diagnostic,
                                    NativeTerminalBroker.MaximumDiagnosticBytes, false);
                        }
                        finally
                        {
                            brokerActive = false;
                            if (ownershipTransferred)
                            {
                                if (owner == IntPtr.Zero)
                                {
                                    failedClosed = true;
                                    throw new NativeTerminalAbiException("Broker did not return terminal ownership.");
                                }
                                handle = NativeTerminalInterop.OwnTerminal(owner);
                            }
                        }
                    }
                }
            }
        }

        private static bool BytesEqual(System.Collections.Generic.IReadOnlyList<byte> left,
            System.Collections.Generic.IReadOnlyList<byte> right)
        {
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }

    internal static unsafe partial class NativeTerminalInterop
    {
        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_request_v1_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint TerminalBrokerRequestV1StructSize();

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_begin_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerBeginV1(ref IntPtr owner,
            in TlTerminalEvent terminalEvent, ref IntPtr transaction, ref TlTerminalBuffer diagnostic);

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_request_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerRequestV1(IntPtr transaction,
            ref TlTerminalBrokerRequestV1 request, ref TlTerminalBuffer reference);

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_supply_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerSupplyV1(IntPtr transaction,
            in TlTerminalBrokerRequestV1 request, uint outcome, IntPtr data, ulong length);

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_diagnostic_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerDiagnosticV1(IntPtr transaction,
            ref TlTerminalBuffer diagnostic);

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_preview_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerPreviewV1(IntPtr transaction,
            ref TlTerminalBuffer effects);

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_commit_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerCommitV1(ref IntPtr transaction,
            ref IntPtr owner, ref TlTerminalBuffer effects);

        [DllImport("termlink_terminal_ffi.dll", EntryPoint = "tl_terminal_broker_cancel_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBrokerCancelV1(ref IntPtr transaction,
            ref IntPtr owner);
    }
}
