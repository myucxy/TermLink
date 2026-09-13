using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TermLink.Agent
{
    /// <summary>
    /// Frozen CLR representation of termlink_direct_ffi_v5.h. These records
    /// deliberately do not reuse a v4 layout even where promotion copies the
    /// same candidate data.
    /// </summary>
    internal static class ProductionDirectNativeAbiV5
    {
        internal const uint AbiVersion = 5;
        internal const uint MaximumFrames = 256;
        internal const ulong MaximumBytes = 8UL * 1024UL * 1024UL;
        internal const int FailureActionTail = 4;
        internal const int MaximumEnvelopeBytes = (1024 * 1024) - 1;
        internal const ulong FirstDataFrameSequence = 4;
        internal const ulong MaximumFrameSequence = long.MaxValue;
        internal const int MaximumAppliedCursorsPerDirection = 256;
        internal const int MaximumSessions = 32;
        internal const int MaximumSignatureBytes = 128;

        internal const int Ok = 0;
        internal const int NoData = 1;
        internal const int ErrorInvalidHandle = -1;
        internal const int ErrorInvalidArgument = -2;
        internal const int ErrorQueueFull = -3;
        internal const int ErrorStalePath = -4;
        internal const int ErrorBufferTooSmall = -5;
        internal const int ErrorNotReady = -6;
        internal const int ErrorCancelled = -7;
        internal const int ErrorLimit = -8;
        internal const int ErrorProtocol = -9;
        internal const int ErrorDisabled = -10;
        internal const int ErrorTimeout = -11;
        internal const int ErrorTicketExpired = -12;

        internal const int PathSize = 64;
        internal const int CreateSize = 112;
        internal const int EventSize = 80;
        internal const int SocketAddressSize = 48;
        internal const int CandidateSize = 128;
        internal const int CandidatePairSize = 288;
        internal const int PromoteSize = 544;
        internal const int PathInfoSize = 128;
        internal const int SignatureSize = 232;
        internal const int SessionBarrierSize = 24;
        internal const int ProposalSize = 968;
        internal const int TransitionSize = 176;
        internal const int CursorSize = 120;
        internal const int RouteEnvelopeSize = 128;
        internal const int IngressSize = 136;
        internal const int RouteActionSize = 200;

        internal static bool TryValidateLayout(out string error)
        {
            var checks = new List<(bool Valid, string Name)>
            {
                Size<TermLinkDirectPathV5>(PathSize, "path"),
                Size<TermLinkDirectCreateV5>(CreateSize, "create"),
                Size<TermLinkDirectEventV5>(EventSize, "event"),
                Size<TermLinkSocketAddressV5>(SocketAddressSize, "socket_address"),
                Size<TermLinkDirectCandidateV5>(CandidateSize, "candidate"),
                Size<TermLinkDirectCandidatePairV5>(CandidatePairSize, "candidate_pair"),
                Size<TermLinkDirectPromoteV5>(PromoteSize, "promote"),
                Size<TermLinkDirectPathInfoV5>(PathInfoSize, "path_info"),
                Size<TermLinkDirectSignatureV5>(SignatureSize, "signature"),
                Size<TermLinkDirectSessionBarrierV5>(SessionBarrierSize, "session_barrier"),
                Size<TermLinkDirectProposalV5>(ProposalSize, "proposal"),
                Size<TermLinkDirectTransitionV5>(TransitionSize, "transition"),
                Size<TermLinkDirectCursorV5>(CursorSize, "cursor"),
                Size<TermLinkDirectRouteEnvelopeV5>(RouteEnvelopeSize, "route_envelope"),
                Size<TermLinkDirectIngressV5>(IngressSize, "ingress"),
                Size<TermLinkDirectRouteActionV5>(RouteActionSize, "route_action"),

                Offset<TermLinkDirectPathV5>(nameof(TermLinkDirectPathV5.PathEpoch), 8, "path.path_epoch"),
                Offset<TermLinkDirectPathV5>(nameof(TermLinkDirectPathV5.ConnectionId), 16, "path.connection_id"),
                Offset<TermLinkDirectPathV5>(nameof(TermLinkDirectPathV5.AttemptId), 32, "path.attempt_id"),
                Offset<TermLinkDirectPathV5>(nameof(TermLinkDirectPathV5.Reserved), 48, "path.reserved"),

                Offset<TermLinkDirectCreateV5>(nameof(TermLinkDirectCreateV5.QueueCapacityFrames), 16,
                    "create.queue_capacity_frames"),
                Offset<TermLinkDirectCreateV5>(nameof(TermLinkDirectCreateV5.QueueCapacityBytes), 24,
                    "create.queue_capacity_bytes"),
                Offset<TermLinkDirectCreateV5>(nameof(TermLinkDirectCreateV5.Path), 32, "create.path"),
                Offset<TermLinkDirectCreateV5>(nameof(TermLinkDirectCreateV5.Reserved), 96, "create.reserved"),

                Offset<TermLinkDirectEventV5>(nameof(TermLinkDirectEventV5.PathEpoch), 24, "event.path_epoch"),
                Offset<TermLinkDirectEventV5>(nameof(TermLinkDirectEventV5.AttemptId), 32, "event.attempt_id"),
                Offset<TermLinkDirectEventV5>(nameof(TermLinkDirectEventV5.FrameSequence), 48, "event.frame_seq"),
                Offset<TermLinkDirectEventV5>(nameof(TermLinkDirectEventV5.PayloadBytes), 56,
                    "event.payload_bytes"),
                Offset<TermLinkDirectEventV5>(nameof(TermLinkDirectEventV5.ReleasedFrames), 60,
                    "event.released_frames"),
                Offset<TermLinkDirectEventV5>(nameof(TermLinkDirectEventV5.Reserved), 64, "event.reserved"),

                Offset<TermLinkSocketAddressV5>(nameof(TermLinkSocketAddressV5.Ip), 8, "socket_address.ip"),
                Offset<TermLinkSocketAddressV5>(nameof(TermLinkSocketAddressV5.IpLength), 24,
                    "socket_address.ip_length"),
                Offset<TermLinkSocketAddressV5>(nameof(TermLinkSocketAddressV5.Port), 28,
                    "socket_address.port"),
                Offset<TermLinkSocketAddressV5>(nameof(TermLinkSocketAddressV5.Reserved), 32,
                    "socket_address.reserved"),

                Offset<TermLinkDirectCandidateV5>(nameof(TermLinkDirectCandidateV5.CandidateId), 8,
                    "candidate.candidate_id"),
                Offset<TermLinkDirectCandidateV5>(nameof(TermLinkDirectCandidateV5.RelatedCandidateId), 24,
                    "candidate.related_candidate_id"),
                Offset<TermLinkDirectCandidateV5>(nameof(TermLinkDirectCandidateV5.Endpoint), 40,
                    "candidate.endpoint"),
                Offset<TermLinkDirectCandidateV5>(nameof(TermLinkDirectCandidateV5.Kind), 88, "candidate.kind"),
                Offset<TermLinkDirectCandidateV5>(nameof(TermLinkDirectCandidateV5.ExpiresAtMilliseconds), 104,
                    "candidate.expires_at_ms"),
                Offset<TermLinkDirectCandidateV5>(nameof(TermLinkDirectCandidateV5.Reserved), 112,
                    "candidate.reserved"),

                Offset<TermLinkDirectCandidatePairV5>(nameof(TermLinkDirectCandidatePairV5.InitiatorCandidate), 16,
                    "candidate_pair.initiator_candidate"),
                Offset<TermLinkDirectCandidatePairV5>(nameof(TermLinkDirectCandidatePairV5.ResponderCandidate), 144,
                    "candidate_pair.responder_candidate"),
                Offset<TermLinkDirectCandidatePairV5>(nameof(TermLinkDirectCandidatePairV5.Reserved), 272,
                    "candidate_pair.reserved"),

                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.Path), 8, "promote.path"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.CheckRoundId), 72,
                    "promote.check_round_id"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.NominationId), 88,
                    "promote.nomination_id"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.WinningPair), 104,
                    "promote.winning_pair"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.Role), 392, "promote.role"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.PeerCertificateSha256), 408,
                    "promote.peer_certificate_sha256"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.NowMilliseconds), 504,
                    "promote.now_ms"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.LocalOutboundDirection), 512,
                    "promote.local_outbound_direction"),
                Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.Reserved), 520,
                    "promote.reserved"),

                Offset<TermLinkDirectPathInfoV5>(nameof(TermLinkDirectPathInfoV5.Path), 8, "path_info.path"),
                Offset<TermLinkDirectPathInfoV5>(nameof(TermLinkDirectPathInfoV5.PathKind), 72,
                    "path_info.path_kind"),
                Offset<TermLinkDirectPathInfoV5>(nameof(TermLinkDirectPathInfoV5.DirectPathBinding), 80,
                    "path_info.direct_path_binding"),
                Offset<TermLinkDirectPathInfoV5>(nameof(TermLinkDirectPathInfoV5.Reserved), 112,
                    "path_info.reserved"),

                Offset<TermLinkDirectSignatureV5>(nameof(TermLinkDirectSignatureV5.Path), 8, "signature.path"),
                Offset<TermLinkDirectSignatureV5>(nameof(TermLinkDirectSignatureV5.RequestId), 72,
                    "signature.request_id"),
                Offset<TermLinkDirectSignatureV5>(nameof(TermLinkDirectSignatureV5.SignatureLength), 80,
                    "signature.signature_length"),
                Offset<TermLinkDirectSignatureV5>(nameof(TermLinkDirectSignatureV5.Signature), 88,
                    "signature.signature"),
                Offset<TermLinkDirectSignatureV5>(nameof(TermLinkDirectSignatureV5.Reserved), 216,
                    "signature.reserved"),

                Offset<TermLinkDirectSessionBarrierV5>(nameof(TermLinkDirectSessionBarrierV5.FirstDirectSequence), 16,
                    "session_barrier.first_direct_seq"),

                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.Path), 8, "proposal.path"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.Direction), 72,
                    "proposal.direction"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.BarrierId), 80,
                    "proposal.barrier_id"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.BarrierSha256), 96,
                    "proposal.barrier_sha256"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.DirectPathBinding), 128,
                    "proposal.direct_path_binding"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.NowMilliseconds), 168,
                    "proposal.now_ms"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.Sessions), 184,
                    "proposal.sessions"),
                Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.Reserved), 952,
                    "proposal.reserved"),

                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.Path), 8,
                    "transition.path"),
                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.Direction), 72,
                    "transition.direction"),
                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.BarrierId), 80,
                    "transition.barrier_id"),
                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.BarrierSha256), 96,
                    "transition.barrier_sha256"),
                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.Accepted), 128,
                    "transition.accepted"),
                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.NowMilliseconds), 144,
                    "transition.now_ms"),
                Offset<TermLinkDirectTransitionV5>(nameof(TermLinkDirectTransitionV5.Reserved), 160,
                    "transition.reserved"),

                Offset<TermLinkDirectCursorV5>(nameof(TermLinkDirectCursorV5.Path), 8, "cursor.path"),
                Offset<TermLinkDirectCursorV5>(nameof(TermLinkDirectCursorV5.Direction), 72,
                    "cursor.direction"),
                Offset<TermLinkDirectCursorV5>(nameof(TermLinkDirectCursorV5.SessionId), 80,
                    "cursor.session_id"),
                Offset<TermLinkDirectCursorV5>(nameof(TermLinkDirectCursorV5.HighestContiguous), 96,
                    "cursor.highest_contiguous"),
                Offset<TermLinkDirectCursorV5>(nameof(TermLinkDirectCursorV5.Reserved), 104, "cursor.reserved"),

                Offset<TermLinkDirectRouteEnvelopeV5>(nameof(TermLinkDirectRouteEnvelopeV5.Path), 8,
                    "route_envelope.path"),
                Offset<TermLinkDirectRouteEnvelopeV5>(nameof(TermLinkDirectRouteEnvelopeV5.Direction), 72,
                    "route_envelope.direction"),
                Offset<TermLinkDirectRouteEnvelopeV5>(nameof(TermLinkDirectRouteEnvelopeV5.SessionId), 80,
                    "route_envelope.session_id"),
                Offset<TermLinkDirectRouteEnvelopeV5>(nameof(TermLinkDirectRouteEnvelopeV5.EnvelopeSequence), 96,
                    "route_envelope.envelope_sequence"),
                Offset<TermLinkDirectRouteEnvelopeV5>(nameof(TermLinkDirectRouteEnvelopeV5.Reserved), 112,
                    "route_envelope.reserved"),

                Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.Path), 8, "ingress.path"),
                Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.Direction), 72,
                    "ingress.direction"),
                Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.SessionId), 80,
                    "ingress.session_id"),
                Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.EnvelopeSequence), 96,
                    "ingress.envelope_sequence"),
                Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.DuplicateGraceMilliseconds), 112,
                    "ingress.duplicate_grace_ms"),
                Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.Reserved), 120,
                    "ingress.reserved"),

                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.Path), 8,
                    "route_action.path"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.Kind), 72,
                    "route_action.kind"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.SessionId), 80,
                    "route_action.session_id"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.EnvelopeSequence), 96,
                    "route_action.envelope_sequence"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.BarrierId), 112,
                    "route_action.barrier_id"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.BarrierSha256), 128,
                    "route_action.barrier_sha256"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.Carrier), 168,
                    "route_action.carrier"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.PayloadBytes), 180,
                    "route_action.payload_bytes"),
                Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.Reserved), 184,
                    "route_action.reserved")
            };
            foreach (var check in checks)
            {
                if (!check.Valid)
                {
                    error = check.Name;
                    return false;
                }
            }
            error = string.Empty;
            return true;
        }

        internal static TermLinkDirectPathV5 EmptyPath() => new()
        {
            ConnectionId = new byte[16], AttemptId = new byte[16], Reserved = new ulong[2]
        };

        internal static TermLinkDirectEventV5 NewEventBuffer() => new()
        {
            AttemptId = new byte[16], Reserved = new ulong[2]
        };

        internal static TermLinkDirectPathInfoV5 NewPathInfoBuffer() => new()
        {
            Path = EmptyPath(), DirectPathBinding = new byte[32], Reserved = new ulong[2]
        };

        internal static TermLinkDirectRouteActionV5 NewRouteActionBuffer() => new()
        {
            Path = EmptyPath(), SessionId = new byte[16], BarrierId = new byte[16],
            BarrierSha256 = new byte[32], Reserved = new ulong[2]
        };

        private static (bool Valid, string Name) Size<T>(int expected, string name) where T : struct =>
            (Marshal.SizeOf<T>() == expected, name + ".size");

        private static (bool Valid, string Name) Offset<T>(string field, int expected, string name)
            where T : struct => (checked((int)Marshal.OffsetOf<T>(field)) == expected, name);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectPathV5
    {
        internal uint AbiVersion; internal uint StructSize; internal ulong PathEpoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] ConnectionId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCreateV5
    {
        internal uint AbiVersion; internal uint StructSize; internal uint Enabled; internal uint Reserved0;
        internal uint QueueCapacityFrames; internal uint Reserved1; internal ulong QueueCapacityBytes;
        internal TermLinkDirectPathV5 Path;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectEventV5
    {
        internal uint AbiVersion; internal uint StructSize; internal uint Kind; internal int Status;
        internal uint Flags; internal uint Reserved0; internal ulong PathEpoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal ulong FrameSequence; internal uint PayloadBytes; internal uint ReleasedFrames;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkSocketAddressV5
    {
        internal uint AbiVersion; internal uint StructSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] Ip;
        internal uint IpLength; internal uint Port;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCandidateV5
    {
        internal uint AbiVersion; internal uint StructSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CandidateId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] RelatedCandidateId;
        internal TermLinkSocketAddressV5 Endpoint; internal uint Kind; internal uint Carrier;
        internal uint Priority; internal uint NetworkCost; internal long ExpiresAtMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCandidatePairV5
    {
        internal uint AbiVersion; internal uint StructSize; internal uint Rank; internal uint Reserved0;
        internal TermLinkDirectCandidateV5 InitiatorCandidate; internal TermLinkDirectCandidateV5 ResponderCandidate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectPromoteV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] NominationId;
        internal TermLinkDirectCandidatePairV5 WinningPair;
        internal uint Role; internal uint TimeoutMilliseconds; internal uint SelectionConfirmed;
        internal uint LocalProofNonceLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerCertificateSha256;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerDeviceSigningPublicKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] LocalProofNonce;
        internal long NowMilliseconds; internal uint LocalOutboundDirection; internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectPathInfoV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint PathKind; internal uint Ready;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] DirectPathBinding;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectSignatureV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal ulong RequestId; internal uint SignatureLength; internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] internal byte[] Signature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectSessionBarrierV5
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] SessionId;
        internal ulong FirstDirectSequence;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectProposalV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint Direction; internal uint SessionCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] BarrierId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] BarrierSha256;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] DirectPathBinding;
        internal uint DuplicateGraceMilliseconds; internal uint Reserved0;
        internal ulong NowMilliseconds; internal ulong TimeoutMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal TermLinkDirectSessionBarrierV5[] Sessions;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectTransitionV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint Direction; internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] BarrierId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] BarrierSha256;
        internal uint Accepted; internal uint IsEcho; internal uint Reserved1; internal uint Reserved2;
        internal ulong NowMilliseconds; internal ulong TimeoutMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCursorV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint Direction; internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] SessionId;
        internal ulong HighestContiguous;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectRouteEnvelopeV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint Direction; internal uint RouteEligible;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] SessionId;
        internal ulong EnvelopeSequence; internal ulong NowMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectIngressV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint Direction; internal uint Source;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] SessionId;
        internal ulong EnvelopeSequence; internal ulong NowMilliseconds; internal uint DuplicateGraceMilliseconds;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectRouteActionV5
    {
        internal uint AbiVersion; internal uint StructSize; internal TermLinkDirectPathV5 Path;
        internal uint Kind; internal uint Direction;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] SessionId;
        internal ulong EnvelopeSequence; internal ulong DirectFrameSequence;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] BarrierId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] BarrierSha256;
        internal uint Accepted; internal uint IsEcho; internal uint Carrier; internal uint Flags;
        internal uint Reason; internal uint PayloadBytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    internal interface IProductionDirectNativeApiV5 : IDisposable
    {
        uint AbiVersion();
        IntPtr Create(ref TermLinkDirectCreateV5 create);
        int PathUsage(IntPtr handle, ref TermLinkDirectPathV5 path);
        int Attach(IntPtr handle, ref TermLinkDirectPathV5 path);
        int Promote(IntPtr handle, IntPtr v4Handle, ref TermLinkDirectPromoteV5 promote);
        int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV5 signature);
        int ReceiveSigningPayload(IntPtr handle, ref TermLinkDirectPathV5 path, ref ulong requestId,
            IntPtr output, uint outputCapacity, ref uint requiredBytes);
        int PathInfo(IntPtr handle, ref TermLinkDirectPathV5 path, ref TermLinkDirectPathInfoV5 output);
        int ReceiveEnvelope(IntPtr handle, ref TermLinkDirectPathV5 path, ref ulong frameSequence,
            IntPtr output, uint outputCapacity, ref uint requiredBytes);
        int StartProposal(IntPtr handle, ref TermLinkDirectProposalV5 proposal);
        int ReceiveProposal(IntPtr handle, ref TermLinkDirectProposalV5 proposal);
        int ReceiveAccept(IntPtr handle, ref TermLinkDirectTransitionV5 transition);
        int ReceiveCommit(IntPtr handle, ref TermLinkDirectTransitionV5 transition);
        int CommitEchoSent(IntPtr handle, ref TermLinkDirectTransitionV5 transition);
        int ReceiveFallback(IntPtr handle, ref TermLinkDirectTransitionV5 transition);
        int Tick(IntPtr handle, ref TermLinkDirectTransitionV5 transition);
        int UpdateAppliedCursor(IntPtr handle, ref TermLinkDirectCursorV5 cursor);
        int DeregisterSession(IntPtr handle, ref TermLinkDirectCursorV5 session);
        int RouteEnvelope(IntPtr handle, ref TermLinkDirectRouteEnvelopeV5 route,
            IntPtr envelope, uint envelopeBytes);
        int PollRouteAction(IntPtr handle, ref TermLinkDirectRouteActionV5 action,
            IntPtr output, uint outputCapacity, ref uint requiredBytes);
        int ArbitrateIngress(IntPtr handle, ref TermLinkDirectIngressV5 ingress,
            IntPtr envelope, uint envelopeBytes);
        int TickIngress(IntPtr handle, ref TermLinkDirectPathV5 path, ulong nowMilliseconds);
        int PollEvent(IntPtr handle, ref TermLinkDirectEventV5 output);
        int Cancel(IntPtr handle, ref TermLinkDirectPathV5 path);
        int NetworkChanged(IntPtr handle, ref TermLinkDirectPathV5 path);
        void Destroy(IntPtr handle);
    }

    internal interface IProductionDirectNativeLoaderV5
    {
        bool TryLoad(out IProductionDirectNativeApiV5 api, out string error);
    }
}
