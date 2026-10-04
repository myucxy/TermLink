using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent;

internal sealed partial class ProductionAgentRuntime
{
    private readonly AiSessionManager aiSessions;
    private readonly SemaphoreSlim aiRequestSlots = new(8, 8);
    private readonly SemaphoreSlim aiWatchSlots = new(4, 4);

    // Provider startup and approvals must never hold up terminal ingress or device refresh.
    private void DispatchAiRequest(System.Net.WebSockets.ClientWebSocket active, Guid pcId, Subscriber subscriber,
        ulong sequence, AiAgentRequestPayload request, CancellationToken token)
    {
        if (GuidFromBytes(request.RequestId.Span) == Guid.Empty || request.Json.Length > 48 * 1024)
            throw new InvalidDataException("Invalid AI request.");
        var slots = aiRequestSlots;
        try {
            using var parsed = System.Text.Json.JsonDocument.Parse(request.Json, new() { MaxDepth = 32 });
            if (parsed.RootElement.TryGetProperty("action", out var action) && action.ValueKind == System.Text.Json.JsonValueKind.String && action.GetString() == "watch") slots = aiWatchSlots;
        } catch (System.Text.Json.JsonException) { }
        if (!slots.Wait(0)) return; // Bounded; Android retains its operation ID on timeout.
        _ = Task.Run(async () => {
            try {
                var response = await aiSessions.ExecuteAsync(subscriber.DeviceId, request.Json, token,
                    () => !IsReauthenticationRequired && registry.RemoteAccessEnabled && IsCurrentSubscriberRoute(subscriber)).ConfigureAwait(false);
                if (token.IsCancellationRequested || IsReauthenticationRequired || !registry.RemoteAccessEnabled ||
                    !IsCurrentSubscriberRoute(subscriber)) return;
                var bytes = new AiAgentResponsePayload { RequestId = request.RequestId, Json = response }.ToByteArray();
                if (bytes.Length > 900 * 1024) {
                    CryptographicOperations.ZeroMemory(bytes);
                    bytes = new AiAgentResponsePayload { RequestId = request.RequestId,
                        Json = AiSessionManager.Encode(new { hostId = aiSessions.HostId, error = "RESPONSE_TOO_LARGE" }) }.ToByteArray();
                }
                try { await SendSessionControlEnvelopeAsync(active, pcId, subscriber, sequence, MessageType.AiAgentResponse, bytes, token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            } catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException) { }
            finally { slots.Release(); }
        });
    }
}
