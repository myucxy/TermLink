using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TermLink.Platform.MacOS;

public sealed record MacUpdateOptions(
    Uri ServiceUri, string ApplicationSlug, string Channel, string Architecture,
    long CurrentBuild, string TrustedTeamId, string ApplicationBundlePath)
{
    public static MacUpdateOptions? CreateDefault()
    {
        // Ad-hoc DEV builds have no publisher trust anchor and cannot silently
        // become a production update client through a process environment variable.
        if (MacPlatformOptions.IsDevelopmentBuild || Metadata("TermLinkMacUpdatesEnabled") != "true") return null;
        var team = Metadata("TermLinkMacSigningTeamId");
        if (!Regex.IsMatch(team, "^[A-Z0-9]{10}$")) return null;
        var architecture = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var options = new MacUpdateOptions(new Uri(Metadata("TermLinkMacUpdateBaseUrl")),
            $"termlink-macos-{architecture}", "stable", architecture,
            typeof(MacUpdateOptions).Assembly.GetName().Version?.Revision ?? 0,
            team, "/Applications/TermLink.app");
        return options;
    }

    private static string Metadata(string key) => typeof(MacUpdateOptions).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
}
