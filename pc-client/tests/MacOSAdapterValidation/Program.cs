using System.Xml.Linq;
using System.Text.Json;
using TermLink.Platform.MacOS;
using TermLink.Platform.Unix;

if (args.Contains("--inspect-local", StringComparer.Ordinal))
{
    var found = await new PosixAiAgentDiscovery().DiscoverAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(found, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var options = new MacLaunchdOptions(
    "dev.termlink.agent",
    "/Applications/TermLink.app/Contents/MacOS/TermLink.Agent",
    ["--agent", "--ipc", "/tmp/TermLink & <test>.sock"],
    "/Users/validation/Library/LaunchAgents",
    501);
await MacDesktopValidation.RunAsync();
await MacUpdateValidation.RunAsync();
var plist = MacLaunchdAutostartManager.BuildPlist(options);
var document = XDocument.Parse(plist, LoadOptions.PreserveWhitespace);
Require(document.Root?.Name.LocalName == "plist", "The launchd plist is invalid XML.");
Require(plist.Contains("&amp;", StringComparison.Ordinal) && plist.Contains("&lt;test&gt;", StringComparison.Ordinal),
    "launchd arguments were not XML-escaped.");
Require(plist.Contains("<key>KeepAlive</key>", StringComparison.Ordinal) &&
    plist.Contains("<key>SuccessfulExit</key><false/>", StringComparison.Ordinal) &&
    plist.Contains("<key>ThrottleInterval</key><integer>3</integer>", StringComparison.Ordinal),
    "launchd restart throttling is incomplete.");

var keychain = new MacKeychainSecretStore("dev.termlink.validation");
if (!OperatingSystem.IsMacOS())
{
    Require(!keychain.IsAvailable, "Keychain claimed availability off macOS.");
    RequireThrows<PlatformNotSupportedException>(
        () => keychain.RetrieveAsync("identity", CancellationToken.None).AsTask().GetAwaiter().GetResult(),
        "Keychain did not fail closed off macOS.");
    Console.WriteLine("macOS adapter static validation passed; Keychain, Gatekeeper, notarization, launchd runtime, sleep/wake, and x64/arm64 E2E require macOS hosts.");
}
else
{
    var defaults = MacPlatformOptions.CreateDefault();
    Require(defaults.IpcEndpoint.Length <= 100, "The macOS Agent socket path exceeds its supported bound.");
    Require(defaults.DataDirectory.EndsWith(MacPlatformOptions.IsDevelopmentBuild ? "TermLink DEV" : "TermLink", StringComparison.Ordinal),
        "The macOS data profile does not match the build channel.");
    Require(MacPlatformOptions.AgentServiceName == (MacPlatformOptions.IsDevelopmentBuild ? "dev.termlink.agent.dev" : "dev.termlink.agent"),
        "The development Keychain service is not isolated.");
    Require(defaults.IpcEndpoint.EndsWith(MacPlatformOptions.IsDevelopmentBuild ? "agent-dev-v1.sock" : "agent-v1.sock", StringComparison.Ordinal),
        "The development Agent socket is not isolated.");
    Console.WriteLine("macOS adapter structure passed; run the signed-host E2E pipeline for Keychain/Gatekeeper/notarization evidence.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}
