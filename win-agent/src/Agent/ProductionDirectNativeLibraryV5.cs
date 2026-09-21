using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TermLink.Agent
{
    /// <summary>Atomically resolves the complete frozen ABI v5 export surface.</summary>
    internal sealed class ProductionDirectNativeLibraryLoaderV5 : IProductionDirectNativeLoaderV5
    {
        private readonly string libraryPath;

        internal ProductionDirectNativeLibraryLoaderV5(string libraryPath = null) =>
            this.libraryPath = Path.GetFullPath(libraryPath ??
                Path.Combine(AppContext.BaseDirectory, "termlink_direct_ffi.dll"));

        public bool TryLoad(out IProductionDirectNativeApiV5 api, out string error)
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
                // The constructor resolves all 26 exports before ownership of
                // the module can escape this method.
                api = new ProductionDirectNativeLibraryApiV5(module);
                module = IntPtr.Zero;
                return true;
            }
            catch (Exception value) when (value is EntryPointNotFoundException or BadImageFormatException or
                DllNotFoundException or TypeLoadException or ArgumentException)
            {
                error = "NATIVE_LIBRARY_V5_INVALID";
                return false;
            }
            finally
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
            }
        }
    }

    internal sealed class ProductionDirectNativeLibraryApiV5 : IProductionDirectNativeApiV5
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate(
            ref TermLinkDirectCreateV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PathDelegate(
            IntPtr handle, ref TermLinkDirectPathV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PromoteDelegate(
            IntPtr handle, IntPtr v4Handle, ref TermLinkDirectPromoteV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SignatureDelegate(
            IntPtr handle, ref TermLinkDirectSignatureV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SigningPayloadDelegate(
            IntPtr handle, ref TermLinkDirectPathV5 path, ref ulong requestId,
            IntPtr output, uint outputCapacity, ref uint requiredBytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PathInfoDelegate(
            IntPtr handle, ref TermLinkDirectPathV5 path, ref TermLinkDirectPathInfoV5 output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReceiveEnvelopeDelegate(
            IntPtr handle, ref TermLinkDirectPathV5 path, ref ulong frameSequence,
            IntPtr output, uint outputCapacity, ref uint requiredBytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProposalDelegate(
            IntPtr handle, ref TermLinkDirectProposalV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TransitionDelegate(
            IntPtr handle, ref TermLinkDirectTransitionV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CursorDelegate(
            IntPtr handle, ref TermLinkDirectCursorV5 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RouteEnvelopeDelegate(
            IntPtr handle, ref TermLinkDirectRouteEnvelopeV5 value, IntPtr envelope, uint envelopeBytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PollRouteActionDelegate(
            IntPtr handle, ref TermLinkDirectRouteActionV5 action,
            IntPtr output, uint outputCapacity, ref uint requiredBytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IngressDelegate(
            IntPtr handle, ref TermLinkDirectIngressV5 value, IntPtr envelope, uint envelopeBytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TickIngressDelegate(
            IntPtr handle, ref TermLinkDirectPathV5 path, ulong nowMilliseconds);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PollEventDelegate(
            IntPtr handle, ref TermLinkDirectEventV5 output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr handle);

        private readonly AbiDelegate abi;
        private readonly CreateDelegate create;
        private readonly PathDelegate pathUsage;
        private readonly PathDelegate attach;
        private readonly PromoteDelegate promote;
        private readonly SignatureDelegate submitSignature;
        private readonly SigningPayloadDelegate receiveSigningPayload;
        private readonly PathInfoDelegate pathInfo;
        private readonly ReceiveEnvelopeDelegate receiveEnvelope;
        private readonly ProposalDelegate startProposal;
        private readonly ProposalDelegate receiveProposal;
        private readonly TransitionDelegate receiveAccept;
        private readonly TransitionDelegate receiveCommit;
        private readonly TransitionDelegate commitEchoSent;
        private readonly TransitionDelegate receiveFallback;
        private readonly TransitionDelegate tick;
        private readonly CursorDelegate updateAppliedCursor;
        private readonly CursorDelegate deregisterSession;
        private readonly RouteEnvelopeDelegate routeEnvelope;
        private readonly PollRouteActionDelegate pollRouteAction;
        private readonly IngressDelegate arbitrateIngress;
        private readonly TickIngressDelegate tickIngress;
        private readonly PollEventDelegate pollEvent;
        private readonly RenewApplyDelegate renewApply;
        private readonly PathDelegate cancel;
        private readonly PathDelegate networkChanged;
        private readonly DestroyDelegate destroy;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RenewApplyDelegate(IntPtr handle, byte[] ticket, UIntPtr ticketLength);
        private IntPtr module;

        internal ProductionDirectNativeLibraryApiV5(IntPtr module)
        {
            if (module == IntPtr.Zero) throw new ArgumentException("A native module is required.", nameof(module));
            abi = Export<AbiDelegate>(module, "termlink_direct_abi_version_v5");
            create = Export<CreateDelegate>(module, "termlink_direct_create_v5");
            pathUsage = Export<PathDelegate>(module, "termlink_direct_path_usage_v5");
            attach = Export<PathDelegate>(module, "termlink_direct_attach_v5");
            promote = Export<PromoteDelegate>(module, "termlink_direct_promote_v5");
            submitSignature = Export<SignatureDelegate>(module, "termlink_direct_submit_signature_v5");
            receiveSigningPayload = Export<SigningPayloadDelegate>(module,
                "termlink_direct_receive_signing_payload_v5");
            pathInfo = Export<PathInfoDelegate>(module, "termlink_direct_path_info_v5");
            receiveEnvelope = Export<ReceiveEnvelopeDelegate>(module, "termlink_direct_receive_envelope_v5");
            startProposal = Export<ProposalDelegate>(module, "termlink_direct_start_proposal_v5");
            receiveProposal = Export<ProposalDelegate>(module, "termlink_direct_receive_proposal_v5");
            receiveAccept = Export<TransitionDelegate>(module, "termlink_direct_receive_accept_v5");
            receiveCommit = Export<TransitionDelegate>(module, "termlink_direct_receive_commit_v5");
            commitEchoSent = Export<TransitionDelegate>(module, "termlink_direct_commit_echo_sent_v5");
            receiveFallback = Export<TransitionDelegate>(module, "termlink_direct_receive_fallback_v5");
            tick = Export<TransitionDelegate>(module, "termlink_direct_tick_v5");
            updateAppliedCursor = Export<CursorDelegate>(module, "termlink_direct_update_applied_cursor_v5");
            deregisterSession = Export<CursorDelegate>(module, "termlink_direct_deregister_session_v5");
            routeEnvelope = Export<RouteEnvelopeDelegate>(module, "termlink_direct_route_envelope_v5");
            pollRouteAction = Export<PollRouteActionDelegate>(module, "termlink_direct_poll_route_action_v5");
            arbitrateIngress = Export<IngressDelegate>(module, "termlink_direct_arbitrate_ingress_v5");
            tickIngress = Export<TickIngressDelegate>(module, "termlink_direct_tick_ingress_v5");
            pollEvent = Export<PollEventDelegate>(module, "termlink_direct_poll_event_v5");
            renewApply = Export<RenewApplyDelegate>(module, "termlink_direct_renew_apply_v5");
            cancel = Export<PathDelegate>(module, "termlink_direct_cancel_v5");
            networkChanged = Export<PathDelegate>(module, "termlink_direct_network_changed_v5");
            destroy = Export<DestroyDelegate>(module, "termlink_direct_destroy_v5");
            this.module = module;
        }

        public uint AbiVersion() => abi();
        public int RenewApply(IntPtr handle, byte[] ticket) => renewApply(handle, ticket, (UIntPtr)ticket.Length);
        public IntPtr Create(ref TermLinkDirectCreateV5 value) => create(ref value);
        public int PathUsage(IntPtr handle, ref TermLinkDirectPathV5 value) => pathUsage(handle, ref value);
        public int Attach(IntPtr handle, ref TermLinkDirectPathV5 value) => attach(handle, ref value);
        public int Promote(IntPtr handle, IntPtr v4Handle, ref TermLinkDirectPromoteV5 value) =>
            promote(handle, v4Handle, ref value);
        public int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV5 value) =>
            submitSignature(handle, ref value);
        public int ReceiveSigningPayload(IntPtr handle, ref TermLinkDirectPathV5 path, ref ulong requestId,
            IntPtr output, uint outputCapacity, ref uint requiredBytes) =>
            receiveSigningPayload(handle, ref path, ref requestId, output, outputCapacity, ref requiredBytes);
        public int PathInfo(IntPtr handle, ref TermLinkDirectPathV5 path, ref TermLinkDirectPathInfoV5 output) =>
            pathInfo(handle, ref path, ref output);
        public int ReceiveEnvelope(IntPtr handle, ref TermLinkDirectPathV5 path, ref ulong frameSequence,
            IntPtr output, uint outputCapacity, ref uint requiredBytes) =>
            receiveEnvelope(handle, ref path, ref frameSequence, output, outputCapacity, ref requiredBytes);
        public int StartProposal(IntPtr handle, ref TermLinkDirectProposalV5 value) =>
            startProposal(handle, ref value);
        public int ReceiveProposal(IntPtr handle, ref TermLinkDirectProposalV5 value) =>
            receiveProposal(handle, ref value);
        public int ReceiveAccept(IntPtr handle, ref TermLinkDirectTransitionV5 value) =>
            receiveAccept(handle, ref value);
        public int ReceiveCommit(IntPtr handle, ref TermLinkDirectTransitionV5 value) =>
            receiveCommit(handle, ref value);
        public int CommitEchoSent(IntPtr handle, ref TermLinkDirectTransitionV5 value) =>
            commitEchoSent(handle, ref value);
        public int ReceiveFallback(IntPtr handle, ref TermLinkDirectTransitionV5 value) =>
            receiveFallback(handle, ref value);
        public int Tick(IntPtr handle, ref TermLinkDirectTransitionV5 value) => tick(handle, ref value);
        public int UpdateAppliedCursor(IntPtr handle, ref TermLinkDirectCursorV5 value) =>
            updateAppliedCursor(handle, ref value);
        public int DeregisterSession(IntPtr handle, ref TermLinkDirectCursorV5 value) =>
            deregisterSession(handle, ref value);
        public int RouteEnvelope(IntPtr handle, ref TermLinkDirectRouteEnvelopeV5 value,
            IntPtr envelope, uint envelopeBytes) => routeEnvelope(handle, ref value, envelope, envelopeBytes);
        public int PollRouteAction(IntPtr handle, ref TermLinkDirectRouteActionV5 action,
            IntPtr output, uint outputCapacity, ref uint requiredBytes) =>
            pollRouteAction(handle, ref action, output, outputCapacity, ref requiredBytes);
        public int ArbitrateIngress(IntPtr handle, ref TermLinkDirectIngressV5 value,
            IntPtr envelope, uint envelopeBytes) => arbitrateIngress(handle, ref value, envelope, envelopeBytes);
        public int TickIngress(IntPtr handle, ref TermLinkDirectPathV5 path, ulong nowMilliseconds) =>
            tickIngress(handle, ref path, nowMilliseconds);
        public int PollEvent(IntPtr handle, ref TermLinkDirectEventV5 value) => pollEvent(handle, ref value);
        public int Cancel(IntPtr handle, ref TermLinkDirectPathV5 value) => cancel(handle, ref value);
        public int NetworkChanged(IntPtr handle, ref TermLinkDirectPathV5 value) =>
            networkChanged(handle, ref value);
        public void Destroy(IntPtr handle) => destroy(handle);

        public void Dispose()
        {
            var retained = Interlocked.Exchange(ref module, IntPtr.Zero);
            if (retained != IntPtr.Zero) NativeLibrary.Free(retained);
        }

        private static T Export<T>(IntPtr value, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(value, name, out var address) || address == IntPtr.Zero)
                throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
