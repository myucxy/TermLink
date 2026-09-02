using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TermLink.Agent
{
    internal static class ProductionDirectNativeAbiV4
    {
        internal const uint AbiVersion = 4;
        internal const uint PlatformWindows = 1;
        internal const int MaximumQueueCapacity = 64;
        internal const int MaximumStunServers = 2;
        internal const int MaximumCandidatesPerBatch = 8;
        internal const int MaximumCandidatesTotal = 32;
        internal const int MaximumPairsPerBatch = 8;
        internal const int MaximumPairsTotal = 32;
        internal const int MaximumTicketBytes = 2_048;
        internal const int MaximumEventBytes = 2_048;
        internal const int MaximumSignatureBytes = 128;
        internal const int MaximumProofNonceBytes = 32;

        internal const int CreateSize = 32;
        internal const int SocketAddressSize = 48;
        internal const int GatherSize = 256;
        internal const int CandidateSize = 128;
        internal const int CandidateBatchSize = 1_088;
        internal const int CandidatePairSize = 288;
        internal const int BeginChecksSize = 4_656;
        internal const int AppendCheckPairsSize = 2_400;
        internal const int CommitNominationSize = 504;
        internal const int ControlSize = 48;
        internal const int SignatureSize = 192;
        internal const int OpaqueBytesSize = 2_080;
        internal const int PairValidatedSize = 360;
        internal const int ChecksCompleteSize = 80;
        internal const int NominationReadySize = 376;
        internal const int ShadowReadySize = 400;
        internal const int EventPayloadSize = 2_080;
        internal const int EventSize = 2_224;

        internal static bool TryValidateLayout(out string error)
        {
            var checks = new List<(bool Valid, string Name)>
            {
                Size<TermLinkDirectCreateV4>(CreateSize, "create"),
                Size<TermLinkSocketAddressV4>(SocketAddressSize, "socket_address"),
                Size<TermLinkDirectGatherV4>(GatherSize, "gather"),
                Size<TermLinkDirectCandidateV4>(CandidateSize, "candidate"),
                Size<TermLinkDirectCandidateBatchV4>(CandidateBatchSize, "candidate_batch"),
                Size<TermLinkDirectCandidatePairV4>(CandidatePairSize, "candidate_pair"),
                Size<TermLinkDirectBeginChecksV4>(BeginChecksSize, "begin_checks"),
                Size<TermLinkDirectAppendCheckPairsV4>(AppendCheckPairsSize, "append_pairs"),
                Size<TermLinkDirectCommitNominationV4>(CommitNominationSize, "commit_nomination"),
                Size<TermLinkDirectControlV4>(ControlSize, "control"),
                Size<TermLinkDirectSignatureV4>(SignatureSize, "signature"),
                Size<TermLinkDirectOpaqueBytesV4>(OpaqueBytesSize, "opaque"),
                Size<TermLinkDirectPairValidatedV4>(PairValidatedSize, "pair_validated"),
                Size<TermLinkDirectChecksCompleteV4>(ChecksCompleteSize, "checks_complete"),
                Size<TermLinkDirectNominationReadyV4>(NominationReadySize, "nomination_ready"),
                Size<TermLinkDirectShadowReadyV4>(ShadowReadySize, "shadow_ready"),
                Size<TermLinkDirectEventV4>(EventSize, "event"),
                Offset<TermLinkDirectGatherV4>(nameof(TermLinkDirectGatherV4.LocalBind), 40, "gather.local_bind"),
                Offset<TermLinkDirectGatherV4>(nameof(TermLinkDirectGatherV4.StunServers), 128, "gather.stun_servers"),
                Offset<TermLinkDirectCandidateV4>(nameof(TermLinkDirectCandidateV4.Endpoint), 40, "candidate.endpoint"),
                Offset<TermLinkDirectCandidateBatchV4>(nameof(TermLinkDirectCandidateBatchV4.Candidates), 48, "candidate_batch.candidates"),
                Offset<TermLinkDirectCandidatePairV4>(nameof(TermLinkDirectCandidatePairV4.InitiatorCandidate), 16, "pair.initiator"),
                Offset<TermLinkDirectCandidatePairV4>(nameof(TermLinkDirectCandidatePairV4.ResponderCandidate), 144, "pair.responder"),
                Offset<TermLinkDirectBeginChecksV4>(nameof(TermLinkDirectBeginChecksV4.Ticket), 128, "begin.ticket"),
                Offset<TermLinkDirectBeginChecksV4>(nameof(TermLinkDirectBeginChecksV4.ServerSigningPublicKey), 2_176, "begin.server_key"),
                Offset<TermLinkDirectBeginChecksV4>(nameof(TermLinkDirectBeginChecksV4.LocalSendProbeHmacKey), 2_256, "begin.local_key"),
                Offset<TermLinkDirectBeginChecksV4>(nameof(TermLinkDirectBeginChecksV4.PeerReceiveProbeHmacKey), 2_288, "begin.peer_key"),
                Offset<TermLinkDirectBeginChecksV4>(nameof(TermLinkDirectBeginChecksV4.Pairs), 2_320, "begin.pairs"),
                Offset<TermLinkDirectAppendCheckPairsV4>(nameof(TermLinkDirectAppendCheckPairsV4.Pairs), 64, "append.pairs"),
                Offset<TermLinkDirectCommitNominationV4>(nameof(TermLinkDirectCommitNominationV4.WinningPair), 64, "commit.pair"),
                Offset<TermLinkDirectCommitNominationV4>(nameof(TermLinkDirectCommitNominationV4.LocalProofNonce), 432, "commit.proof_nonce"),
                Offset<TermLinkDirectEventV4>(nameof(TermLinkDirectEventV4.Endpoint), 64, "event.endpoint"),
                Offset<TermLinkDirectEventV4>(nameof(TermLinkDirectEventV4.Payload), 112, "event.payload"),
                Offset<TermLinkDirectEventV4>(nameof(TermLinkDirectEventV4.Reserved), 2_192, "event.reserved")
            };
            foreach (var check in checks)
            {
                if (!check.Valid) { error = check.Name; return false; }
            }
            error = string.Empty;
            return true;
        }

        internal static TermLinkDirectEventV4 NewEventBuffer() => new()
        {
            AttemptId = new byte[16],
            Endpoint = TermLinkSocketAddressV4.Empty(),
            Payload = new byte[EventPayloadSize],
            Reserved = new ulong[4]
        };

        private static (bool Valid, string Name) Size<T>(int expected, string name) where T : struct =>
            (Marshal.SizeOf<T>() == expected, name + ".size");

        private static (bool Valid, string Name) Offset<T>(string field, int expected, string name)
            where T : struct => (checked((int)Marshal.OffsetOf<T>(field)) == expected, name);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCreateV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Enabled;
        internal uint QueueCapacity;
        internal uint Platform;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] internal uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkSocketAddressV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] Ip;
        internal uint IpLength;
        internal uint Port;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;

        internal static TermLinkSocketAddressV4 Empty() => new()
        {
            AbiVersion = ProductionDirectNativeAbiV4.AbiVersion,
            StructSize = ProductionDirectNativeAbiV4.SocketAddressSize,
            Ip = new byte[16],
            Reserved = new ulong[2]
        };

        // Unused fixed-array slots are required by ABI v4 to be byte-for-byte
        // zero. They are not encoded socket records and therefore must not use
        // the normal Empty() header values.
        internal static TermLinkSocketAddressV4 Unused() => new()
        {
            Ip = new byte[16],
            Reserved = new ulong[2]
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectGatherV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal ulong NetworkHandle;
        internal TermLinkSocketAddressV4 LocalBind;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AdvertisedLocalIp;
        internal uint AdvertisedLocalIpLength;
        internal uint NetworkCost;
        internal long CandidateExpiresAtMilliseconds;
        internal uint StunCount;
        internal uint GatherTimeoutMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal TermLinkSocketAddressV4[] StunServers;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCandidateV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CandidateId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] RelatedCandidateId;
        internal TermLinkSocketAddressV4 Endpoint;
        internal uint Kind;
        internal uint Carrier;
        internal uint Priority;
        internal uint NetworkCost;
        internal long ExpiresAtMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCandidateBatchV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal uint BatchSequence;
        internal uint CandidateCount;
        internal uint TotalCandidateCount;
        internal uint GatheringComplete;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] internal TermLinkDirectCandidateV4[] Candidates;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCandidatePairV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Rank;
        internal uint Reserved0;
        internal TermLinkDirectCandidateV4 InitiatorCandidate;
        internal TermLinkDirectCandidateV4 ResponderCandidate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectBeginChecksV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] ConnectionId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] LocalDeviceId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] PeerDeviceId;
        internal ulong NetworkHandle;
        internal long NowMilliseconds;
        internal uint TimeoutMilliseconds;
        internal uint Role;
        internal uint TicketLength;
        internal uint PairBatchSequence;
        internal uint PairCount;
        internal uint TotalPairCount;
        internal uint PlanComplete;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] internal byte[] Ticket;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] ServerSigningPublicKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] TicketId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] ConnectivityNonce;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] LocalSendProbeHmacKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerReceiveProbeHmacKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] internal TermLinkDirectCandidatePairV4[] Pairs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectAppendCheckPairsV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        internal uint PairBatchSequence;
        internal uint PairCount;
        internal uint TotalPairCount;
        internal uint PlanComplete;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] internal TermLinkDirectCandidatePairV4[] Pairs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectCommitNominationV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] NominationId;
        internal TermLinkDirectCandidatePairV4 WinningPair;
        internal uint Role;
        internal uint TimeoutMilliseconds;
        internal uint SelectionConfirmed;
        internal uint LocalProofNonceLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerCertificateSha256;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PeerDeviceSigningPublicKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] LocalProofNonce;
        internal long NowMilliseconds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectControlV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectSignatureV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal ulong RequestId;
        internal uint SignatureLength;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] internal byte[] Signature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectOpaqueBytesV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint DataLength;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] internal byte[] Data;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectPairValidatedV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        internal TermLinkDirectCandidatePairV4 Pair;
        internal uint RoundTripTimeMilliseconds;
        internal uint ProbeAttempts;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectChecksCompleteV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        internal uint TotalPairCount;
        internal uint ValidatedPairCount;
        internal uint ExhaustedPairCount;
        internal uint Result;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectNominationReadyV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] NominationId;
        internal TermLinkDirectCandidatePairV4 WinningPair;
        internal uint Role;
        internal uint Reserved0;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectShadowReadyV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Epoch;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] CheckRoundId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] NominationId;
        internal TermLinkDirectCandidatePairV4 WinningPair;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] DirectPathBinding;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved;
    }

    // The native union is represented as owned raw bytes. CLR explicit-layout
    // unions cannot safely overlap structs that contain ByValArray fields.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct TermLinkDirectEventV4
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Kind;
        internal uint State;
        internal uint Reason;
        internal uint PayloadSize;
        internal ulong Epoch;
        internal ulong Generation;
        internal ulong RequestId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] AttemptId;
        internal TermLinkSocketAddressV4 Endpoint;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2080)] internal byte[] Payload;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Reserved;
    }

    internal interface IProductionDirectNativeApiV4 : IDisposable
    {
        uint AbiVersion();
        IntPtr Create(ref TermLinkDirectCreateV4 create);
        int Gather(IntPtr handle, ref TermLinkDirectGatherV4 gather);
        int BeginChecks(IntPtr handle, ref TermLinkDirectBeginChecksV4 begin);
        int AppendCheckPairs(IntPtr handle, ref TermLinkDirectAppendCheckPairsV4 append);
        int CommitNomination(IntPtr handle, ref TermLinkDirectCommitNominationV4 commit);
        int Cancel(IntPtr handle, ref TermLinkDirectControlV4 control);
        int NetworkChanged(IntPtr handle, ref TermLinkDirectControlV4 control);
        int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV4 signature);
        int PollEvent(IntPtr handle, ref TermLinkDirectEventV4 output);
        void Destroy(IntPtr handle);
    }

    internal interface IProductionDirectNativeLoaderV4
    {
        bool TryLoad(out IProductionDirectNativeApiV4 api, out string error);
    }

    internal static class ProductionDirectNativeCapabilityV4
    {
        internal static bool IsUsable(IProductionDirectNativeLoaderV4 loader = null)
        {
            if (!ProductionDirectNativeAbiV4.TryValidateLayout(out _)) return false;
            loader ??= new ProductionDirectNativeLibraryLoaderV4();
            if (!loader.TryLoad(out var api, out _) || api == null) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (api.AbiVersion() != ProductionDirectNativeAbiV4.AbiVersion) return false;
                var create = new TermLinkDirectCreateV4
                {
                    AbiVersion = ProductionDirectNativeAbiV4.AbiVersion,
                    StructSize = ProductionDirectNativeAbiV4.CreateSize,
                    Enabled = 1,
                    QueueCapacity = 8,
                    Platform = ProductionDirectNativeAbiV4.PlatformWindows,
                    Reserved = new uint[3]
                };
                handle = api.Create(ref create);
                return handle != IntPtr.Zero;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try { api.Destroy(handle); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
                try { api.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
    }

    internal sealed class ProductionDirectNativeLibraryLoaderV4 : IProductionDirectNativeLoaderV4
    {
        private readonly string libraryPath;

        public ProductionDirectNativeLibraryLoaderV4(string libraryPath = null) =>
            this.libraryPath = Path.GetFullPath(libraryPath ??
                Path.Combine(AppContext.BaseDirectory, "termlink_direct_ffi.dll"));

        public bool TryLoad(out IProductionDirectNativeApiV4 api, out string error)
        {
            api = null;
            error = string.Empty;
            IntPtr module = IntPtr.Zero;
            try
            {
                if (!File.Exists(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out module))
                {
                    error = "NATIVE_LIBRARY_MISSING";
                    return false;
                }
                api = new ProductionDirectNativeLibraryApiV4(module);
                module = IntPtr.Zero;
                return true;
            }
            catch (Exception errorValue) when (
                errorValue is EntryPointNotFoundException or BadImageFormatException or
                    DllNotFoundException or TypeLoadException or ArgumentException)
            {
                error = "NATIVE_LIBRARY_V4_INVALID";
                return false;
            }
            finally
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
            }
        }
    }

    internal sealed class ProductionDirectNativeLibraryApiV4 : IProductionDirectNativeApiV4
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate(ref TermLinkDirectCreateV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GatherDelegate(IntPtr handle, ref TermLinkDirectGatherV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BeginDelegate(IntPtr handle, ref TermLinkDirectBeginChecksV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AppendDelegate(IntPtr handle, ref TermLinkDirectAppendCheckPairsV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommitDelegate(IntPtr handle, ref TermLinkDirectCommitNominationV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ControlDelegate(IntPtr handle, ref TermLinkDirectControlV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SignatureDelegate(IntPtr handle, ref TermLinkDirectSignatureV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PollDelegate(IntPtr handle, ref TermLinkDirectEventV4 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr handle);

        private readonly AbiDelegate abi;
        private readonly CreateDelegate create;
        private readonly GatherDelegate gather;
        private readonly BeginDelegate begin;
        private readonly AppendDelegate append;
        private readonly CommitDelegate commit;
        private readonly ControlDelegate cancel;
        private readonly ControlDelegate networkChanged;
        private readonly SignatureDelegate submitSignature;
        private readonly PollDelegate poll;
        private readonly DestroyDelegate destroy;
        private IntPtr module;

        internal ProductionDirectNativeLibraryApiV4(IntPtr module)
        {
            if (module == IntPtr.Zero) throw new ArgumentException("A native module is required.", nameof(module));
            abi = Export<AbiDelegate>(module, "termlink_direct_abi_version_v4");
            create = Export<CreateDelegate>(module, "termlink_direct_create_v4");
            gather = Export<GatherDelegate>(module, "termlink_direct_gather_v4");
            begin = Export<BeginDelegate>(module, "termlink_direct_begin_checks_v4");
            append = Export<AppendDelegate>(module, "termlink_direct_append_check_pairs_v4");
            commit = Export<CommitDelegate>(module, "termlink_direct_commit_nomination_v4");
            cancel = Export<ControlDelegate>(module, "termlink_direct_cancel_v4");
            networkChanged = Export<ControlDelegate>(module, "termlink_direct_network_changed_v4");
            submitSignature = Export<SignatureDelegate>(module, "termlink_direct_submit_signature_v4");
            poll = Export<PollDelegate>(module, "termlink_direct_poll_event_v4");
            destroy = Export<DestroyDelegate>(module, "termlink_direct_destroy_v4");
            this.module = module;
        }

        public uint AbiVersion() => abi();
        public IntPtr Create(ref TermLinkDirectCreateV4 value) => create(ref value);
        public int Gather(IntPtr handle, ref TermLinkDirectGatherV4 value) => gather(handle, ref value);
        public int BeginChecks(IntPtr handle, ref TermLinkDirectBeginChecksV4 value) => begin(handle, ref value);
        public int AppendCheckPairs(IntPtr handle, ref TermLinkDirectAppendCheckPairsV4 value) => append(handle, ref value);
        public int CommitNomination(IntPtr handle, ref TermLinkDirectCommitNominationV4 value) => commit(handle, ref value);
        public int Cancel(IntPtr handle, ref TermLinkDirectControlV4 value) => cancel(handle, ref value);
        public int NetworkChanged(IntPtr handle, ref TermLinkDirectControlV4 value) => networkChanged(handle, ref value);
        public int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV4 value) => submitSignature(handle, ref value);
        public int PollEvent(IntPtr handle, ref TermLinkDirectEventV4 value) => poll(handle, ref value);
        public void Destroy(IntPtr handle) => destroy(handle);

        public void Dispose()
        {
            var retained = Interlocked.Exchange(ref module, IntPtr.Zero);
            if (retained != IntPtr.Zero) NativeLibrary.Free(retained);
        }

        private static T Export<T>(IntPtr module, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(module, name, out var address) || address == IntPtr.Zero)
                throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
