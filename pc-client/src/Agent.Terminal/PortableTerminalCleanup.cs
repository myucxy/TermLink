// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent.Terminal;

public sealed partial class PortableTerminalHost
{
    public bool SupportsCleanup => deletions is not null;
    public IReadOnlyList<Guid> DeletedSessions => deletions?.Records.Select(x => x.SessionId).ToArray() ?? Array.Empty<Guid>();

    public async Task<SessionCleanupResponsePayload> CleanupAsync(SessionCleanupRequestPayload request, Func<Task<bool>> authorized, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        var response = new SessionCleanupResponsePayload { RequestId = request.RequestId };
        if (request.RequestId.Length != 16 || GuidOf(request.RequestId) == Guid.Empty || request.SessionIds.Count is < 1 or > 64 ||
            request.SessionIds.Any(x => x.Length != 16 || GuidOf(x) == Guid.Empty) || request.SessionIds.Distinct().Count() != request.SessionIds.Count)
        { response.ErrorCode = "SESSION_CLEANUP_INVALID"; return response; }
        if (deletions is null) { response.ErrorCode = "SESSION_CLEANUP_UNSUPPORTED"; return response; }
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            foreach (var bytes in request.SessionIds)
            {
                token.ThrowIfCancellationRequested();
                if (!await authorized().ConfigureAwait(false)) { response.ErrorCode = "MANAGE_NOT_GRANTED"; break; }
                var id = GuidOf(bytes);
                var result = new SessionCleanupResult { SessionId = bytes, Status = "FAILED" };
                try { result.Status = await DeleteInactiveAsync(id, authorized, token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { }
                response.Results.Add(result);
            }
            return response;
        }
        finally { lifecycle.Release(); }
    }

    private async Task<string> DeleteInactiveAsync(Guid id, Func<Task<bool>> authorized, CancellationToken token)
    {
        if (!sessions.TryGetValue(id, out var session))
        {
            if (deletions!.Contains(id)) { deletions.RemoveStateFile(id); return "DELETED"; }
            return "NOT_FOUND";
        }
        await session.StopGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (session.View.State == "RUNNING" || !session.Reader.IsCompleted) return "SKIPPED_ACTIVE";
            if (session.PtyAttached)
            {
                // EOF can precede shell exit. Observe exit without signalling;
                // a process which closed its PTY but is alive is not deletable.
                using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
                observation.CancelAfter(TimeSpan.FromMilliseconds(500));
                try { await pty.WaitForExitAsync(id, observation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return "SKIPPED_ACTIVE"; }
                await pty.StopAsync(id, token).ConfigureAwait(false);
                session.PtyAttached = false;
            }
            await session.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (session.View.State is not ("EXITED" or "INTERRUPTED" or "FAILED")) return "SKIPPED_ACTIVE";
                if (!await authorized().ConfigureAwait(false)) return "FAILED";
                // Commit the restore fence before deleting any history. Retain
                // operation IDs so a lost OPEN_TERMINAL receipt cannot relaunch.
                deletions!.Mark(id, session.OperationId);
                deletions.RemoveStateFile(id);
                if (!sessions.TryRemove(id, out _)) return "DELETED";
                session.Deleted = true;
                session.Core.Dispose();
                if (session.Checkpoint is { } checkpoint) CryptographicOperations.ZeroMemory(checkpoint.Bytes);
                foreach (var item in session.Events) CryptographicOperations.ZeroMemory(item.Payload);
                foreach (var receipt in session.Receipts.Values) CryptographicOperations.ZeroMemory(receipt.Hash);
                session.Events.Clear(); session.Receipts.Clear(); session.InputSequences.Clear();
                return "DELETED";
            }
            finally { session.Gate.Release(); }
        }
        finally { session.StopGate.Release(); }
    }
    private static Guid GuidOf(ByteString value) => new(Convert.ToHexString(value.Span));
}
