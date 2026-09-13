using System;
using System.Net;
using System.Security.Cryptography;
using System.Linq;

namespace TermLink.Agent
{
    /// <summary>
    /// Short-lived, owned handoff from the V4 nomination actor to the V5 data
    /// actor.  It contains no Relay lifecycle authority.  Every secret is
    /// copied on entry and zeroed when the synchronous promotion/registration
    /// transaction has finished.
    /// </summary>
    internal sealed class ProductionDirectDataPromotionHandoff : IDisposable
    {
        private byte[] pairwiseKey;
        private byte[] signalKeyId;
        private ProductionDirectCommitRequestV4 nomination;
        private int disposed;

        internal ProductionDirectDataPromotionHandoff(
            Guid peerDeviceId,
            Guid connectionId,
            long keyEpoch,
            long admissionGeneration,
            ulong localSignalSequence,
            ulong remoteSignalSequence,
            ulong remoteSignalAcknowledgement,
            byte[] pairwiseKey,
            byte[] signalKeyId,
            IProductionDirectPromotableNativeV4 native,
            ProductionDirectCommitRequestV4 nomination,
            Func<byte[], byte[]> signNativeTranscript,
            Action<byte[]> nominationCompleted = null,
            Action<string> nominationFailed = null)
        {
            if (peerDeviceId == Guid.Empty || connectionId == Guid.Empty || keyEpoch < 1 ||
                admissionGeneration < 1 || pairwiseKey?.Length != 32 ||
                pairwiseKey.AsSpan().IndexOfAnyExcept((byte)0) < 0 ||
                signalKeyId?.Length is < 1 or > 64 ||
                native == null || nomination == null || nomination.Epoch == 0 ||
                nomination.AttemptId == Guid.Empty || nomination.CheckRoundId == Guid.Empty ||
                nomination.NominationId == Guid.Empty || nomination.WinningPair == null ||
                signNativeTranscript == null || remoteSignalAcknowledgement > localSignalSequence)
                throw new ArgumentException("The Direct V4-to-V5 handoff is invalid.");

            PeerDeviceId = peerDeviceId;
            ConnectionId = connectionId;
            KeyEpoch = keyEpoch;
            AdmissionGeneration = admissionGeneration;
            LocalSignalSequence = localSignalSequence;
            RemoteSignalSequence = remoteSignalSequence;
            RemoteSignalAcknowledgement = remoteSignalAcknowledgement;
            this.pairwiseKey = pairwiseKey.ToArray();
            this.signalKeyId = signalKeyId.ToArray();
            Native = native;
            this.nomination = CopyNomination(nomination);
            SignNativeTranscript = signNativeTranscript;
            NominationCompleted = nominationCompleted;
            NominationFailed = nominationFailed;
        }

        internal Guid PeerDeviceId { get; }
        internal Guid ConnectionId { get; }
        internal long KeyEpoch { get; }
        internal long AdmissionGeneration { get; }
        internal ulong LocalSignalSequence { get; }
        internal ulong RemoteSignalSequence { get; }
        internal ulong RemoteSignalAcknowledgement { get; }
        internal IProductionDirectPromotableNativeV4 Native { get; }
        internal Func<byte[], byte[]> SignNativeTranscript { get; }
        internal Action<byte[]> NominationCompleted { get; }
        internal Action<string> NominationFailed { get; }

        internal ProductionDirectPathFenceV5 Path => new()
        {
            PathEpoch = Nomination.Epoch,
            ConnectionId = ConnectionId,
            AttemptId = Nomination.AttemptId
        };

        internal byte[] CopyPairwiseKey()
        {
            ThrowIfDisposed();
            return pairwiseKey.ToArray();
        }

        internal byte[] CopySignalKeyId()
        {
            ThrowIfDisposed();
            return signalKeyId.ToArray();
        }

        internal ProductionDirectCommitRequestV4 CopyNomination()
        {
            ThrowIfDisposed();
            return CopyNomination(nomination);
        }

        internal ProductionDirectCommitRequestV4 Nomination
        {
            get
            {
                ThrowIfDisposed();
                return nomination;
            }
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
            ZeroNomination(nomination);
            nomination = null;
            if (pairwiseKey != null) CryptographicOperations.ZeroMemory(pairwiseKey);
            if (signalKeyId != null) CryptographicOperations.ZeroMemory(signalKeyId);
            pairwiseKey = null;
            signalKeyId = null;
        }

        private void ThrowIfDisposed()
        {
            if (System.Threading.Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(ProductionDirectDataPromotionHandoff));
        }

        internal static ProductionDirectCommitRequestV4 CopyNomination(
            ProductionDirectCommitRequestV4 value) => value == null ? null : new()
            {
                Epoch = value.Epoch,
                AttemptId = value.AttemptId,
                CheckRoundId = value.CheckRoundId,
                NominationId = value.NominationId,
                WinningPair = CopyPair(value.WinningPair),
                Role = value.Role,
                TimeoutMilliseconds = value.TimeoutMilliseconds,
                PeerCertificateSha256 = value.PeerCertificateSha256?.ToArray(),
                PeerDeviceSigningPublicKey = value.PeerDeviceSigningPublicKey?.ToArray(),
                LocalProofNonce = value.LocalProofNonce?.ToArray(),
                NowMilliseconds = value.NowMilliseconds
            };

        private static ProductionDirectCandidatePairV4 CopyPair(
            ProductionDirectCandidatePairV4 value) => value == null ? null : new()
        {
            Rank = value.Rank,
            Initiator = CopyCandidate(value.Initiator),
            Responder = CopyCandidate(value.Responder)
        };

        private static ProductionDirectCandidateV4 CopyCandidate(
            ProductionDirectCandidateV4 value) => value == null ? null : new()
        {
            CandidateId = value.CandidateId,
            RelatedCandidateId = value.RelatedCandidateId,
            Endpoint = value.Endpoint?.Address == null ? null :
                new ProductionDirectNativeEndpoint(
                    new IPAddress(value.Endpoint.Address.GetAddressBytes()),
                    value.Endpoint.Port),
            Kind = value.Kind,
            Priority = value.Priority,
            NetworkCost = value.NetworkCost,
            ExpiresAtMilliseconds = value.ExpiresAtMilliseconds
        };

        internal static void ZeroNomination(ProductionDirectCommitRequestV4 value)
        {
            if (value == null) return;
            if (value.PeerCertificateSha256 != null)
                CryptographicOperations.ZeroMemory(value.PeerCertificateSha256);
            if (value.PeerDeviceSigningPublicKey != null)
                CryptographicOperations.ZeroMemory(value.PeerDeviceSigningPublicKey);
            if (value.LocalProofNonce != null)
                CryptographicOperations.ZeroMemory(value.LocalProofNonce);
        }
    }
}
