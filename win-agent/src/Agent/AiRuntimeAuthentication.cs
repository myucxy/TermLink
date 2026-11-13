// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;

namespace TermLink.Agent;

// Only the selected account's isolated runtime handles credentials. TermLink
// projects bounded status and a device challenge; provider tokens never leave it.
internal sealed partial class AiAgentSession
{
    private string runtimeLoginId = "";
    private bool? runtimeLoginSucceeded;
    private (string Id, bool Success)? earlyLoginCompletion;
    internal static async Task<AiAgentSession> OpenAuthenticationAsync(AiAgentInstallation installation, CancellationToken token)
    {
        if (installation.Protocol != "codex" || installation.Strategy != "native" || installation.Status != "available" ||
            !Path.IsPathFullyQualified(installation.ConfigurationDataDirectory) || installation.ConfigurationDataDirectory.Length == 0)
            throw new AiOperationException("RUNTIME_AUTH_UNSUPPORTED");
        var probe = new AiAgentSession(installation, installation.ConfigurationDataDirectory, Guid.Empty) { discovering = true };
        using var registration = token.Register(probe.Dispose);
        try { await probe.InitializeAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested(); return probe; }
        catch { probe.Dispose(); throw; }
    }

    internal async Task<string> ReadAuthenticationAsync()
    {
        var result = await RpcAsync("account/read", new { refreshToken = false }).ConfigureAwait(false);
        var account = P(result, "account");
        if (account.ValueKind == JsonValueKind.Object)
        {
            var type = S(account, "type");
            if (type is "apiKey" or "apikey" or "chatgpt" or "chatgptAuthTokens" or "amazonBedrock" or "agentIdentity" or "personalAccessToken" or "gcp")
                return "authenticated"; // Account presence, not proof of model entitlement or successful inference.
            return "unknown";
        }
        if (account.ValueKind != JsonValueKind.Null) return "unknown";
        return P(result, "requiresOpenaiAuth").ValueKind switch
        {
            JsonValueKind.True => "required", JsonValueKind.False => "not_required", _ => "unknown"
        };
    }

    internal async Task<(string Id, string Code, string Url)> StartDeviceAuthenticationAsync()
    {
        var result = await RpcAsync("account/login/start", new { type = "chatgptDeviceCode" }).ConfigureAwait(false);
        var id = S(result, "loginId"); var code = S(result, "userCode"); var url = S(result, "verificationUrl");
        if (S(result, "type") != "chatgptDeviceCode" || !Guid.TryParseExact(id, "D", out _) ||
            code.Length is < 4 or > 32 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            url != "https://auth.openai.com/codex/device")
        {
            if (Guid.TryParseExact(id, "D", out _)) try { await CancelDeviceAuthenticationAsync(id).ConfigureAwait(false); } catch { }
            throw new AiOperationException("RUNTIME_AUTH_RESPONSE_INVALID");
        }
        lock (sync) { runtimeLoginId = id; runtimeLoginSucceeded = earlyLoginCompletion is { } completed && completed.Id == id ? completed.Success : null; earlyLoginCompletion = null; }
        return (id, code, url);
    }
    internal bool RuntimeLoginFailed { get { lock (sync) return runtimeLoginSucceeded == false; } }
    private bool ReceiveRuntimeAuthentication(JsonElement root)
    {
        if (S(root, "method") != "account/login/completed") return false;
        var value = P(root, "params");
        var id = S(value, "loginId");
        lock (sync) if (Guid.TryParseExact(id, "D", out _) && P(value, "success").ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var succeeded = P(value, "success").GetBoolean();
            if (id == runtimeLoginId) runtimeLoginSucceeded = succeeded;
            else if (discovering && runtimeLoginId.Length == 0) earlyLoginCompletion = (id, succeeded);
        }
        return true; // Never project upstream error/provider account fields.
    }
    internal Task<JsonElement> CancelDeviceAuthenticationAsync(string loginId) => RpcAsync("account/login/cancel", new { loginId });
}

internal sealed partial class AiSessionManager
{
    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private readonly Dictionary<(Guid Caller, string Agent), RuntimeAuthentication> runtimeAuthentications = new();
    private sealed class RuntimeAuthentication(AiAgentSession session) : IDisposable
    {
        internal readonly AiAgentSession Session = session;
        private readonly Timer expiry = new(_ => {
            try { session.Dispose(); }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { Console.Error.WriteLine("RUNTIME_AUTH_EXPIRY_CLEANUP_FAILED"); }
        }, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        internal readonly DateTime Expires = DateTime.UtcNow.AddMinutes(10);
        internal string LoginId = "", Code = "", Url = "";
        public void Dispose() { expiry.Dispose(); Code = Url = LoginId = ""; Session.Dispose(); }
    }
    private static object AuthenticationDescriptor(AiAgentInstallation installation)
    {
        var isolated = !string.IsNullOrEmpty(installation.ConfigurationDataDirectory);
        var deviceCode = isolated && installation.Protocol == "codex" && installation.Status == "available";
        return new { version = 1, status = isolated ? "isolated_required" : "not_checked", isolated,
            method = deviceCode ? "device_code" : "host_setup", canCheck = deviceCode, canStart = deviceCode,
            configurationDirectory = isolated && installation.ConfigurationDataDirectory.Length <= 4096 && !installation.ConfigurationDataDirectory.Any(char.IsControl) ? installation.ConfigurationDataDirectory : "" };
    }

    private async Task<string> RuntimeAuthenticationAsync(Guid caller, Request request, Func<bool> authorized)
    {
        if (!await authenticationGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) return Error("RUNTIME_AUTH_BUSY");
        var keyOwner = (caller, request.AgentId);
        RuntimeAuthentication selectedFlow = null;
        var retire = false;
        try
        {
            lock (sync)
            {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                foreach (var key in runtimeAuthentications.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray())
                { runtimeAuthentications[key].Dispose(); runtimeAuthentications.Remove(key); }
            }
            RuntimeAuthentication flow;
            lock (sync)
            {
                runtimeAuthentications.TryGetValue(keyOwner, out flow);
                if (runtimeAuthentications.Keys.Any(k => k.Agent == request.AgentId && k.Caller != caller)) return Error("RUNTIME_AUTH_BUSY");
            }
            selectedFlow = flow;
            if (request.Action == "authentication.cancel")
            {
                retire = true;
                if (flow == null) return Encode(new { hostId = HostId, authentication = new { status = "cancelled", agentId = request.AgentId } });
                if (flow.LoginId.Length > 0) await flow.Session.CancelDeviceAuthenticationAsync(flow.LoginId).ConfigureAwait(false);
                lock (sync) { runtimeAuthentications.Remove(keyOwner); flow.Dispose(); }
                return Encode(new { hostId = HostId, authentication = new { status = "cancelled", agentId = request.AgentId } });
            }
            if (flow == null)
            {
                var installation = (await catalog().WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token).ConfigureAwait(false)).FirstOrDefault(a => a.Id == request.AgentId);
                if (installation == null) return Error("AGENT_UNAVAILABLE");
                lock (sync) { if (runtimeAuthentications.Count >= 4) return Error("RUNTIME_AUTH_BUSY"); }
                var session = await AiAgentSession.OpenAuthenticationAsync(installation, lifetime.Token).ConfigureAwait(false);
                flow = new RuntimeAuthentication(session);
                lock (sync)
                {
                    if (disposed || authorized != null && !authorized()) { flow.Dispose(); return Error("AUTHORITY_CHANGED"); }
                    runtimeAuthentications.Add(keyOwner, flow); selectedFlow = flow;
                }
            }
            using var cancellation = lifetime.Token.Register(flow.Dispose);
            var status = await flow.Session.ReadAuthenticationAsync().ConfigureAwait(false);
            if (request.Action == "authentication.start" && status == "required")
            {
                if (flow.LoginId.Length == 0)
                {
                    if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    var challenge = await flow.Session.StartDeviceAuthenticationAsync().ConfigureAwait(false);
                    flow.LoginId = challenge.Id; flow.Code = challenge.Code; flow.Url = challenge.Url;
                }
                status = "pending";
            }
            if (status is "authenticated" or "not_required") { flow.Code = ""; flow.Url = ""; retire = true; }
            else if (flow.Session.RuntimeLoginFailed) { status = "failed"; flow.Code = flow.Url = ""; retire = true; }
            else if (flow.LoginId.Length > 0) status = "pending";
            if (authorized != null && !authorized())
            {
                lock (sync) { runtimeAuthentications.Remove(keyOwner); flow.Dispose(); }
                return Error("AUTHORITY_CHANGED");
            }
            lock (sync) { if (disposed) return Error("HOST_CHANGED"); }
            return Encode(new { hostId = HostId, authentication = new { version = 1, agentId = request.AgentId, status,
                isolated = true, method = "device_code", verificationUrl = request.Action == "authentication" ? flow.Url : "", userCode = request.Action == "authentication" ? flow.Code : "",
                expiresAt = flow.Expires.ToString("O"), modelAccessVerified = false } });
        }
        catch (AiOperationException error) { retire = true; return Error(error.Code is "AGENT_METHOD_NOT_FOUND" or "AGENT_INVALID_PARAMS" ? "RUNTIME_AUTH_UNSUPPORTED" : error.Code); }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
        { retire = true; return Error("RUNTIME_AUTH_UNAVAILABLE"); }
        finally
        {
            if (selectedFlow != null && (retire || selectedFlow.LoginId.Length == 0))
            {
                lock (sync)
                {
                    if (runtimeAuthentications.TryGetValue(keyOwner, out var current) && ReferenceEquals(current, selectedFlow)) runtimeAuthentications.Remove(keyOwner);
                    selectedFlow.Dispose();
                }
            }
            authenticationGate.Release();
        }
    }
}
