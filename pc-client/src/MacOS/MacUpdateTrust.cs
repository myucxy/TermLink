using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

internal static class MacUpdateTrust
{
    public static string TeamId(string identity)
    {
        if (Regex.IsMatch(identity, "^[A-Z0-9]{10}$")) return identity;
        var match = Regex.Match(identity, @"^Developer ID Installer: [^\r\n]+ \(([A-Z0-9]{10})\)$");
        if (!match.Success) throw new CryptographicException("Invalid macOS publisher identity.");
        return match.Groups[1].Value;
    }

    public static void RequirePackageSignature(MacProcessResult signature, MacProcessResult assessment, string team)
    {
        var leaf = @"^\s*1\. Developer ID Installer: [^\r\n]+ \(" + Regex.Escape(team) + @"\)\s*$";
        var origin = @"^origin=Developer ID Installer: [^\r\n]+ \(" + Regex.Escape(team) + @"\)\s*$";
        var gatekeeper = assessment.StandardOutput + "\n" + assessment.StandardError;
        if (signature.ExitCode != 0 || assessment.ExitCode != 0 ||
            !Regex.IsMatch(signature.StandardOutput + "\n" + signature.StandardError, leaf, RegexOptions.Multiline) ||
            !Regex.IsMatch(gatekeeper, "^source=Notarized Developer ID\\s*$", RegexOptions.Multiline) ||
            !Regex.IsMatch(gatekeeper, origin, RegexOptions.Multiline))
            throw new CryptographicException("The update is not notarized and signed by this application's publisher.");
    }

    public static async ValueTask VerifyApplicationAsync(IMacCommandRunner runner, string path, string team,
        UpdatePackage? expected, CancellationToken token)
    {
        var verify = await runner.RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", path], token).ConfigureAwait(false);
        var identity = await runner.RunAsync("/usr/bin/codesign", ["-dv", "--verbose=4", path], token).ConfigureAwait(false);
        var assessment = await runner.RunAsync("/usr/sbin/spctl", ["--assess", "--type", "execute", "--verbose=4", path], token).ConfigureAwait(false);
        var text = identity.StandardOutput + "\n" + identity.StandardError;
        if (verify.ExitCode != 0 || identity.ExitCode != 0 || assessment.ExitCode != 0 ||
            !Regex.IsMatch(text, "^TeamIdentifier=" + Regex.Escape(team) + "\\s*$", RegexOptions.Multiline) ||
            !Regex.IsMatch(text, @"^Authority=Developer ID Application: [^\r\n]+ \(" + Regex.Escape(team) + @"\)\s*$", RegexOptions.Multiline) ||
            !Regex.IsMatch(assessment.StandardOutput + "\n" + assessment.StandardError,
                "^source=Notarized Developer ID\\s*$", RegexOptions.Multiline))
            throw new CryptographicException("The installed Mac application has no matching Developer ID trust anchor.");
        foreach (var (key, value) in new[] { ("CFBundleIdentifier", "dev.termlink.desktop"),
            ("CFBundleShortVersionString", expected?.Version), ("CFBundleVersion", expected?.BuildNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)) })
        {
            if (value is null) continue;
            var result = await runner.RunAsync("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", Path.Combine(path, "Contents", "Info.plist")], token).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.StandardOutput.Trim() != value)
                throw new CryptographicException("The installed application does not match the verified update.");
        }
    }
}
