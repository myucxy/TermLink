using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent;

// ACP transport is shared; Grok's model metadata and launch policy stay explicit.
// No vendor SDK, credential extraction, terminal scraping or automatic approval.
internal sealed partial class AiAgentSession
{
    private bool acpLoadSession, acpImages, acpLoading, acpModelConfig, acpEffortConfig, acpCancelling;
    private string acpPromptId = "", acpChunkKind = "", acpChunkId = "";
    private string acpTurnKey = "";
    private int acpChunkIndex;
    private readonly Dictionary<string, JsonElement> acpTools = new(StringComparer.Ordinal);

    private async Task<JsonElement> InitializeAcpAsync()
    {
        var result = await RpcAsync("initialize", new {
            protocolVersion = 1, clientInfo = new { name = "termlink", version = "1" },
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
        });
        if (P(result, "protocolVersion").ValueKind != JsonValueKind.Number || !P(result, "protocolVersion").TryGetInt32(out var version) || version != 1)
            throw new AiOperationException("AGENT_PROTOCOL_INCOMPATIBLE");
        acpLoadSession = P(P(result, "agentCapabilities"), "loadSession").ValueKind == JsonValueKind.True;
        acpImages = P(P(P(result, "agentCapabilities"), "promptCapabilities"), "image").ValueKind == JsonValueKind.True;
        ReadAcpCommands(P(P(result, "_meta"), "availableCommands"));
        return result;
    }

    internal static ModelList ParseAcpModels(JsonElement state)
    {
        var available = P(state, "availableModels");
        if (available.ValueKind != JsonValueKind.Array) throw new AiOperationException("MODEL_LIST_UNAVAILABLE");
        var models = new List<ModelOption>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in available.EnumerateArray()) {
            var id = S(value, "modelId");
            if (!ValidOption(id, 200) || !keys.Add(id)) continue;
            if (models.Count == 200) return new(models.ToArray(), true);
            var meta = P(value, "_meta"); var efforts = P(meta, "reasoningEfforts");
            var reasoning = UnknownReasoning();
            if (P(meta, "supportsReasoningEffort").ValueKind == JsonValueKind.False) reasoning = new("unsupported", Array.Empty<EffortOption>());
            else if (efforts.ValueKind == JsonValueKind.Array) {
                var options = efforts.EnumerateArray().Select(e => new EffortOption(S(e, "value").Length > 0 ? S(e, "value") : S(e, "id"), Clip(S(e, "description"), 300)))
                    .Where(e => ValidOption(e.Value)).DistinctBy(e => e.Value).Take(20).ToArray();
                var preferred = efforts.EnumerateArray().FirstOrDefault(e => P(e, "default").ValueKind == JsonValueKind.True);
                var defaultValue = S(preferred, "value").Length > 0 ? S(preferred, "value") : S(preferred, "id");
                reasoning = new(options.Length > 0 ? "available" : "unsupported", options, options.Any(e => e.Value == defaultValue) ? defaultValue : "");
            }
            var name = S(value, "name");
            models.Add(new(id, ValidOption(name, 200) ? name : id, IsDefault: id == S(state, "currentModelId"), Reasoning: reasoning));
        }
        return new(models.ToArray(), false);
    }

    private async Task StartAcpSessionAsync()
    {
        if (providerId.Length > 0) throw new AiOperationException("INVALID_MODEL");
        var requestedModel = modelId; var requestedEffort = reasoningEffort;
        var loading = threadId.Length > 0;
        if (loading && !acpLoadSession) throw new AiOperationException("RESUME_UNAVAILABLE");
        var parameters = new Dictionary<string, object> { ["cwd"] = directory, ["mcpServers"] = Array.Empty<object>() };
        if (agent.Id == "grok") parameters["_meta"] = new { yoloMode = permissionMode == "full-access", autoMode = false };
        if (loading) parameters["sessionId"] = threadId;
        JsonElement response;
        acpLoading = loading;
        try { response = await RpcAsync(loading ? "session/load" : "session/new", parameters); }
        finally { acpLoading = false; }
        if (!loading) threadId = S(response, "sessionId");
        if (!ValidOption(threadId, 512)) throw new AiOperationException("AGENT_PROTOCOL_INCOMPATIBLE");
        lock (sync) {
            modelId = ""; reasoningEffort = "";
            ReadAcpConfiguration(response);
            if (defaultModelId.Length == 0) defaultModelId = modelId.Length > 0 ? modelId : modelOptions.FirstOrDefault(m => m.IsDefault)?.ModelId ?? "";
            if (modelId.Length == 0) modelId = defaultModelId;
        }
        if (requestedModel.Length > 0 && requestedModel != modelId) await SetAcpOptionAsync("model", requestedModel);
        if (requestedEffort.Length > 0 && requestedEffort != reasoningEffort) {
            if (!ReasoningFor(modelId).Options.Any(e => e.Value == requestedEffort)) throw new AiOperationException("INVALID_REASONING_EFFORT");
            await SetAcpOptionAsync("reasoning_effort", requestedEffort);
        }
    }

    private void ReadAcpConfiguration(JsonElement response)
    {
        var models = P(response, "models");
        if (P(models, "availableModels").ValueKind == JsonValueKind.Array) {
            var listed = ParseAcpModels(models).Models;
            // Older ACP session replies omit Grok's per-model metadata already
            // provided by initialize. Preserve it only for the identical model.
            modelOptions = listed.Select(m => m.Reasoning.Status == "unknown" && modelOptions.FirstOrDefault(old => old.ModelId == m.ModelId) is { } old
                ? m with { Reasoning = old.Reasoning } : m).ToArray();
        }
        if (ValidOption(S(models, "currentModelId"), 200)) modelId = S(models, "currentModelId");
        var options = P(response, "configOptions");
        if (options.ValueKind != JsonValueKind.Array) return;
        acpModelConfig = false; acpEffortConfig = false;
        foreach (var option in options.EnumerateArray().Take(40)) {
            var id = S(option, "id"); var current = S(option, "currentValue");
            if (id == "model" && S(option, "type") == "select") {
                acpModelConfig = true;
                if (ValidOption(current, 200)) modelId = current;
            }
        }
        foreach (var option in options.EnumerateArray().Take(40)) {
            if (S(option, "id") != "reasoning_effort" || S(option, "type") != "select") continue;
            var values = P(option, "options"); if (values.ValueKind != JsonValueKind.Array) continue;
            var efforts = values.EnumerateArray().Select(e => new EffortOption(S(e, "value"), Clip(S(e, "description"), 300)))
                .Where(e => ValidOption(e.Value)).DistinctBy(e => e.Value).Take(20).ToArray();
            var current = S(option, "currentValue");
            if (efforts.Length == 0 || !efforts.Any(e => e.Value == current)) continue;
            acpEffortConfig = true; reasoningEffort = current;
            modelOptions = modelOptions.Select(m => m.ModelId == modelId ? m with { Reasoning = new("available", efforts,
                efforts.Any(e => e.Value == m.Reasoning?.DefaultValue) ? m.Reasoning.DefaultValue : "") } : m).ToArray();
        }
        if (!acpEffortConfig) reasoningEffort = "";
    }

    private async Task SetAcpOptionAsync(string configId, string value)
    {
        if (!(configId == "model" ? acpModelConfig : acpEffortConfig)) throw new AiOperationException("CONFIGURE_UNSUPPORTED");
        try {
            var result = await RpcAsync("session/set_config_option", new { sessionId = threadId, configId, value });
            lock (sync) {
                ReadAcpConfiguration(result);
                if ((configId == "model" ? modelId : reasoningEffort) != value) throw new AiOperationException("SETTINGS_OUTCOME_UNKNOWN");
            }
        } catch (AiOperationException e) when (e.Code is "AGENT_TIMEOUT" or "SETTINGS_OUTCOME_UNKNOWN") {
            Fail("SETTINGS_OUTCOME_UNKNOWN"); throw;
        }
    }

    private async Task ConfigureAcpAsync(AiSessionManager.Request request)
    {
        if (request.PermissionMode != permissionMode) throw new AiOperationException("PERMISSION_REQUIRES_NEW_SESSION");
        if (request.ModelId == null || request.ProviderId != "" || request.ModelId.Length > 200 || request.ModelId.Any(char.IsControl)) throw new AiOperationException("INVALID_MODEL");
        var nextModel = request.ModelId.Length > 0 ? request.ModelId : defaultModelId;
        if (nextModel.Length == 0) throw new AiOperationException("DEFAULT_MODEL_UNAVAILABLE");
        var effort = request.ReasoningEffort;
        if (effort != null && (effort.Length > 100 || effort.Any(char.IsControl))) throw new AiOperationException("INVALID_REASONING_EFFORT");
        if (nextModel != modelId && !string.IsNullOrEmpty(effort)) throw new AiOperationException("ONE_SETTING_AT_A_TIME");
        if (nextModel != modelId) await SetAcpOptionAsync("model", nextModel);
        else if (effort != null) {
            effort = effort.Length > 0 ? effort : ReasoningFor(modelId).DefaultValue;
            if (!ReasoningFor(modelId).Options.Any(e => e.Value == effort)) throw new AiOperationException("INVALID_REASONING_EFFORT");
            if (effort != reasoningEffort) await SetAcpOptionAsync("reasoning_effort", effort);
        }
        lock (sync) { settingsVersion++; settingsNotice = ""; SaveArchive(); }
    }

    private async Task SendAcpPromptAsync(string text, Attachment[] attachments)
    {
        var sequence = Interlocked.Increment(ref nextCall); var id = sequence.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new List<object> { new { type = "text", text } };
        if (acpImages) prompt.AddRange(attachments.Where(a => a.IsImage).Select(a => (object)new { type = "image", mimeType = a.Mime, data = Convert.ToBase64String(System.IO.File.ReadAllBytes(a.Path)) }));
        calls[id] = completion;
        lock (sync) { acpPromptId = id; acpTurnKey = Guid.NewGuid().ToString("N"); acpChunkKind = ""; acpChunkIndex = 0; acpCancelling = false; acpTools.Clear(); }
        try { await WriteAsync(new { jsonrpc = "2.0", id = sequence, method = "session/prompt", @params = new { sessionId = threadId, prompt } }); }
        catch { calls.TryRemove(id, out _); throw; }
        // ACP returns only after the entire turn. Release actions immediately so
        // permission answers, control changes and cancellation remain usable.
        _ = ObserveAcpPromptAsync(id, completion.Task);
    }

    private async Task ObserveAcpPromptAsync(string id, Task<JsonElement> response)
    {
        try {
            var result = await response.WaitAsync(lifetime.Token);
            lock (sync) {
                if (acpPromptId != id || disposed || lifetime.IsCancellationRequested) return;
                var reason = S(result, "stopReason");
                CompleteTurn(reason is not ("end_turn" or "cancelled" or "max_tokens" or "max_turn_requests" or "refusal"));
                acpPromptId = ""; acpTools.Clear(); PublishChange();
            }
        } catch (OperationCanceledException) { }
        catch (AiOperationException e) {
            lock (sync) {
                if (disposed || lifetime.IsCancellationRequested) return;
                CompleteTurn(true); error = e.Code; acpPromptId = ""; acpTools.Clear(); PublishChange();
            }
        } finally { calls.TryRemove(id, out _); }
    }

    private async Task CancelAcpPromptAsync()
    {
        string id;
        lock (sync) {
            id = acpPromptId;
            if (id.Length > 0) { acpCancelling = true; pending.Clear(); }
        }
        if (id.Length == 0) return;
        await WriteAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = threadId } });
        _ = CheckAcpCancellationAsync(id);
    }
    private async Task CheckAcpCancellationAsync(string id)
    {
        try {
            await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token);
            lock (sync) if (acpPromptId == id && !disposed) Fail("CANCEL_OUTCOME_UNKNOWN");
        } catch (OperationCanceledException) { }
    }

    private async Task ReceiveAcpAsync(JsonElement value)
    {
        if (S(value, "jsonrpc") != "2.0") throw new System.IO.IOException("Invalid ACP frame.");
        var id = P(value, "id"); var method = S(value, "method"); var parameters = P(value, "params");
        if (id.ValueKind is JsonValueKind.String or JsonValueKind.Number && method.Length == 0) {
            if (!calls.TryGetValue(id.ToString(), out var call)) return;
            var failure = P(value, "error");
            if (failure.ValueKind != JsonValueKind.Undefined) {
                var message = S(failure, "message");
                call.TrySetException(new AiOperationException(message.Contains("Authentication required", StringComparison.OrdinalIgnoreCase) ? "AGENT_AUTH_REQUIRED" : "AGENT_REQUEST_REJECTED"));
            } else if (P(value, "result").ValueKind == JsonValueKind.Object) call.TrySetResult(P(value, "result").Clone());
            else call.TrySetException(new AiOperationException("AGENT_PROTOCOL_INCOMPATIBLE"));
            return;
        }
        if (id.ValueKind is JsonValueKind.String or JsonValueKind.Number) {
            if (method == "session/request_permission") {
                bool active;
                lock (sync) {
                    active = !discovering && !acpLoading && !acpCancelling && state == "running" && acpPromptId.Length > 0 && S(parameters, "sessionId") == threadId;
                    if (active) AddPending(id, method, parameters, "approval");
                }
                if (!active) { await AnswerAcpPermissionAsync(id, parameters, false); return; }
            } else await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported interaction" } });
            return;
        }
        if (S(parameters, "sessionId") != threadId || threadId.Length == 0) return;
        var update = P(parameters, "update"); var kind = S(update, "sessionUpdate");
        if (method == "_x.ai/session_notification") {
            lock (sync) {
                if (!acpLoading && !acpCancelling && acpPromptId.Length > 0 && state == "running" &&
                    kind == "retry_state" && S(update, "type") == "retrying") {
                    // Only expose stable local codes, never raw upstream errors or URLs.
                    error = S(update, "error_type") == "rate_limited" ? "AGENT_RATE_LIMITED" : "AGENT_NETWORK_RETRY";
                    phase = "retrying";
                }
            }
            return;
        }
        if (method != "session/update") return;
        lock (sync) {
            if (acpLoading) {
                if (importNative && agent.Id == "opencode" && (kind is "user_message_chunk" or "agent_message_chunk")) {
                    var content = P(update, "content");
                    if (S(content, "type") == "text" && S(content, "text").Length > 0) {
                        if (kind != acpChunkKind) { acpChunkKind = kind; acpChunkId = "acp/native/" + ++acpChunkIndex; }
                        Put(acpChunkId, kind == "user_message_chunk" ? "user" : "assistant", S(content, "text"), append: true, status: "completed");
                    }
                }
                return;
            }
            if (kind == "config_option_update") { ReadAcpConfiguration(update); settingsVersion++; SaveArchive(); return; }
            if (kind == "available_commands_update") { ReadAcpCommands(P(update, "availableCommands")); return; }
            if (acpPromptId.Length == 0 || state != "running") return;
            if (kind is "agent_message_chunk" or "agent_thought_chunk") {
                var content = P(update, "content"); if (S(content, "type") != "text") return;
                if (S(content, "text").Length == 0) return;
                if (error is "AGENT_RATE_LIMITED" or "AGENT_NETWORK_RETRY") error = "";
                if (kind != acpChunkKind) { acpChunkKind = kind; acpChunkId = "acp/" + acpTurnKey + "/" + ++acpChunkIndex; }
                Put(acpChunkId, "assistant", S(content, "text"), append: true, kind: kind == "agent_thought_chunk" ? "reasoning" : "text", status: kind == "agent_thought_chunk" ? "running" : "");
            } else if (kind is "tool_call" or "tool_call_update") {
                acpChunkKind = "";
                var toolId = S(update, "toolCallId"); if (!ValidOption(toolId, 512)) return;
                if (error is "AGENT_RATE_LIMITED" or "AGENT_NETWORK_RETRY") error = "";
                if (!acpTools.ContainsKey(toolId) && acpTools.Count >= 256) throw new System.IO.IOException("Too many ACP tools.");
                var fields = acpTools.TryGetValue(toolId, out var old) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(old.GetRawText()) : new();
                foreach (var property in update.EnumerateObject())
                    if (property.Name is "title" or "kind" or "status" or "rawInput" or "rawOutput" or "content" or "locations") fields[property.Name] = property.Value.Clone();
                var merged = JsonSerializer.SerializeToElement(fields);
                if (merged.GetRawText().Length > 1024 * 1024 || acpTools.Where(t => t.Key != toolId).Sum(t => t.Value.GetRawText().Length) + merged.GetRawText().Length > 4 * 1024 * 1024)
                    throw new System.IO.IOException("ACP tool detail exceeds its bound.");
                acpTools[toolId] = merged;
                var status = S(merged, "status") switch { "completed" => "completed", "failed" => "failed", "pending" or "in_progress" => "running", _ => "unknown" };
                Put("acp/tool/" + acpTurnKey + "/" + toolId, "tool", merged.GetRawText(), kind: "acp", status: status);
            } else if (kind == "plan" && P(update, "entries").ValueKind == JsonValueKind.Array) {
                plan = JsonSerializer.SerializeToElement(P(update, "entries").EnumerateArray().Take(64).Select(e => new { step = Clip(S(e, "content"), 2000), status = S(e, "status") }).ToArray());
            }
        }
    }

    private async Task AnswerAcpPermissionAsync(JsonElement id, JsonElement parameters, bool allow)
    {
        var options = P(parameters, "options"); string optionId = "";
        if (options.ValueKind == JsonValueKind.Array) optionId = options.EnumerateArray().Where(o => S(o, "kind") == (allow ? "allow_once" : "reject_once"))
            .Select(o => S(o, "optionId")).FirstOrDefault(v => ValidOption(v, 512)) ?? "";
        // Never turn a one-time phone approval into allow_always.
        if (allow && optionId.Length == 0) throw new AiOperationException("APPROVAL_OPTION_UNSUPPORTED");
        object outcome = optionId.Length > 0 ? new { outcome = "selected", optionId } : new { outcome = "cancelled" };
        await WriteAsync(new { jsonrpc = "2.0", id, result = new { outcome } });
    }

    private void ReadAcpCommands(JsonElement commands)
    {
        if (commands.ValueKind == JsonValueKind.Array) slashCommands = commands.EnumerateArray().Select(c => S(c, "name"))
            .Where(c => ValidOption(c, 80)).Distinct().Take(100).ToArray();
    }
}
