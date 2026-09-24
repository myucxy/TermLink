using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TermLink.SessionHost
{
    // One backend owns create/resize/close for the entire lifetime of each HPCON.
    // Do not search PATH/CWD or borrow a mutable Windows Terminal installation.
    internal static class PseudoConsoleApi
    {
        internal const string BackendEnvironment = "TERMLINK_CONPTY_BACKEND";
        internal static readonly string Backend;
        internal static readonly CreateDelegate Create;
        internal static readonly ResizeDelegate Resize;
        internal static readonly CloseDelegate Close;

        static PseudoConsoleApi()
        {
            var choice = Environment.GetEnvironmentVariable(BackendEnvironment);
            if (!string.IsNullOrEmpty(choice) && choice != "bundled" && choice != "system")
                throw new InvalidOperationException(BackendEnvironment + " must be bundled or system.");

            var dll = Path.Combine(AppContext.BaseDirectory, "conpty.dll");
            var host = Path.Combine(AppContext.BaseDirectory,
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), "OpenConsole.exe");
            if (choice == "system" || (!File.Exists(dll) && !File.Exists(host) && choice != "bundled"))
            {
                Backend = "system";
                Create = CreatePseudoConsole;
                Resize = ResizePseudoConsole;
                Close = ClosePseudoConsole;
                return;
            }
            // A partial/corrupt installation must not silently downgrade. Missing
            // packages in legacy layouts may fall back; production packaging checks
            // require both signed vendor binaries.
            if (!File.Exists(dll) || !File.Exists(host))
                throw new FileNotFoundException("The bundled ConPTY runtime is incomplete. Repair the installation.");

            var library = NativeLibrary.Load(dll, typeof(PseudoConsoleApi).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            try
            {
                Create = Marshal.GetDelegateForFunctionPointer<CreateDelegate>(
                    NativeLibrary.GetExport(library, "ConptyCreatePseudoConsole"));
                Resize = Marshal.GetDelegateForFunctionPointer<ResizeDelegate>(
                    NativeLibrary.GetExport(library, "ConptyResizePseudoConsole"));
                Close = Marshal.GetDelegateForFunctionPointer<CloseDelegate>(
                    NativeLibrary.GetExport(library, "ConptyClosePseudoConsole"));
                Backend = "bundled";
                // Deliberately keep the module loaded until process exit. A close
                // can outlive the output-drain timeout on a background thread.
            }
            catch
            {
                NativeLibrary.Free(library);
                throw;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Size { public short X; public short Y; }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CreateDelegate(Size size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int ResizeDelegate(IntPtr console, Size size);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void CloseDelegate(IntPtr console);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int CreatePseudoConsole(Size size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int ResizePseudoConsole(IntPtr console, Size size);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern void ClosePseudoConsole(IntPtr console);
    }
}
