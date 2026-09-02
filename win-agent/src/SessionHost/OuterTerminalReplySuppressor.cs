using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TermLink.SessionHost
{
    /// <summary>
    /// Suppresses bounded terminal-generated replies for which terminal-core has already emitted
    /// the authoritative PTY reply. If terminal-core has no configured OSC color baseline, the
    /// outer terminal's expected color reply is forwarded to ConPTY as protocol input without
    /// being classified as a local key press. Renderer-local text-area pixel reports are consumed
    /// because terminal-core deliberately leaves CSI 14 t unanswered. Ordinary key input and
    /// unmatched escape sequences pass through unchanged. A query or reply may span multiple reads.
    /// </summary>
    internal sealed class OuterTerminalReplySuppressor : IDisposable
    {
        private static readonly TimeSpan ExpectationLifetime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CandidateLifetime = TimeSpan.FromMilliseconds(250);
        private const int MaximumCsiReplyBytes = 1024;
        private const int MaximumReplyBytes = 64 * 1024;
        private const int MaximumPaletteOperations = 256;

        private readonly object sync = new object();
        private readonly List<ExpectedReply> expected = new List<ExpectedReply>();
        private readonly List<AuthorityReply> pendingAuthority = new List<AuthorityReply>();
        private readonly List<PendingByte> candidate = new List<PendingByte>(128);
        private readonly MemoryStream deferredPassThrough = new MemoryStream();
        private readonly Action<byte[]> expiredRawPassThrough;
        private readonly Action<byte[]> terminalReplyPassThrough;
        private readonly Timer expiryTimer;
        private readonly List<byte> observedOscSelector = new List<byte>(4);
        private readonly List<byte> observedOscPayload = new List<byte>(256);
        private readonly List<byte> observedCsiBody = new List<byte>(32);
        private readonly bool[] observedPaletteKnown = new bool[256];
        private readonly bool[] observedDefaultColorKnown = new bool[3];
        private DateTime candidateExpiresUtc;
        private long nextExpectationId;
        private long candidateMaximumExpectationId;
        private long nextProjectionId;
        private List<AuthorityReply> projectingAuthority;
        private OutputParseState outputParseState;
        private bool disposed;

        public OuterTerminalReplySuppressor(
            Action<byte[]> expiredRawPassThrough = null,
            Action<byte[]> terminalReplyPassThrough = null)
        {
            this.expiredRawPassThrough = expiredRawPassThrough;
            this.terminalReplyPassThrough = terminalReplyPassThrough;
            expiryTimer = new Timer(OnExpiryTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Expect(byte[] authoritativeReply)
        {
            ValidateReply(authoritativeReply, nameof(authoritativeReply));
            lock (sync)
            {
                ThrowIfDisposed();
                var now = DateTime.UtcNow;
                PurgeExpired(now);
                int qualifier;
                var kind = ReplyKindOf(authoritativeReply, 0, authoritativeReply.Length, out qualifier);
                expected.Add(new ExpectedReply(
                    NextExpectationId(),
                    kind,
                    qualifier,
                    (byte[])authoritativeReply.Clone(),
                    ExpectationDisposition.Suppress,
                    now + ExpectationLifetime,
                    projectionId: 0,
                    sourceSequence: 0,
                    sourceOffsetEnd: 0,
                    sourceOrdinal: 0,
                    hasSourceCoordinate: false));
                ScheduleExpiryTimer(now);
            }
        }

        public void RegisterAuthorityReply(
            byte[] authoritativeReply,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            uint sourceOrdinal)
        {
            ValidateReply(authoritativeReply, nameof(authoritativeReply));
            if (sourceSequence == 0) throw new ArgumentOutOfRangeException(nameof(sourceSequence));
            if (sourceOffsetEnd == 0) throw new ArgumentOutOfRangeException(nameof(sourceOffsetEnd));
            lock (sync)
            {
                ThrowIfDisposed();
                int qualifier;
                var kind = ReplyKindOf(authoritativeReply, 0, authoritativeReply.Length, out qualifier);
                pendingAuthority.Add(new AuthorityReply(
                    kind,
                    qualifier,
                    (byte[])authoritativeReply.Clone(),
                    sourceSequence,
                    sourceOffsetEnd,
                    sourceOrdinal));
            }
        }

        /// <summary>
        /// Observes the exact ConPTY output before it is projected to Windows Terminal. Only
        /// syntactically bounded OSC 4/10/11/12 color queries create expectations. This lets an
        /// outer-terminal reply be distinguished from human input even when terminal-core has no
        /// color baseline from which to mint an authoritative reply.
        /// </summary>
        public void ObserveOutput(byte[] output, int length)
        {
            ProjectOutput(output, length, 0, () => true);
        }

        public bool ProjectOutput(
            byte[] output,
            int length,
            ulong sourceSequence,
            Func<bool> projection)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (length < 0 || length > output.Length) throw new ArgumentOutOfRangeException(nameof(length));
            if (projection == null) throw new ArgumentNullException(nameof(projection));

            lock (sync)
            {
                ThrowIfDisposed();
                var now = DateTime.UtcNow;
                PurgeExpired(now);
                var projectionId = NextProjectionId();
                projectingAuthority = TakeAuthorityReplies(sourceSequence);
                try
                {
                    for (var offset = 0; offset < length; offset++)
                    {
                        ObserveOutputByte(
                            output[offset],
                            now,
                            sourceSequence,
                            checked((uint)offset + 1),
                            projectionId);
                    }
                    ActivateRemainingAuthorityReplies(projectionId, now);
                    bool projected;
                    try
                    {
                        projected = projection();
                    }
                    catch
                    {
                        CancelProjection(projectionId);
                        throw;
                    }
                    if (projected)
                    {
                        var projectedAt = DateTime.UtcNow;
                        RefreshProjectionExpiry(projectionId, projectedAt + ExpectationLifetime);
                        ScheduleExpiryTimer(projectedAt);
                    }
                    else
                    {
                        CancelProjection(projectionId);
                        ScheduleExpiryTimer(DateTime.UtcNow);
                    }
                    return projected;
                }
                catch
                {
                    ClearProjectingAuthority();
                    CancelProjection(projectionId);
                    throw;
                }
                finally
                {
                    projectingAuthority = null;
                }
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
                candidateExpiresUtc = now + CandidateLifetime;
                candidateMaximumExpectationId = nextExpectationId;
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
                    var matchIndex = MatchingExpectationIndex(bytes, 0, bytes.Length);
                    if (matchIndex >= 0)
                    {
                        var matched = expected[matchIndex];
                        expected.RemoveAt(matchIndex);
                        var forward = matched.Disposition == ExpectationDisposition.Forward;
                        ClearExpectation(matched);
                        ResolveCandidate(suppressed: true, output, retainTrailingEscape: false);
                        candidate.Clear();
                        candidateExpiresUtc = default;
                        candidateMaximumExpectationId = 0;
                        if (forward) terminalReplyPassThrough(bytes);
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
            candidateExpiresUtc = default;
            candidateMaximumExpectationId = 0;
            if (retainTrailingEscape && expected.Count != 0)
            {
                candidate.Add(trailing);
                candidateExpiresUtc = DateTime.UtcNow + CandidateLifetime;
                candidateMaximumExpectationId = nextExpectationId;
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
            for (var index = expected.Count - 1; index >= 0; index--)
            {
                if (expected[index].ExpiresUtc > now) continue;
                var expired = expected[index];
                expected.RemoveAt(index);
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
            candidateExpiresUtc = default;
            candidateMaximumExpectationId = 0;
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
            foreach (var expectation in expected)
            {
                if (!deadline.HasValue || expectation.ExpiresUtc < deadline.Value)
                    deadline = expectation.ExpiresUtc;
            }
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
            if (expectation.ExactBytes != null)
                Array.Clear(expectation.ExactBytes, 0, expectation.ExactBytes.Length);
        }

        private void ObserveOutputByte(
            byte value,
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            switch (outputParseState)
            {
                case OutputParseState.Normal:
                    if (value == 0x1b)
                    {
                        outputParseState = OutputParseState.Escape;
                    }
                    else if (value == 0x9b)
                    {
                        observedCsiBody.Clear();
                        outputParseState = OutputParseState.Csi;
                    }
                    return;
                case OutputParseState.Escape:
                    if (value == (byte)'[')
                    {
                        observedCsiBody.Clear();
                        outputParseState = OutputParseState.Csi;
                    }
                    else if (value == (byte)']')
                    {
                        observedOscSelector.Clear();
                        observedOscPayload.Clear();
                        outputParseState = OutputParseState.OscSelector;
                    }
                    else
                    {
                        outputParseState = value == 0x1b
                            ? OutputParseState.Escape
                            : OutputParseState.Normal;
                    }
                    return;
                case OutputParseState.Csi:
                    if (value >= 0x40 && value <= 0x7e)
                    {
                        CompleteObservedCsi(
                            value,
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                    else if (value >= 0x20 && value <= 0x3f &&
                        observedCsiBody.Count < MaximumCsiReplyBytes)
                    {
                        observedCsiBody.Add(value);
                    }
                    else if (value == 0x1b)
                    {
                        observedCsiBody.Clear();
                        outputParseState = OutputParseState.Escape;
                    }
                    else if (value == 0x9b)
                    {
                        observedCsiBody.Clear();
                    }
                    else
                    {
                        ResetObservedCsi();
                    }
                    return;
                case OutputParseState.OscSelector:
                    if (value == (byte)';')
                    {
                        outputParseState = OutputParseState.OscPayload;
                    }
                    else if (value == 0x07)
                    {
                        CompleteObservedOsc(
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                    else if (value == 0x1b)
                    {
                        outputParseState = OutputParseState.OscSelectorEscape;
                    }
                    else if (observedOscSelector.Count < 4 && value >= (byte)'0' && value <= (byte)'9')
                    {
                        observedOscSelector.Add(value);
                    }
                    else
                    {
                        outputParseState = OutputParseState.IgnoreOsc;
                    }
                    return;
                case OutputParseState.OscSelectorEscape:
                    if (value == (byte)'\\')
                    {
                        CompleteObservedOsc(
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                    else
                    {
                        outputParseState = value == 0x1b
                            ? OutputParseState.IgnoreOscEscape
                            : OutputParseState.IgnoreOsc;
                    }
                    return;
                case OutputParseState.OscPayload:
                    if (value == 0x07)
                    {
                        CompleteObservedOsc(
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                    else if (value == 0x1b)
                    {
                        outputParseState = OutputParseState.OscEscape;
                    }
                    else if (observedOscPayload.Count < MaximumReplyBytes)
                    {
                        observedOscPayload.Add(value);
                    }
                    else
                    {
                        outputParseState = OutputParseState.IgnoreOsc;
                    }
                    return;
                case OutputParseState.OscEscape:
                    if (value == (byte)'\\')
                    {
                        CompleteObservedOsc(
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                    else if (observedOscPayload.Count + 2 <= MaximumReplyBytes)
                    {
                        observedOscPayload.Add(0x1b);
                        observedOscPayload.Add(value);
                        outputParseState = OutputParseState.OscPayload;
                    }
                    else
                    {
                        outputParseState = OutputParseState.IgnoreOsc;
                    }
                    return;
                case OutputParseState.IgnoreOsc:
                    if (value == 0x07) ResetObservedOsc();
                    else if (value == 0x1b) outputParseState = OutputParseState.IgnoreOscEscape;
                    return;
                case OutputParseState.IgnoreOscEscape:
                    if (value == (byte)'\\') ResetObservedOsc();
                    else outputParseState = value == 0x1b
                        ? OutputParseState.IgnoreOscEscape
                        : OutputParseState.IgnoreOsc;
                    return;
                default:
                    throw new InvalidOperationException("Unknown terminal output parser state.");
            }
        }

        private void CompleteObservedCsi(
            byte finalByte,
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            try
            {
                // CSI 14 t asks for renderer-local text-area pixel geometry. terminal-core owns
                // only durable cell geometry and intentionally emits no PTY reply. Windows
                // Terminal still answers the projected query through the console input buffer;
                // consume that compatibility reply so it cannot masquerade as a local key press.
                if (finalByte == (byte)'t' && IsExactAscii(observedCsiBody, "14"))
                {
                    EnqueueProjectionExpectation(
                        ReplyKind.TextAreaPixelSize,
                        4,
                        ExpectationDisposition.Suppress,
                        now,
                        sourceSequence,
                        sourceOffsetEnd,
                        projectionId);
                }
            }
            finally
            {
                ResetObservedCsi();
            }
        }

        private void ResetObservedCsi()
        {
            observedCsiBody.Clear();
            outputParseState = OutputParseState.Normal;
        }

        private void CompleteObservedOsc(
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            try
            {
                int selector;
                if (!TryParseDecimal(observedOscSelector, 0, observedOscSelector.Count, ushort.MaxValue, out selector))
                    return;
                if (selector >= 10 && selector <= 12)
                {
                    var slot = selector - 10;
                    var kind = selector == 10
                        ? ReplyKind.DefaultForeground
                        : selector == 11
                            ? ReplyKind.DefaultBackground
                            : ReplyKind.DefaultCursor;
                    if (observedOscPayload.Count == 1 && observedOscPayload[0] == (byte)'?')
                    {
                        if (sourceSequence != 0 && observedDefaultColorKnown[slot])
                        {
                            ReconcileAuthorityExpectation(
                                kind,
                                -1,
                                now,
                                sourceSequence,
                                sourceOffsetEnd,
                                projectionId);
                        }
                        else
                        {
                            EnqueueExternalExpectation(
                                kind,
                                -1,
                                now,
                                sourceSequence,
                                sourceOffsetEnd,
                                projectionId);
                        }
                    }
                    else if (sourceSequence != 0)
                    {
                        observedDefaultColorKnown[slot] = !IsExactAscii(observedOscPayload, "default");
                    }
                    return;
                }
                if (selector == 4)
                {
                    ObservePaletteQueries(
                        now,
                        sourceSequence,
                        sourceOffsetEnd,
                        projectionId);
                }
                else if (selector == 104 && sourceSequence != 0)
                {
                    ResetObservedPaletteKnowledge();
                }
                else if (selector >= 110 && selector <= 112 && sourceSequence != 0)
                {
                    observedDefaultColorKnown[selector - 110] = false;
                }
            }
            finally
            {
                ResetObservedOsc();
            }
        }

        private void ObservePaletteQueries(
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            var fields = new List<Field>(MaximumPaletteOperations * 2);
            var start = 0;
            for (var index = 0; index <= observedOscPayload.Count; index++)
            {
                if (index != observedOscPayload.Count && observedOscPayload[index] != (byte)';') continue;
                if (fields.Count >= MaximumPaletteOperations * 2) return;
                fields.Add(new Field(start, index - start));
                start = index + 1;
            }
            if (fields.Count == 0 || (fields.Count & 1) != 0) return;

            for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex += 2)
            {
                var indexField = fields[fieldIndex];
                int paletteIndex;
                if (!TryParseDecimal(
                    observedOscPayload,
                    indexField.Offset,
                    indexField.Length,
                    byte.MaxValue,
                    out paletteIndex))
                {
                    return;
                }
                var valueField = fields[fieldIndex + 1];
                if (valueField.Length == 1 &&
                    observedOscPayload[valueField.Offset] == (byte)'?')
                {
                    if (sourceSequence != 0 && observedPaletteKnown[paletteIndex])
                    {
                        ReconcileAuthorityExpectation(
                            ReplyKind.PaletteColor,
                            paletteIndex,
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                    else
                    {
                        EnqueueExternalExpectation(
                            ReplyKind.PaletteColor,
                            paletteIndex,
                            now,
                            sourceSequence,
                            sourceOffsetEnd,
                            projectionId);
                    }
                }
                else if (sourceSequence != 0)
                {
                    observedPaletteKnown[paletteIndex] = true;
                }
            }
        }

        private void ResetObservedPaletteKnowledge()
        {
            if (observedOscPayload.Count == 0)
            {
                Array.Clear(observedPaletteKnown, 0, observedPaletteKnown.Length);
                return;
            }
            var fieldStart = 0;
            for (var index = 0; index <= observedOscPayload.Count; index++)
            {
                if (index != observedOscPayload.Count && observedOscPayload[index] != (byte)';') continue;
                int paletteIndex;
                if (!TryParseDecimal(
                    observedOscPayload,
                    fieldStart,
                    index - fieldStart,
                    byte.MaxValue,
                    out paletteIndex)) return;
                observedPaletteKnown[paletteIndex] = false;
                fieldStart = index + 1;
            }
        }

        private static bool IsExactAscii(IList<byte> bytes, string value)
        {
            if (bytes.Count != value.Length) return false;
            for (var index = 0; index < value.Length; index++)
                if (bytes[index] != (byte)value[index]) return false;
            return true;
        }

        private void EnqueueExternalExpectation(
            ReplyKind kind,
            int qualifier,
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            if (terminalReplyPassThrough == null) return;
            EnqueueProjectionExpectation(
                kind,
                qualifier,
                ExpectationDisposition.Forward,
                now,
                sourceSequence,
                sourceOffsetEnd,
                projectionId);
        }

        private void EnqueueProjectionExpectation(
            ReplyKind kind,
            int qualifier,
            ExpectationDisposition disposition,
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            expected.Add(new ExpectedReply(
                NextExpectationId(),
                kind,
                qualifier,
                null,
                disposition,
                now + ExpectationLifetime,
                projectionId,
                sourceSequence,
                sourceOffsetEnd,
                sourceOrdinal: 0,
                hasSourceCoordinate: sourceSequence != 0));
        }

        private void ReconcileAuthorityExpectation(
            ReplyKind kind,
            int qualifier,
            DateTime now,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            long projectionId)
        {
            if (projectingAuthority == null)
                throw new InvalidOperationException("No terminal output projection is active.");
            for (var index = 0; index < projectingAuthority.Count; index++)
            {
                var authority = projectingAuthority[index];
                if (
                    authority.SourceSequence == sourceSequence &&
                    authority.SourceOffsetEnd == sourceOffsetEnd &&
                    authority.Kind == kind && authority.Qualifier == qualifier)
                {
                    projectingAuthority.RemoveAt(index);
                    expected.Add(NewAuthorityExpectation(
                        authority,
                        projectionId,
                        now + ExpectationLifetime));
                    authority.ReleaseOwnership();
                    return;
                }
            }
            throw new InvalidDataException(
                "terminal-core color reply did not match its projected output query coordinate " +
                "(kind=" + kind + ", sequence=" + sourceSequence +
                ", offset=" + sourceOffsetEnd +
                ", pending=" + projectingAuthority.Count + ").");
        }

        private void ResetObservedOsc()
        {
            observedOscSelector.Clear();
            observedOscPayload.Clear();
            outputParseState = OutputParseState.Normal;
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
            int candidateQualifier;
            var candidateKind = ReplyKindOf(candidateBytes, offset, length, out candidateQualifier);
            if (expectation.Kind != ReplyKind.Exact && expectation.Kind == candidateKind &&
                expectation.Qualifier == candidateQualifier)
                return true;
            if (expectation.ExactBytes == null) return false;
            if (expectation.ExactBytes.Length != length) return false;
            for (var index = 0; index < length; index++)
                if (expectation.ExactBytes[index] != candidateBytes[offset + index]) return false;
            return true;
        }

        private int MatchingExpectationIndex(byte[] bytes, int offset, int length)
        {
            for (var index = 0; index < expected.Count; index++)
            {
                if (expected[index].Id <= candidateMaximumExpectationId &&
                    Matches(expected[index], bytes, offset, length)) return index;
            }
            return -1;
        }

        private long NextExpectationId()
        {
            if (nextExpectationId == long.MaxValue)
                throw new InvalidOperationException("Terminal reply expectation sequence is exhausted.");
            return ++nextExpectationId;
        }

        private long NextProjectionId()
        {
            if (nextProjectionId == long.MaxValue)
                throw new InvalidOperationException("Terminal output projection sequence is exhausted.");
            return ++nextProjectionId;
        }

        private List<AuthorityReply> TakeAuthorityReplies(ulong sourceSequence)
        {
            var result = new List<AuthorityReply>();
            if (sourceSequence == 0) return result;
            for (var index = 0; index < pendingAuthority.Count;)
            {
                var authority = pendingAuthority[index];
                if (authority.SourceSequence != sourceSequence)
                {
                    index++;
                    continue;
                }
                pendingAuthority.RemoveAt(index);
                result.Add(authority);
            }
            return result;
        }

        private void ActivateRemainingAuthorityReplies(long projectionId, DateTime now)
        {
            if (projectingAuthority == null) return;
            foreach (var authority in projectingAuthority)
            {
                expected.Add(NewAuthorityExpectation(
                    authority,
                    projectionId,
                    now + ExpectationLifetime));
                authority.ReleaseOwnership();
            }
            projectingAuthority.Clear();
        }

        private ExpectedReply NewAuthorityExpectation(
            AuthorityReply authority,
            long projectionId,
            DateTime expiresUtc)
        {
            return new ExpectedReply(
                NextExpectationId(),
                authority.Kind,
                authority.Qualifier,
                authority.ExactBytes,
                ExpectationDisposition.Suppress,
                expiresUtc,
                projectionId,
                authority.SourceSequence,
                authority.SourceOffsetEnd,
                authority.SourceOrdinal,
                hasSourceCoordinate: true);
        }

        private void ClearProjectingAuthority()
        {
            if (projectingAuthority == null) return;
            foreach (var authority in projectingAuthority) authority.Clear();
            projectingAuthority.Clear();
        }

        private void CancelProjection(long projectionId)
        {
            for (var index = expected.Count - 1; index >= 0; index--)
            {
                if (expected[index].ProjectionId != projectionId) continue;
                var canceled = expected[index];
                expected.RemoveAt(index);
                ClearExpectation(canceled);
            }
        }

        private void RefreshProjectionExpiry(long projectionId, DateTime expiresUtc)
        {
            foreach (var expectation in expected)
                if (expectation.ProjectionId == projectionId) expectation.ExpiresUtc = expiresUtc;
        }

        private static void ValidateReply(byte[] reply, string parameterName)
        {
            if (reply == null || reply.Length == 0 || reply.Length > MaximumReplyBytes)
                throw new ArgumentException("A bounded terminal reply is required.", parameterName);
        }

        private static ReplyKind ReplyKindOf(byte[] bytes, int offset, int length, out int qualifier)
        {
            qualifier = -1;
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
                if (final == (byte)'t' && IsTextAreaPixelSizeReply(bytes, offset, length))
                {
                    qualifier = 4;
                    return ReplyKind.TextAreaPixelSize;
                }
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
                var contentEnd = bytes[end - 1] == 0x07 ? end - 1 : end - 2;
                if (contentEnd <= selectorEnd + 1) return ReplyKind.Exact;
                var selectorLength = selectorEnd - offset - 2;
                if (selectorLength == 1 && bytes[offset + 2] == (byte)'4')
                {
                    var indexEnd = Array.IndexOf(
                        bytes,
                        (byte)';',
                        selectorEnd + 1,
                        contentEnd - selectorEnd - 1);
                    if (indexEnd < 0 || !TryParseDecimal(
                        bytes,
                        selectorEnd + 1,
                        indexEnd - selectorEnd - 1,
                        byte.MaxValue,
                        out qualifier) || !IsRgbColorPayload(
                            bytes,
                            indexEnd + 1,
                            contentEnd - indexEnd - 1))
                    {
                        qualifier = -1;
                        return ReplyKind.Exact;
                    }
                    return ReplyKind.PaletteColor;
                }
                if (selectorLength == 2 && bytes[offset + 2] == (byte)'1')
                {
                    if (!IsRgbColorPayload(
                        bytes,
                        selectorEnd + 1,
                        contentEnd - selectorEnd - 1)) return ReplyKind.Exact;
                    if (bytes[offset + 3] == (byte)'0') return ReplyKind.DefaultForeground;
                    if (bytes[offset + 3] == (byte)'1') return ReplyKind.DefaultBackground;
                    if (bytes[offset + 3] == (byte)'2') return ReplyKind.DefaultCursor;
                }
            }
            return ReplyKind.Exact;
        }

        private static bool IsTextAreaPixelSizeReply(byte[] bytes, int offset, int length)
        {
            // Xterm window-operation response: CSI 4 ; height ; width t.
            if (length < 8 || bytes[offset] != 0x1b || bytes[offset + 1] != (byte)'[' ||
                bytes[offset + length - 1] != (byte)'t') return false;
            var end = offset + length - 1;
            var firstSeparator = Array.IndexOf(bytes, (byte)';', offset + 2, end - offset - 2);
            if (firstSeparator < 0) return false;
            var secondSeparator = Array.IndexOf(
                bytes,
                (byte)';',
                firstSeparator + 1,
                end - firstSeparator - 1);
            if (secondSeparator < 0 || Array.IndexOf(
                bytes,
                (byte)';',
                secondSeparator + 1,
                end - secondSeparator - 1) >= 0) return false;
            int operation;
            int height;
            int width;
            return TryParseDecimal(
                    bytes,
                    offset + 2,
                    firstSeparator - offset - 2,
                    4,
                    out operation) && operation == 4 &&
                TryParseDecimal(
                    bytes,
                    firstSeparator + 1,
                    secondSeparator - firstSeparator - 1,
                    int.MaxValue,
                    out height) && height > 0 &&
                TryParseDecimal(
                    bytes,
                    secondSeparator + 1,
                    end - secondSeparator - 1,
                    int.MaxValue,
                    out width) && width > 0;
        }

        private static bool IsRgbColorPayload(IList<byte> bytes, int offset, int length)
        {
            if (length < 9 || offset < 0 || offset > bytes.Count - length) return false;
            if (bytes[offset] != (byte)'r' || bytes[offset + 1] != (byte)'g' ||
                bytes[offset + 2] != (byte)'b' || bytes[offset + 3] != (byte)':') return false;
            var componentStart = offset + 4;
            var componentCount = 0;
            var end = offset + length;
            for (var index = componentStart; index <= end; index++)
            {
                if (index != end && bytes[index] != (byte)'/') continue;
                var componentLength = index - componentStart;
                if (componentLength < 1 || componentLength > 4) return false;
                for (var digitIndex = componentStart; digitIndex < index; digitIndex++)
                {
                    var digit = bytes[digitIndex];
                    var hexadecimal = digit >= (byte)'0' && digit <= (byte)'9' ||
                        digit >= (byte)'a' && digit <= (byte)'f' ||
                        digit >= (byte)'A' && digit <= (byte)'F';
                    if (!hexadecimal) return false;
                }
                componentCount++;
                componentStart = index + 1;
            }
            return componentCount == 3;
        }

        private static bool TryParseDecimal(
            IList<byte> bytes,
            int offset,
            int length,
            int maximum,
            out int value)
        {
            value = 0;
            if (length <= 0 || offset < 0 || offset > bytes.Count - length) return false;
            for (var index = 0; index < length; index++)
            {
                var digit = bytes[offset + index];
                if (digit < (byte)'0' || digit > (byte)'9') return false;
                var next = (long)value * 10 + digit - (byte)'0';
                if (next > maximum) return false;
                value = (int)next;
            }
            return true;
        }

        private sealed class ExpectedReply
        {
            internal ExpectedReply(
                long id,
                ReplyKind kind,
                int qualifier,
                byte[] exactBytes,
                ExpectationDisposition disposition,
                DateTime expiresUtc,
                long projectionId,
                ulong sourceSequence,
                uint sourceOffsetEnd,
                uint sourceOrdinal,
                bool hasSourceCoordinate)
            {
                Id = id;
                Kind = kind;
                Qualifier = qualifier;
                ExactBytes = exactBytes;
                Disposition = disposition;
                ExpiresUtc = expiresUtc;
                ProjectionId = projectionId;
                SourceSequence = sourceSequence;
                SourceOffsetEnd = sourceOffsetEnd;
                SourceOrdinal = sourceOrdinal;
                HasSourceCoordinate = hasSourceCoordinate;
            }

            internal long Id { get; }
            internal ReplyKind Kind { get; }
            internal int Qualifier { get; }
            internal byte[] ExactBytes { get; set; }
            internal ExpectationDisposition Disposition { get; set; }
            internal DateTime ExpiresUtc { get; set; }
            internal long ProjectionId { get; }
            internal ulong SourceSequence { get; }
            internal uint SourceOffsetEnd { get; }
            internal uint SourceOrdinal { get; }
            internal bool HasSourceCoordinate { get; }
        }

        private sealed class AuthorityReply
        {
            internal AuthorityReply(
                ReplyKind kind,
                int qualifier,
                byte[] exactBytes,
                ulong sourceSequence,
                uint sourceOffsetEnd,
                uint sourceOrdinal)
            {
                Kind = kind;
                Qualifier = qualifier;
                ExactBytes = exactBytes;
                SourceSequence = sourceSequence;
                SourceOffsetEnd = sourceOffsetEnd;
                SourceOrdinal = sourceOrdinal;
            }

            internal ReplyKind Kind { get; }
            internal int Qualifier { get; }
            internal byte[] ExactBytes { get; private set; }
            internal ulong SourceSequence { get; }
            internal uint SourceOffsetEnd { get; }
            internal uint SourceOrdinal { get; }

            internal void ReleaseOwnership()
            {
                ExactBytes = null;
            }

            internal void Clear()
            {
                if (ExactBytes != null) Array.Clear(ExactBytes, 0, ExactBytes.Length);
                ExactBytes = null;
            }
        }

        private readonly struct Field
        {
            internal Field(int offset, int length)
            {
                Offset = offset;
                Length = length;
            }

            internal int Offset { get; }
            internal int Length { get; }
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

        private enum ExpectationDisposition
        {
            Suppress,
            Forward
        }

        private enum OutputParseState
        {
            Normal,
            Escape,
            OscSelector,
            OscSelectorEscape,
            OscPayload,
            OscEscape,
            IgnoreOsc,
            IgnoreOscEscape,
            Csi
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
            DefaultCursor,
            TextAreaPixelSize
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
                foreach (var authority in pendingAuthority) authority.Clear();
                pendingAuthority.Clear();
                ClearProjectingAuthority();
                projectingAuthority = null;
                candidate.Clear();
                candidateExpiresUtc = default;
                candidateMaximumExpectationId = 0;
                deferredPassThrough.SetLength(0);
                observedOscSelector.Clear();
                observedOscPayload.Clear();
                Array.Clear(observedPaletteKnown, 0, observedPaletteKnown.Length);
                Array.Clear(observedDefaultColorKnown, 0, observedDefaultColorKnown.Length);
                outputParseState = OutputParseState.Normal;
            }
            expiryTimer.Dispose();
        }
    }
}
