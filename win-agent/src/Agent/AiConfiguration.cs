using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TermLink.Ai;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession
{
    internal sealed record EffortOption(string Value, string Description = "");
    internal sealed record ReasoningOptions(string Status, EffortOption[] Options, string DefaultValue = "");
    internal sealed record PermissionOption(string Id, string Label, string Description, bool RequiresConfirmation = false);
    internal sealed record ConfigurationOptions(bool ModelListing, bool ModelSwitch, bool ReasoningSwitch,
        bool PermissionSwitch, string ReasoningScope, PermissionOption[] PermissionModes);
    private ModelOption[] modelOptions = Array.Empty<ModelOption>();
    private string reasoningEffort = "", settingsNotice = "";
    private JsonElement codexRequirements;
    private bool requirementsKnown;
    private ReasoningOptions zcodeReasoning = UnknownReasoning();
    private long? zcodeRevision;

    internal static ReasoningOptions UnknownReasoning() => new("unknown", Array.Empty<EffortOption>());
    private static bool ValidOption(string value, int limit = 100) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
    // These protocol extensions are verified against the installed native versions, not a shared agent enum.
    private static bool AdvancedConfiguration(AiAgentInstallation installation)
    {
        var match = Regex.Match(installation.Version ?? "", @"\d+\.\d+\.\d+");
        if (!Version.TryParse(match.Value, out var version)) return false;
        return installation.Protocol switch {
            "codex" => version.Major == 0 && version >= new Version(0, 160, 0),
            "claude" => version.Major == 2 && version >= new Version(2, 1, 116),
            "zcode" => version.Major == 0 && version >= new Version(0, 16, 9), _ => false };
    }
    internal static ConfigurationOptions ConfigurationFor(AiAgentInstallation installation)
    {
        if (installation.Strategy != "native" || installation.Status != "available")
            return new(false, false, false, false, "", Array.Empty<PermissionOption>());
        var protocol = installation.Protocol;
        if (protocol == "acp" && installation.Id == "grok") return new(true, true, true, true, "session", new[] {
            new PermissionOption("ask", "默认审批", "使用 Grok 默认权限规则，需要确认的工具交由手机批准或拒绝。"),
            new PermissionOption("full-access", "完全访问", "使用 Grok 原生 always-approve；拒绝规则和管理员限制仍生效。仅在新建会话时设置。", true),
        });
        var advanced = AdvancedConfiguration(installation);
        var modes = new List<PermissionOption>();
        if (protocol == "codex") {
            modes.Add(new("ask", "按需确认", "在工作目录沙箱内执行，需要提升权限时由你确认。"));
            modes.Add(new("read-only", "只读沙箱", "使用 Codex 只读沙箱；提升权限仍需要审批。"));
            if (advanced) {
                modes.Add(new("auto-review", "帮我批准", "保留工作目录沙箱，由 Codex 原生自动审核决定批准或拒绝。仅此会话。", true));
                modes.Add(new("full-access", "完全访问", "关闭 Codex 沙箱限制并停止执行审批。可访问当前 Windows 用户有权限的文件和网络，不会获得管理员权限。仅此会话。", true));
            }
        } else if (protocol == "claude") {
            modes.Add(new("ask", "默认审批", "使用 Claude 原生默认工具审批规则。"));
            modes.Add(new("plan", "计划模式", "使用 Claude 原生计划模式，不等同于操作系统只读沙箱。"));
            modes.Add(new("accept-edits", "自动编辑", "自动批准 Claude 定义的文件编辑操作，其他工具仍按原生规则审批。"));
            if (advanced) {
                modes.Add(new("dont-ask", "不询问", "使用 Claude dontAsk：需要审批且未被规则允许的工具将被拒绝，不等同于全部允许。"));
                modes.Add(new("full-access", "跳过权限检查", "使用 Claude bypassPermissions，跳过其工具权限检查。仅此会话，不改变 Windows 用户权限。", true));
            }
        } else if (protocol == "zcode") {
            modes.Add(new("ask", "默认执行", "使用 ZCode build 模式及其原生审批规则。"));
            if (advanced) {
                modes.Add(new("edit", "自动编辑", "使用 ZCode edit 模式，编辑权限由 ZCode 原生规则控制。", true));
                // ZCode's independent plan state is not a permission mode: setMode(plan)
                // leaves settings.mode unchanged. Do not expose it as an applied grant.
                modes.Add(new("auto", "自动审批", "使用 ZCode auto 模式，由其原生审批机制决定操作。仅此会话。", true));
                modes.Add(new("full-access", "完全访问", "使用 ZCode yolo 模式，放开其执行审批。仅此会话，不改变 Windows 用户权限。", true));
            }
        }
        var configurable = protocol is "codex" or "claude" || protocol == "zcode" && advanced;
        return new(protocol is "codex" or "claude", configurable, protocol == "codex" || protocol == "zcode" && advanced,
            configurable, protocol == "claude" ? "new_session" : protocol == "codex" ? "next_turn" : "session", modes.ToArray());
    }
    private ConfigurationOptions CurrentConfiguration()
    {
        var config = ConfigurationFor(agent);
        // Grok ACP modes are reasoning settings, not permission grants. Until a
        // native permission-change ACK is available, keep live grants immutable.
        if (agent.Protocol == "acp") config = config with { PermissionSwitch = false, ReasoningSwitch = acpEffortConfig, ModelSwitch = acpModelConfig };
        if (agent.Protocol == "codex") config = config with { PermissionModes = config.PermissionModes.Where(m => CodexModeAllowed(m.Id)).ToArray() };
        if (nativePermissions != null) config = config with { PermissionModes = new[] { new PermissionOption("historical", "历史授权", "沿用 Codex 原生恢复返回的审批、沙箱和审核方式。") }.Concat(config.PermissionModes).ToArray() };
        return config;
    }
    private bool CodexModeAllowed(string mode)
    {
        if (!requirementsKnown) return mode is "ask" or "read-only";
        bool Allows(string field, string value) { var list = P(codexRequirements, field); return list.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == value); }
        return Allows("allowedApprovalPolicies", CodexApproval(mode)) && Allows("allowedSandboxModes", CodexSandbox(mode)) &&
            Allows("allowedApprovalsReviewers", CodexReviewer(mode));
    }
    private static string CodexApproval(string mode) => mode == "full-access" ? "never" : "on-request";
    private static string CodexReviewer(string mode) => mode == "auto-review" ? "auto_review" : "user";
    private static string CodexSandbox(string mode) => mode == "full-access" ? "danger-full-access" : mode == "read-only" ? "read-only" : "workspace-write";
    private object CodexSandboxPolicy() => permissionMode == "full-access" ? new { type = "dangerFullAccess" } : permissionMode == "read-only" ? (object)new { type = "readOnly" } : new { type = "workspaceWrite", writableRoots = new[] { directory }, networkAccess = false };
    private static string ZcodeMode(string mode) => mode == "ask" ? "build" : mode == "full-access" ? "yolo" : mode;

    internal static void ValidatePermissionSelection(AiAgentInstallation installation, string mode, string confirmed, bool changing = true)
    {
        var option = ConfigurationFor(installation).PermissionModes.FirstOrDefault(m => m.Id == mode) ?? throw new AiOperationException("INVALID_PERMISSION_MODE");
        if (changing && option.RequiresConfirmation && confirmed != mode) throw new AiOperationException("PERMISSION_CONFIRMATION_REQUIRED");
    }
    private ReasoningOptions ReasoningFor(string model, string provider = "") =>
        agent.Protocol == "zcode" ? zcodeReasoning : modelOptions.FirstOrDefault(m => m.ModelId == model && m.ProviderId == provider)?.Reasoning
            ?? (model.Length == 0 ? modelOptions.FirstOrDefault(m => m.IsDefault || m.ModelId == "default")?.Reasoning : null) ?? UnknownReasoning();
    private string EffectiveEffort() => reasoningEffort.Length > 0 ? reasoningEffort : ReasoningFor(modelId, providerId).DefaultValue is { Length: > 0 } value ? value : null;
    internal static ReasoningOptions ParseReasoning(JsonElement model, bool claude)
    {
        var values = P(model, claude ? "supportedEffortLevels" : "supportedReasoningEfforts");
        if (claude && P(model, "supportsEffort").ValueKind == JsonValueKind.False) return new("unsupported", Array.Empty<EffortOption>());
        if (values.ValueKind != JsonValueKind.Array) return UnknownReasoning();
        var options = values.EnumerateArray().Select(v => new EffortOption(claude && v.ValueKind == JsonValueKind.String ? v.GetString() : S(v, "reasoningEffort"), claude ? "" : Clip(S(v, "description"), 300)))
            .Where(v => ValidOption(v.Value)).DistinctBy(v => v.Value).Take(20).ToArray();
        var defaultValue = S(model, "defaultReasoningEffort");
        return new(options.Length > 0 ? "available" : "unsupported", options, options.Any(o => o.Value == defaultValue) ? defaultValue : "");
    }
    private async Task InitializeConfigurationAsync(JsonElement initialized)
    {
        if (agent.Protocol is "codex" or "claude" or "acp") {
            try { var listed = await ReadModelsAsync(initialized); lock (sync) modelOptions = listed.Models; }
            catch (AiOperationException) { settingsNotice = "模型能力暂时不可用，可重试读取；未套用其他模型的推理档位。"; }
        }
        if (agent.Protocol == "codex") {
            try {
                var result = await RpcAsync("configRequirements/read", new { });
                var requirements = P(result, "requirements");
                requirementsKnown = requirements.ValueKind is JsonValueKind.Object or JsonValueKind.Null;
                if (requirementsKnown) codexRequirements = requirements.Clone();
            } catch (AiOperationException) { requirementsKnown = false; }
            if (!importNative && nativePermissions == null && !CodexModeAllowed(permissionMode)) throw new AiOperationException("PERMISSION_MODE_UNAVAILABLE");
        }
        // ACP session/new reports the authoritative options; initialize metadata
        // can be incomplete or differ from the session's current model catalog.
        if (!importNative && nativePermissions == null && reasoningEffort.Length > 0 && agent.Protocol is not ("zcode" or "acp") && !ReasoningFor(modelId, providerId).Options.Any(o => o.Value == reasoningEffort))
            throw new AiOperationException("INVALID_REASONING_EFFORT");
        if (agent.Protocol == "zcode" && reasoningEffort.Length > 0 && !AdvancedConfiguration(agent))
            throw new AiOperationException("INVALID_REASONING_EFFORT");
    }
    internal void UpdateModelOptions(string agentId, ModelList list)
    {
        if (agent.Id == agentId) lock (sync) modelOptions = list.Models;
    }
    private void ReadZcodeConfiguration(JsonElement value)
    {
        var settings = P(value, "settings");
        if (settings.ValueKind != JsonValueKind.Object) throw new AiOperationException("SETTINGS_OUTCOME_UNKNOWN");
        var model = P(P(settings, "model"), "current");
        var thought = P(settings, "thoughtLevel");
        var available = P(thought, "available");
        var mode = S(P(settings, "mode"), "current");
        var permission = ConfigurationFor(agent).PermissionModes.FirstOrDefault(m => ZcodeMode(m.Id) == mode)?.Id;
        var revisionValue = P(P(value, "runtime"), "stateRevision");
        if (permission == null || available.ValueKind != JsonValueKind.Array ||
            revisionValue.ValueKind != JsonValueKind.Number || !revisionValue.TryGetInt64(out var revision))
            throw new AiOperationException("SETTINGS_OUTCOME_UNKNOWN");
        var options = available.EnumerateArray().Select(o => new EffortOption(S(o, "value"), Clip(S(o, "description"), 300)))
            .Where(o => ValidOption(o.Value)).DistinctBy(o => o.Value).Take(20).ToArray();
        var enabled = P(thought, "enabled").ValueKind == JsonValueKind.True;
        var current = S(thought, "current"); var defaultValue = S(thought, "defaultLevel");
        if (current.Length > 0 && !options.Any(o => o.Value == current)) throw new AiOperationException("SETTINGS_OUTCOME_UNKNOWN");
        lock (sync) {
            var id = S(model, "modelId"); var provider = S(model, "providerId");
            modelId = id; providerId = provider; permissionMode = permission;
            zcodeReasoning = new(enabled && options.Length > 0 ? "available" : "unsupported", enabled ? options : Array.Empty<EffortOption>(),
                enabled && options.Any(o => o.Value == defaultValue) ? defaultValue : "");
            reasoningEffort = enabled ? current : "";
            zcodeRevision = revision;
        }
    }
}
