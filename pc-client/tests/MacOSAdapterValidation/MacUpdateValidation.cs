using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TermLink.Platform;
using TermLink.Platform.MacOS;

internal static class MacUpdateValidation
{
    private const string Team = "ABCDEFGHIJ";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("offline signed-package fixture, not an installable package\n");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    public static async Task RunAsync()
    {
        if (MacPlatformOptions.IsDevelopmentBuild)
            Require(MacUpdateOptions.CreateDefault() is null, "A DEV build enabled the production update trust anchor.");
        await ValidateConfigurationAndManifestAsync();
        await ValidateChecksAndConfirmationsAsync();
        await ValidateDownloadIntegrityAsync();
        await ValidateInstallLifecycleAsync();
        await ValidateCurrentApplicationTrustAsync();
        if (OperatingSystem.IsMacOS()) await ValidatePackageInstallerAsync();
        else Console.WriteLine("NOT APPLICABLE Mac package installer OS guard (macOS only)");
        Console.WriteLine("PASS offline Mac updates: manifest, confirmations, integrity/cache/cancel, publisher trust, install lifecycle and recovery");
    }

    private static async Task ValidateConfigurationAndManifestAsync()
    {
        using (var fixture = new Fixture())
        {
            var invalidOptions = new (MacUpdateOptions Options, Type Error)[]
            {
                (fixture.Options with { ServiceUri = new("http://updates.invalid") }, typeof(InvalidDataException)),
                (fixture.Options with { ServiceUri = new("https://user:pass@updates.invalid") }, typeof(InvalidDataException)),
                (fixture.Options with { ServiceUri = new("https://updates.invalid/#fragment") }, typeof(InvalidDataException)),
                (fixture.Options with { ServiceUri = new("https://updates.invalid/?query=1") }, typeof(ArgumentException)),
                (fixture.Options with { ApplicationSlug = "../other" }, typeof(ArgumentException)),
                (fixture.Options with { Channel = "untrusted" }, typeof(ArgumentException)),
                (fixture.Options with { Architecture = "arm32" }, typeof(ArgumentException)),
                (fixture.Options with { CurrentBuild = 0 }, typeof(ArgumentException)),
                (fixture.Options with { CurrentBuild = 65536 }, typeof(ArgumentException)),
                (fixture.Options with { TrustedTeamId = "not-a-team" }, typeof(CryptographicException)),
            };
            foreach (var (options, error) in invalidOptions)
                await RejectedAsync(() => Task.Run(() => fixture.CreateService(options)), error, "invalid local update configuration");
            Require(fixture.Http.Requests.Count == 0, "Invalid configuration made an HTTP request.");
        }

        var invalidManifests = new (string Name, Action<JsonObject> Change, Type Error)[]
        {
            ("unknown decision", root => root["status"] = "install-now", typeof(InvalidDataException)),
            ("platform", root => root["artifact"]!["platform"] = "windows", typeof(InvalidDataException)),
            ("architecture", root => root["artifact"]!["architecture"] = "x64", typeof(InvalidDataException)),
            ("format", root => root["artifact"]!["fileKind"] = "zip", typeof(InvalidDataException)),
            ("artifact readiness", root => root["artifact"]!["status"] = "processing", typeof(InvalidDataException)),
            ("download disabled", root => root["artifact"]!["downloadEnabled"] = false, typeof(InvalidDataException)),
            ("same build", root => root["latest"]!["buildNumber"] = 146, typeof(InvalidDataException)),
            ("older build", root => root["latest"]!["buildNumber"] = 145, typeof(InvalidDataException)),
            ("build range", root => root["latest"]!["buildNumber"] = 65536, typeof(InvalidDataException)),
            ("version", root => root["latest"]!["versionName"] = "preview", typeof(InvalidDataException)),
            ("publisher", root => root["artifact"]!["signerFingerprint"] = "ZYXWVUTSRQ", typeof(CryptographicException)),
            ("unsigned publisher", root => root["artifact"]!["signerFingerprint"] = "", typeof(CryptographicException)),
            ("hash", root => root["artifact"]!["sha256"] = "invalid", typeof(InvalidDataException)),
            ("zero size", root => root["artifact"]!["sizeBytes"] = 0, typeof(InvalidDataException)),
            ("size range", root => root["artifact"]!["sizeBytes"] = 2L * 1024 * 1024 * 1024 + 1, typeof(InvalidDataException)),
            ("expected size", root => root["artifact"]!["expectedSizeBytes"] = Payload.Length + 1, typeof(InvalidDataException)),
            ("filename", root => root["artifact"]!["originalFilename"] = "TermLink.zip", typeof(InvalidDataException)),
            ("HTTP download", root => root["artifact"]!["downloadUrl"] = "http://cdn.invalid/update.pkg", typeof(InvalidDataException)),
            ("URL credentials", root => root["artifact"]!["downloadUrl"] = "https://user:pass@cdn.invalid/update.pkg", typeof(InvalidDataException)),
            ("URL fragment", root => root["artifact"]!["downloadUrl"] = "https://cdn.invalid/update.pkg#fragment", typeof(InvalidDataException)),
            ("notes bound", root => root["latest"]!["releaseNotes"] = new string('x', 32769), typeof(InvalidDataException)),
        };
        foreach (var (name, change, error) in invalidManifests)
        {
            using var fixture = new Fixture();
            fixture.Http.Manifest = Manifest(change);
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(), error, name);
            Require(fixture.Http.Downloads == 0 && fixture.Installer.Verifications == 0 && fixture.Prepared == 0,
                "Rejected manifest reached download or installation: " + name);
        }
        using (var fixture = new Fixture())
        {
            fixture.Http.Manifest = "{";
            await RejectedAsync(() => fixture.Service.CheckAsync(false, null, null, CancellationToken.None).AsTask(),
                typeof(System.Text.Json.JsonException), "malformed JSON");
        }
        using (var fixture = new Fixture())
        {
            fixture.Http.Manifest = new string('x', 65537);
            await RejectedAsync(() => fixture.Service.CheckAsync(false, null, null, CancellationToken.None).AsTask(),
                typeof(InvalidDataException), "manifest response bound");
        }
        using (var fixture = new Fixture())
        {
            fixture.Http.ManifestRedirect = new Uri("https://different.invalid/manifest");
            await RejectedAsync(() => fixture.Service.CheckAsync(false, null, null, CancellationToken.None).AsTask(),
                typeof(HttpRequestException), "manifest redirect");
            Require(fixture.Http.Requests.Count == 1, "A manifest redirect was followed.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Http.DownloadRedirect = new Uri("http://different.invalid/update.pkg");
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(),
                typeof(InvalidDataException), "download HTTPS downgrade");
            Require(fixture.Http.Requests.All(uri => uri.Scheme == "https"), "An HTTP download redirect was followed.");
        }
    }

    private static async Task ValidateChecksAndConfirmationsAsync()
    {
        foreach (var decision in new[] { "no_update", "current" })
        {
            using var fixture = new Fixture();
            fixture.Http.Manifest = "{\"status\":\"" + decision + "\"}";
            var status = await fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None);
            Require(status.Decision == DesktopUpdateDecision.NoUpdate && fixture.Confirmations.Count == 0 && fixture.Http.Downloads == 0,
                "A current release downloaded or prompted for an installation.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Http.ManifestStatus = HttpStatusCode.NotFound;
            var status = await fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None);
            Require(status.Decision == DesktopUpdateDecision.NotConfigured && fixture.Http.Downloads == 0, "An unpublished channel was treated as an update.");
        }
        using (var fixture = new Fixture())
        {
            var status = await fixture.Service.CheckAsync(false, (_, _) => throw new InvalidOperationException("Check-only prompted"), null, CancellationToken.None);
            Require(status is { Decision: DesktopUpdateDecision.Optional, BuildNumber: 147, ReadyToInstall: false, ApplicationRestartRequired: false } &&
                fixture.Http.Downloads == 0 && fixture.Installer.Verifications == 0 && fixture.Prepared == 0,
                "A check-only request downloaded or prepared an update.");
            Require(fixture.Events[0] == "current" && fixture.Http.Requests.Single().PathAndQuery ==
                "/api/v1/apps/termlink-macos-arm64/updates?platform=macos&arch=arm64&channel=stable&build=146",
                "The installed trust anchor was not checked before the architecture/channel/build request.");
            await fixture.Service.CheckAsync(true, null, null, CancellationToken.None);
            Require(fixture.Http.Downloads == 0, "Installation without an approval callback downloaded a package.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Answers.Enqueue(false);
            var status = await fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None);
            Require(!status.ReadyToInstall && fixture.Confirmations.Count == 1 && fixture.Http.Downloads == 0 && fixture.Prepared == 0,
                "Canceling the download approval caused a download or stopped the Agent.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Answers.Enqueue(true);
            fixture.Answers.Enqueue(false);
            var progress = new RecordedProgress();
            var status = await fixture.Service.CheckAsync(true, fixture.Approve, progress, CancellationToken.None);
            Require(status is { ReadyToInstall: true, UsesSystemInstaller: true, ApplicationRestartRequired: false } &&
                fixture.Confirmations.Count == 2 && !fixture.Confirmations[0].ReadyToInstall && fixture.Confirmations[1].ReadyToInstall &&
                fixture.Installer.Verifications == 1 && fixture.Prepared == 0 && fixture.Installer.Launches == 0,
                "Canceling the install approval did not retain a verified package without stopping the Agent.");
            Require(progress.Values.Any(value => value.BytesReceived == Payload.Length && value.Verifying) &&
                progress.Values.All(value => !value.Installing), "Download progress advertised installation before its approval.");
            fixture.Answers.Enqueue(true);
            fixture.Answers.Enqueue(false);
            await fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None);
            Require(fixture.Http.Downloads == 1 && fixture.Installer.Verifications == 2, "A valid cached package was downloaded again or used without reverification.");
            await File.WriteAllTextAsync(fixture.CachedPackage, "corrupt cache");
            fixture.Answers.Enqueue(true);
            fixture.Answers.Enqueue(false);
            await fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None);
            Require(fixture.Http.Downloads == 2 && fixture.Installer.Verifications == 4 &&
                (await File.ReadAllBytesAsync(fixture.CachedPackage)).SequenceEqual(Payload), "A corrupt cached package was not discarded and downloaded again.");
            fixture.RequireNoTemporaryFiles();
        }
    }

    private static async Task ValidateDownloadIntegrityAsync()
    {
        var changed = Payload.ToArray();
        changed[0] ^= 1;
        var cases = new (string Name, Func<HttpContent> Content, Type Error)[]
        {
            ("SHA mismatch", () => new ByteArrayContent(changed), typeof(CryptographicException)),
            ("Content-Length mismatch", () => new ByteArrayContent([.. Payload, 0]), typeof(InvalidDataException)),
            ("truncated body", () => StreamingContent(Payload[..^1]), typeof(CryptographicException)),
            ("oversized body", () => StreamingContent([.. Payload, 0]), typeof(InvalidDataException)),
        };
        foreach (var (name, content, error) in cases)
        {
            using var fixture = new Fixture();
            fixture.Http.DownloadContent = content;
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(), error, name);
            fixture.RequireNoTemporaryFiles();
            Require(!File.Exists(fixture.CachedPackage) && fixture.Installer.Verifications == 0 && fixture.Prepared == 0,
                "An invalid download reached signature verification or installation: " + name);
        }
        using (var fixture = new Fixture())
        {
            using var cancellation = new CancellationTokenSource();
            var stream = new BlockingReadStream();
            fixture.Http.DownloadContent = () => new StreamContent(stream);
            var progress = new RecordedProgress(value => { if (value.BytesReceived > 0) cancellation.Cancel(); });
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, progress, cancellation.Token).AsTask(),
                typeof(OperationCanceledException), "download cancellation");
            Require(stream.Started && fixture.Installer.Verifications == 0 && fixture.Prepared == 0 && !File.Exists(fixture.CachedPackage),
                "Canceled download proceeded to verification or installation.");
            fixture.RequireNoTemporaryFiles();
        }
    }

    private static async Task ValidateInstallLifecycleAsync()
    {
        using (var fixture = new Fixture())
        {
            fixture.CurrentFailure = new CryptographicException("untrusted installed application");
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(),
                typeof(CryptographicException), "current application trust");
            Require(fixture.Http.Requests.Count == 0 && fixture.Prepared == 0, "An untrusted installed application queried or installed updates.");
        }
        foreach (var verification in new[] { 1, 2 })
        {
            using var fixture = new Fixture();
            fixture.Installer.RejectVerification = verification;
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(),
                typeof(CryptographicException), "package signature rejection " + verification);
            Require(fixture.Prepared == 0 && fixture.Recovered == 0 && fixture.Installer.Launches == 0,
                "A signature rejected before/after final approval stopped the Agent.");
            fixture.RequireNoTemporaryFiles();
        }
        using (var fixture = new Fixture())
        {
            var status = await fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None);
            Require(status.ApplicationRestartRequired && !status.ReadyToInstall && fixture.Prepared == 1 && fixture.Installer.Launches == 1 && fixture.Recovered == 0,
                "Successful installation did not request exactly one application restart.");
            Require(fixture.Events.IndexOf("verify:2") < fixture.Events.IndexOf("prepare") &&
                fixture.Events.IndexOf("prepare") < fixture.Events.IndexOf("launch"), "Agent stop, final verification and installer launch were ordered incorrectly.");
        }
        foreach (var reason in new[] { "UPDATE_INSTALLER_FAILED", "UPDATE_INSTALLER_CANCELLED" })
        {
            using var fixture = new Fixture();
            fixture.Installer.LaunchFailure = new InvalidOperationException(reason);
            var failure = await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(),
                typeof(InvalidOperationException), reason);
            Require(failure.Message == reason && fixture.Prepared == 1 && fixture.Recovered == 1 && fixture.Installer.Launches == 1 &&
                fixture.Events.IndexOf("launch") < fixture.Events.IndexOf("recover"), "Failed/canceled system installation did not restore the Agent.");
        }
        using (var fixture = new Fixture())
        {
            fixture.PrepareFailure = new IOException("Agent stopped, preparation then failed");
            await RejectedAsync(() => fixture.Service.CheckAsync(true, fixture.Approve, null, CancellationToken.None).AsTask(),
                typeof(IOException), "partial preparation");
            Require(fixture.Prepared == 1 && fixture.Recovered == 1 && fixture.Installer.Launches == 0,
                "Partially failed preparation left the Agent stopped or launched an installer.");
        }
    }

    private static async Task ValidateCurrentApplicationTrustAsync()
    {
        using var fixture = new Fixture();
        var runner = new FixtureRunner();
        await MacUpdateTrustBridge.VerifyCurrentAsync(runner, fixture.Options, CancellationToken.None);
        Require(runner.Calls.All(call => call.Executable != "/usr/bin/osascript") &&
            runner.Calls.Any(call => call.Executable == "/usr/bin/plutil" && call.Arguments[1] == "CFBundleIdentifier"),
            "Checking the installed publisher skipped the production bundle identifier or invoked installation.");
        foreach (var fault in new[] { "application-team", "application-authority", "application-notarization", "application-id", "application-signature" })
        {
            runner = new FixtureRunner { Fault = fault };
            await RejectedAsync(() => MacUpdateTrustBridge.VerifyCurrentAsync(runner, fixture.Options, CancellationToken.None).AsTask(),
                typeof(CryptographicException), fault);
        }
    }

    private static async Task ValidatePackageInstallerAsync()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "update ' $HOME `id` \"quoted\" \\name.pkg");
        await File.WriteAllBytesAsync(path, Payload);
        var package = new UpdatePackage("0.3.17", 147, new("https://cdn.invalid/update.pkg"), Payload.Length, Digest, Team, "pkg");
        foreach (var fault in new[] { "package-signature", "package-leaf-team", "package-second-certificate", "package-origin", "package-notarization", "package-assessment" })
        {
            var runner = new FixtureRunner { Fault = fault };
            var installer = new MacPackageUpdateInstaller(fixture.Root, runner, Team, fixture.Options.ApplicationBundlePath);
            await RejectedAsync(() => installer.VerifyAndStageAsync(package, path, CancellationToken.None).AsTask(), typeof(CryptographicException), fault);
            Require(runner.Calls.All(call => call.Executable != "/usr/bin/osascript"), "Rejected package invoked the privileged installer.");
        }
        var metadataCases = new (UpdatePackage Package, Type Error)[]
        {
            (package with { SignatureIdentity = "ZYXWVUTSRQ" }, typeof(CryptographicException)),
            (package with { PackageKind = "zip" }, typeof(InvalidDataException)),
            (package with { BuildNumber = 0 }, typeof(InvalidDataException)),
            (package with { Sha256 = "invalid" }, typeof(InvalidDataException)),
            (package with { Size = Payload.Length + 1 }, typeof(CryptographicException)),
            (package with { Sha256 = new string('0', 64) }, typeof(CryptographicException)),
        };
        foreach (var (invalid, error) in metadataCases)
        {
            var runner = new FixtureRunner();
            await RejectedAsync(() => new MacPackageUpdateInstaller(fixture.Root, runner, Team)
                .VerifyAndStageAsync(invalid, path, CancellationToken.None).AsTask(), error, "installer metadata/hash");
            Require(runner.Calls.Count == 0, "Invalid local metadata or file integrity invoked native trust commands.");
        }
        var link = Path.Combine(fixture.Root, "linked.pkg");
        File.CreateSymbolicLink(link, path);
        await RejectedAsync(() => new MacPackageUpdateInstaller(fixture.Root, new FixtureRunner(), Team)
            .VerifyAndStageAsync(package, link, CancellationToken.None).AsTask(), typeof(InvalidDataException), "symlink package");
        var mutatedRunner = new FixtureRunner
        {
            AfterCall = call =>
            {
                if (call.Executable == "/usr/sbin/spctl")
                {
                    var changed = Payload.ToArray(); changed[0] ^= 1;
                    File.WriteAllBytes(path, changed);
                }
            }
        };
        await RejectedAsync(() => new MacPackageUpdateInstaller(fixture.Root, mutatedRunner, Team)
            .VerifyAndStageAsync(package, path, CancellationToken.None).AsTask(), typeof(CryptographicException), "file replaced during native verification");
        await File.WriteAllBytesAsync(path, Payload);

        var success = new FixtureRunner();
        await new MacPackageUpdateInstaller(fixture.Root, success, Team, fixture.Options.ApplicationBundlePath)
            .LaunchAsync(package, path, CancellationToken.None);
        var authorization = success.Calls.Single(call => call.Executable == "/usr/bin/osascript");
        Require(authorization.Arguments.Count == 2 && authorization.Arguments[0] == "-e" && !authorization.CanBeCanceled,
            "System authorization was split into extra arguments or could interrupt a running installer.");
        var command = DecodeAppleScript(authorization.Arguments[1]);
        var copyIndex = command.IndexOf("/bin/cp -X ", StringComparison.Ordinal);
        var digestIndex = command.IndexOf("/usr/bin/shasum -a 256", StringComparison.Ordinal);
        var installIndex = command.IndexOf("/usr/sbin/installer -pkg", StringComparison.Ordinal);
        Require(command.Contains("/usr/bin/mktemp -d /private/tmp/termlink-update.", StringComparison.Ordinal) &&
            command.Contains("/bin/chmod 700", StringComparison.Ordinal) && command.Contains(Digest, StringComparison.Ordinal) &&
            copyIndex >= 0 && copyIndex < digestIndex && digestIndex < installIndex,
            "Privileged installation skipped private staging or the repeated SHA-256 comparison.");
        var argumentStart = copyIndex + "/bin/cp -X ".Length;
        var argumentEnd = command.IndexOf(" \"$stage/update.pkg\"", argumentStart, StringComparison.Ordinal);
        Require(argumentEnd > argumentStart && DecodeShellWord(command[argumentStart..argumentEnd]) == path,
            "The package path was not safely preserved through AppleScript and shell quoting.");
        Require(success.Calls.Any(call => call.Executable == "/usr/bin/plutil" && call.Arguments[1] == "CFBundleVersion") &&
            success.Calls.FindIndex(call => call.Executable == "/usr/bin/plutil") > success.Calls.IndexOf(authorization),
            "The installed application's version/build was not verified after installation.");
        foreach (var fault in new[] { "authorization-cancel", "installer-failure", "installed-version", "installed-build" })
        {
            var runner = new FixtureRunner { Fault = fault };
            var error = await RejectedAsync(() => new MacPackageUpdateInstaller(fixture.Root, runner, Team, fixture.Options.ApplicationBundlePath)
                .LaunchAsync(package, path, CancellationToken.None).AsTask(),
                fault.StartsWith("installed-", StringComparison.Ordinal) ? typeof(CryptographicException) : typeof(InvalidOperationException), fault);
            if (fault == "authorization-cancel") Require(error.Message == "UPDATE_INSTALLER_CANCELLED", "System authorization cancellation was not distinguished.");
            if (fault == "installer-failure") Require(error.Message == "UPDATE_INSTALLER_FAILED", "Installer failure exposed raw system output.");
        }
    }

    private static string DecodeAppleScript(string script)
    {
        const string prefix = "do shell script \"";
        const string suffix = "\" with administrator privileges";
        Require(script.StartsWith(prefix, StringComparison.Ordinal) && script.EndsWith(suffix, StringComparison.Ordinal), "Unexpected authorization script shape.");
        var encoded = script[prefix.Length..^suffix.Length];
        var result = new StringBuilder();
        for (var index = 0; index < encoded.Length; index++)
        {
            var value = encoded[index];
            if (value == '\\')
            {
                Require(++index < encoded.Length && encoded[index] is '\\' or '"', "Invalid AppleScript string escaping.");
                value = encoded[index];
            }
            else Require(value != '"', "Unescaped quote broke the AppleScript argument.");
            result.Append(value);
        }
        return result.ToString();
    }

    private static string DecodeShellWord(string word)
    {
        var result = new StringBuilder();
        for (var index = 0; index < word.Length;)
        {
            if (word[index] == '\'')
            {
                var end = word.IndexOf('\'', ++index);
                Require(end >= index, "Unclosed shell quote.");
                result.Append(word[index..end]); index = end + 1;
            }
            else
            {
                Require(index + 1 < word.Length && word[index] == '\\' && word[index + 1] == '\'', "Package path contains unquoted shell expansion.");
                result.Append('\''); index += 2;
            }
        }
        return result.ToString();
    }

    private static string Manifest(Action<JsonObject>? change = null)
    {
        var root = new JsonObject
        {
            ["status"] = "optional",
            ["latest"] = new JsonObject { ["versionName"] = "0.3.17", ["buildNumber"] = 147, ["releaseNotes"] = "Offline fixture notes" },
            ["artifact"] = new JsonObject
            {
                ["platform"] = "macos", ["architecture"] = "arm64", ["fileKind"] = "pkg", ["status"] = "ready",
                ["downloadEnabled"] = true, ["sizeBytes"] = Payload.Length, ["expectedSizeBytes"] = Payload.Length,
                ["sha256"] = Digest, ["signerFingerprint"] = "Developer ID Installer: Test Publisher (" + Team + ")",
                ["originalFilename"] = "TermLink.pkg", ["downloadUrl"] = "https://cdn.invalid/update.pkg",
            }
        };
        change?.Invoke(root);
        return root.ToJsonString();
    }

    private static HttpContent StreamingContent(byte[] bytes) => new StreamContent(new NonSeekableReadStream(bytes));

    private static async Task<Exception> RejectedAsync(Func<Task> action, Type type, string name)
    {
        try { await action(); }
        catch (Exception error) when (type.IsInstanceOfType(error)) { return error; }
        throw new InvalidOperationException("Expected rejection: " + name + " (" + type.Name + ").");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "termlink mac update " + Guid.NewGuid().ToString("N"));
        public List<string> Events { get; } = [];
        public Queue<bool> Answers { get; } = new();
        public List<DesktopUpdateStatus> Confirmations { get; } = [];
        public FakeHttp Http { get; }
        public RecordingInstaller Installer { get; }
        public MacUpdateOptions Options { get; }
        public MacUpdateService Service { get; }
        public Exception? CurrentFailure { get; set; }
        public Exception? PrepareFailure { get; set; }
        public int Prepared { get; private set; }
        public int Recovered { get; private set; }
        private readonly HttpClient client;
        public string Cache => Path.Combine(Root, "updates");
        public string CachedPackage => Path.Combine(Cache, "TermLink-147-" + Digest + ".pkg");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Http = new FakeHttp(Events); client = new HttpClient(Http);
            Installer = new RecordingInstaller(Events);
            Options = new(new("https://updates.invalid"), "termlink-macos-arm64", "stable", "arm64", 146, Team, Path.Combine(Root, "TermLink.app"));
            Service = CreateService(Options);
        }

        public MacUpdateService CreateService(MacUpdateOptions options) => new(options, Cache, Installer,
            token => { token.ThrowIfCancellationRequested(); Events.Add("current"); if (CurrentFailure is not null) throw CurrentFailure; return ValueTask.CompletedTask; },
            token => { token.ThrowIfCancellationRequested(); Prepared++; Events.Add("prepare"); if (PrepareFailure is not null) throw PrepareFailure; return ValueTask.CompletedTask; },
            () => { Recovered++; Events.Add("recover"); return ValueTask.CompletedTask; }, client);

        public ValueTask<bool> Approve(DesktopUpdateStatus status, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Confirmations.Add(status); Events.Add("confirm");
            return ValueTask.FromResult(Answers.Count == 0 || Answers.Dequeue());
        }

        public void RequireNoTemporaryFiles() => Require(!Directory.Exists(Cache) || !Directory.EnumerateFiles(Cache, ".download-*").Any(), "A failed/canceled download left a temporary file.");
        public void Dispose() { client.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class FakeHttp(List<string> events) : HttpMessageHandler
    {
        public string Manifest { get; set; } = MacUpdateValidation.Manifest();
        public HttpStatusCode ManifestStatus { get; set; } = HttpStatusCode.OK;
        public Func<HttpContent> DownloadContent { get; set; } = () => new ByteArrayContent(Payload);
        public Uri? ManifestRedirect { get; set; }
        public Uri? DownloadRedirect { get; set; }
        public List<Uri> Requests { get; } = [];
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(request.Method == HttpMethod.Get && request.RequestUri is not null, "Unexpected update HTTP request.");
            var uri = request.RequestUri!; Requests.Add(uri);
            Require(uri.Host is "updates.invalid" or "cdn.invalid", "An unconfigured host reached the offline HTTP fixture.");
            var manifest = uri.Host == "updates.invalid";
            events.Add(manifest ? "manifest" : "download");
            if (!manifest) Downloads++;
            var redirect = manifest ? ManifestRedirect : DownloadRedirect;
            if (redirect is not null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = redirect;
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(manifest ? ManifestStatus : HttpStatusCode.OK)
                { Content = manifest ? new StringContent(Manifest) : DownloadContent() });
        }
    }

    private sealed class RecordingInstaller(List<string> events) : IUpdateInstaller
    {
        public int Verifications { get; private set; }
        public int Launches { get; private set; }
        public int RejectVerification { get; set; }
        public Exception? LaunchFailure { get; set; }
        public async ValueTask VerifyAndStageAsync(UpdatePackage package, string localPath, CancellationToken token)
        {
            events.Add("verify:" + ++Verifications);
            var bytes = await File.ReadAllBytesAsync(localPath, token);
            if (bytes.LongLength != package.Size || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != package.Sha256.ToLowerInvariant())
                throw new CryptographicException("Invalid cached fixture bytes");
            if (Verifications == RejectVerification) throw new CryptographicException("Rejected package signature fixture");
        }
        public ValueTask LaunchAsync(UpdatePackage package, string localPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); events.Add("launch"); Launches++;
            if (LaunchFailure is not null) throw LaunchFailure;
            Require(File.Exists(localPath), "The staged package was missing at launch.");
            return ValueTask.CompletedTask;
        }
        public ValueTask RollbackAsync(CancellationToken token) => throw new InvalidOperationException("Unverified rollback was attempted");
    }

    private sealed class RecordedProgress(Action<DesktopUpdateProgress>? callback = null) : IProgress<DesktopUpdateProgress>
    {
        public List<DesktopUpdateProgress> Values { get; } = [];
        public void Report(DesktopUpdateProgress value) { Values.Add(value); callback?.Invoke(value); }
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class BlockingReadStream : Stream
    {
        public bool Started { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!Started) { Started = true; Payload.AsMemory(0, 4).CopyTo(buffer); return 4; }
            await Task.Delay(Timeout.Infinite, token); return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CommandCall(string Executable, IReadOnlyList<string> Arguments, bool CanBeCanceled);

    private sealed class FixtureRunner : IMacCommandRunner
    {
        public string? Fault { get; init; }
        public Action<CommandCall>? AfterCall { get; init; }
        public List<CommandCall> Calls { get; } = [];
        public ValueTask<MacProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var call = new CommandCall(executable, arguments.ToArray(), token.CanBeCanceled); Calls.Add(call);
            var result = Result(call); AfterCall?.Invoke(call); return ValueTask.FromResult(result);
        }

        private MacProcessResult Result(CommandCall call)
        {
            if (call.Executable == "/usr/sbin/pkgutil")
            {
                Require(call.Arguments.Count == 2 && call.Arguments[0] == "--check-signature", "Unexpected package signature command.");
                var team = Fault is "package-leaf-team" or "package-second-certificate" ? "ZYXWVUTSRQ" : Team;
                var output = "    1. Developer ID Installer: Test Publisher (" + team + ")\n";
                if (Fault == "package-second-certificate") output += "    2. Developer ID Installer: Test Publisher (" + Team + ")\n";
                return new(Fault == "package-signature" ? 1 : 0, output, "");
            }
            if (call.Executable == "/usr/sbin/spctl")
            {
                var install = call.Arguments.Contains("install");
                var source = Fault == (install ? "package-notarization" : "application-notarization") ? "Developer ID" : "Notarized Developer ID";
                var origin = "Developer ID Installer: Test Publisher (" + (Fault == "package-origin" ? "ZYXWVUTSRQ" : Team) + ")";
                return new(Fault == "package-assessment" && install ? 1 : 0, "", "source=" + source + "\norigin=" + origin + "\n");
            }
            if (call.Executable == "/usr/bin/codesign")
            {
                if (call.Arguments[0] == "--verify") return new(Fault == "application-signature" ? 1 : 0, "", "");
                Require(call.Arguments[0] == "-dv", "Unexpected application signing command.");
                return new(0, "", "TeamIdentifier=" + (Fault == "application-team" ? "ZYXWVUTSRQ" : Team) +
                    "\nAuthority=Developer ID Application: Test Publisher (" + (Fault == "application-authority" ? "ZYXWVUTSRQ" : Team) + ")\n");
            }
            if (call.Executable == "/usr/bin/plutil")
            {
                var output = call.Arguments[1] switch
                {
                    "CFBundleIdentifier" => Fault == "application-id" ? "dev.termlink.desktop.dev" : "dev.termlink.desktop",
                    "CFBundleShortVersionString" => Fault == "installed-version" ? "0.3.16" : "0.3.17",
                    "CFBundleVersion" => Fault == "installed-build" ? "146" : "147",
                    _ => throw new InvalidOperationException("Unexpected installed metadata key"),
                };
                return new(0, output + "\n", "");
            }
            if (call.Executable == "/usr/bin/osascript")
                return Fault switch
                {
                    "authorization-cancel" => new(1, "", "User canceled. (-128)"),
                    "installer-failure" => new(1, "", "sensitive native installer output"),
                    _ => new(0, "", ""),
                };
            throw new InvalidOperationException("Real command execution is forbidden in offline update validation: " + call.Executable);
        }
    }
}
