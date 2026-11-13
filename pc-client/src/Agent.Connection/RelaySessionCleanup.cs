// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol;

namespace TermLink.Agent.Connection;

public sealed partial class RelayConnectionService
{
    private readonly SemaphoreSlim sessionMetadata = new(1, 1);
    private IRelayTerminalHost? mirroredDeletionHost;
    private readonly object deletionMirrorSync = new();
    private readonly HashSet<Guid> mirroredDeletions = new();

    private async Task<SessionCleanupResponsePayload> CleanupTerminalSessionsAsync(Account current, DeviceIdentity device,
        IRelayTerminalHost host, Guid sender, SessionCleanupRequestPayload request, CancellationToken token)
    {
        async Task<bool> Authorized()
        {
            if (token.IsCancellationRequested || !ReferenceEquals(host, terminalHost) || account?.RemoteAccessEnabled != true ||
                account.Identity.DeviceId != current.Identity.DeviceId) return false;
            var recipients = await ReadAiRecipientsAsync(current, device, token).ConfigureAwait(false);
            return recipients.TryGetValue(sender, out var recipient) && recipient.Permission == "MANAGE";
        }
        if (!await Authorized().ConfigureAwait(false)) return new() { RequestId = request.RequestId, ErrorCode = "MANAGE_NOT_GRANTED" };
        var response = await host.CleanupAsync(request, Authorized, token).ConfigureAwait(false);
        var deleted = response.Results.Where(r => r.Status == "DELETED").Select(r => new Guid(Convert.ToHexString(r.SessionId.Span))).ToArray();
        if (deleted.Length > 0)
        {
            try { await SyncSessionDeletionsAsync(current, device, host, deleted, token).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or RelayOperationException)
            { if (response.ErrorCode.Length == 0) response.ErrorCode = "MIRROR_SYNC_PENDING"; }
        }
        return response;
    }

    private async Task RetrySessionDeletionsAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        var host = terminalHost;
        if (host is null || !host.SupportsCleanup) return;
        Guid[] pending;
        lock (deletionMirrorSync)
        {
            if (!ReferenceEquals(mirroredDeletionHost, host)) { mirroredDeletionHost = host; mirroredDeletions.Clear(); }
            pending = host.DeletedSessions.Where(id => !mirroredDeletions.Contains(id)).Take(64).ToArray();
        }
        if (pending.Length > 0) await SyncSessionDeletionsAsync(current, device, host, pending, token).ConfigureAwait(false);
    }

    private async Task SyncSessionDeletionsAsync(Account current, DeviceIdentity device, IRelayTerminalHost host, Guid[] ids, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await sessionMetadata.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(host, terminalHost)) return;
            var body = JsonSerializer.SerializeToUtf8Bytes(new { session_ids = ids });
            using var response = await SendAsync(current, device, HttpMethod.Post,
                "/v1/pcs/" + current.Identity.PcId.ToString("D") + "/session-deletions", body, true, deadline.Token).ConfigureAwait(false);
            RelayHttp.RequireSuccess(response);
            lock (deletionMirrorSync)
            {
                if (!ReferenceEquals(mirroredDeletionHost, host)) { mirroredDeletionHost = host; mirroredDeletions.Clear(); }
                mirroredDeletions.UnionWith(ids);
            }
        }
        finally { sessionMetadata.Release(); }
    }
}
