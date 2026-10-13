using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

namespace TermLink.Agent;

// Owns detached native captures. No native capture ID or OwnerId is an authorization credential.
internal sealed class ProductionFrozenHistoryService : IDisposable
{
    private const long LeaseMilliseconds = 300_000;
    private const long MaximumAccountedBytes = 256L * 1024 * 1024;
    private const long MaximumCaptureBytes = 128L * 1024 * 1024;
    private const int MaximumLeases = 4;
    private const int MaximumTombstones = 1024;
    private const int MaximumRecipientTombstones = 64;
    private readonly object sync = new();
    private readonly ProductionTerminalHistoryStore store;
    private readonly Guid pcDeviceId;
    private readonly Func<Guid, bool> isDestroyed;
    private readonly Func<long> nowTicks;
    private readonly List<Lease> leases = new();
    private readonly List<ReleasedLease> released = new();
    private readonly List<CancelledBegin> cancelledBegins = new();
    private readonly Dictionary<Guid, long> beginAdmissionBlockedUntil = new();
    private long selectedBytes;
    private readonly Timer cleanup;
    private bool disposed;

    private readonly record struct PageKey(ulong Anchor, uint Lines, uint Cells, uint Utf8, uint Format);

    private sealed class CachedPage
    {
        public Guid ObjectId;
        public byte[] Sealed;
        public int Columns;
        public int Rows;
        public ulong First;
        public ulong Last;
        public ulong Next;
        public bool HasMore;
        public bool HeadTruncated;
    }

    private sealed class Lease : IDisposable
    {
        public Guid SessionId;
        public Guid RecipientId;
        public long Epoch;
        public string Profile;
        public ulong SnapshotSequence;
        public byte[] Token;
        public byte[] BeginRequestId;
        public bool BeginDelivered;
        public PageKey BeginPage;
        public NativeTerminalHistoryCapture Capture;
        public long Deadline;
        public Dictionary<PageKey, CachedPage> Pages = new();

        public void Dispose()
        {
            foreach (var page in Pages.Values) CryptographicOperations.ZeroMemory(page.Sealed);
            Pages.Clear();
            try { Capture?.Dispose(); }
            finally
            {
                if (Token != null) CryptographicOperations.ZeroMemory(Token);
                if (BeginRequestId != null) CryptographicOperations.ZeroMemory(BeginRequestId);
            }
        }
    }

    private sealed class ReleasedLease
    {
        public Guid SessionId;
        public Guid RecipientId;
        public long Epoch;
        public ulong SnapshotSequence;
        public ulong CaptureId;
        public ulong Revision;
        public string Profile;
        public byte[] Token;
        public byte[] BeginRequestId;
        public long Deadline;
    }

    private sealed class CancelledBegin
    {
        public Guid SessionId;
        public Guid RecipientId;
        public byte[] RequestId;
        public long Deadline;
    }

    public ProductionFrozenHistoryService(ProductionTerminalHistoryStore store, Guid pcDeviceId,
        Func<Guid, bool> isDestroyed, Func<long> nowTicks = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.pcDeviceId = pcDeviceId;
        this.isDestroyed = isDestroyed ?? throw new ArgumentNullException(nameof(isDestroyed));
        this.nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        cleanup = new Timer(_ => CleanupExpired(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public ProductionHostHistorySelection Select(Guid sessionId, HostHistoryRequestPayload request,
        ProductionHistoryRecipient recipient, ByteString requestId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || request?.FrozenHistory == null || recipient?.DeviceId == Guid.Empty ||
            recipient.DeviceId == pcDeviceId ||
            recipient.PairwiseKey?.Length != 32 || recipient.KeyEpoch < 1 || requestId?.Length != 16 ||
            !requestId.Equals(request.RequestId)) return Reject("INVALID_REQUEST");
        var frozen = request.FrozenHistory;
        if (!request.SubscriberCapabilities.Contains("host_history_frozen_v1") ||
            !request.SubscriberCapabilities.Contains("tlhp_v2"))
            return Reject("UNSUPPORTED_FORMAT");
        if (frozen.PageFormatVersion != FrozenHistoryCrypto.PageFormatV2)
            return Reject("UNSUPPORTED_FORMAT");
        if (request.SnapshotSeq == 0 || request.SnapshotSeq >= long.MaxValue)
            return Reject("INVALID_REQUEST");
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (disposed) return Reject("STALE_CAPTURE");
            PruneLocked();
            if (isDestroyed(sessionId)) { InvalidateSessionLocked(sessionId); return Reject("STALE_CAPTURE"); }
            var profile = store.GetSessionProfile(sessionId);
            if (!profile.IsGraphics) return Reject("UNSUPPORTED_FORMAT");
            return request.Kind switch
            {
                HostHistoryRequestKindV1.FrozenBegin => Begin(sessionId, request, recipient,
                    requestId, profile.StateContract, cancellationToken),
                HostHistoryRequestKindV1.FrozenPage => Page(sessionId, request, recipient,
                    profile.StateContract, cancellationToken),
                HostHistoryRequestKindV1.FrozenRelease => Release(sessionId, request, recipient,
                    profile.StateContract),
                _ => Reject("INVALID_HISTORY_KIND")
            };
        }
    }

    // An authenticated CANCEL may arrive before selection or after its final chunk.
    // The tombstone makes a retry of that BEGIN ID unable to allocate a fresh capture.
    internal void CancelBegin(Guid sessionId, Guid recipientDeviceId, ByteString requestId)
    {
        if (sessionId == Guid.Empty || recipientDeviceId == Guid.Empty || requestId?.Length != 16)
            return;
        lock (sync)
        {
            if (disposed) return;
            PruneLocked();
            CancelBeginLocked(sessionId, recipientDeviceId, requestId);
        }
    }

    internal bool MarkBeginDelivered(Guid sessionId, Guid recipientDeviceId, ByteString requestId)
    {
        if (sessionId == Guid.Empty || recipientDeviceId == Guid.Empty || requestId?.Length != 16)
            return false;
        lock (sync)
        {
            if (disposed) return false;
            PruneLocked();
            var lease = leases.FirstOrDefault(candidate =>
                candidate.SessionId == sessionId && candidate.RecipientId == recipientDeviceId &&
                candidate.BeginRequestId.AsSpan().SequenceEqual(requestId.Span));
            if (lease == null) return false;
            lease.BeginDelivered = true;
            return true;
        }
    }

    internal void CancelUndeliveredBegin(Guid sessionId, Guid recipientDeviceId, ByteString requestId)
    {
        if (sessionId == Guid.Empty || recipientDeviceId == Guid.Empty || requestId?.Length != 16)
            return;
        lock (sync)
        {
            if (disposed) return;
            PruneLocked();
            var delivered = leases.Any(candidate =>
                candidate.SessionId == sessionId && candidate.RecipientId == recipientDeviceId &&
                candidate.BeginRequestId.AsSpan().SequenceEqual(requestId.Span) &&
                candidate.BeginDelivered);
            if (!delivered) CancelBeginLocked(sessionId, recipientDeviceId, requestId);
        }
    }

    private void CancelBeginLocked(Guid sessionId, Guid recipientDeviceId, ByteString requestId)
    {
        var id = requestId.Span;
        if (WasBeginCancelled(sessionId, recipientDeviceId, id) ||
            WasBeginReleased(sessionId, recipientDeviceId, id)) return;
        Lease lease = null;
        foreach (var candidate in leases)
            if (candidate.SessionId == sessionId && candidate.RecipientId == recipientDeviceId &&
                candidate.BeginRequestId.AsSpan().SequenceEqual(id))
            { lease = candidate; break; }
        if (lease != null) RemoveLocked(lease);
        if (!IsBeginAdmissionBlocked(recipientDeviceId) &&
            RecipientRecordCount(recipientDeviceId) < MaximumRecipientTombstones &&
            released.Count + cancelledBegins.Count + leases.Count < MaximumTombstones)
            cancelledBegins.Add(new CancelledBegin { SessionId = sessionId,
                RecipientId = recipientDeviceId, RequestId = requestId.ToByteArray(),
                Deadline = DeadlineAfter(LeaseMilliseconds) });
        else
        {
            // Once this recipient cannot retain another ID, deny only its
            // new captures for the cancelled request's retry window.
            var deadline = DeadlineAfter(LeaseMilliseconds);
            beginAdmissionBlockedUntil[recipientDeviceId] =
                Math.Max(beginAdmissionBlockedUntil.GetValueOrDefault(recipientDeviceId), deadline);
        }
    }

    private ProductionHostHistorySelection Begin(Guid sessionId, HostHistoryRequestPayload request,
        ProductionHistoryRecipient recipient, ByteString requestId, string profile, CancellationToken cancellationToken)
    {
        var f = request.FrozenHistory;
        if (f.Token.Length != 0 || f.CaptureId != 0 || f.CaptureRevision != 0 ||
            request.BeforeLineId != NativeTerminalHistoryCapture.LatestAnchor || !ValidBudget(f))
            return Reject("INVALID_REQUEST");
        if (WasBeginCancelled(sessionId, recipient.DeviceId, requestId.Span))
            return Reject("STALE_CAPTURE");
        if (WasBeginReleased(sessionId, recipient.DeviceId, requestId.Span))
            return Reject("STALE_CAPTURE");
        var existing = leases.FirstOrDefault(x => x.SessionId == sessionId && x.RecipientId == recipient.DeviceId);
        if (existing != null && existing.Epoch != recipient.KeyEpoch)
        {
            RemoveLocked(existing);
            existing = null;
        }
        var key = Key(request);
        if (existing != null)
        {
            if (!existing.BeginRequestId.AsSpan().SequenceEqual(requestId.Span))
                return Reject("CAPTURE_LIMIT");
            if (existing.SnapshotSequence != request.SnapshotSeq || existing.BeginPage != key ||
                existing.Profile != profile) return Reject("INVALID_REQUEST");
            return PageResult(existing, key, recipient, cancellationToken);
        }
        if (IsBeginAdmissionBlocked(recipient.DeviceId) ||
            RecipientRecordCount(recipient.DeviceId) >= MaximumRecipientTombstones ||
            leases.Count >= MaximumLeases ||
            released.Count + cancelledBegins.Count + leases.Count >= MaximumTombstones ||
            AccountedLocked() > MaximumAccountedBytes - MaximumCaptureBytes)
            return Reject("CAPTURE_LIMIT");
        var artifact = store.ReadExactGraphicsSnapshot(sessionId, request.SnapshotSeq, out var currentSequence);
        if (artifact == null) return currentSequence == 0
            ? new ProductionHostHistorySelection { Status = HostHistoryResponseStatusV1.NotReady,
                ErrorCode = "SNAPSHOT_NOT_READY" }
            : new ProductionHostHistorySelection { Status = HostHistoryResponseStatusV1.Rejected,
                ErrorCode = "ANCHOR_CHANGED",
                AvailableSnapshotSequence = currentSequence > request.SnapshotSeq ? currentSequence : 0 };
        NativeTerminalHistoryCapture capture = null;
        long captureStartedAt = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var terminal = NativeTerminalCoreV2.ImportGraphicsSnapshot(artifact.Plaintext))
            {
                if (terminal.GetPresentationInfo().NextSourceSequence != checked(request.SnapshotSeq + 1))
                    return Reject("ANCHOR_CHANGED");
                captureStartedAt = nowTicks();
                capture = terminal.CaptureHistory();
            }
            if (capture.Revision != request.SnapshotSeq + 1 ||
                capture.AccountedBytes > MaximumCaptureBytes ||
                AccountedLocked() > MaximumAccountedBytes - (long)capture.AccountedBytes)
                return Reject("CAPTURE_LIMIT");
            var lease = new Lease
            {
                SessionId = sessionId, RecipientId = recipient.DeviceId, Epoch = recipient.KeyEpoch,
                Profile = profile, SnapshotSequence = request.SnapshotSeq,
                Token = RandomNumberGenerator.GetBytes(32), BeginRequestId = requestId.ToByteArray(),
                BeginPage = key, Capture = capture,
                Deadline = checked(captureStartedAt + LeaseMilliseconds * Stopwatch.Frequency / 1000)
            };
            capture = null;
            ProductionHostHistorySelection result = null;
            try
            {
                result = PageResult(lease, key, recipient, cancellationToken);
                if (result.Status != HostHistoryResponseStatusV1.Ok) { lease.Dispose(); return result; }
                cancellationToken.ThrowIfCancellationRequested();
                leases.Add(lease);
                return result;
            }
            catch { result?.Dispose(); lease.Dispose(); throw; }
        }
        catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.LimitExceeded)
        { return Reject("CAPTURE_LIMIT"); }
        finally
        {
            capture?.Dispose();
            CryptographicOperations.ZeroMemory(artifact.Plaintext);
        }
    }

    private ProductionHostHistorySelection Page(Guid sessionId, HostHistoryRequestPayload request,
        ProductionHistoryRecipient recipient, string profile, CancellationToken cancellationToken)
    {
        var f = request.FrozenHistory;
        if (!ValidBudget(f) || f.Token.Length != 32 || f.CaptureId == 0 || f.CaptureRevision == 0 ||
            request.BeforeLineId == 0) return Reject("INVALID_REQUEST");
        var lease = FindLease(sessionId, recipient, f.Token.Span);
        if (lease == null) return Reject("STALE_CAPTURE");
        if (lease.Profile != profile || lease.Capture.CaptureId != f.CaptureId)
            return Reject("STALE_CAPTURE");
        if (lease.Capture.Revision != f.CaptureRevision) return Reject("STALE_REVISION");
        if (lease.SnapshotSequence != request.SnapshotSeq) return Reject("STALE_ANCHOR");
        return PageResult(lease, Key(request), recipient, cancellationToken);
    }

    private ProductionHostHistorySelection Release(Guid sessionId, HostHistoryRequestPayload request,
        ProductionHistoryRecipient recipient, string profile)
    {
        var f = request.FrozenHistory;
        if (f.Token.Length != 32 || f.CaptureId == 0 || f.CaptureRevision == 0)
            return Reject("INVALID_REQUEST");
        var lease = FindLease(sessionId, recipient, f.Token.Span);
        if (lease != null)
        {
            if (lease.Capture.CaptureId != f.CaptureId || lease.Profile != profile)
                return Reject("STALE_CAPTURE");
            if (lease.Capture.Revision != f.CaptureRevision) return Reject("STALE_REVISION");
            if (lease.SnapshotSequence != request.SnapshotSeq) return Reject("STALE_ANCHOR");
            var metadata = Metadata(lease, false, 0, 0);
            released.Add(new ReleasedLease { SessionId = sessionId, RecipientId = recipient.DeviceId,
                Epoch = recipient.KeyEpoch, Profile = profile, SnapshotSequence = request.SnapshotSeq,
                CaptureId = f.CaptureId, Revision = f.CaptureRevision,
                Token = (byte[])lease.Token.Clone(), BeginRequestId = (byte[])lease.BeginRequestId.Clone(),
                Deadline = DeadlineAfter(LeaseMilliseconds) });
            RemoveLocked(lease);
            return OkRelease(metadata);
        }
        var prior = released.FirstOrDefault(x => x.SessionId == sessionId && x.RecipientId == recipient.DeviceId &&
            x.Epoch == recipient.KeyEpoch && x.SnapshotSequence == request.SnapshotSeq &&
            x.CaptureId == f.CaptureId && x.Revision == f.CaptureRevision && x.Profile == profile &&
            CryptographicOperations.FixedTimeEquals(x.Token, f.Token.Span));
        if (prior == null) return Reject("STALE_CAPTURE");
        return OkRelease(new FrozenHistoryMetadata { Token = ByteString.CopyFrom(prior.Token),
            CaptureId = prior.CaptureId, CaptureRevision = prior.Revision,
            SnapshotSeq = prior.SnapshotSequence, StateContract = profile });
    }

    private ProductionHostHistorySelection PageResult(Lease lease, PageKey key,
        ProductionHistoryRecipient recipient, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Remaining(lease) == 0) { RemoveLocked(lease); return Reject("STALE_CAPTURE"); }
        try { lease.Capture.CheckLease(); }
        catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.HistoryStaleCapture)
        { RemoveLocked(lease); return Reject("STALE_CAPTURE"); }
        catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.HistoryStaleRevision)
        { RemoveLocked(lease); return Reject("STALE_REVISION"); }
        if (!lease.Pages.TryGetValue(key, out var cached))
        {
            byte[] plaintext = null;
            byte[] sealedObject = null;
            try
            {
                plaintext = lease.Capture.ReadBytesV2(key.Anchor, key.Lines, key.Cells, key.Utf8);
                cancellationToken.ThrowIfCancellationRequested();
                var page = NativeTerminalHistoryPageV2.Decode(plaintext, lease.Capture.CaptureId);
                if (page.Revision != lease.Capture.Revision || page.RequestedAnchor != key.Anchor)
                    throw new InvalidDataException("Native frozen history page identity changed.");
                sealedObject = FrozenHistoryCrypto.Seal(recipient.PairwiseKey, lease.SessionId, pcDeviceId,
                    recipient.DeviceId, recipient.KeyEpoch, lease.Token, lease.Capture.CaptureId,
                    lease.Capture.Revision, lease.SnapshotSequence, key.Anchor, key.Lines, key.Cells,
                    key.Utf8, key.Format, lease.Profile, plaintext);
                cancellationToken.ThrowIfCancellationRequested();
                if (AccountedLocked() > MaximumAccountedBytes - sealedObject.LongLength -
                    (leases.Contains(lease) ? 0 : (long)lease.Capture.AccountedBytes))
                    return Reject("CAPTURE_LIMIT");
                cached = new CachedPage { ObjectId = Guid.NewGuid(), Sealed = sealedObject,
                    Columns = checked((int)page.Columns),
                    Rows = page.Lines.Sum(line => checked((int)line.RowCount)),
                    First = page.Lines.Count == 0 ? 0 : page.Lines[0].Id,
                    Last = page.Lines.Count == 0 ? 0 : page.Lines[^1].Id,
                    Next = page.NextBeforeLineId, HasMore = page.HasMoreBefore,
                    HeadTruncated = page.HeadTruncated };
                sealedObject = null;
                lease.Pages.Add(key, cached);
            }
            catch (NativeTerminalHistoryBudgetException error)
            {
                if (error.RequiredCells == 0 && error.RequiredUtf8Bytes == 0)
                    return Reject("INVALID_HISTORY_STATE");
                return new ProductionHostHistorySelection { Status = HostHistoryResponseStatusV1.Rejected,
                    ErrorCode = "PAGE_BUDGET", FrozenHistory = Metadata(lease, false,
                        error.RequiredCells, error.RequiredUtf8Bytes) };
            }
            catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.HistoryStaleAnchor)
            { return Reject("STALE_ANCHOR"); }
            catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.HistoryStaleCapture)
            { RemoveLocked(lease); return Reject("STALE_CAPTURE"); }
            catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.HistoryStaleRevision)
            { return Reject("STALE_REVISION"); }
            catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.LimitExceeded)
            { return Reject("PAGE_LIMIT"); }
            catch (NativeTerminalAbiException)
            { RemoveLocked(lease); return Reject("INVALID_HISTORY_STATE"); }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (sealedObject != null) CryptographicOperations.ZeroMemory(sealedObject);
            }
        }
        var untracked = leases.Contains(lease) ? 0L : checked((long)lease.Capture.AccountedBytes +
            lease.Pages.Values.Sum(p => (long)p.Sealed.Length));
        if (AccountedLocked() > MaximumAccountedBytes - untracked - cached.Sealed.LongLength)
            return Reject("CAPTURE_LIMIT");
        var selectedCopy = (byte[])cached.Sealed.Clone();
        selectedBytes += selectedCopy.LongLength;
        return new ProductionHostHistorySelection { Status = HostHistoryResponseStatusV1.Ok,
            FrozenHistory = Metadata(lease, cached.HeadTruncated, 0, 0),
            Object = new ProductionHostHistoryObject { Kind = HostHistoryObjectKindV1.FrozenPage,
                ObjectId = cached.ObjectId, SnapshotSequence = lease.SnapshotSequence,
                Columns = cached.Columns, Rows = cached.Rows,
                RequestBeforeLineId = key.Anchor, FirstLineId = cached.First, LastLineId = cached.Last,
                HasMoreBefore = cached.HasMore, NextBeforeLineId = cached.Next,
                KeyId = FrozenHistoryCrypto.KeyId(recipient.DeviceId, recipient.KeyEpoch, lease.Profile),
                FormatName = "TERMLINK_FROZEN_HISTORY_PAGE_V2", FormatVersion = 1,
                SealedObject = selectedCopy,
                OnDisposed = bytes => { lock (sync) selectedBytes -= bytes; } } };
    }

    private static PageKey Key(HostHistoryRequestPayload request) => new(request.BeforeLineId,
        request.FrozenHistory.MaxLines, request.FrozenHistory.MaxCells,
        request.FrozenHistory.MaxUtf8Bytes, request.FrozenHistory.PageFormatVersion);

    private static bool ValidBudget(FrozenHistoryRequest f) => f.MaxLines is >= 1 and <= 128 &&
        f.MaxCells is >= 1 and <= 65536 && f.MaxUtf8Bytes is >= 1 and <= 4 * 1024 * 1024;

    private Lease FindLease(Guid sessionId, ProductionHistoryRecipient recipient, ReadOnlySpan<byte> token)
    {
        foreach (var lease in leases)
            if (lease.SessionId == sessionId && lease.RecipientId == recipient.DeviceId &&
                lease.Epoch == recipient.KeyEpoch && CryptographicOperations.FixedTimeEquals(lease.Token, token))
                return lease;
        return null;
    }

    private bool WasBeginCancelled(Guid sessionId, Guid recipientDeviceId, ReadOnlySpan<byte> requestId)
    {
        foreach (var cancelled in cancelledBegins)
            if (cancelled.SessionId == sessionId && cancelled.RecipientId == recipientDeviceId &&
                cancelled.RequestId.AsSpan().SequenceEqual(requestId)) return true;
        return false;
    }

    private bool WasBeginReleased(Guid sessionId, Guid recipientDeviceId, ReadOnlySpan<byte> requestId)
    {
        foreach (var old in released)
            if (old.SessionId == sessionId && old.RecipientId == recipientDeviceId &&
                old.BeginRequestId.AsSpan().SequenceEqual(requestId)) return true;
        return false;
    }

    private int RecipientRecordCount(Guid recipientDeviceId) =>
        cancelledBegins.Count(x => x.RecipientId == recipientDeviceId) +
        released.Count(x => x.RecipientId == recipientDeviceId) +
        leases.Count(x => x.RecipientId == recipientDeviceId);

    private bool IsBeginAdmissionBlocked(Guid recipientDeviceId) =>
        beginAdmissionBlockedUntil.TryGetValue(recipientDeviceId, out var deadline) &&
        nowTicks() < deadline;

    private FrozenHistoryMetadata Metadata(Lease lease, bool headTruncated,
        ulong requiredCells, ulong requiredUtf8) => new()
    {
        Token = ByteString.CopyFrom(lease.Token), CaptureId = lease.Capture.CaptureId,
        CaptureRevision = lease.Capture.Revision, LeaseRemainingMs = Remaining(lease),
        SnapshotSeq = lease.SnapshotSequence, StateContract = lease.Profile,
        HeadTruncated = headTruncated,
        RequiredCells = requiredCells, RequiredUtf8Bytes = requiredUtf8
    };

    private static ProductionHostHistorySelection Reject(string code) => new()
    { Status = HostHistoryResponseStatusV1.Rejected, ErrorCode = code };

    private static ProductionHostHistorySelection OkRelease(FrozenHistoryMetadata metadata) => new()
    { Status = HostHistoryResponseStatusV1.Ok, FrozenHistory = metadata };

    private long DeadlineAfter(long milliseconds) =>
        checked(nowTicks() + milliseconds * Stopwatch.Frequency / 1000);

    private ulong Remaining(Lease lease)
    {
        var ticks = lease.Deadline - nowTicks();
        return ticks <= 0 ? 0 : checked((ulong)(ticks * 1000 / Stopwatch.Frequency));
    }

    private long AccountedLocked() => checked(selectedBytes + leases.Sum(x =>
        checked((long)x.Capture.AccountedBytes + x.Pages.Values.Sum(p => (long)p.Sealed.Length))));

    private void RemoveLocked(Lease lease) { leases.Remove(lease); lease.Dispose(); }

    private void PruneLocked()
    {
        foreach (var lease in leases.ToArray())
            if (Remaining(lease) == 0 || isDestroyed(lease.SessionId)) RemoveLocked(lease);
        foreach (var old in released.ToArray())
            if (old.Deadline <= nowTicks() || isDestroyed(old.SessionId))
            { released.Remove(old); CryptographicOperations.ZeroMemory(old.Token);
                CryptographicOperations.ZeroMemory(old.BeginRequestId); }
        foreach (var old in cancelledBegins.ToArray())
            if (old.Deadline <= nowTicks() || isDestroyed(old.SessionId))
            { cancelledBegins.Remove(old); CryptographicOperations.ZeroMemory(old.RequestId); }
        foreach (var blocked in beginAdmissionBlockedUntil.ToArray())
            if (blocked.Value <= nowTicks()) beginAdmissionBlockedUntil.Remove(blocked.Key);
    }

    private void CleanupExpired()
    {
        try { lock (sync) { if (!disposed) PruneLocked(); } }
        catch { /* Timer thread must not terminate the agent; request path reports errors. */ }
    }

    private void InvalidateSessionLocked(Guid sessionId)
    {
        foreach (var lease in leases.Where(x => x.SessionId == sessionId).ToArray()) RemoveLocked(lease);
        foreach (var old in released.Where(x => x.SessionId == sessionId).ToArray())
        { released.Remove(old); CryptographicOperations.ZeroMemory(old.Token);
            CryptographicOperations.ZeroMemory(old.BeginRequestId); }
        foreach (var old in cancelledBegins.Where(x => x.SessionId == sessionId).ToArray())
        { cancelledBegins.Remove(old); CryptographicOperations.ZeroMemory(old.RequestId); }
    }

    internal void InvalidateSession(Guid sessionId)
    { lock (sync) { if (!disposed) InvalidateSessionLocked(sessionId); } }

    internal void InvalidateRecipient(Guid recipientDeviceId)
    {
        lock (sync)
        {
            if (disposed) return;
            foreach (var lease in leases.Where(x => x.RecipientId == recipientDeviceId).ToArray()) RemoveLocked(lease);
            foreach (var old in released.Where(x => x.RecipientId == recipientDeviceId).ToArray())
            { released.Remove(old); CryptographicOperations.ZeroMemory(old.Token);
                CryptographicOperations.ZeroMemory(old.BeginRequestId); }
            foreach (var old in cancelledBegins.Where(x => x.RecipientId == recipientDeviceId).ToArray())
            { cancelledBegins.Remove(old); CryptographicOperations.ZeroMemory(old.RequestId); }
            beginAdmissionBlockedUntil.Remove(recipientDeviceId);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            cleanup.Dispose();
            foreach (var lease in leases.ToArray()) RemoveLocked(lease);
            foreach (var old in released)
            { CryptographicOperations.ZeroMemory(old.Token);
                CryptographicOperations.ZeroMemory(old.BeginRequestId); }
            released.Clear();
            foreach (var old in cancelledBegins) CryptographicOperations.ZeroMemory(old.RequestId);
            cancelledBegins.Clear();
            beginAdmissionBlockedUntil.Clear();
        }
    }
}
