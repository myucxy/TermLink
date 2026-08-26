using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Tray;

namespace TermLink.Tray.UpdateValidation
{
    internal static class Program
    {
        private static readonly List<string> Cases = new List<string>();

        public static int Main(string[] args)
        {
            try
            {
                ParsesAppShipArtifactContract();
                ExportsBoundedDesktopUpdateStatus();
                ParsesDesktopUpdateArguments();
                UsesFourthVersionComponentAsAppShipBuild();
                UsesBuildBoundUpdateChannel();
                RejectsIncompleteOrWrongArtifactMetadata();
                EnforcesHttpsRedirectLimit();
                ValidatesSizeAndSha256();
                ValidatesSignerIdentityAndTrustPolicy();
                ValidatesDownloadResponseHeaders();
                if (args.Length == 1) ValidatesSignedDevelopmentSetup(args[0]);
                Console.WriteLine("Tray update validation passed: " + string.Join(", ", Cases));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void UsesBuildBoundUpdateChannel()
        {
            Assert(AppShipUpdateClient.ApplicationSlug == "termlink-win64", "stable application slug metadata");
            Assert(AppShipUpdateClient.UpdateChannel == "stable", "stable update channel metadata");
            Cases.Add("build_bound_update_channel");
        }

        private static void ParsesAppShipArtifactContract()
        {
            const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            const string signer = "0123456789abcdef0123456789abcdef01234567";
            var json = "{\"status\":\"optional\",\"latest\":{\"versionName\":\"1.2.3\",\"buildNumber\":123," +
                "\"releaseNotes\":\"notes\"},\"artifact\":{\"platform\":\"windows\",\"fileKind\":\"exe\"," +
                "\"architecture\":\"x64\",\"status\":\"ready\",\"downloadEnabled\":true," +
                "\"originalFilename\":\"TermLink-Setup-1.2.3.exe\",\"sizeBytes\":99,\"expectedSizeBytes\":99," +
                "\"sha256\":\"" + hash + "\",\"signerFingerprint\":\"" + signer + "\"," +
                "\"downloadUrl\":\"https://updates.example.test/setup.exe\"}}";
            var result = AppShipUpdateClient.ParseForValidation(json, 122);
            Assert(result.Decision == WindowsUpdateDecision.Optional, "valid response decision");
            Assert(result.BuildNumber == 123 && result.SizeBytes == 99, "valid response numbers");
            Assert(result.Sha256 == hash && result.SignerFingerprint == signer, "valid response hashes");
            Assert(result.DownloadUrl == "https://updates.example.test/setup.exe", "valid response URL");
            var current = AppShipUpdateClient.ParseForValidation(json, 123);
            Assert(current.Decision == WindowsUpdateDecision.NoUpdate && current.ReleaseNotes == "notes",
                "stale response cannot downgrade current build and preserves AppShip notes");
            Cases.Add("appship_contract");
        }

        private static void ExportsBoundedDesktopUpdateStatus()
        {
            var json = WindowsUpdateResultExport.Serialize(new WindowsUpdateResult
            {
                Decision = WindowsUpdateDecision.NoUpdate,
                VersionName = "0.2.4",
                BuildNumber = 45,
                ReleaseNotes = " AppShip export fixture ",
                DownloadUrl = "https://updates.example.test/private-implementation-detail.exe",
                Sha256 = new string('a', 64),
                Message = "current"
            });
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert(root.GetProperty("schemaVersion").GetInt32() == 1, "desktop export schema version");
            Assert(root.GetProperty("releaseNotes").GetString() == "AppShip export fixture",
                "desktop export release notes");
            Assert(!root.TryGetProperty("downloadUrl", out _) && !root.TryGetProperty("sha256", out _),
                "desktop export excludes update authority fields");
            Cases.Add("desktop_update_export");
        }

        private static void ParsesDesktopUpdateArguments()
        {
            var resultPath = Path.Combine(Path.GetTempPath(), "termlink-update-result.json");
            var options = TrayOptions.Parse(new[]
            {
                "--check-update",
                "--check-update-silent",
                "--check-update-result-json",
                resultPath
            });
            Assert(options.CheckUpdate && options.CheckUpdateSilent &&
                options.CheckUpdateResultJsonPath == resultPath,
                "desktop update result arguments");
            Expect<ArgumentException>(() => TrayOptions.Parse(new[]
            {
                "--check-update-result-json",
                resultPath
            }));
            Cases.Add("desktop_update_arguments");
        }

        private static void UsesFourthVersionComponentAsAppShipBuild()
        {
            Assert(AppShipUpdateClient.CurrentBuild(new Version(0, 1, 14, 18)) == 18,
                "four-part setup version uses revision as AppShip build");
            Assert(AppShipUpdateClient.CurrentBuild(new Version(0, 1, 18)) == 18,
                "three-part version falls back to build component");
            Assert(AppShipUpdateClient.CurrentBuild(new Version(0, 1)) == 0,
                "two-part development version has build zero");
            Cases.Add("current_build_mapping");
        }

        private static void RejectsIncompleteOrWrongArtifactMetadata()
        {
            var missingHash = "{\"status\":\"optional\",\"latest\":{\"buildNumber\":2},\"artifact\":{" +
                "\"sizeBytes\":10,\"downloadUrl\":\"https://example.test/a.exe\"}}";
            Assert(AppShipUpdateClient.ParseForValidation(missingHash, 1).Decision == WindowsUpdateDecision.Error,
                "missing SHA-256 rejected");
            var wrongKind = "{\"status\":\"optional\",\"latest\":{\"buildNumber\":2},\"artifact\":{" +
                "\"fileKind\":\"zip\",\"sizeBytes\":10,\"sha256\":\"" + new string('a', 64) +
                "\",\"downloadUrl\":\"https://example.test/a.exe\"}}";
            Assert(AppShipUpdateClient.ParseForValidation(wrongKind, 1).Decision == WindowsUpdateDecision.Error,
                "non-EXE artifact rejected");
            var http = wrongKind.Replace("https://", "http://").Replace("\"zip\"", "\"exe\"");
            Assert(AppShipUpdateClient.ParseForValidation(http, 1).Decision == WindowsUpdateDecision.Error,
                "HTTP download rejected");
            Assert(AppShipUpdateClient.ParseForValidation(
                "{\"status\":\"error\",\"message\":\"temporarily unavailable\"}", 1).Decision ==
                WindowsUpdateDecision.Error,
                "successful HTTP response with error status rejected");
            Cases.Add("metadata_rejection");
        }

        private static void EnforcesHttpsRedirectLimit()
        {
            var current = HttpsRedirectPolicy.ValidateInitial("https://updates.example.test/a");
            var next = HttpsRedirectPolicy.Resolve(current, new Uri("/b", UriKind.Relative), 0);
            Assert(next.AbsoluteUri == "https://updates.example.test/b", "relative HTTPS redirect accepted");
            Expect<InvalidDataException>(() =>
                HttpsRedirectPolicy.Resolve(current, new Uri("http://updates.example.test/b"), 0));
            Expect<HttpRequestException>(() =>
                HttpsRedirectPolicy.Resolve(current, new Uri("https://updates.example.test/b"),
                    HttpsRedirectPolicy.MaximumRedirects));
            Cases.Add("redirect_policy");
        }

        private static void ValidatesSizeAndSha256()
        {
            var root = Path.Combine(Path.GetTempPath(), "TermLink-TrayUpdateValidation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "payload.exe");
            try
            {
                var bytes = new byte[] { 1, 3, 3, 7, 9 };
                File.WriteAllBytes(path, bytes);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                UpdatePackageValidator.ValidateIntegrity(path, bytes.Length, hash);
                Expect<InvalidDataException>(() => UpdatePackageValidator.ValidateIntegrity(path, bytes.Length + 1, hash));
                Expect<CryptographicException>(() =>
                    UpdatePackageValidator.ValidateIntegrity(path, bytes.Length, new string('0', 64)));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(root)) Directory.Delete(root);
            }
            Cases.Add("payload_integrity");
        }

        private static void ValidatesSignerIdentityAndTrustPolicy()
        {
            const string signer = "0123456789abcdef0123456789abcdef01234567";
            UpdatePackageValidator.ValidateSignerIdentity(signer, signer.ToUpperInvariant(), signer);
            Expect<CryptographicException>(() =>
                UpdatePackageValidator.ValidateSignerIdentity(signer, new string('a', 40), signer));
            Expect<CryptographicException>(() =>
                UpdatePackageValidator.ValidateSignerIdentity(signer, signer, new string('b', 40)));
            Assert(UpdatePackageValidator.IsAcceptableAuthenticodeStatus(UpdatePackageValidator.TrustSuccess),
                "trusted signature accepted");
            Assert(UpdatePackageValidator.IsAcceptableAuthenticodeStatus(UpdatePackageValidator.CertificateUntrustedRoot),
                "pinned development signer with untrusted root accepted");
            Assert(UpdatePackageValidator.IsAcceptableAuthenticodeStatus(UpdatePackageValidator.CertificateChaining),
                "pinned signer with unavailable chain accepted");
            Assert(!UpdatePackageValidator.IsAcceptableAuthenticodeStatus(UpdatePackageValidator.TrustBadDigest),
                "bad digest rejected");
            Assert(!UpdatePackageValidator.IsAcceptableAuthenticodeStatus(UpdatePackageValidator.TrustNoSignature),
                "unsigned file rejected");
            Cases.Add("authenticode_policy");
        }

        private static void ValidatesDownloadResponseHeaders()
        {
            const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var update = new WindowsUpdateResult { SizeBytes = 42, Sha256 = hash };
            using (var valid = new HttpResponseMessage(HttpStatusCode.OK))
            {
                valid.Content = new ByteArrayContent(new byte[42]);
                valid.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                valid.Headers.Add("X-Checksum-SHA256", hash);
                WindowsUpdateInstaller.ValidateResponseHeaders(valid, update);
            }
            using (var wrongLength = new HttpResponseMessage(HttpStatusCode.OK))
            {
                wrongLength.Content = new ByteArrayContent(new byte[41]);
                Expect<InvalidDataException>(() => WindowsUpdateInstaller.ValidateResponseHeaders(wrongLength, update));
            }
            using (var wrongHash = new HttpResponseMessage(HttpStatusCode.OK))
            {
                wrongHash.Content = new ByteArrayContent(new byte[42]);
                wrongHash.Headers.Add("X-Checksum-SHA256", new string('f', 64));
                Expect<InvalidDataException>(() => WindowsUpdateInstaller.ValidateResponseHeaders(wrongHash, update));
            }
            Cases.Add("download_headers");
        }

        private static void ValidatesSignedDevelopmentSetup(string path)
        {
            var status = AuthenticodeVerifier.VerifyEmbeddedSignature(path);
            Assert(UpdatePackageValidator.IsAcceptableAuthenticodeStatus(status),
                "development Setup signature has an acceptable pinned-signer trust result");
            var signer = AuthenticodeVerifier.ReadSignerThumbprint(path);
            Assert(AppShipUpdateClient.IsHex(signer, 40), "development Setup signer thumbprint extracted");
            UpdatePackageValidator.ValidateSignerIdentity(signer, signer, signer);
            Cases.Add("signed_development_setup");
        }

        private static void Assert(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + description);
        }

        private static void Expect<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException("Expected exception: " + typeof(TException).Name);
        }
    }
}
