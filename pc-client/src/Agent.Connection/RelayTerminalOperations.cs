using System.Net;
using System.Text.Json;
using TermLink.Protocol;

namespace TermLink.Agent.Connection;

public sealed partial class RelayConnectionService
{
    private async Task PollTerminalOperationsAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        if (terminalHost is null) return;
        var prefix = "/v1/pcs/" + current.Identity.PcId.ToString("D") + "/file-operations";
        using var claimed = await SendAsync(current, device, HttpMethod.Post, prefix + "/claim", [], true, token).ConfigureAwait(false);
        // Older servers require file browsing for terminal creation. Local CLI
        // creation/attach remains usable without falsely claiming that capability.
        if (claimed.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return;
        RelayHttp.RequireSuccess(claimed);
        using var json = await RelayHttp.ReadJsonAsync(claimed, token).ConfigureAwait(false);
        var operation = json.RootElement.GetProperty("file_operation");
        var id = operation.GetProperty("id").GetGuid();
        var lease = json.RootElement.GetProperty("lease_token").GetString();
        if (id == Guid.Empty || lease is null || lease.Length is < 32 or > 256) throw new RelayOperationException("TERMINAL_OPERATION_INVALID");
        var target = prefix + "/" + id.ToString("D");
        string? error = null;
        object? result = null;
        try
        {
            if (operation.GetProperty("operation").GetString() != "OPEN_TERMINAL") throw new InvalidDataException("TERMINAL_OPERATION_UNSUPPORTED");
            var recipients = await ReadAiRecipientsAsync(current, device, token).ConfigureAwait(false);
            var requester = operation.GetProperty("requester_device_id").GetGuid();
            if (!recipients.TryGetValue(requester, out var authority) || authority.Permission != "MANAGE")
                throw new InvalidDataException("MANAGE_NOT_GRANTED");
            // Recheck the operation's live lease/authority immediately before spawn.
            using var renewed = await SendAsync(current, device, HttpMethod.Post, target + "/renew",
                JsonSerializer.SerializeToUtf8Bytes(new { lease_token = lease }), true, token).ConfigureAwait(false);
            RelayHttp.RequireSuccess(renewed);
            var shell = operation.GetProperty("terminal_command_id").GetString() ?? "";
            var directory = operation.GetProperty("path").GetString() ?? "";
            var session = await terminalHost.StartAsync(shell, directory, id, token).ConfigureAwait(false);
            await PublishTerminalSessionsAsync(current, device, token).ConfigureAwait(false);
            result = new { session_id = session, terminal_command_id = shell, shell_type = shell, cwd = directory };
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or HttpRequestException or KeyNotFoundException)
        {
            // Never send paths, command lines, credentials or HTTP bodies as diagnostics.
            error = e is InvalidDataException { Message: "MANAGE_NOT_GRANTED" } ? "MANAGE_NOT_GRANTED" : "TERMINAL_START_FAILED";
        }
        using var completed = await SendAsync(current, device, HttpMethod.Post, target + "/complete",
            JsonSerializer.SerializeToUtf8Bytes(new { lease_token = lease, status = error is null ? "SUCCEEDED" : "FAILED", result, error }), true, token).ConfigureAwait(false);
        // A cancelled/revoked lease is authoritative; do not continue creating work.
        if (completed.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound)) RelayHttp.RequireSuccess(completed);
    }
}
