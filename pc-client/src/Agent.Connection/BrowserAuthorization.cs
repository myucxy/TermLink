using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TermLink.Agent.Connection;

public sealed record RelayOidcConfiguration(string Issuer, string ClientId, Uri AuthorizationEndpoint, Uri TokenEndpoint);
public sealed record RelayTokens(string AccessToken, string RefreshToken, int ExpiresIn);

public interface IRelayAuthorization
{
    Task<RelayTokens> AuthorizeAsync(RelayOidcConfiguration configuration, HttpClient http, CancellationToken cancellationToken);
}

public sealed class BrowserAuthorization(Action<Uri>? openBrowser = null) : IRelayAuthorization
{
    public async Task<RelayTokens> AuthorizeAsync(
        RelayOidcConfiguration configuration, HttpClient http, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        var redirect = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/oauth2/callback");
        var state = RelaySecurity.Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = RelaySecurity.Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = configuration.ClientId, ["response_type"] = "code", ["scope"] = "openid profile email offline_access",
            ["redirect_uri"] = redirect.AbsoluteUri, ["state"] = state, ["code_challenge_method"] = "S256",
            ["code_challenge"] = RelaySecurity.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        };
        var builder = new UriBuilder(configuration.AuthorizationEndpoint)
        {
            Query = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))),
        };
        (openBrowser ?? OpenBrowser)(builder.Uri);
        string code;
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            await using var stream = client.GetStream();
            try
            {
                var request = await ReadHeadersAsync(stream, requestDeadline.Token).ConfigureAwait(false);
                var result = ParseCallback(request, redirect, state);
                await RespondAsync(stream, result is not null, requestDeadline.Token).ConfigureAwait(false);
                if (result is null) continue;
                if (result == "") throw new RelayOperationException("ACCOUNT_AUTHORIZATION_CANCELLED");
                code = result;
                break;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SocketException) { }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
        }
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = configuration.ClientId,
            ["code"] = code, ["redirect_uri"] = redirect.AbsoluteUri, ["code_verifier"] = verifier,
        });
        using var response = await http.PostAsync(configuration.TokenEndpoint, content, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RelayOperationException("ACCOUNT_CODE_EXCHANGE_FAILED");
        using var json = await RelayHttp.ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
        return ReadTokens(json.RootElement);
    }

    public static RelayTokens ReadTokens(JsonElement root)
    {
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var value) ? value.GetString() ?? "" : "";
        var expiry = root.GetProperty("expires_in").GetInt32();
        if (access.Length is < 1 or > 24 * 1024 || refresh.Length > 24 * 1024 || expiry is < 1 or > 86400 ||
            access.Any(char.IsControl) || refresh.Any(char.IsControl))
            throw new RelayOperationException("ACCOUNT_TOKEN_INVALID");
        return new RelayTokens(access, refresh, expiry);
    }

    public static string? ParseCallback(string request, Uri redirect, string state)
    {
        var lines = request.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        var hosts = lines.Where(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1" || hosts.Length != 1 ||
            hosts[0][5..].Trim() != redirect.Authority || !first[1].StartsWith("/oauth2/callback?", StringComparison.Ordinal) ||
            first[1].Contains('#')) return null;
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in first[1]["/oauth2/callback?".Length..].Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !fields.TryAdd(Uri.UnescapeDataString(parts[0]),
                    Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) return null;
            }
            if (!fields.TryGetValue("state", out var receivedState) || receivedState != state) return null;
            if (fields.ContainsKey("error")) return "";
            return fields.TryGetValue("code", out var code) && code.Length is > 0 and <= 4096 &&
                !code.Any(char.IsControl) ? code : null;
        }
        catch (UriFormatException) { return null; }
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[16 * 1024];
        for (var count = 0; count < bytes.Length; count++)
        {
            if (await stream.ReadAsync(bytes.AsMemory(count, 1), token).ConfigureAwait(false) == 0)
                throw new InvalidDataException("Incomplete browser callback.");
            if (count >= 3 && bytes.AsSpan(count - 3, 4).SequenceEqual("\r\n\r\n"u8))
                return Encoding.ASCII.GetString(bytes, 0, count + 1);
        }
        throw new InvalidDataException("Browser callback headers exceeded the limit.");
    }

    private static Task RespondAsync(Stream stream, bool accepted, CancellationToken token)
    {
        var body = accepted ? "Authorization received. Return to TermLink." : "Invalid callback.";
        var response = $"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        return stream.WriteAsync(Encoding.UTF8.GetBytes(response), token).AsTask();
    }

    private static void OpenBrowser(Uri uri) =>
        _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }) ??
            throw new RelayOperationException("ACCOUNT_BROWSER_START_FAILED");
}
