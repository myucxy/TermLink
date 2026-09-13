using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using TermLink.Agent;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionDirectCutoverSignalCodecValidation
    {
        internal static void Validate()
        {
            Require(ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation == null,
                "The cutover codec temporary-buffer observer was already installed.");
            Require(ProductionDirectCutoverCodec.TemporaryBufferObserverForValidation == null,
                "The proposal codec temporary-buffer observer was already installed.");
            ValidateProposalCreationOwnershipAndCleanup();
            ValidateProposalParseOwnershipAndCleanup();
            ValidateProposalCanonicalMismatchCleanup();
            ValidateProposalParseRejectionCleanup();
            ValidateProposalExceptionalCleanup();
            ValidateAcceptedAndRejectedOwnershipTransfer();
            ValidateCommitOwnershipTransfer();
            ValidateFallbackSuccessCleanup();
            ValidateNonCanonicalRejectionCleanup();
            ValidateMalformedPayloadCleanup();
            ValidateExceptionalCleanup();
            Require(ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation == null,
                "The cutover codec temporary-buffer observer escaped validation.");
            Require(ProductionDirectCutoverCodec.TemporaryBufferObserverForValidation == null,
                "The proposal codec temporary-buffer observer escaped validation.");
        }

        private static void ValidateProposalCreationOwnershipAndCleanup()
        {
            var binding = NonZeroHash(0x11);
            ProductionDirectCutoverProposal proposal = null;
            try
            {
                var observed = CaptureProposal(() => proposal = CreateProposal(binding));
                Require(proposal != null && proposal.DirectPathBinding.SequenceEqual(binding) &&
                        !IsAllZero(proposal.BarrierSha256) && !IsAllZero(proposal.WireBytes),
                    "Proposal creation did not preserve caller-owned result data.");
                Require(ReferenceEquals(
                            Single(observed.Cutover,
                                ProductionDirectCutoverTemporaryBufferKind.ResultDirectPathBinding),
                            proposal.DirectPathBinding) &&
                        ReferenceEquals(
                            Single(observed.Cutover,
                                ProductionDirectCutoverTemporaryBufferKind.ResultBarrierSha256),
                            proposal.BarrierSha256) &&
                        ReferenceEquals(
                            Single(observed.Cutover,
                                ProductionDirectCutoverTemporaryBufferKind.ResultWireBytes),
                            proposal.WireBytes),
                    "Proposal result buffers were copied again or cleared before ownership transfer.");
                RequireCutoverZeroed(
                    observed.Cutover,
                    ProductionDirectCutoverTemporaryBufferKind.CreateWire,
                    ProductionDirectCutoverTemporaryBufferKind.DigestInput);
                Require(observed.Signal.Count == 0,
                    "Inner proposal creation unexpectedly allocated signal-codec buffers.");
            }
            finally
            {
                ZeroProposal(proposal);
                CryptographicOperations.ZeroMemory(binding);
            }
        }

        private static void ValidateProposalParseOwnershipAndCleanup()
        {
            var binding = NonZeroHash(0x21);
            ProductionDirectCutoverProposal source = null;
            ProductionDirectCutoverProposal parsed = null;
            byte[] plaintext = null;
            try
            {
                source = CreateProposal(binding);
                plaintext = ProductionDirectCutoverSignalCodec.CreateProposal(source);
                var observed = CaptureProposal(() =>
                    Require(ProductionDirectCutoverSignalCodec.TryParseProposal(
                            plaintext,
                            ProposalConnectionId,
                            ProposalPathEpoch,
                            Direction.PcToAndroid,
                            out parsed),
                        "A canonical proposal signal was rejected."));

                Require(parsed != null && parsed.BarrierId == source.BarrierId &&
                        parsed.DirectPathBinding.SequenceEqual(source.DirectPathBinding) &&
                        parsed.BarrierSha256.SequenceEqual(source.BarrierSha256) &&
                        parsed.WireBytes.SequenceEqual(source.WireBytes),
                    "Canonical proposal parsing changed returned ownership data.");
                Require(ReferenceEquals(
                            Single(observed.Cutover,
                                ProductionDirectCutoverTemporaryBufferKind.ResultDirectPathBinding),
                            parsed.DirectPathBinding) &&
                        ReferenceEquals(
                            Single(observed.Cutover,
                                ProductionDirectCutoverTemporaryBufferKind.ResultBarrierSha256),
                            parsed.BarrierSha256) &&
                        ReferenceEquals(
                            Single(observed.Cutover,
                                ProductionDirectCutoverTemporaryBufferKind.ResultWireBytes),
                            parsed.WireBytes) &&
                        !IsAllZero(parsed.DirectPathBinding) &&
                        !IsAllZero(parsed.BarrierSha256) &&
                        !IsAllZero(parsed.WireBytes),
                    "Canonical proposal result buffers were not transferred intact.");
                RequireZeroed(
                    observed.Signal,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalPayload,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalCanonical);
                RequireCutoverZeroed(
                    observed.Cutover,
                    ProductionDirectCutoverTemporaryBufferKind.ParseOwnedInput,
                    ProductionDirectCutoverTemporaryBufferKind.ParseSessionId,
                    ProductionDirectCutoverTemporaryBufferKind.ParseCanonical,
                    ProductionDirectCutoverTemporaryBufferKind.DigestInput);
                Require(observed.Cutover.Count(value =>
                            value.Kind == ProductionDirectCutoverTemporaryBufferKind.ParseSessionId) == 2,
                    "Proposal parsing did not expose both per-session temporary IDs.");
            }
            finally
            {
                ZeroProposal(parsed);
                ZeroProposal(source);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(binding);
            }
        }

        private static void ValidateProposalCanonicalMismatchCleanup()
        {
            var binding = NonZeroHash(0x41);
            ProductionDirectCutoverProposal source = null;
            byte[] nonCanonicalSignal = null;
            byte[] nonCanonicalProposal = null;
            try
            {
                source = CreateProposal(binding);
                nonCanonicalSignal = AppendUnknownField(
                    ProductionDirectCutoverSignalCodec.CreateProposal(source));
                var signalObserved = CaptureProposal(() =>
                    Require(!ProductionDirectCutoverSignalCodec.TryParseProposal(
                            nonCanonicalSignal,
                            ProposalConnectionId,
                            ProposalPathEpoch,
                            Direction.PcToAndroid,
                            out var parsed) && parsed == null,
                        "A non-canonical proposal signal was accepted or exposed a rejected value."));
                RequireZeroed(
                    signalObserved.Signal,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalPayload,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalCanonical);
                RequireAllCutoverZeroed(signalObserved.Cutover);

                nonCanonicalProposal = AppendUnknownField(source.WireBytes.ToArray());
                var innerObserved = CaptureProposal(() =>
                    Require(!ProductionDirectCutoverCodec.TryParseProposal(
                            nonCanonicalProposal,
                            ProposalConnectionId,
                            ProposalPathEpoch,
                            Direction.PcToAndroid,
                            out var parsed) && parsed == null,
                        "A non-canonical inner proposal was accepted."));
                Require(innerObserved.Signal.Count == 0,
                    "Inner proposal mismatch unexpectedly allocated signal-codec buffers.");
                RequireCutoverZeroed(
                    innerObserved.Cutover,
                    ProductionDirectCutoverTemporaryBufferKind.ParseOwnedInput,
                    ProductionDirectCutoverTemporaryBufferKind.ParseSessionId,
                    ProductionDirectCutoverTemporaryBufferKind.ParseCanonical);
            }
            finally
            {
                ZeroProposal(source);
                if (nonCanonicalSignal != null)
                    CryptographicOperations.ZeroMemory(nonCanonicalSignal);
                if (nonCanonicalProposal != null)
                    CryptographicOperations.ZeroMemory(nonCanonicalProposal);
                CryptographicOperations.ZeroMemory(binding);
            }
        }

        private static void ValidateProposalExceptionalCleanup()
        {
            var binding = NonZeroHash(0x61);
            ProductionDirectCutoverProposal source = null;
            byte[] plaintext = null;
            try
            {
                var createObserved = CaptureProposal(
                    () => CreateProposal(binding),
                    cutoverThrowAt:
                        ProductionDirectCutoverTemporaryBufferKind.ResultWireBytes);
                RequireAllCutoverZeroed(createObserved.Cutover);

                source = CreateProposal(binding);
                plaintext = ProductionDirectCutoverSignalCodec.CreateProposal(source);
                var parseObserved = CaptureProposal(
                    () => ProductionDirectCutoverSignalCodec.TryParseProposal(
                        plaintext,
                        ProposalConnectionId,
                        ProposalPathEpoch,
                        Direction.PcToAndroid,
                        out _),
                    cutoverThrowAt:
                        ProductionDirectCutoverTemporaryBufferKind.ParseCanonical);
                RequireZeroed(
                    parseObserved.Signal,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalPayload);
                RequireAllCutoverZeroed(parseObserved.Cutover);

                var outerObserved = CaptureProposal(
                    () => ProductionDirectCutoverSignalCodec.TryParseProposal(
                        plaintext,
                        ProposalConnectionId,
                        ProposalPathEpoch,
                        Direction.PcToAndroid,
                        out _),
                    signalThrowAt:
                        ProductionDirectCutoverSignalTemporaryBufferKind.ProposalCanonical);
                RequireZeroed(
                    outerObserved.Signal,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalPayload,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalCanonical);
                RequireAllCutoverZeroed(outerObserved.Cutover);
            }
            finally
            {
                ZeroProposal(source);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(binding);
            }
        }

        private static void ValidateProposalParseRejectionCleanup()
        {
            var malformed = new byte[] { 0x80 };
            try
            {
                var observed = CaptureProposal(() =>
                    Require(!ProductionDirectCutoverCodec.TryParseProposal(
                            malformed,
                            ProposalConnectionId,
                            ProposalPathEpoch,
                            Direction.PcToAndroid,
                            out var parsed) && parsed == null,
                        "A malformed inner proposal was accepted."));
                Require(observed.Signal.Count == 0,
                    "Malformed inner proposal parsing allocated signal-codec buffers.");
                Require(observed.Cutover.Count == 1,
                    "Malformed inner proposal parsing allocated unexpected buffers.");
                RequireCutoverZeroed(
                    observed.Cutover,
                    ProductionDirectCutoverTemporaryBufferKind.ParseOwnedInput);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(malformed);
            }
        }

        private static readonly Guid ProposalConnectionId =
            Guid.Parse("11000000-0000-0000-0000-000000000001");

        private const ulong ProposalPathEpoch = 7;

        private static ProductionDirectCutoverProposal CreateProposal(byte[] binding) =>
            ProductionDirectCutoverCodec.CreateProposal(
                ProposalConnectionId,
                ProposalPathEpoch,
                Direction.PcToAndroid,
                Guid.Parse("22000000-0000-0000-0000-000000000002"),
                binding,
                new[]
                {
                    new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = Guid.Parse("33000000-0000-0000-0000-000000000001"),
                        FirstDirectSequence = 11
                    },
                    new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = Guid.Parse("33000000-0000-0000-0000-000000000002"),
                        FirstDirectSequence = 13
                    }
                },
                duplicateGraceMilliseconds: 250);

        private static void ValidateAcceptedAndRejectedOwnershipTransfer()
        {
            ValidateAcceptOwnershipTransfer(accepted: true, reasonCode: string.Empty);
            ValidateAcceptOwnershipTransfer(accepted: false, reasonCode: "REMOTE_REJECTED");
        }

        private static void ValidateAcceptOwnershipTransfer(bool accepted, string reasonCode)
        {
            var barrierId = Guid.NewGuid();
            var hash = NonZeroHash(accepted ? (byte)0x31 : (byte)0x51);
            var plaintext = ProductionDirectCutoverSignalCodec.CreateAccept(
                barrierId,
                hash,
                accepted,
                reasonCode);
            ProductionDirectCutoverAcceptSignal parsed = null;
            try
            {
                var observed = Capture(() =>
                    Require(ProductionDirectCutoverSignalCodec.TryParseAccept(
                            plaintext,
                            out parsed),
                        "A canonical cutover accept signal was rejected."));

                Require(parsed != null && parsed.BarrierId == barrierId &&
                        parsed.Accepted == accepted && parsed.ReasonCode == reasonCode &&
                        parsed.BarrierSha256.SequenceEqual(hash),
                    "A canonical cutover accept signal changed during parsing.");
                var observedHash = Single(
                    observed,
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptHash);
                Require(ReferenceEquals(observedHash, parsed.BarrierSha256) &&
                        !IsAllZero(observedHash),
                    "The accepted hash was not transferred to caller ownership intact.");
                RequireZeroed(
                    observed,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptCanonical);
            }
            finally
            {
                if (parsed?.BarrierSha256 != null)
                    CryptographicOperations.ZeroMemory(parsed.BarrierSha256);
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static void ValidateCommitOwnershipTransfer()
        {
            var barrierId = Guid.NewGuid();
            var hash = NonZeroHash(0x71);
            var plaintext = ProductionDirectCutoverSignalCodec.CreateCommit(barrierId, hash);
            ProductionDirectCutoverCommitSignal parsed = null;
            try
            {
                var observed = Capture(() =>
                    Require(ProductionDirectCutoverSignalCodec.TryParseCommit(
                            plaintext,
                            out parsed),
                        "A canonical cutover commit signal was rejected."));

                Require(parsed != null && parsed.BarrierId == barrierId &&
                        parsed.BarrierSha256.SequenceEqual(hash),
                    "A canonical cutover commit signal changed during parsing.");
                var observedHash = Single(
                    observed,
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitHash);
                Require(ReferenceEquals(observedHash, parsed.BarrierSha256) &&
                        !IsAllZero(observedHash),
                    "The committed hash was not transferred to caller ownership intact.");
                RequireZeroed(
                    observed,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitCanonical);
            }
            finally
            {
                if (parsed?.BarrierSha256 != null)
                    CryptographicOperations.ZeroMemory(parsed.BarrierSha256);
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static void ValidateFallbackSuccessCleanup()
        {
            var barrierId = Guid.NewGuid();
            var plaintext = ProductionDirectCutoverSignalCodec.CreateFallback(
                barrierId,
                "DIRECT_PATH_LOST",
                42);
            try
            {
                ProductionDirectFallbackSignal parsed = null;
                var observed = Capture(() =>
                    Require(ProductionDirectCutoverSignalCodec.TryParseFallback(
                            plaintext,
                            out parsed),
                        "A canonical fallback signal was rejected."));
                Require(parsed != null && parsed.BarrierId == barrierId &&
                        parsed.ReasonCode == "DIRECT_PATH_LOST" &&
                        parsed.LastReceivedDirectFrameSequence == 42,
                    "A canonical fallback signal changed during parsing.");
                RequireZeroed(
                    observed,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.FallbackCanonical);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static void ValidateNonCanonicalRejectionCleanup()
        {
            var barrierId = Guid.NewGuid();
            var hash = NonZeroHash(0x91);
            var accept = AppendUnknownField(
                ProductionDirectCutoverSignalCodec.CreateAccept(
                    barrierId,
                    hash,
                    accepted: false,
                    "NON_CANONICAL"));
            var commit = AppendUnknownField(
                ProductionDirectCutoverSignalCodec.CreateCommit(barrierId, hash));
            var fallback = AppendUnknownField(
                ProductionDirectCutoverSignalCodec.CreateFallback(
                    barrierId,
                    "NON_CANONICAL",
                    73));
            try
            {
                var acceptObserved = Capture(() =>
                    Require(!ProductionDirectCutoverSignalCodec.TryParseAccept(
                            accept,
                            out var parsed) && parsed == null,
                        "A non-canonical cutover accept signal was accepted."));
                RequireZeroed(
                    acceptObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptHash,
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptCanonical);

                var commitObserved = Capture(() =>
                    Require(!ProductionDirectCutoverSignalCodec.TryParseCommit(
                            commit,
                            out var parsed) && parsed == null,
                        "A non-canonical cutover commit signal was accepted."));
                RequireZeroed(
                    commitObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitHash,
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitCanonical);

                var fallbackObserved = Capture(() =>
                    Require(!ProductionDirectCutoverSignalCodec.TryParseFallback(
                            fallback,
                            out var parsed) && parsed == null,
                        "A non-canonical fallback signal was accepted."));
                RequireZeroed(
                    fallbackObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.FallbackCanonical);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(accept);
                CryptographicOperations.ZeroMemory(commit);
                CryptographicOperations.ZeroMemory(fallback);
            }
        }

        private static void ValidateMalformedPayloadCleanup()
        {
            var malformed = new byte[] { 0x80 };
            try
            {
                var observed = Capture(() =>
                    Require(!ProductionDirectCutoverSignalCodec.TryParseFallback(
                            malformed,
                            out var parsed) && parsed == null,
                        "A malformed signal payload was accepted."));
                RequireZeroed(
                    observed,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput);
                Require(observed.Count == 1,
                    "Malformed payload parsing allocated unexpected codec buffers.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(malformed);
            }
        }

        private static void ValidateExceptionalCleanup()
        {
            var barrierId = Guid.NewGuid();
            var hash = NonZeroHash(0xB1);
            var accept = ProductionDirectCutoverSignalCodec.CreateAccept(
                barrierId,
                hash,
                accepted: true,
                string.Empty);
            var commit = ProductionDirectCutoverSignalCodec.CreateCommit(barrierId, hash);
            var fallback = ProductionDirectCutoverSignalCodec.CreateFallback(
                barrierId,
                "FORCED_EXCEPTION",
                81);
            try
            {
                var acceptObserved = CaptureThrowingAt(
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptCanonical,
                    () => ProductionDirectCutoverSignalCodec.TryParseAccept(accept, out _));
                RequireZeroed(
                    acceptObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptHash,
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptCanonical);

                var commitObserved = CaptureThrowingAt(
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitCanonical,
                    () => ProductionDirectCutoverSignalCodec.TryParseCommit(commit, out _));
                RequireZeroed(
                    commitObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitHash,
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitCanonical);

                var fallbackObserved = CaptureThrowingAt(
                    ProductionDirectCutoverSignalTemporaryBufferKind.FallbackCanonical,
                    () => ProductionDirectCutoverSignalCodec.TryParseFallback(fallback, out _));
                RequireZeroed(
                    fallbackObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    ProductionDirectCutoverSignalTemporaryBufferKind.FallbackCanonical);

                var parserObserved = CaptureThrowingAt(
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    () => ProductionDirectCutoverSignalCodec.TryParseFallback(fallback, out _));
                RequireZeroed(
                    parserObserved,
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput);
                Require(parserObserved.Count == 1,
                    "Parser-input observer failure allocated unexpected codec buffers.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(accept);
                CryptographicOperations.ZeroMemory(commit);
                CryptographicOperations.ZeroMemory(fallback);
            }
        }

        private static List<ObservedBuffer> Capture(Action action)
        {
            var observed = new List<ObservedBuffer>();
            InstallObserver((kind, buffer) => observed.Add(new ObservedBuffer(kind, buffer)));
            try
            {
                action();
                return observed;
            }
            finally
            {
                ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation = null;
            }
        }

        private static List<ObservedBuffer> CaptureThrowingAt(
            ProductionDirectCutoverSignalTemporaryBufferKind throwAt,
            Action action)
        {
            var observed = new List<ObservedBuffer>();
            InstallObserver((kind, buffer) =>
            {
                observed.Add(new ObservedBuffer(kind, buffer));
                if (kind == throwAt) throw new ObserverFailureException();
            });
            try
            {
                try
                {
                    action();
                }
                catch (ObserverFailureException)
                {
                    return observed;
                }
                throw new InvalidOperationException(
                    $"The {throwAt} observation did not trigger the expected exception.");
            }
            finally
            {
                ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation = null;
            }
        }

        private static void InstallObserver(
            Action<ProductionDirectCutoverSignalTemporaryBufferKind, byte[]> observer)
        {
            Require(ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation == null,
                "A cutover codec temporary-buffer observer is already installed.");
            ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation = observer;
        }

        private static ProposalObservation CaptureProposal(
            Action action,
            ProductionDirectCutoverSignalTemporaryBufferKind? signalThrowAt = null,
            ProductionDirectCutoverTemporaryBufferKind? cutoverThrowAt = null)
        {
            Require(ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation == null,
                "A cutover signal-codec observer is already installed.");
            Require(ProductionDirectCutoverCodec.TemporaryBufferObserverForValidation == null,
                "A proposal-codec observer is already installed.");
            var observed = new ProposalObservation();
            ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation =
                (kind, buffer) =>
                {
                    observed.Signal.Add(new ObservedBuffer(kind, buffer));
                    if (kind == signalThrowAt) throw new ObserverFailureException();
                };
            ProductionDirectCutoverCodec.TemporaryBufferObserverForValidation =
                (kind, buffer) =>
                {
                    observed.Cutover.Add(new ObservedCutoverBuffer(kind, buffer));
                    if (kind == cutoverThrowAt) throw new ObserverFailureException();
                };
            var expectsException = signalThrowAt.HasValue || cutoverThrowAt.HasValue;
            try
            {
                try
                {
                    action();
                }
                catch (ObserverFailureException) when (expectsException)
                {
                    return observed;
                }
                if (expectsException)
                    throw new InvalidOperationException(
                        "The proposal codec observer did not trigger the expected exception.");
                return observed;
            }
            finally
            {
                ProductionDirectCutoverSignalCodec.TemporaryBufferObserverForValidation = null;
                ProductionDirectCutoverCodec.TemporaryBufferObserverForValidation = null;
            }
        }

        private static byte[] Single(
            IEnumerable<ObservedBuffer> observed,
            ProductionDirectCutoverSignalTemporaryBufferKind kind) =>
            observed.Single(value => value.Kind == kind).Buffer;

        private static byte[] Single(
            IEnumerable<ObservedCutoverBuffer> observed,
            ProductionDirectCutoverTemporaryBufferKind kind) =>
            observed.Single(value => value.Kind == kind).Buffer;

        private static void RequireZeroed(
            IReadOnlyCollection<ObservedBuffer> observed,
            params ProductionDirectCutoverSignalTemporaryBufferKind[] expectedKinds)
        {
            foreach (var kind in expectedKinds)
            {
                var buffer = Single(observed, kind);
                Require(buffer.Length > 0 && IsAllZero(buffer),
                    $"The {kind} codec buffer was not zeroed.");
            }
        }

        private static void RequireCutoverZeroed(
            IReadOnlyCollection<ObservedCutoverBuffer> observed,
            params ProductionDirectCutoverTemporaryBufferKind[] expectedKinds)
        {
            foreach (var kind in expectedKinds)
            {
                var matches = observed.Where(value => value.Kind == kind).ToArray();
                Require(matches.Length > 0,
                    $"The {kind} proposal-codec buffer was not observed.");
                foreach (var match in matches)
                {
                    Require(match.Buffer.Length > 0 && IsAllZero(match.Buffer),
                        $"The {kind} proposal-codec buffer was not zeroed.");
                }
            }
        }

        private static void RequireAllCutoverZeroed(
            IReadOnlyCollection<ObservedCutoverBuffer> observed)
        {
            Require(observed.Count > 0,
                "The proposal codec did not expose any temporary buffers.");
            foreach (var value in observed)
            {
                Require(value.Buffer.Length > 0 && IsAllZero(value.Buffer),
                    $"The {value.Kind} proposal-codec buffer was not zeroed.");
            }
        }

        private static byte[] AppendUnknownField(byte[] canonical)
        {
            try
            {
                // Unknown root field 127, varint value 1. The protobuf parser accepts it,
                // while the exact canonical re-encoding deliberately omits it.
                var value = new byte[canonical.Length + 3];
                Buffer.BlockCopy(canonical, 0, value, 0, canonical.Length);
                value[^3] = 0xF8;
                value[^2] = 0x07;
                value[^1] = 0x01;
                return value;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }

        private static byte[] NonZeroHash(byte seed)
        {
            var value = new byte[32];
            for (var index = 0; index < value.Length; index++)
                value[index] = unchecked((byte)(seed + index));
            return value;
        }

        private static void ZeroProposal(ProductionDirectCutoverProposal value)
        {
            if (value?.DirectPathBinding != null)
                CryptographicOperations.ZeroMemory(value.DirectPathBinding);
            if (value?.BarrierSha256 != null)
                CryptographicOperations.ZeroMemory(value.BarrierSha256);
            if (value?.WireBytes != null)
                CryptographicOperations.ZeroMemory(value.WireBytes);
        }

        private static bool IsAllZero(byte[] value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class ObservedBuffer
        {
            internal ObservedBuffer(
                ProductionDirectCutoverSignalTemporaryBufferKind kind,
                byte[] buffer)
            {
                Kind = kind;
                Buffer = buffer;
            }

            internal ProductionDirectCutoverSignalTemporaryBufferKind Kind { get; }
            internal byte[] Buffer { get; }
        }

        private sealed class ObservedCutoverBuffer
        {
            internal ObservedCutoverBuffer(
                ProductionDirectCutoverTemporaryBufferKind kind,
                byte[] buffer)
            {
                Kind = kind;
                Buffer = buffer;
            }

            internal ProductionDirectCutoverTemporaryBufferKind Kind { get; }
            internal byte[] Buffer { get; }
        }

        private sealed class ProposalObservation
        {
            internal List<ObservedBuffer> Signal { get; } = new();
            internal List<ObservedCutoverBuffer> Cutover { get; } = new();
        }

        private sealed class ObserverFailureException : Exception
        {
        }
    }
}
