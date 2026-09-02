using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TermLink.Agent
{
    // V3 deliberately embeds the exact V2 start record. Its header remains V2;
    // surrounding control/signature/event records carry a V3 abi_version.
    internal static class ProductionDirectNativeAbiV2
    {
        internal const uint AbiVersion = 2;
        internal const int MaximumQueueCapacity = 64;
        internal const int MaximumTicketBytes = 2_048;
        internal const int MaximumEventPayloadBytes = 2_048;
        internal const int MaximumSignatureBytes = 128;
        internal const int MaximumProofNonceBytes = 32;
        internal const int MaximumStunServers = 2;
        internal const int SocketAddressSize = 24;
        internal const int StartSize = 2_592;
        internal const int ControlSize = 32;
        internal const int SignatureSize = 176;
        internal const int EventSize = 2_136;
    }

    internal static class ProductionDirectNativeAbiV3
    {
        internal const uint AbiVersion = 3;
        internal const uint PlatformWindows = 1;
        internal const int MaximumCandidates = 3;
        internal const int CreateSize = 32;
        internal const int GatherSize = 176;
        internal const int NominateSize = 2_696;
        internal const int CandidatesSize = 80;

        internal static bool TryValidateLayout(out string error)
        {
            var checks = new List<(bool Valid, string Name)>
            {
                (Marshal.SizeOf<TermLinkDirectCreateV3>() == CreateSize, "create_v3.size"),
                (Marshal.SizeOf<TermLinkDirectGatherV3>() == GatherSize, "gather_v3.size"),
                (Marshal.SizeOf<TermLinkDirectNominateV3>() == NominateSize, "nominate_v3.size"),
                (Marshal.SizeOf<TermLinkDirectCandidatesV3>() == CandidatesSize, "candidates_v3.size"),
                (Marshal.SizeOf<TermLinkSocketAddressV2>() == ProductionDirectNativeAbiV2.SocketAddressSize, "socket_v2.size"),
                (Marshal.SizeOf<TermLinkDirectStartV2>() == ProductionDirectNativeAbiV2.StartSize, "start_v2.size"),
                (Marshal.SizeOf<TermLinkDirectControlV2>() == ProductionDirectNativeAbiV2.ControlSize, "control_v2.size"),
                (Marshal.SizeOf<TermLinkDirectSignatureV2>() == ProductionDirectNativeAbiV2.SignatureSize, "signature_v2.size"),
                (Marshal.SizeOf<TermLinkDirectEventV2>() == ProductionDirectNativeAbiV2.EventSize, "event_v2.size"),
                (Offset<TermLinkDirectGatherV3>(nameof(TermLinkDirectGatherV3.LocalBind)) == 40, "gather_v3.local_bind"),
                (Offset<TermLinkDirectGatherV3>(nameof(TermLinkDirectGatherV3.StunServers)) == 88, "gather_v3.stun_servers"),
                (Offset<TermLinkDirectGatherV3>(nameof(TermLinkDirectGatherV3.Reserved)) == 144, "gather_v3.reserved"),
                (Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.StartV2)) == 8, "nominate_v3.start_v2"),
                (Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.LocalSendProbeHmacKey)) == 2_600, "nominate_v3.local_send_probe_hmac_key"),
                (Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.PeerReceiveProbeHmacKey)) == 2_632, "nominate_v3.peer_receive_probe_hmac_key"),
                (Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.Reserved)) == 2_664, "nominate_v3.reserved"),
                (Offset<TermLinkDirectCandidatesV3>(nameof(TermLinkDirectCandidatesV3.Candidates)) == 8, "candidates_v3.candidates"),
                (Offset<TermLinkDirectStartV2>(nameof(TermLinkDirectStartV2.NetworkHandle)) == 88, "start_v2.network"),
                (Offset<TermLinkDirectStartV2>(nameof(TermLinkDirectStartV2.StunServers)) == 152, "start_v2.stun"),
                (Offset<TermLinkDirectStartV2>(nameof(TermLinkDirectStartV2.Ticket)) == 208, "start_v2.ticket"),
                (Offset<TermLinkDirectStartV2>(nameof(TermLinkDirectStartV2.PeerCertificateSha256)) == 2_288, "start_v2.certificate_hash"),
                (Offset<TermLinkDirectStartV2>(nameof(TermLinkDirectStartV2.NowMilliseconds)) == 2_552, "start_v2.now"),
                (Offset<TermLinkDirectSignatureV2>(nameof(TermLinkDirectSignatureV2.Signature)) == 48, "signature_v2.bytes"),
                (Offset<TermLinkDirectEventV2>(nameof(TermLinkDirectEventV2.AttemptId)) == 48, "event_v2.attempt"),
                (Offset<TermLinkDirectEventV2>(nameof(TermLinkDirectEventV2.Endpoint)) == 64, "event_v2.endpoint"),
                (Offset<TermLinkDirectEventV2>(nameof(TermLinkDirectEventV2.Payload)) == 88, "event_v2.payload")
            };
            foreach (var check in checks)
            {
                if (!check.Valid) { error = check.Name; return false; }
            }
            error = string.Empty;
            return true;
        }

        internal static TermLinkDirectEventV2 NewEventBuffer() => new()
        {
            AttemptId = new byte[16],
            Endpoint = TermLinkSocketAddressV2.Empty(),
            Payload = new byte[ProductionDirectNativeAbiV2.MaximumEventPayloadBytes]
        };

        private static int Offset<T>(string field) where T : struct => checked((int)Marshal.OffsetOf<T>(field));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectCreateV3
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Enabled;
        internal uint QueueCapacity;
        internal uint Platform;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] internal uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectGatherV3
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal ulong NetworkHandle;
        internal TermLinkSocketAddressV2 LocalBind;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AdvertisedLocalIp;
        internal uint AdvertisedLocalIpLength;
        internal uint StunCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumStunServers)] internal TermLinkSocketAddressV2[] StunServers;
        internal uint GatherTimeoutMilliseconds;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectNominateV3
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal TermLinkDirectStartV2 StartV2;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] LocalSendProbeHmacKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerReceiveProbeHmacKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectCandidatesV3
    {
        internal uint CandidateCount;
        internal uint Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV3.MaximumCandidates)] internal TermLinkSocketAddressV2[] Candidates;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkSocketAddressV2
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] Ip;
        internal uint IpLength;
        internal uint Port;
        internal static TermLinkSocketAddressV2 Empty() => new() { Ip = new byte[16] };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectStartV2
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] ConnectionId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] LocalDeviceId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] PeerDeviceId;
        internal uint Role;
        internal uint TimeoutMilliseconds;
        internal ulong NetworkHandle;
        internal TermLinkSocketAddressV2 LocalBind;
        internal TermLinkSocketAddressV2 RemoteCandidate;
        internal uint StunCount;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumStunServers)] internal TermLinkSocketAddressV2[] StunServers;
        internal uint TicketLength;
        internal uint Reserved1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumTicketBytes)] internal byte[] Ticket;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] ServerSigningPublicKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerCertificateSha256;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerDeviceSigningPublicKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] ProbeHmacKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] TicketId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] NominationId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] InitiatorCandidateId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] ResponderCandidateId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] LocalProbeNonce;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] PeerProbeNonce;
        internal uint LocalProofNonceLength;
        internal uint PeerProofNonceLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumProofNonceBytes)] internal byte[] LocalProofNonce;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumProofNonceBytes)] internal byte[] PeerProofNonce;
        internal long NowMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectControlV2
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectSignatureV2
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal ulong RequestId;
        internal uint SignatureLength;
        internal uint Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumSignatureBytes)] internal byte[] Signature;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TermLinkDirectEventV2
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Kind;
        internal uint State;
        internal uint Reason;
        internal uint PayloadLength;
        internal ulong Epoch;
        internal ulong Generation;
        internal ulong RequestId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal TermLinkSocketAddressV2 Endpoint;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ProductionDirectNativeAbiV2.MaximumEventPayloadBytes)] internal byte[] Payload;
    }
}
