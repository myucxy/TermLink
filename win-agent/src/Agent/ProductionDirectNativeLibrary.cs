using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TermLink.Agent
{
    internal interface IProductionDirectNativeApi
    {
        uint AbiVersion();
        IntPtr Create(ref TermLinkDirectCreateV3 create);
        int Gather(IntPtr handle, ref TermLinkDirectGatherV3 gather);
        int Nominate(IntPtr handle, ref TermLinkDirectNominateV3 nominate);
        int Cancel(IntPtr handle, ref TermLinkDirectControlV2 control);
        int NetworkChanged(IntPtr handle, ref TermLinkDirectControlV2 control);
        int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV2 signature);
        int PollEvent(IntPtr handle, ref TermLinkDirectEventV2 output);
        void Destroy(IntPtr handle);
    }

    internal interface IProductionDirectNativeLoader
    {
        bool TryLoad(out IProductionDirectNativeApi api, out string error);
    }

    internal sealed class ProductionDirectNativeLibraryLoader : IProductionDirectNativeLoader
    {
        private const string DefaultLibraryName = "termlink_direct_ffi.dll";
        private readonly string libraryPath;

        public ProductionDirectNativeLibraryLoader(string libraryPath = null)
        {
            this.libraryPath = Path.GetFullPath(
                libraryPath ?? Path.Combine(AppContext.BaseDirectory, DefaultLibraryName));
        }

        public bool TryLoad(out IProductionDirectNativeApi api, out string error)
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
                api = new ProductionDirectNativeLibraryApi(module);
                // The module stays pinned after all exports resolve. Destroy is
                // a native handle operation, not permission to unload code while
                // a bounded worker is completing cancellation.
                module = IntPtr.Zero;
                return true;
            }
            catch (Exception exception) when (
                exception is EntryPointNotFoundException or BadImageFormatException or
                    DllNotFoundException or TypeLoadException or ArgumentException)
            {
                error = "NATIVE_LIBRARY_INVALID";
                return false;
            }
            finally
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
            }
        }
    }

    internal sealed class ProductionDirectNativeLibraryApi : IProductionDirectNativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiVersionDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate(ref TermLinkDirectCreateV3 create);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GatherDelegate(IntPtr handle, ref TermLinkDirectGatherV3 gather);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NominateDelegate(IntPtr handle, ref TermLinkDirectNominateV3 nominate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ControlDelegate(IntPtr handle, ref TermLinkDirectControlV2 control);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SignatureDelegate(IntPtr handle, ref TermLinkDirectSignatureV2 signature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PollDelegate(IntPtr handle, ref TermLinkDirectEventV2 output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr handle);

        private readonly AbiVersionDelegate abiVersion;
        private readonly CreateDelegate create;
        private readonly GatherDelegate gather;
        private readonly NominateDelegate nominate;
        private readonly ControlDelegate cancel;
        private readonly ControlDelegate networkChanged;
        private readonly SignatureDelegate submitSignature;
        private readonly PollDelegate pollEvent;
        private readonly DestroyDelegate destroy;

        internal ProductionDirectNativeLibraryApi(IntPtr module)
        {
            if (module == IntPtr.Zero) throw new ArgumentException("A native module is required.", nameof(module));
            abiVersion = Export<AbiVersionDelegate>(module, "termlink_direct_abi_version_v3");
            create = Export<CreateDelegate>(module, "termlink_direct_create_v3");
            gather = Export<GatherDelegate>(module, "termlink_direct_gather_v3");
            nominate = Export<NominateDelegate>(module, "termlink_direct_nominate_v3");
            cancel = Export<ControlDelegate>(module, "termlink_direct_cancel_v3");
            networkChanged = Export<ControlDelegate>(module, "termlink_direct_network_changed_v3");
            submitSignature = Export<SignatureDelegate>(module, "termlink_direct_submit_signature_v3");
            pollEvent = Export<PollDelegate>(module, "termlink_direct_poll_event_v3");
            destroy = Export<DestroyDelegate>(module, "termlink_direct_destroy_v3");
        }

        public uint AbiVersion() => abiVersion();
        public IntPtr Create(ref TermLinkDirectCreateV3 value) => create(ref value);
        public int Gather(IntPtr handle, ref TermLinkDirectGatherV3 value) => gather(handle, ref value);
        public int Nominate(IntPtr handle, ref TermLinkDirectNominateV3 value) => nominate(handle, ref value);
        public int Cancel(IntPtr handle, ref TermLinkDirectControlV2 value) => cancel(handle, ref value);
        public int NetworkChanged(IntPtr handle, ref TermLinkDirectControlV2 value) => networkChanged(handle, ref value);
        public int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV2 value) => submitSignature(handle, ref value);
        public int PollEvent(IntPtr handle, ref TermLinkDirectEventV2 output) => pollEvent(handle, ref output);
        public void Destroy(IntPtr handle) => destroy(handle);

        private static T Export<T>(IntPtr module, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(module, name, out var address) || address == IntPtr.Zero)
                throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
