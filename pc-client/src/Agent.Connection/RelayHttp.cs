using System.Net;
using System.Text.Json;

namespace TermLink.Agent.Connection;

public static class RelayHttp
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 64 * 1024,
    };

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > 64 * 1024) throw new RelayOperationException("SERVER_RESPONSE_TOO_LARGE");
        // ResponseHeadersRead ends HttpClient.Timeout before the body is consumed.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int size;
            while ((size = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + size > 64 * 1024) throw new RelayOperationException("SERVER_RESPONSE_TOO_LARGE");
                output.Write(buffer, 0, size);
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new RelayOperationException("RELAY_TIMEOUT");
        }
    }

    public static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new RelayOperationException("ACCOUNT_REAUTHENTICATION_REQUIRED");
        if (!response.IsSuccessStatusCode) throw new RelayOperationException("SERVER_HTTP_" + (int)response.StatusCode);
    }
}
