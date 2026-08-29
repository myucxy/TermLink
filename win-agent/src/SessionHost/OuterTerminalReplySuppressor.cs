using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TermLink.SessionHost
{
    /// <summary>
    /// Suppresses only bounded terminal-generated replies for which terminal-core has already
    /// emitted the authoritative PTY reply. Ordinary key input and unmatched escape sequences
    /// pass through unchanged. A reply candidate may span multiple input reads.
    /// </summary>
    internal sealed class OuterTerminalReplySuppressor : IDisposable
    {
        private static readonly TimeSpan ExpectationLifetime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CandidateLifetime = TimeSpan.FromMilliseconds(250);
        private const int MaximumCsiReplyBytes = 1024;
        private const int MaximumReplyBytes = 64 * 1024;

        private readonly object sync = new object();
        private readonly Queue<ExpectedReply> expected = new Queue<ExpectedReply>();
        private readonly List<PendingByte> candidate = new List<PendingByte>(128);
        private readonly MemoryStream deferredPassThrough = new MemoryStream();
        private readonly Action<byte[]> expiredRawPassThrough;
        private readonly Timer expiryTimer;
        private ExpectedReply candidateExpectation;
        private DateTime candidateExpiresUtc;
        private bool disposed;

        public OuterTerminalReplySuppressor(Action<byte[]> expiredRawPassThrough = null)
        {
            this.expiredRawPassThrough = expiredRawPassThrough;
            expiryTimer = new Timer(OnExpiryTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Expect(byte[] authoritativeReply)
        {
            if (authoritativeReply == null || authoritativeReply.Length == 0 ||
                authoritativeReply.Length > MaximumReplyBytes)
            {
                throw new ArgumentException("A bounded terminal reply is required.", nameof(authoritativeReply));
            }
            lock (sync)
            {
                ThrowIfDisposed();
                var now = DateTime.UtcNow;
                PurgeExpired(now);
                expected.Enqueue(new ExpectedReply(
                    ReplyKindOf(authoritativeReply),
                    (byte[])authoritativeReply.Clone(),
                    now + ExpectationLifetime));
                ScheduleExpiryTimer(now);
            }
        }

        public byte[] Filter(byte[] input, int length)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (length < 0 || length > input.Length) throw new ArgumentOutOfRangeException(nameof(length));

            lock (sync)
            {
                ThrowIfDisposed();
                var now = DateTime.UtcNow;
                ExpireCandidate(now);
                PurgeExpired(now);
                using (var output = new MemoryStream(checked(length + (int)deferredPassThrough.Length)))
                {
                    DrainDeferred(output);
                    for (var offset = 0; offset < length; offset++)
                        Process(input[offset], output, null, now);
                    ScheduleExpiryTimer(now);
                    return output.ToArray();
                }
            }
        }

        /// <summary>
        /// Filters one indivisible console-key projection. Resolution may be deferred until a
        /// reply spanning later records completes. The callback runs exactly once with true for
        /// user input and false for a suppressed compatibility reply.
        /// </summary>
        public void FilterFragment(
            byte[] input,
            int length,
            Action<bool> resolved)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (length < 1 || length > input.Length) throw new ArgumentOutOfRangeException(nameof(length));
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));

            lock (sync)
            {
                ThrowIfDisposed();
                var now = DateTime.UtcNow;
                ExpireCandidate(now);
                PurgeExpired(now);
                var fragment = new PendingFragment(length, resolved);
                for (var offset = 0; offset < length; offset++)
                    Process(input[offset], null, fragment, now);
                ScheduleExpiryTimer(now);
            }
        }

        internal int PendingCount
        {
            get
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    var now = DateTime.UtcNow;
                    ExpireCandidate(now);
                    PurgeExpired(now);
                    ScheduleExpiryTimer(now);
                    return expected.Count;
                }
            }
        }

        internal int BufferedCandidateBytes
        {
            get { lock (sync) return candidate.Count; }
        }

        private void Process(
            byte value,
            MemoryStream output,
            PendingFragment fragment,
            DateTime now)
        {
            if (candidate.Count == 0)
            {
                if (value != 0x1b || expected.Count == 0)
                {
                    Pass(value, output, fragment);
                    return;
                }
                candidate.Add(new PendingByte(value, fragment));
                candidateExpectation = expected.Peek();
                candidateExpiresUtc = Earlier(
                    now + CandidateLifetime,
                    candidateExpectation.ExpiresUtc);
                return;
            }

            candidate.Add(new PendingByte(value, fragment));
            switch (CandidateStatusOf(candidate))
            {
                case CandidateStatus.Incomplete:
                    return;
                case CandidateStatus.Complete:
                    var bytes = new byte[candidate.Count];
                    for (var index = 0; index < candidate.Count; index++)
                        bytes[index] = candidate[index].Value;
                    if (candidateExpectation == expected.Peek() &&
                        Matches(candidateExpectation, bytes, 0, bytes.Length))
                    {
                        var matched = expected.Dequeue();
                        ClearExpectation(matched);
                        ResolveCandidate(suppressed: true, output, retainTrailingEscape: false);
                        candidate.Clear();
                        candidateExpectation = null;
                        candidateExpiresUtc = default;
                    }
                    else
                    {
                        ReleaseCandidate(output, retainTrailingEscape: false);
                    }
                    return;
                case CandidateStatus.Invalid:
                    ReleaseCandidate(output, retainTrailingEscape: value == 0x1b);
                    return;
                default:
                    throw new InvalidOperationException("Unknown terminal reply candidate state.");
            }
        }

        private void ReleaseCandidate(MemoryStream output, bool retainTrailingEscape)
        {
            var releaseLength = candidate.Count - (retainTrailingEscape ? 1 : 0);
            for (var index = 0; index < releaseLength; index++)
                Pass(candidate[index].Value, output, candidate[index].Fragment);

            var trailing = retainTrailingEscape ? candidate[candidate.Count - 1] : null;
            candidate.Clear();
            candidateExpectation = null;
            candidateExpiresUtc = default;
            if (retainTrailingEscape && expected.Count != 0)
            {
                candidate.Add(trailing);
                candidateExpectation = expected.Peek();
                candidateExpiresUtc = Earlier(
                    DateTime.UtcNow + CandidateLifetime,
                    candidateExpectation.ExpiresUtc);
            }
        }

        private void ResolveCandidate(
            bool suppressed,
            MemoryStream output,
            bool retainTrailingEscape)
        {
            var resolveLength = candidate.Count - (retainTrailingEscape ? 1 : 0);
            for (var index = 0; index < resolveLength; index++)
            {
                var item = candidate[index];
                if (suppressed) item.Fragment?.Resolve(suppressed: true);
                else Pass(item.Value, output, item.Fragment);
            }
        }

        private void PurgeExpired(DateTime now)
        {
            while (expected.Count != 0 && expected.Peek().ExpiresUtc <= now)
            {
                var expired = expected.Dequeue();
                if (ReferenceEquals(candidateExpectation, expired))
                {
                    for (var index = 0; index < candidate.Count; index++)
                    {
                        var item = candidate[index];
                        if (item.Fragment == null) deferredPassThrough.WriteByte(item.Value);
                        else item.Fragment.Resolve(suppressed: false);
                    }
                    candidate.Clear();
                    candidateExpectation = null;
                    candidateExpiresUtc = default;
                }
                ClearExpectation(expired);
            }
        }

        private void ExpireCandidate(DateTime now)
        {
            if (candidate.Count == 0 || candidateExpiresUtc > now) return;
            for (var index = 0; index < candidate.Count; index++)
            {
                var item = candidate[index];
                if (item.Fragment == null) deferredPassThrough.WriteByte(item.Value);
                else item.Fragment.Resolve(suppressed: false);
            }
            candidate.Clear();
            candidateExpectation = null;
            candidateExpiresUtc = default;
        }

        private void OnExpiryTimer(object state)
        {
            lock (sync)
            {
                if (disposed) return;
                var now = DateTime.UtcNow;
                ExpireCandidate(now);
                PurgeExpired(now);
                if (expiredRawPassThrough != null && deferredPassThrough.Length != 0)
                {
                    var bytes = deferredPassThrough.ToArray();
                    deferredPassThrough.SetLength(0);
                    deferredPassThrough.Position = 0;
                    expiredRawPassThrough(bytes);
                }
                ScheduleExpiryTimer(now);
            }
        }

        private void ScheduleExpiryTimer(DateTime now)
        {
            if (disposed) return;
            DateTime? deadline = null;
            if (candidate.Count != 0) deadline = candidateExpiresUtc;
            if (expected.Count != 0 &&
                (!deadline.HasValue || expected.Peek().ExpiresUtc < deadline.Value))
                deadline = expected.Peek().ExpiresUtc;
            if (!deadline.HasValue)
            {
                expiryTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            var delay = deadline.Value <= now
                ? 1
                : Math.Max(1, (int)Math.Ceiling((deadline.Value - now).TotalMilliseconds));
            expiryTimer.Change(delay, Timeout.Infinite);
        }

        private static DateTime Earlier(DateTime left, DateTime right) =>
            left <= right ? left : right;

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(OuterTerminalReplySuppressor));
        }

        private void DrainDeferred(MemoryStream output)
        {
            if (deferredPassThrough.Length == 0) return;
            deferredPassThrough.Position = 0;
            deferredPassThrough.CopyTo(output);
            deferredPassThrough.SetLength(0);
            deferredPassThrough.Position = 0;
        }

        private static void ClearExpectation(ExpectedReply expectation)
        {
            Array.Clear(expectation.ExactBytes, 0, expectation.ExactBytes.Length);
        }

        private static CandidateStatus CandidateStatusOf(List<PendingByte> bytes)
        {
            if (bytes.Count == 0 || bytes[0].Value != 0x1b) return CandidateStatus.Invalid;
            if (bytes.Count == 1) return CandidateStatus.Incomplete;

            var introducer = bytes[1].Value;
            if (introducer == (byte)'[')
            {
                if (bytes.Count > MaximumCsiReplyBytes) return CandidateStatus.Invalid;
                for (var index = 2; index < bytes.Count; index++)
                {
                    var value = bytes[index].Value;
                    if (value >= 0x40 && value <= 0x7e)
                        return index == bytes.Count - 1
                            ? CandidateStatus.Complete
                            : CandidateStatus.Invalid;
                    if (value < 0x20 || value > 0x3f) return CandidateStatus.Invalid;
                }
                return CandidateStatus.Incomplete;
            }

            if (introducer != (byte)']' && introducer != (byte)'P')
                return CandidateStatus.Invalid;
            if (bytes.Count > MaximumReplyBytes) return CandidateStatus.Invalid;

            for (var index = 2; index < bytes.Count; index++)
            {
                if (introducer == (byte)']' && bytes[index].Value == 0x07)
                    return index == bytes.Count - 1
                        ? CandidateStatus.Complete
                        : CandidateStatus.Invalid;
                if (bytes[index].Value == 0x1b && index + 1 < bytes.Count &&
                    bytes[index + 1].Value == (byte)'\\')
                {
                    return index + 2 == bytes.Count
                        ? CandidateStatus.Complete
                        : CandidateStatus.Invalid;
                }
            }
            return CandidateStatus.Incomplete;
        }

        private static void Pass(
            byte value,
            MemoryStream output,
            PendingFragment fragment)
        {
            if (fragment == null) output.WriteByte(value);
            else fragment.Resolve(suppressed: false);
        }

        private static bool Matches(
            ExpectedReply expectation,
            byte[] candidateBytes,
            int offset,
            int length)
        {
            var candidateKind = ReplyKindOf(candidateBytes, offset, length);
            if (expectation.Kind != ReplyKind.Exact && expectation.Kind == candidateKind)
                return true;
            if (expectation.ExactBytes.Length != length) return false;
            for (var index = 0; index < length; index++)
                if (expectation.ExactBytes[index] != candidateBytes[offset + index]) return false;
            return true;
        }

        private static ReplyKind ReplyKindOf(byte[] bytes) => ReplyKindOf(bytes, 0, bytes.Length);

        private static ReplyKind ReplyKindOf(byte[] bytes, int offset, int length)
        {
            if (length < 3 || bytes[offset] != 0x1b) return ReplyKind.Exact;
            var end = offset + length;
            if (bytes[offset + 1] == (byte)'[')
            {
                var final = bytes[end - 1];
                var first = bytes[offset + 2];
                if (final == (byte)'c')
                    return first == (byte)'?' ? ReplyKind.PrimaryDeviceAttributes :
                        first == (byte)'>' ? ReplyKind.SecondaryDeviceAttributes : ReplyKind.Exact;
                if (final == (byte)'n')
                    return first == (byte)'?' ? ReplyKind.PrivateDeviceStatus : ReplyKind.DeviceStatus;
                if (final == (byte)'R')
                    return first == (byte)'?' ? ReplyKind.PrivateCursorPosition : ReplyKind.CursorPosition;
                if (final == (byte)'u') return ReplyKind.KeyboardFlags;
                if (final == (byte)'y' && length >= 5 && bytes[end - 2] == (byte)'$')
                    return ReplyKind.ModeReport;
                return ReplyKind.Exact;
            }
            if (bytes[offset + 1] == (byte)'P')
            {
                if (length >= 6 && bytes[offset + 3] == (byte)'$' && bytes[offset + 4] == (byte)'r')
                    return ReplyKind.StatusString;
                if (length >= 6 && bytes[offset + 3] == (byte)'+' && bytes[offset + 4] == (byte)'r')
                    return ReplyKind.TerminalCapability;
                return ReplyKind.Exact;
            }
            if (bytes[offset + 1] == (byte)']')
            {
                var selectorEnd = Array.IndexOf(bytes, (byte)';', offset + 2, length - 2);
                if (selectorEnd < 0) return ReplyKind.Exact;
                var selectorLength = selectorEnd - offset - 2;
                if (selectorLength == 1 && bytes[offset + 2] == (byte)'4') return ReplyKind.PaletteColor;
                if (selectorLength == 2 && bytes[offset + 2] == (byte)'1')
                {
                    if (bytes[offset + 3] == (byte)'0') return ReplyKind.DefaultForeground;
                    if (bytes[offset + 3] == (byte)'1') return ReplyKind.DefaultBackground;
                    if (bytes[offset + 3] == (byte)'2') return ReplyKind.DefaultCursor;
                }
            }
            return ReplyKind.Exact;
        }

        private sealed class ExpectedReply
        {
            internal ExpectedReply(ReplyKind kind, byte[] exactBytes, DateTime expiresUtc)
            {
                Kind = kind;
                ExactBytes = exactBytes;
                ExpiresUtc = expiresUtc;
            }

            internal ReplyKind Kind { get; }
            internal byte[] ExactBytes { get; }
            internal DateTime ExpiresUtc { get; }
        }

        private sealed class PendingByte
        {
            internal PendingByte(byte value, PendingFragment fragment)
            {
                Value = value;
                Fragment = fragment;
            }

            internal byte Value { get; }
            internal PendingFragment Fragment { get; }
        }

        private sealed class PendingFragment
        {
            private readonly int byteCount;
            private readonly Action<bool> resolved;
            private int resolvedBytes;
            private bool suppressed;

            internal PendingFragment(int byteCount, Action<bool> resolved)
            {
                this.byteCount = byteCount;
                this.resolved = resolved;
            }

            internal void Resolve(bool suppressed)
            {
                this.suppressed |= suppressed;
                resolvedBytes++;
                if (resolvedBytes == byteCount) resolved(!this.suppressed);
            }
        }

        private enum CandidateStatus
        {
            Incomplete,
            Complete,
            Invalid
        }

        private enum ReplyKind
        {
            Exact,
            PrimaryDeviceAttributes,
            SecondaryDeviceAttributes,
            DeviceStatus,
            PrivateDeviceStatus,
            CursorPosition,
            PrivateCursorPosition,
            KeyboardFlags,
            ModeReport,
            StatusString,
            TerminalCapability,
            PaletteColor,
            DefaultForeground,
            DefaultBackground,
            DefaultCursor
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                expiryTimer.Change(Timeout.Infinite, Timeout.Infinite);
                foreach (var expectation in expected) ClearExpectation(expectation);
                expected.Clear();
                candidate.Clear();
                candidateExpectation = null;
                candidateExpiresUtc = default;
                deferredPassThrough.SetLength(0);
            }
            expiryTimer.Dispose();
        }
    }
}
