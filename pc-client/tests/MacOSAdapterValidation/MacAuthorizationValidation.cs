using TermLink.Pc.Contracts;
using TermLink.Platform.MacOS;

internal static class MacAuthorizationValidation
{
    public static async Task RunAsync()
    {
        foreach (var notification in new[] { -1, 0, 1, 2, 3, 4, 99 })
        foreach (var keychain in new[] { -1, 0, 1 })
        {
            var calls = 0;
            var probe = new MacAuthorizationProbe(Path.GetTempPath(), () =>
            {
                calls++;
                return new(keychain, notification, 0, 1);
            });
            var checks = await probe.DetectAsync(CancellationToken.None);
            Require(calls == 1 && checks.Count == 7, "Permission detection did not use one read-only observation.");
            Require(checks.Single(x => x.Id == "mac_notifications").Status == (notification==2 ? CapabilityCheckStatus.Available : notification is 3 or 4 ? CapabilityCheckStatus.Limited : notification is 0 or 1 ? CapabilityCheckStatus.NeedsAuthorization : CapabilityCheckStatus.Unknown),
                "Unknown or denied notification permission became available.");
            Require(checks.Single(x => x.Id == "mac_keychain").Status == (keychain == 1 ? CapabilityCheckStatus.Limited
                : keychain == 0 ? CapabilityCheckStatus.NeedsAuthorization : CapabilityCheckStatus.Unknown),
                "Unlocked Keychain was confused with credential ACL authorization.");
            Require(checks.Single(x => x.Id == "mac_full_disk").Status == CapabilityCheckStatus.Unknown,
                "Application-directory access was confused with full disk authorization.");
            Require(checks.Where(x => x.Id is "mac_accessibility" or "mac_automation").All(x => x.Status == CapabilityCheckStatus.NotRequired),
                "Detection requested unrelated accessibility/automation permission.");
        }
        var unavailable = new MacAuthorizationProbe(Path.GetTempPath(), () => throw new DllNotFoundException());
        Require((await unavailable.DetectAsync(CancellationToken.None)).Single(x => x.Id == "mac_notifications").Status == CapabilityCheckStatus.Unknown,
            "Missing native helper claimed notification permission.");
        var ran = false;
        var cancelled = new MacAuthorizationProbe(Path.GetTempPath(), () => { ran = true; return new(1, 2, 1, 1); });
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await cancelled.DetectAsync(stop.Token); throw new InvalidOperationException("Cancelled permission check continued."); }
        catch (OperationCanceledException) { }
        Require(!ran, "Cancelled permission check called the native API.");
        Console.WriteLine("PASS macOS authorization: read-only state matrix, Keychain ACL distinction, no inferred full-disk grant, missing helper and cancellation");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
