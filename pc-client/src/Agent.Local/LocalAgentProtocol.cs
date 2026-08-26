using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TermLink.Pc.Contracts;

namespace TermLink.Agent.Local;

public static class LocalAgentProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int NonceBytes = 24;
    public const int AuthenticationKeyBytes = 32;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static LocalAgentEnvelope CreateRequest(
        LocalAgentCommand command,
        object? payload,
        ReadOnlySpan<byte> key,
        DateTimeOffset now)
    {
        ValidateKey(key);
        var envelope = new LocalAgentEnvelope
        {
            Version = Version,
            RequestId = Guid.NewGuid(),
            TimestampUnixSeconds = now.ToUnixTimeSeconds(),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes)),
            Command = command,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions),
        };
        envelope.AuthenticationTag = Sign(envelope, key);
        return envelope;
    }

    public static void AuthenticateRequest(
        LocalAgentEnvelope envelope,
        ReadOnlySpan<byte> key,
        DateTimeOffset now,
        ILocalAgentNonceStore nonceStore)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(nonceStore);
        ValidateKey(key);
        ValidateCommon(envelope, now);
        var nonce = DecodeNonce(envelope.Nonce);
        try
        {
            // Authenticate before consuming nonce capacity. An untrusted
            // same-user process must not reserve a captured nonce by sending
            // an invalid proof first.
            VerifyTag(envelope, key);
            if (!nonceStore.TryRemember(nonce, now)) throw new InvalidDataException("LOCAL_AGENT_REPLAY_DETECTED");
        }
        finally { CryptographicOperations.ZeroMemory(nonce); }
    }

    public static LocalAgentResponse CreateResponse(
        LocalAgentEnvelope request,
        bool success,
        string code,
        object? payload,
        ReadOnlySpan<byte> key,
        DateTimeOffset now)
    {
        ValidateKey(key);
        var response = new LocalAgentResponse
        {
            Version = Version,
            RequestId = request.RequestId,
            TimestampUnixSeconds = now.ToUnixTimeSeconds(),
            RequestNonce = request.Nonce,
            Success = success,
            Code = code,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions),
        };
        response.AuthenticationTag = Sign(response, key);
        return response;
    }

    public static void AuthenticateResponse(
        LocalAgentResponse response,
        LocalAgentEnvelope request,
        ReadOnlySpan<byte> key,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateKey(key);
        if (response.Version != Version || response.RequestId != request.RequestId ||
            !string.Equals(response.RequestNonce, request.Nonce, StringComparison.Ordinal))
            throw new InvalidDataException("LOCAL_AGENT_RESPONSE_BINDING_FAILED");
        var observed = DateTimeOffset.FromUnixTimeSeconds(response.TimestampUnixSeconds);
        if ((now - observed).Duration() > MaximumClockSkew)
            throw new InvalidDataException("LOCAL_AGENT_RESPONSE_EXPIRED");
        VerifyTag(response, key);
    }

    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (body.Length == 0 || body.Length > MaximumFrameBytes) throw new InvalidDataException("LOCAL_AGENT_FRAME_TOO_LARGE");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new InvalidDataException("LOCAL_AGENT_FRAME_INVALID");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions) ??
                throw new InvalidDataException("LOCAL_AGENT_FRAME_EMPTY");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("LOCAL_AGENT_FRAME_JSON_INVALID", error);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static string Sign<T>(T value, ReadOnlySpan<byte> key)
    {
        byte[] body = value switch
        {
            LocalAgentEnvelope request => JsonSerializer.SerializeToUtf8Bytes(
                request with { AuthenticationTag = string.Empty }, JsonOptions),
            LocalAgentResponse response => JsonSerializer.SerializeToUtf8Bytes(
                response with { AuthenticationTag = string.Empty }, JsonOptions),
            _ => throw new ArgumentException("Unsupported local Agent envelope.", nameof(value)),
        };
        try { return Convert.ToBase64String(HMACSHA256.HashData(key, body)); }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static void VerifyTag<T>(T value, ReadOnlySpan<byte> key)
    {
        var actualText = value switch
        {
            LocalAgentEnvelope request => request.AuthenticationTag,
            LocalAgentResponse response => response.AuthenticationTag,
            _ => string.Empty,
        };
        byte[] actual;
        try { actual = Convert.FromBase64String(actualText); }
        catch (FormatException error) { throw new InvalidDataException("LOCAL_AGENT_AUTHENTICATION_FAILED", error); }
        var expectedText = Sign(value, key);
        var expected = Convert.FromBase64String(expectedText);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new InvalidDataException("LOCAL_AGENT_AUTHENTICATION_FAILED");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static void ValidateCommon(LocalAgentEnvelope envelope, DateTimeOffset now)
    {
        if (envelope.Version != Version || envelope.RequestId == Guid.Empty || !Enum.IsDefined(envelope.Command))
            throw new InvalidDataException("LOCAL_AGENT_REQUEST_INVALID");
        var observed = DateTimeOffset.FromUnixTimeSeconds(envelope.TimestampUnixSeconds);
        if ((now - observed).Duration() > MaximumClockSkew) throw new InvalidDataException("LOCAL_AGENT_REQUEST_EXPIRED");
    }

    private static byte[] DecodeNonce(string text)
    {
        try
        {
            var nonce = Convert.FromBase64String(text);
            if (nonce.Length != NonceBytes) throw new InvalidDataException("LOCAL_AGENT_NONCE_INVALID");
            return nonce;
        }
        catch (FormatException error) { throw new InvalidDataException("LOCAL_AGENT_NONCE_INVALID", error); }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != AuthenticationKeyBytes) throw new CryptographicException("The local Agent key is invalid.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var result = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        result.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return result;
    }
}

public enum LocalAgentCommand
{
    GetStatus = 1,
    SetRemoteAccess = 2,
    RetryConnection = 3,
    StopSession = 4,
    GrantDevice = 5,
    RevokeDevice = 6,
    Login = 7,
    Register = 8,
    SignOut = 9,
    SaveTerminalCommands = 10,
}

public sealed record LocalAgentEnvelope
{
    public int Version { get; init; }
    public Guid RequestId { get; init; }
    public long TimestampUnixSeconds { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public LocalAgentCommand Command { get; init; }
    public JsonElement? Payload { get; init; }
    public string AuthenticationTag { get; set; } = string.Empty;
}

public sealed record LocalAgentResponse
{
    public int Version { get; init; }
    public Guid RequestId { get; init; }
    public long TimestampUnixSeconds { get; init; }
    public string RequestNonce { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Code { get; init; } = string.Empty;
    public JsonElement? Payload { get; init; }
    public string AuthenticationTag { get; set; } = string.Empty;
}

public interface ILocalAgentNonceStore
{
    bool TryRemember(ReadOnlySpan<byte> nonce, DateTimeOffset now);
}

public sealed class BoundedLocalAgentNonceStore(int capacity = 4096) : ILocalAgentNonceStore
{
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> values = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset At)> order = new();

    public bool TryRemember(ReadOnlySpan<byte> nonce, DateTimeOffset now)
    {
        var key = Convert.ToBase64String(nonce);
        lock (sync)
        {
            while (order.Count > 0 && (order.Count >= capacity || now - order.Peek().At > LocalAgentProtocol.MaximumClockSkew))
            {
                var expired = order.Dequeue();
                if (values.TryGetValue(expired.Key, out var at) && at == expired.At) values.Remove(expired.Key);
            }
            if (!values.TryAdd(key, now)) return false;
            order.Enqueue((key, now));
            return true;
        }
    }
}

public sealed record SetRemoteAccessPayload(bool Enabled);
public sealed record SessionPayload(Guid SessionId);
public sealed record DevicePayload(Guid DeviceId, string? Permission = null);
public sealed record LoginPayload(string Username, string Password, string PcName);
public sealed record RegisterPayload(string Username, string Email, string Password, string PasswordConfirmation, string PcName);
public sealed record TerminalCommandsPayload(IReadOnlyList<TerminalCommandCapability> Commands);
