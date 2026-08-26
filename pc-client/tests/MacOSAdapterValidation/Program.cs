using System.Xml.Linq;
using TermLink.Platform.MacOS;

var options = new MacLaunchdOptions(
    "dev.termlink.agent",
    "/Applications/TermLink.app/Contents/MacOS/TermLink.Agent",
    ["--agent", "--ipc", "/tmp/TermLink & <test>.sock"],
    "/Users/validation/Library/LaunchAgents",
    501);
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
