using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TermLink.Tray
{
    internal enum UpdateTransferPhase
    {
        Downloading,
        Verifying,
        Launching
    }

    internal sealed class UpdateTransferProgress
    {
        public UpdateTransferPhase Phase { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
    }

    internal static class UpdatePackageValidator
    {
        internal const uint TrustSuccess = 0x00000000;
        internal const uint CertificateUntrustedRoot = 0x800B0109;
        internal const uint CertificateChaining = 0x800B010A;
        internal const uint TrustBadDigest = 0x80096010;
        internal const uint TrustNoSignature = 0x800B0100;

        internal static void ValidateIntegrity(string path, long expectedSize, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The downloaded update package is missing.", path);
            var normalizedHash = AppShipUpdateClient.NormalizeHex(expectedSha256);
            if (expectedSize <= 0 || !AppShipUpdateClient.IsHex(normalizedHash, 64))
                throw new InvalidDataException("The expected update size or SHA-256 is invalid.");
            var info = new FileInfo(path);
            if (info.Length != expectedSize)
                throw new InvalidDataException("The downloaded update size does not match AppShip metadata.");
            byte[] actual;
            using (var stream = File.OpenRead(path)) actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(normalizedHash)))
                throw new CryptographicException("The downloaded update SHA-256 does not match AppShip metadata.");
        }

        internal static string ValidateAuthenticode(
            string path,
            string expectedThumbprint,
            string reportedThumbprint)
        {
            var status = AuthenticodeVerifier.VerifyEmbeddedSignature(path);
            if (!IsAcceptableAuthenticodeStatus(status))
            {
                throw new CryptographicException(
                    "The update Authenticode signature failed validation (0x" + status.ToString("X8") + ").");
            }
            var actual = AuthenticodeVerifier.ReadSignerThumbprint(path);
            ValidateSignerIdentity(actual, expectedThumbprint, reportedThumbprint);
            return actual;
        }

        internal static bool IsAcceptableAuthenticodeStatus(uint status)
        {
            // WinVerifyTrust has already checked the embedded digest. A missing
            // public trust anchor is accepted only because the signer is pinned
            // independently below; bad digests and every other trust error fail.
            return status == TrustSuccess ||
                status == CertificateUntrustedRoot ||
                status == CertificateChaining;
        }

        internal static void ValidateSignerIdentity(
            string actualThumbprint,
            string expectedThumbprint,
            string reportedThumbprint)
        {
            var actual = NormalizeThumbprint(actualThumbprint, "downloaded Authenticode signer");
            var expected = NormalizeThumbprint(expectedThumbprint, "installed/configured update signer");
            if (!FixedHexEquals(actual, expected))
                throw new CryptographicException("The update signer does not match the installed or configured TermLink publisher.");
            if (!string.IsNullOrWhiteSpace(reportedThumbprint))
            {
                var reported = NormalizeThumbprint(reportedThumbprint, "AppShip signer fingerprint");
                if (!FixedHexEquals(actual, reported))
                    throw new CryptographicException("The update signer does not match the AppShip signer fingerprint.");
            }
        }

        internal static string NormalizeThumbprint(string value, string description)
        {
            var normalized = AppShipUpdateClient.NormalizeHex(value);
            if (!AppShipUpdateClient.IsHex(normalized, 40))
                throw new CryptographicException("The " + description + " is not a SHA-1 certificate thumbprint.");
            return normalized;
        }

        private static bool FixedHexEquals(string left, string right)
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
    }

    internal static class UpdateSigningIdentity
    {
        private const string PublisherCertificateFileName = "TermLink.UpdatePublisher.cer";

        internal static string ResolveExpectedThumbprint()
        {
            var configuredThumbprint = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_SIGNER_THUMBPRINT");
            var configuredCertificate = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_PUBLISHER_CERTIFICATE");
            string expected = null;
            if (!string.IsNullOrWhiteSpace(configuredThumbprint))
            {
                expected = UpdatePackageValidator.NormalizeThumbprint(
                    configuredThumbprint,
                    "configured update signer");
            }
            if (!string.IsNullOrWhiteSpace(configuredCertificate))
            {
                var certificateThumbprint = ReadCertificateThumbprint(configuredCertificate, required: true);
                if (expected != null)
                {
                    UpdatePackageValidator.ValidateSignerIdentity(certificateThumbprint, expected, null);
                }
                expected = certificateThumbprint;
            }
            // An explicit identity supports controlled publisher rotation and
            // takes precedence over the identity shipped by the old install.
            if (expected != null) return expected;

            var bundledCertificate = Path.Combine(AppContext.BaseDirectory, PublisherCertificateFileName);
            if (File.Exists(bundledCertificate))
            {
                expected = ReadCertificateThumbprint(bundledCertificate, required: true);
                var processPath = Environment.ProcessPath;
                if (IsTrayExecutable(processPath))
                {
                    var currentSigner = AuthenticodeVerifier.ReadSignerThumbprint(processPath);
                    UpdatePackageValidator.ValidateSignerIdentity(currentSigner, expected, null);
                }
                return expected;
            }

            var currentPath = Environment.ProcessPath;
            if (IsTrayExecutable(currentPath))
            {
                var status = AuthenticodeVerifier.VerifyEmbeddedSignature(currentPath);
                if (!UpdatePackageValidator.IsAcceptableAuthenticodeStatus(status))
                    throw new CryptographicException("The installed Tray signature cannot establish an update publisher identity.");
                return UpdatePackageValidator.NormalizeThumbprint(
                    AuthenticodeVerifier.ReadSignerThumbprint(currentPath),
                    "installed Tray signer");
            }
            throw new CryptographicException(
                "No installed or configured TermLink update publisher identity is available.");
        }

        internal static string ReadCertificateThumbprint(string path, bool required)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (required) throw new FileNotFoundException("The configured update publisher certificate was not found.", path);
                return null;
            }
            using (var certificate = new X509Certificate2(Path.GetFullPath(path)))
            {
                return UpdatePackageValidator.NormalizeThumbprint(
                    certificate.Thumbprint,
                    "update publisher certificate thumbprint");
            }
        }

        private static bool IsTrayExecutable(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                (string.Equals(Path.GetFileName(path), "TermLink.exe", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileName(path), "TermLink.Tray.exe", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static class AuthenticodeVerifier
    {
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        internal static uint VerifyEmbeddedSignature(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The Authenticode file was not found.", path);

            var pathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
            var filePointer = IntPtr.Zero;
            var dataPointer = IntPtr.Zero;
            try
            {
                var file = new WinTrustFileInfo
                {
                    StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                    FilePath = pathPointer,
                    FileHandle = IntPtr.Zero,
                    KnownSubject = IntPtr.Zero
                };
                filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(file, filePointer, false);
                var data = new WinTrustData
                {
                    StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                    PolicyCallbackData = IntPtr.Zero,
                    SipClientData = IntPtr.Zero,
                    UiChoice = 2, // WTD_UI_NONE
                    RevocationChecks = 0, // WTD_REVOKE_NONE
                    UnionChoice = 1, // WTD_CHOICE_FILE
                    File = filePointer,
                    StateAction = 0, // WTD_STATEACTION_IGNORE
                    StateData = IntPtr.Zero,
                    UrlReference = IntPtr.Zero,
                    ProviderFlags = 0x00001000, // WTD_CACHE_ONLY_URL_RETRIEVAL
                    UiContext = 0
                };
                dataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
                Marshal.StructureToPtr(data, dataPointer, false);
                return unchecked((uint)WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPointer));
            }
            finally
            {
                if (dataPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(dataPointer);
                if (filePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(filePointer);
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        internal static string ReadSignerThumbprint(string path)
        {
            try
            {
                using (var certificate = X509Certificate.CreateFromSignedFile(Path.GetFullPath(path)))
                using (var certificate2 = new X509Certificate2(certificate.GetRawCertData()))
                {
                    return UpdatePackageValidator.NormalizeThumbprint(
                        certificate2.Thumbprint,
                        "Authenticode signer thumbprint");
                }
            }
            catch (CryptographicException error)
            {
                throw new CryptographicException("The update EXE has no readable Authenticode signer.", error);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            IntPtr trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructureSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructureSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }
    }

    internal static class WindowsUpdateInstaller
    {
        private static readonly HttpClient DownloadClient = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        internal static async Task<string> DownloadVerifyAndLaunchAsync(
            WindowsUpdateResult update,
            IProgress<UpdateTransferProgress> progress,
            CancellationToken cancellationToken)
        {
            var expectedSigner = await Task.Run(
                UpdateSigningIdentity.ResolveExpectedThumbprint,
                cancellationToken).ConfigureAwait(true);
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = await DownloadAsync(
                update,
                localData,
                DownloadClient,
                progress,
                cancellationToken).ConfigureAwait(true);
            progress?.Report(new UpdateTransferProgress
            {
                Phase = UpdateTransferPhase.Verifying,
                BytesReceived = update.SizeBytes.Value,
                TotalBytes = update.SizeBytes.Value
            });
            try
            {
                await Task.Run(() =>
                {
                    UpdatePackageValidator.ValidateIntegrity(path, update.SizeBytes.Value, update.Sha256);
                    UpdatePackageValidator.ValidateAuthenticode(path, expectedSigner, update.SignerFingerprint);
                }, cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                TryDeleteUnverified(path);
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new UpdateTransferProgress
            {
                Phase = UpdateTransferPhase.Launching,
                BytesReceived = update.SizeBytes.Value,
                TotalBytes = update.SizeBytes.Value
            });
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Path.GetTempPath(),
                UseShellExecute = true
            });
            if (process == null) throw new InvalidOperationException("The verified TermLink Setup could not be started.");
            process.Dispose();
            return path;
        }

        private static void TryDeleteUnverified(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // Preserve the verification error; the file was never launched.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the verification error; the file was never launched.
            }
        }

        internal static async Task<string> DownloadAsync(
            WindowsUpdateResult update,
            string localApplicationData,
            HttpClient client,
            IProgress<UpdateTransferProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateDownloadContract(update);
            if (string.IsNullOrWhiteSpace(localApplicationData))
                throw new InvalidOperationException("Local application data is unavailable.");
            var root = Path.GetFullPath(Path.Combine(localApplicationData, "TermLink", "Updates"));
            Directory.CreateDirectory(root);
            var finalPath = Path.Combine(root, CreateLocalFileName(update));
            var temporaryPath = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var initial = HttpsRedirectPolicy.ValidateInitial(update.DownloadUrl);
                using (var response = await AppShipUpdateClient.SendWithRedirectsAsync(
                    client,
                    initial,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    ValidateResponseHeaders(response, update);
                    using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    using (var destination = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[128 * 1024];
                        long received = 0;
                        int count;
                        while ((count = await source.ReadAsync(
                            buffer,
                            0,
                            buffer.Length,
                            cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            received = checked(received + count);
                            if (received > update.SizeBytes.Value)
                                throw new InvalidDataException("The update download exceeded the AppShip artifact size.");
                            await destination.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                            sha.AppendData(buffer, 0, count);
                            progress?.Report(new UpdateTransferProgress
                            {
                                Phase = UpdateTransferPhase.Downloading,
                                BytesReceived = received,
                                TotalBytes = update.SizeBytes.Value
                            });
                        }
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (received != update.SizeBytes.Value)
                            throw new InvalidDataException("The update download size does not match AppShip metadata.");
                        var actualHash = sha.GetHashAndReset();
                        var expectedHash = Convert.FromHexString(update.Sha256);
                        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                            throw new CryptographicException("The update download SHA-256 does not match AppShip metadata.");
                    }
                }
                File.Move(temporaryPath, finalPath, overwrite: true);
                return finalPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup never broadens the path or hides the download error.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup never broadens the path or hides the download error.
                }
            }
        }

        internal static void ValidateResponseHeaders(HttpResponseMessage response, WindowsUpdateResult update)
        {
            if (response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value != update.SizeBytes.Value)
            {
                throw new InvalidDataException("The HTTP Content-Length does not match AppShip metadata.");
            }
            if (response.Headers.TryGetValues("X-Checksum-SHA256", out var values))
            {
                var hashes = values.Select(AppShipUpdateClient.NormalizeHex).ToArray();
                if (hashes.Length == 0 || hashes.Any(value =>
                    !AppShipUpdateClient.IsHex(value, 64) ||
                    !string.Equals(value, update.Sha256, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("The HTTP checksum header does not match AppShip metadata.");
                }
            }
            // AppShip currently serves application/octet-stream. Content type is
            // deliberately not used as an integrity signal; signed EXE validation
            // below is authoritative and remains compatible with that response.
        }

        private static void ValidateDownloadContract(WindowsUpdateResult update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            if (update.Decision != WindowsUpdateDecision.Optional &&
                update.Decision != WindowsUpdateDecision.Required)
                throw new InvalidOperationException("The update result does not contain an available update.");
            if (!update.SizeBytes.HasValue || update.SizeBytes.Value <= 0 ||
                !AppShipUpdateClient.IsHex(update.Sha256, 64))
                throw new InvalidDataException("The AppShip download contract is incomplete.");
            HttpsRedirectPolicy.ValidateInitial(update.DownloadUrl);
        }

        private static string CreateLocalFileName(WindowsUpdateResult update)
        {
            var version = SafeComponent(update.VersionName, "unknown");
            var build = update.BuildNumber.HasValue ? update.BuildNumber.Value.ToString() : "unknown";
            return "TermLink-Setup-" + version + "-" + build + "-x64.exe";
        }

        private static string SafeComponent(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var result = new string(value.Where(character =>
                char.IsLetterOrDigit(character) || character == '.' || character == '-' || character == '_').ToArray());
            return string.IsNullOrEmpty(result) ? fallback : result.Substring(0, Math.Min(50, result.Length));
        }
    }

    internal sealed class WindowsUpdateWorkflow
    {
        private int inFlight;

        internal async Task<WindowsUpdateResult> CheckAndOfferAsync(
            IWin32Window owner,
            bool manual,
            Action<string> statusChanged = null)
        {
            if (Interlocked.Exchange(ref inFlight, 1) != 0)
            {
                statusChanged?.Invoke(TrayText.Pick("更新检查正在进行中", "An update check is already running"));
                return new WindowsUpdateResult
                {
                    Decision = WindowsUpdateDecision.Error,
                    Message = "An update check is already running."
                };
            }
            try
            {
                statusChanged?.Invoke(TrayText.Pick("正在检查更新…", "Checking for updates…"));
                var current = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
                var result = await AppShipUpdateClient.CheckAsync(
                    AppShipUpdateClient.ConfiguredBaseUrl(),
                    current).ConfigureAwait(true);
                statusChanged?.Invoke(StatusText(result));
                if (result.Decision == WindowsUpdateDecision.Optional ||
                    result.Decision == WindowsUpdateDecision.Required)
                {
                    using (var prompt = new UpdateAvailableForm(result))
                    {
                        var choice = owner == null ? prompt.ShowDialog() : prompt.ShowDialog(owner);
                        if (choice != DialogResult.OK) return result;
                    }
                    using (var progress = new UpdateProgressForm(result))
                    {
                        if (owner == null) progress.ShowDialog(); else progress.ShowDialog(owner);
                        if (progress.SetupLaunched)
                        {
                            statusChanged?.Invoke(TrayText.Pick(
                                "已验证并启动安装程序，请在安装向导中确认升级。",
                                "Verified Setup was started; confirm the upgrade in the installer."));
                        }
                        else if (!string.IsNullOrWhiteSpace(progress.ErrorMessage))
                        {
                            statusChanged?.Invoke(TrayText.Pick("更新失败 · ", "Update failed · ") + progress.ErrorMessage);
                        }
                    }
                }
                else if (manual && result.Decision == WindowsUpdateDecision.NoUpdate)
                {
                    MessageBox.Show(
                        owner,
                        TrayText.Pick("当前已是最新版本。", "You are up to date."),
                        "TermLink",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else if (manual && result.Decision == WindowsUpdateDecision.Error)
                {
                    MessageBox.Show(
                        owner,
                        TrayText.Pick("检查更新失败。", "Update check failed.") +
                            "\r\n\r\n" + result.Message,
                        "TermLink",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return result;
            }
            finally
            {
                Volatile.Write(ref inFlight, 0);
            }
        }

        internal static string StatusText(WindowsUpdateResult result)
        {
            if (result == null) return TrayText.Pick("更新状态不可用", "Update status is unavailable");
            switch (result.Decision)
            {
                case WindowsUpdateDecision.Optional:
                    return TrayText.Pick("发现新版本：", "Update available: ") + (result.VersionName ?? "-");
                case WindowsUpdateDecision.Required:
                    return TrayText.Pick("发现必要更新：", "Required update: ") + (result.VersionName ?? "-");
                case WindowsUpdateDecision.NoUpdate:
                    return TrayText.Pick("当前已是最新版本", "You are up to date");
                case WindowsUpdateDecision.NotConfigured:
                    return TrayText.Pick("更新服务未配置", "Update service is not configured");
                default:
                    return TrayText.Pick("检查更新失败", "Update check failed") +
                        (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : " · " + result.Message);
            }
        }
    }

    internal sealed class UpdateAvailableForm : Form
    {
        private static readonly Size LogicalClientSize = new Size(590, 405);
        private readonly Dictionary<Control, Rectangle> logicalBounds = new Dictionary<Control, Rectangle>();

        internal UpdateAvailableForm(WindowsUpdateResult update)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(DpiLayout.DesignDpi, DpiLayout.DesignDpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = TrayText.Pick("TermLink 更新", "TermLink Update");
            Icon = BrandIcons.LoadApplicationIcon();
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(590, 405);

            var title = new Label
            {
                AutoSize = false,
                Location = new Point(22, 20),
                Size = new Size(546, 48),
                Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
                Text = TrayText.Pick("发现 TermLink 新版本", "A TermLink update is available"),
                UseMnemonic = false
            };
            var version = new Label
            {
                AutoSize = false,
                Location = new Point(22, 73),
                Size = new Size(546, 42),
                Text = TrayText.Pick("版本：", "Version: ") + (update.VersionName ?? "-") +
                    TrayText.Pick("    构建：", "    Build: ") + (update.BuildNumber?.ToString() ?? "-") +
                    "\r\n" + TrayText.Pick("大小：", "Size: ") + FormatBytes(update.SizeBytes ?? 0),
                UseMnemonic = false
            };
            var notesLabel = new Label
            {
                AutoSize = true,
                Location = new Point(22, 122),
                Text = TrayText.Pick("更新说明", "Release notes"),
                Font = new Font(Font, FontStyle.Bold),
                UseMnemonic = false
            };
            var notes = new TextBox
            {
                Location = new Point(22, 147),
                Size = new Size(546, 155),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                    ? TrayText.Pick("暂无更新说明。", "No release notes were provided.")
                    : update.ReleaseNotes,
                TabStop = false
            };
            var safety = new Label
            {
                AutoSize = false,
                Location = new Point(22, 311),
                Size = new Size(546, 42),
                Text = TrayText.Pick(
                    "确认后将下载并验证大小、SHA-256 与 Authenticode 签名，然后打开安装向导。",
                    "After confirmation, TermLink verifies size, SHA-256 and the signer, then opens Setup."),
                UseMnemonic = false
            };
            var install = new Button
            {
                Location = new Point(292, 363),
                Size = new Size(158, 30),
                Text = TrayText.Pick("下载并安装", "Download and install"),
                DialogResult = DialogResult.OK,
                UseMnemonic = false
            };
            var later = new Button
            {
                Location = new Point(458, 363),
                Size = new Size(110, 30),
                Text = TrayText.Pick("以后再说", "Later"),
                DialogResult = DialogResult.Cancel,
                UseMnemonic = false
            };
            Controls.AddRange(new Control[] { title, version, notesLabel, notes, safety, install, later });
            AcceptButton = install;
            CancelButton = later;
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
            CaptureLogicalBounds();
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyLogicalBounds();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
        }

        private void CaptureLogicalBounds()
        {
            foreach (Control child in Controls) logicalBounds[child] = child.Bounds;
        }

        private void ApplyLogicalBounds()
        {
            SuspendLayout();
            ClientSize = new Size(
                DpiLayout.Scale96(LogicalClientSize.Width, DeviceDpi),
                DpiLayout.Scale96(LogicalClientSize.Height, DeviceDpi));
            foreach (var pair in logicalBounds)
            {
                pair.Key.Bounds = new Rectangle(
                    DpiLayout.Scale96(pair.Value.X, DeviceDpi),
                    DpiLayout.Scale96(pair.Value.Y, DeviceDpi),
                    DpiLayout.Scale96(pair.Value.Width, DeviceDpi),
                    DpiLayout.Scale96(pair.Value.Height, DeviceDpi));
            }
            ResumeLayout(false);
            PerformLayout();
        }

        private static string FormatBytes(long value)
        {
            return value >= 1024L * 1024L
                ? (value / (1024d * 1024d)).ToString("0.0") + " MB"
                : value.ToString() + " bytes";
        }
    }

    internal sealed class UpdateProgressForm : Form
    {
        private static readonly Size LogicalClientSize = new Size(530, 190);
        private readonly WindowsUpdateResult update;
        private readonly Label status;
        private readonly Label detail;
        private readonly ProgressBar progressBar;
        private readonly Button cancel;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Dictionary<Control, Rectangle> logicalBounds = new Dictionary<Control, Rectangle>();
        private bool completed;

        internal UpdateProgressForm(WindowsUpdateResult update)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(DpiLayout.DesignDpi, DpiLayout.DesignDpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            this.update = update ?? throw new ArgumentNullException(nameof(update));
            Text = TrayText.Pick("TermLink 更新", "TermLink Update");
            Icon = BrandIcons.LoadApplicationIcon();
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(530, 190);
            status = new Label
            {
                AutoSize = false,
                Location = new Point(20, 20),
                Size = new Size(490, 30),
                Font = new Font(Font, FontStyle.Bold),
                Text = TrayText.Pick("正在下载更新", "Downloading update"),
                UseMnemonic = false
            };
            detail = new Label
            {
                AutoSize = false,
                Location = new Point(20, 55),
                Size = new Size(490, 24),
                Text = "0%",
                UseMnemonic = false
            };
            progressBar = new ProgressBar
            {
                Location = new Point(20, 85),
                Size = new Size(490, 24),
                Minimum = 0,
                Maximum = 100
            };
            cancel = new Button
            {
                Location = new Point(370, 137),
                Size = new Size(140, 30),
                Text = TrayText.Pick("取消", "Cancel"),
                UseMnemonic = false
            };
            cancel.Click += delegate { cancellation.Cancel(); cancel.Enabled = false; };
            Controls.AddRange(new Control[] { status, detail, progressBar, cancel });
            Shown += async delegate { await RunAsync(); };
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
            CaptureLogicalBounds();
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyLogicalBounds();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
        }

        private void CaptureLogicalBounds()
        {
            foreach (Control child in Controls) logicalBounds[child] = child.Bounds;
        }

        private void ApplyLogicalBounds()
        {
            SuspendLayout();
            ClientSize = new Size(
                DpiLayout.Scale96(LogicalClientSize.Width, DeviceDpi),
                DpiLayout.Scale96(LogicalClientSize.Height, DeviceDpi));
            foreach (var pair in logicalBounds)
            {
                pair.Key.Bounds = new Rectangle(
                    DpiLayout.Scale96(pair.Value.X, DeviceDpi),
                    DpiLayout.Scale96(pair.Value.Y, DeviceDpi),
                    DpiLayout.Scale96(pair.Value.Width, DeviceDpi),
                    DpiLayout.Scale96(pair.Value.Height, DeviceDpi));
            }
            ResumeLayout(false);
            PerformLayout();
        }

        internal bool SetupLaunched { get; private set; }
        internal string ErrorMessage { get; private set; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!completed && e.CloseReason == CloseReason.UserClosing)
            {
                cancellation.Cancel();
                e.Cancel = true;
                cancel.Enabled = false;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cancellation.Dispose();
                Icon?.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task RunAsync()
        {
            try
            {
                var progress = new Progress<UpdateTransferProgress>(ApplyProgress);
                await WindowsUpdateInstaller.DownloadVerifyAndLaunchAsync(
                    update,
                    progress,
                    cancellation.Token);
                SetupLaunched = true;
                completed = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException)
            {
                completed = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
            catch (Exception error)
            {
                ErrorMessage = Limit(error.Message, 500);
                completed = true;
                MessageBox.Show(
                    this,
                    TrayText.Pick(
                        "更新未安装，已拒绝不安全或无效的安装包。",
                        "The update was not installed because the package was unsafe or invalid.") +
                            "\r\n\r\n" + ErrorMessage,
                    "TermLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                DialogResult = DialogResult.Abort;
                Close();
            }
        }

        private void ApplyProgress(UpdateTransferProgress value)
        {
            if (value == null || IsDisposed) return;
            if (value.Phase == UpdateTransferPhase.Verifying)
            {
                status.Text = TrayText.Pick("正在验证大小、SHA-256 与签名", "Verifying size, SHA-256 and signer");
                detail.Text = TrayText.Pick("已完成下载", "Download complete");
                progressBar.Value = 100;
                cancel.Enabled = false;
                return;
            }
            if (value.Phase == UpdateTransferPhase.Launching)
            {
                status.Text = TrayText.Pick("正在打开安装向导", "Opening TermLink Setup");
                detail.Text = TrayText.Pick("请在安装向导中确认升级", "Confirm the upgrade in Setup");
                progressBar.Value = 100;
                cancel.Enabled = false;
                return;
            }
            var percent = value.TotalBytes <= 0
                ? 0
                : (int)Math.Min(100, value.BytesReceived * 100L / value.TotalBytes);
            status.Text = TrayText.Pick("正在下载更新", "Downloading update");
            detail.Text = percent + "% · " + FormatMegabytes(value.BytesReceived) + " / " + FormatMegabytes(value.TotalBytes);
            progressBar.Value = percent;
        }

        private static string FormatMegabytes(long value)
        {
            return (value / (1024d * 1024d)).ToString("0.0") + " MB";
        }

        private static string Limit(string value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value)) return "No error details were provided.";
            value = value.Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }
}
