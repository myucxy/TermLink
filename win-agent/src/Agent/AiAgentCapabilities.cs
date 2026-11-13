// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession
{
    // Retain only the public projection, never the initialization/config/account payload.
    private AiCapabilityProjection.Snapshot agentFeatureMetadata = new();

    // Also accepts Claude system/init and the ACP available_commands_update body.
    internal void CaptureAgentCapabilities(JsonElement initialized)
    {
        lock (sync) agentFeatureMetadata = AiCapabilityProjection.Capture(agent.Protocol, initialized, agentFeatureMetadata);
    }

    internal async Task<object> AgentFeaturesAsync()
    {
        bool queryCodex;
        lock (sync) queryCodex = agent.Protocol == "codex" && !disposed && state is "idle" or "running";
        var optional = queryCodex
            ? await AiCapabilityProjection.ReadCodexAsync(directory, RpcAsync).ConfigureAwait(false)
            : null;
        lock (sync) {
            // This describes supported controls; it neither changes control ownership nor
            // promises that a busy/suspended session can execute an action immediately.
            return AiCapabilityProjection.Catalog(agent.Id, agent.Protocol,
                JsonSerializer.SerializeToElement(Capabilities()), agentFeatureMetadata, slashCommands, optional);
        }
    }
}

// Pure, bounded public metadata projections. An unavailable inventory is not evidence
// that the agent cannot use its configured skills/tools. No general RPC forwarding.
internal static class AiCapabilityProjection
{
    internal const int MaximumEntries = 100, MaximumText = 200;
    internal sealed record Feature(string Id, string Label, string Status, string Detail);
    internal sealed record Skill(string Name, string Description);
    internal sealed record Tool(string Name, string Status);
    internal sealed record Command(string Name, string Description);
    internal sealed record Result(string Agent, string Protocol, Feature[] Features, Skill[] Skills,
        Tool[] Tools, Command[] Commands, bool Truncated);
    internal sealed record Snapshot
    {
        internal Feature[] Features { get; init; } = Array.Empty<Feature>();
        internal Skill[] Skills { get; init; } = Array.Empty<Skill>();
        internal Tool[] Tools { get; init; } = Array.Empty<Tool>();
        internal Command[] Commands { get; init; } = Array.Empty<Command>();
        internal bool Truncated { get; init; }
    }
    private sealed record ReadResult(bool Supported, JsonElement Value, bool RequiresAuthentication = false);
    private const string Unreported = "not-reported";
    private const string InventoryUnknown = "当前协议未报告此清单；不代表代理不能使用已配置的技能或工具。";
    private static readonly Regex PrivateText = new(
        @"(?i)\b(?:bearer\s+[^\s,;]+|(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|secret|token)\s*[:=]\s*[^\s,;]+|(?:sk|ghp|github_pat|xoxb|xoxp)[-_][a-z0-9_-]{8,})|https?://[^\s]+|(?:/Users/|/home/|[A-Za-z]:\\)[^\s]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static Snapshot Capture(string protocol, JsonElement value, Snapshot previous = null)
    {
        var result = previous ?? new Snapshot();
        if (value.ValueKind != JsonValueKind.Object) return result;
        var cut = result.Truncated;
        var features = result.Features.ToDictionary(f => f.Id, StringComparer.Ordinal);
        if (protocol == "claude") {
            var commands = P(value, "commands");
            if (commands.ValueKind != JsonValueKind.Array && S(value, "type") == "system" && S(value, "subtype") == "init")
                commands = P(value, "slash_commands");
            if (commands.ValueKind == JsonValueKind.Array) {
                result = result with { Commands = Commands(commands, result.Commands, ref cut) };
                features["commands"] = Listed("commands", "命令", result.Commands.Length);
            }
            // SDK system/init reports these arrays. A control/initialize response
            // need not carry them; omission must not clear earlier observations.
            if (S(value, "type") == "system" && S(value, "subtype") == "init") {
                var tools = P(value, "tools");
                if (tools.ValueKind == JsonValueKind.Array) {
                    result = result with { Tools = Names(tools, ref cut).Select(n => new Tool(n, "available")).ToArray() };
                    features["toolCatalog"] = Listed("toolCatalog", "工具清单", result.Tools.Length);
                }
                var skills = P(value, "skills");
                if (skills.ValueKind == JsonValueKind.Array) {
                    result = result with { Skills = Names(skills, ref cut).Select(n => new Skill(n, "")).ToArray() };
                    features["skills"] = Listed("skills", "技能清单", result.Skills.Length);
                }
                var servers = P(value, "mcp_servers");
                if (servers.ValueKind == JsonValueKind.Array) {
                    var connected = 0; var needsAuth = 0; var failed = 0;
                    foreach (var server in Bounded(servers, ref cut)) {
                        // Unknown/error strings and server config never leave the adapter.
                        switch (S(server, "status")) { case "connected": connected++; break; case "needs-auth": needsAuth++; break; case "failed": failed++; break; }
                    }
                    features["mcp"] = new("mcp", "MCP 状态", "available",
                        $"初始化报告 {Math.Min(servers.GetArrayLength(), MaximumEntries)} 个服务：已连接 {connected}，待授权 {needsAuth}，失败 {failed}。工具执行仍受原生权限控制。");
                }
            }
        } else if (protocol == "acp") {
            var capabilities = P(value, "agentCapabilities");
            Flag(features, "nativeLoad", "代理历史加载", P(capabilities, "loadSession"));
            Flag(features, "reportedImages", "代理图片输入", P(P(capabilities, "promptCapabilities"), "image"));
            Flag(features, "mcpHttp", "MCP HTTP 传输", P(P(capabilities, "mcpCapabilities"), "http"));
            Flag(features, "mcpSse", "MCP SSE 传输", P(P(capabilities, "mcpCapabilities"), "sse"));
            var update = S(value, "sessionUpdate") == "available_commands_update" ? value
                : P(P(value, "params"), "update");
            if (update.ValueKind != JsonValueKind.Object) update = P(value, "update");
            var commands = S(update, "sessionUpdate") == "available_commands_update"
                ? P(update, "availableCommands") : P(P(value, "_meta"), "availableCommands");
            if (commands.ValueKind == JsonValueKind.Array) {
                // ACP updates replace the list, including an explicitly empty list.
                result = result with { Commands = Commands(commands, Array.Empty<Command>(), ref cut) };
                features["commands"] = Listed("commands", "命令", result.Commands.Length);
            }
        } else if (protocol == "zcode") {
            // Some runtime versions advertise method names. Recognize only methods
            // already used by our adapter; absent declarations stay not-reported.
            var methods = P(value, "methods");
            if (methods.ValueKind == JsonValueKind.Array) {
                var names = Names(methods, ref cut).ToHashSet(StringComparer.Ordinal);
                foreach (var (id, label, method) in new[] {
                    ("runtimeSessionRead", "代理会话读取", "session/read"),
                    ("runtimeSessionStream", "代理事件订阅", "session/subscribe"),
                    ("runtimeSessionStop", "代理中断", "session/stop") })
                    features[id] = new(id, label, names.Contains(method) ? "available" : Unreported,
                        names.Contains(method) ? "运行时声明了此方法；目录查询不会执行它。" : "此运行时响应未报告该方法。");
            }
        }
        return result with { Features = features.Values.Take(MaximumEntries).ToArray(), Truncated = cut || features.Count > MaximumEntries };
    }

    internal static Result Catalog(string agent, string protocol, JsonElement controls, Snapshot captured,
        string[] fallbackCommands = null, Snapshot codex = null)
    {
        captured ??= new Snapshot();
        var source = codex ?? captured;
        var cut = source.Truncated || captured.Truncated;
        var features = new Dictionary<string, Feature>(StringComparer.Ordinal);
        var supported = protocol is "codex" or "claude" or "zcode" or "acp";
        features["stream"] = new("stream", "流式对话", supported ? "available" : Unreported, "表示客户端适配能力；当前会话状态和控制租约仍决定操作是否可用。");
        features["tools"] = new("tools", "工具过程展示", supported ? "available" : Unreported, "展示代理发送的工具事件；不授予执行权限，也不代表工具清单完整。");
        foreach (var (id, label) in new[] {
            ("history", "本地会话历史"), ("artifacts", "工作区成果"), ("attachments", "附件"),
            ("fileReferences", "电脑文件引用"), ("nativeImages", "原生图片输入"), ("configure", "会话设置"),
            ("steer", "运行中补充指令"), ("resume", "恢复会话"), ("fork", "分叉会话") }) {
            var flag = P(controls, id);
            features[id] = new(id, label, BooleanStatus(flag),
                flag.ValueKind == JsonValueKind.True ? "当前 TermLink 适配器支持此操作；执行时仍检查会话状态和权限。"
                    : flag.ValueKind == JsonValueKind.False ? "当前 TermLink 适配器未开放此操作。" : "客户端未报告此能力。");
        }
        foreach (var (id, label) in new[] { ("skills", "技能清单"), ("mcp", "MCP 状态"), ("commands", "命令"),
            ("toolCatalog", "工具清单"), ("rateLimits", "用量限额") })
            features[id] = new(id, label, Unreported, id == "rateLimits" ? "代理尚未提供可展示的限额摘要。" : InventoryUnknown);
        foreach (var feature in captured.Features.Concat(source.Features)) features[feature.Id] = feature;
        if (protocol == "acp") foreach (var (id, label) in new[] { ("nativeLoad", "代理历史加载"), ("reportedImages", "代理图片输入"),
            ("mcpHttp", "MCP HTTP 传输"), ("mcpSse", "MCP SSE 传输") })
            features.TryAdd(id, new(id, label, Unreported, "ACP 初始化未报告此字段。"));
        if (protocol == "zcode") foreach (var (id, label) in new[] { ("runtimeSessionRead", "代理会话读取"),
            ("runtimeSessionStream", "代理事件订阅"), ("runtimeSessionStop", "代理中断") })
            features.TryAdd(id, new(id, label, Unreported, "运行时尚未提供可识别的能力声明。"));
        var commands = source.Commands;
        // Older callers already retain slashCommands but have no capture hook yet.
        // Never resurrect commands after an explicit empty update.
        if (!features.TryGetValue("commands", out var commandFeature) || commandFeature.Status == Unreported) {
            if (fallbackCommands is { Length: > 0 }) {
                commands = Commands(JsonSerializer.SerializeToElement(fallbackCommands.Take(MaximumEntries).ToArray()), commands, ref cut);
                cut |= fallbackCommands.Length > MaximumEntries;
                features["commands"] = Listed("commands", "命令", commands.Length);
            }
        }
        if (features.Count > MaximumEntries) cut = true;
        return new(Text(agent, ref cut), Text(protocol, ref cut), features.Values.Take(MaximumEntries).ToArray(),
            source.Skills, source.Tools, commands, cut);
    }

    // This callback seam permits deterministic fixtures. Production passes the
    // session's own RpcAsync; callers cannot select a method or supply parameters.
    internal static async Task<Snapshot> ReadCodexAsync(string cwd, Func<string, object, Task<JsonElement>> read)
    {
        var skills = ReadOptionalAsync(read, "skills/list", new { cwds = new[] { cwd } });
        var mcp = ReadOptionalAsync(read, "mcpServerStatus/list", new { limit = MaximumEntries });
        var limits = ReadOptionalAsync(read, "account/rateLimits/read", new { });
        await Task.WhenAll(skills, mcp, limits).ConfigureAwait(false);
        var features = new List<Feature>(); var cut = false;
        var skillRows = ProjectSkills(skills.Result, cwd, features, ref cut);
        var tools = ProjectMcp(mcp.Result, features, ref cut);
        ProjectLimits(limits.Result, features);
        return new() { Features = features.ToArray(), Skills = skillRows, Tools = tools, Truncated = cut };
    }

    private static async Task<ReadResult> ReadOptionalAsync(Func<string, object, Task<JsonElement>> read, string method, object parameters)
    {
        try { return new(true, await read(method, parameters).ConfigureAwait(false)); }
        catch (AiOperationException error) when (method == "account/rateLimits/read" && error.Code == "AGENT_AUTH_REQUIRED") {
            // Account authorization is optional inventory state; keep working
            // Skills/MCP lists visible without pretending limits are unsupported.
            return new(true, default, true);
        }
        catch (AiOperationException error) when (error.Code is "AGENT_METHOD_NOT_FOUND" or "AGENT_INVALID_PARAMS") {
            return new(false, default);
        }
    }

    private static Skill[] ProjectSkills(ReadResult read, string cwd, List<Feature> features, ref bool cut)
    {
        if (!read.Supported) { features.Add(Unavailable("skills", "技能清单")); return Array.Empty<Skill>(); }
        var data = P(read.Value, "data");
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<Skill>();
        var skills = new List<Skill>(); var names = new HashSet<string>(StringComparer.Ordinal);
        var inspected = 0; var scoped = data.GetArrayLength() == 0; var partial = false;
        foreach (var group in Bounded(data, ref cut)) {
            if (S(group, "cwd") != cwd) { cut = partial = true; continue; }
            var rows = P(group, "skills");
            if (rows.ValueKind != JsonValueKind.Array) { cut = partial = true; continue; }
            scoped = true;
            if (P(group, "errors").ValueKind == JsonValueKind.Array && P(group, "errors").GetArrayLength() > 0) cut = partial = true;
            foreach (var skill in rows.EnumerateArray()) {
                if (inspected++ >= MaximumEntries) { cut = true; break; }
                if (P(skill, "enabled").ValueKind == JsonValueKind.False) continue;
                var name = Name(S(skill, "name"), ref cut);
                if (name.Length > 0 && names.Add(name)) skills.Add(new(name, Text(S(skill, "description"), ref cut)));
            }
            if (inspected > MaximumEntries) break;
        }
        if (scoped) features.Add(new("skills", "技能清单", "available", partial
            ? "技能清单部分可读；只展示当前工作区的有效条目，未保留加载错误或其他工作区资料。"
            : $"当前工作区报告 {skills.Count} 个可用技能；这里只展示名称和描述。"));
        return skills.ToArray();
    }

    private static Tool[] ProjectMcp(ReadResult read, List<Feature> features, ref bool cut)
    {
        if (!read.Supported) {
            features.Add(Unavailable("mcp", "MCP 状态")); features.Add(Unavailable("toolCatalog", "工具清单"));
            return Array.Empty<Tool>();
        }
        var data = P(read.Value, "data");
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<Tool>();
        var cursor = P(read.Value, "nextCursor");
        cut |= cursor.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && (cursor.ValueKind != JsonValueKind.String || cursor.GetString().Length > 0);
        var rows = new List<Tool>(); var names = new HashSet<string>(StringComparer.Ordinal);
        var servers = 0; var needsAuth = 0; var inspected = 0; var reportedTools = data.GetArrayLength() == 0;
        foreach (var server in Bounded(data, ref cut)) {
            servers++;
            var serverName = Name(S(server, "name"), ref cut);
            var waiting = S(server, "authStatus") == "notLoggedIn";
            if (waiting) needsAuth++;
            var tools = P(server, "tools");
            if (tools.ValueKind != JsonValueKind.Object || serverName.Length == 0) continue;
            reportedTools = true;
            foreach (var tool in tools.EnumerateObject()) {
                if (inspected++ >= MaximumEntries) { cut = true; break; }
                var name = Name(tool.Name, ref cut);
                if (name.Length == 0) continue;
                var qualified = Text(serverName + "/" + name, ref cut);
                if (names.Add(qualified)) rows.Add(new(qualified, waiting ? Unreported : "available"));
            }
        }
        features.Add(new("mcp", "MCP 状态", "available", $"已读取一页，共 {servers} 个服务，其中 {needsAuth} 个待授权。不会发起登录或刷新配置；工具执行仍受原生权限控制。"));
        if (reportedTools) features.Add(Listed("toolCatalog", "工具清单", rows.Count));
        return rows.ToArray();
    }

    private static void ProjectLimits(ReadResult read, List<Feature> features)
    {
        if (!read.Supported) { features.Add(Unavailable("rateLimits", "用量限额")); return; }
        if (read.RequiresAuthentication) { features.Add(new("rateLimits", "用量限额", "requires_auth", "需要登录")); return; }
        var limits = P(read.Value, "rateLimits");
        if (limits.ValueKind != JsonValueKind.Object) limits = P(P(read.Value, "rateLimitsByLimitId"), "codex");
        var summaries = new List<string>();
        foreach (var (key, label) in new[] { ("primary", "主要窗口"), ("secondary", "次要窗口") }) {
            var window = P(limits, key); var percent = P(window, "usedPercent");
            if (percent.ValueKind == JsonValueKind.Number && percent.TryGetDecimal(out var number) && number is >= 0 and <= 100)
                summaries.Add(label + "已用 " + number.ToString("0.##", CultureInfo.InvariantCulture) + "%");
        }
        if (summaries.Count > 0) features.Add(new("rateLimits", "用量限额", "available", string.Join("；", summaries) + "。仅包含用量百分比，不包含账户或凭据信息。"));
    }

    private static Command[] Commands(JsonElement value, Command[] previous, ref bool cut)
    {
        var rows = new List<Command>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Bounded(value, ref cut)) {
            var name = Name(item.ValueKind == JsonValueKind.String ? item.GetString() : S(item, "name"), ref cut);
            if (name.Length == 0 || !names.Add(name)) continue;
            var description = item.ValueKind == JsonValueKind.String ? previous.FirstOrDefault(c => c.Name == name)?.Description ?? "" : S(item, "description");
            rows.Add(new(name, Text(description, ref cut)));
        }
        return rows.ToArray();
    }
    private static string[] Names(JsonElement value, ref bool cut)
    {
        var rows = new List<string>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Bounded(value, ref cut)) {
            if (item.ValueKind != JsonValueKind.String) continue;
            var name = Name(item.GetString(), ref cut);
            if (name.Length > 0 && names.Add(name)) rows.Add(name);
        }
        return rows.ToArray();
    }
    private static IEnumerable<JsonElement> Bounded(JsonElement value, ref bool cut)
    {
        cut |= value.GetArrayLength() > MaximumEntries;
        return value.EnumerateArray().Take(MaximumEntries);
    }
    private static string Name(string value, ref bool cut)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.Any(char.IsControl)) { cut = true; return ""; }
        var text = Text(value, ref cut);
        return text.Contains("[redacted]", StringComparison.Ordinal) ? "" : text;
    }
    private static string Text(string value, ref bool cut)
    {
        value ??= "";
        // Inspect before shortening so a secret straddling the display boundary
        // cannot survive as an apparently harmless prefix.
        if (value.Length > 4096) { value = Clip(value, 4096); cut = true; }
        try {
            var safe = PrivateText.Replace(value, "[redacted]");
            if (safe != value) cut = true;
            value = safe;
        } catch (RegexMatchTimeoutException) { cut = true; return "[redacted]"; }
        value = new string(value.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray()).Trim();
        if (value.Length > MaximumText) { value = Clip(value, MaximumText); cut = true; }
        return value;
    }
    private static string Clip(string value, int length)
    {
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
    private static void Flag(Dictionary<string, Feature> features, string id, string label, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            features[id] = new(id, label, BooleanStatus(value), value.ValueKind == JsonValueKind.True
                ? "代理明确声明支持；此项只投影声明，不改变客户端权限。" : "代理明确声明不支持此项能力。");
    }
    private static string BooleanStatus(JsonElement value) => value.ValueKind == JsonValueKind.True ? "available" : value.ValueKind == JsonValueKind.False ? "unsupported" : Unreported;
    private static Feature Listed(string id, string label, int count) => new(id, label, "available", $"代理报告 {count} 项；仅展示公开元数据，不证明执行权限或清单完整性。");
    private static Feature Unavailable(string id, string label) => new(id, label, "unsupported", "当前协议版本不支持此读取接口或参数；不代表代理不能使用已配置的技能或工具。");
    private static JsonElement P(JsonElement value, string name) => AiAgentSession.P(value, name);
    private static string S(JsonElement value, string name) => AiAgentSession.S(value, name);
}
