namespace TermLink.Platform.MacOS;

public static class MacUpdateTrustBridge
{
    public static async ValueTask VerifyCurrentAsync(IMacCommandRunner runner, MacUpdateOptions options, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await MacUpdateTrust.VerifyApplicationAsync(runner, options.ApplicationBundlePath, options.TrustedTeamId, null, deadline.Token).ConfigureAwait(false);
    }
}
