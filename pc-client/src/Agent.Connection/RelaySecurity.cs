using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Protocol;

namespace TermLink.Agent.Connection;

public sealed class RelayOperationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class RelaySecurity
{
    public static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NormalizeOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsSecureEndpoint(uri) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new RelayOperationException("ACCOUNT_SERVER_URL_INVALID");
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/');
    }

    // Plain HTTP is limited to loopback development. Never disable TLS checks.
    public static bool IsSecureEndpoint(Uri value) => value.UserInfo.Length == 0 && value.Fragment.Length == 0 &&
        (value.Scheme == "https" || value.Scheme == "http" &&
            (value.Host == "localhost" || IPAddress.TryParse(value.Host, out var ip) && IPAddress.IsLoopback(ip)));

    public static string AccountScope(string origin, string issuer, string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "TERMLINK-PORTABLE-ACCOUNT-V1\n" + origin + "\n" + issuer + "\n" + subject))).ToLowerInvariant();

    public static IReadOnlyDictionary<string, string> Proof(
        HttpMethod method, string target, ReadOnlySpan<byte> body, Guid deviceId, DeviceIdentity identity, string token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", method.Method.ToUpperInvariant(), target,
            timestamp, nonce, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        return new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token,
            ["X-TermLink-Device"] = deviceId.ToString("D"),
            ["X-TermLink-Timestamp"] = timestamp,
            ["X-TermLink-Nonce"] = nonce,
            ["X-TermLink-Signature"] = Base64Url(identity.Sign(Encoding.UTF8.GetBytes(canonical))),
        };
    }

    public static byte[] Registration(Guid deviceId, string name, DeviceIdentity identity)
    {
        const string type = "PC_AGENT";
        var signing = Base64Url(identity.SigningPublicKey);
        var exchange = Base64Url(identity.Exchange.PublicKey);
        var keyId = "device-key-" + deviceId.ToString("D");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", deviceId.ToString("D"), type, name,
            signing, exchange, keyId, now.ToString(CultureInfo.InvariantCulture), nonce);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = deviceId, type, name, signing_public_key = signing, exchange_public_key = exchange, key_id = keyId,
            timestamp_ms = now, nonce, signature = Base64Url(identity.Sign(Encoding.UTF8.GetBytes(canonical))),
        });
    }

    // Claims only select an account cache. The Server authenticates the token
    // and the device proof before any new credentials can be committed.
    public static RelayClaims ReadClaims(string token, string issuer, string clientId)
    {
        if (token.Length is < 1 or > 24 * 1024) throw new RelayOperationException("ACCOUNT_TOKEN_INVALID");
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new FormatException();
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            var root = json.RootElement;
            var sub = root.GetProperty("sub").GetString() ?? "";
            if (root.GetProperty("iss").GetString()?.TrimEnd('/') != issuer.TrimEnd('/') ||
                root.GetProperty("azp").GetString() != clientId || sub.Length is < 1 or > 512 || sub.Any(char.IsControl))
                throw new FormatException();
            var expires = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
            if (expires <= DateTimeOffset.UtcNow) throw new FormatException();
            static string Display(JsonElement root, string key)
            {
                if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return "";
                var result = new StringBuilder();
                var bytes = 0;
                foreach (var rune in (value.GetString() ?? "").EnumerateRunes())
                {
                    if (Rune.IsControl(rune)) continue;
                    if (bytes + rune.Utf8SequenceLength > TermLink.Pc.Contracts.PcContractLimits.MaximumDisplayNameBytes) break;
                    result.Append(rune.ToString());
                    bytes += rune.Utf8SequenceLength;
                }
                return result.ToString();
            }
            return new RelayClaims(sub, Display(root, "preferred_username"), Display(root, "name"), expires);
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentOutOfRangeException)
        {
            throw new RelayOperationException("ACCOUNT_TOKEN_INVALID");
        }
    }
}

public sealed record RelayClaims(string Subject, string Username, string Nickname, DateTimeOffset ExpiresAt);
