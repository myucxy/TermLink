// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TermLink.Agent;

// Shared by live adapters and native history. This is presentation data only: no
// paths, URLs, commands, resource links or provider metadata grant capabilities.
internal static class AiToolPresentation
{
    internal const string Schema = "termlink.tool.v1";
    internal const int MaxLength = 256 * 1024;
    internal const int OutputLimit = 128 * 1024;
    private static readonly string[] TextFields = { "name", "command", "cwd", "input", "output", "progress", "raw" };
    private static readonly Regex Secrets = new(
        "(?i)(bearer\\s+)[a-z0-9._~+/=-]+|((?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|secret|client[_-]?secret|token)[\\\"']?\\s*[:=]\\s*[\\\"']?)(?:bearer\\s+)?[^\\s\\\"'&,;}]+|((?:--(?:api-key|token|password|secret))\\s+)[^\\s]+|(?<=://)[^/@\\s]+:[^/@\\s]+@",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static string Normalize(JsonElement value, string kind = "", string previous = "")
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal) { ["schema"] = Schema, ["kind"] = "tool" };
        var cut = false;
        if (!string.IsNullOrEmpty(previous)) {
            try {
                using var document = JsonDocument.Parse(previous);
                if (S(document.RootElement, "schema") == Schema) ReadNormalized(document.RootElement, fields, ref cut);
            } catch (JsonException) { /* Old archives can contain incomplete legacy JSON. */ }
        }
        if (kind == "session/request_permission") value = P(value, "toolCall");
        if (S(value, "schema") == Schema) ReadNormalized(value, fields, ref cut);
        else if (value.ValueKind == JsonValueKind.Object) {
            var input = FirstValue(value, "input", "rawInput", "arguments");
            var state = P(value, "state");
            if (state.ValueKind == JsonValueKind.Object && !Present(input)) input = P(state, "input");
            var name = FirstText(value, "name", "tool_name", "toolName", "tool");
            if (name.Length > 0) SetText(fields, "name", name, 512, ref cut);
            var classified = Classify(value, kind, name, input);
            if (classified != "tool" || (string)fields["kind"] == "tool") fields["kind"] = classified;
            if (name.Length == 0 && !fields.ContainsKey("name") && S(value, "title").Length > 0)
                SetText(fields, "name", S(value, "title"), 512, ref cut);
            if (Present(input)) SetText(fields, "input", Render(input, ref cut), 32768, ref cut);
            SetFrom(fields, "command", value, input, new[] { "command", "cmd" }, 8192, ref cut);
            SetFrom(fields, "cwd", value, input, new[] { "cwd", "workdir", "working_directory" }, 4096, ref cut);
            var output = FirstValue(value, "aggregatedOutput", "output", "rawOutput", "result");
            if (!Present(output) && state.ValueKind == JsonValueKind.Object) output = P(state, "output");
            if (Present(output)) SetText(fields, "output", ContentText(output, ref cut), OutputLimit, ref cut);
            else {
                var content = FirstValue(value, "content", "contentItems");
                if (Present(content)) SetText(fields, "output", ContentText(content, ref cut), OutputLimit, ref cut);
            }
            if (Present(P(value, "error"))) SetText(fields, "output", ErrorCode(P(value, "error")), 512, ref cut);
            CopyNumber(fields, "exitCode", FirstValue(value, "exitCode", "exit_code"));
            CopyNumber(fields, "durationMs", FirstValue(value, "durationMs", "duration_ms"), nonnegative: true);
            if (P(value, "elapsed_time_seconds").ValueKind == JsonValueKind.Number && P(value, "elapsed_time_seconds").TryGetDouble(out var seconds) && seconds >= 0 && double.IsFinite(seconds))
                fields["durationMs"] = Math.Min(seconds * 1000, 9007199254740991d);
            var progress = FirstValue(value, "progress", "progressMessage");
            if (Present(progress)) SetText(fields, "progress", Render(progress, ref cut), 4096, ref cut);
            ReadFiles(value, input, fields, ref cut);
            ReadChildren(value, fields, ref cut);
            ReadUrls(value, fields, ref cut);
            if ((string)fields["kind"] == "search" && !fields.ContainsKey("input")) {
                var query = FirstValue(value, "query", "pattern");
                if (!Present(query)) query = FirstValue(P(value, "action"), "query", "queries", "pattern");
                if (Present(query)) SetText(fields, "input", Render(query, ref cut), 32768, ref cut);
            }
            SetText(fields, "raw", Render(value, ref cut), 16384, ref cut);
        } else if (Present(value)) SetText(fields, "raw", Render(value, ref cut), 16384, ref cut);
        if ((string)fields["kind"] == "tool") fields["kind"] = Classify(value, kind, "", default);
        if (cut) fields["truncated"] = true;
        return Bounded(fields, MaxLength);
    }

    internal static (string Title, string Summary, string Status) Describe(string text, string kind = "")
    {
        try {
            using var source = JsonDocument.Parse(text);
            var status = Status(kind == "session/request_permission" ? P(source.RootElement, "toolCall") : source.RootElement);
            using var document = JsonDocument.Parse(Normalize(source.RootElement, kind));
            var value = document.RootElement;
            var title = S(value, "kind") switch {
                "command" => "运行命令", "file" => "文件操作", "mcp" => "调用 MCP 工具",
                "search" => "搜索", "agent" => "子 Agent 任务", "image" => "查看图像",
                _ => S(value, "name").Length > 0 ? S(value, "name") : "工具操作",
            };
            var summary = FirstText(value, "command", "progress");
            if (summary.Length == 0 && P(value, "files").ValueKind == JsonValueKind.Array)
                summary = string.Join(" · ", P(value, "files").EnumerateArray().Take(4).Select(f => S(f, "path")));
            if (summary.Length == 0 && S(value, "kind") == "search") summary = S(value, "input");
            if (summary.Length == 0) summary = S(value, "name");
            if (summary.Length == 0) summary = "展开查看记录";
            return (Short(title, 80), Short(summary, 240), status);
        } catch (JsonException) { return ("工具操作", "展开查看记录", "unknown"); }
    }

    internal static string Status(JsonElement value, string fallback = "unknown")
    {
        var state = FirstValue(value, "status", "state");
        if (state.ValueKind == JsonValueKind.Object) state = P(state, "status");
        var status = state.ValueKind == JsonValueKind.String ? state.GetString() : "";
        var result = status switch {
            "completed" or "success" or "succeeded" or "done" => "completed",
            "failed" or "error" => "failed", "declined" or "denied" or "rejected" => "denied",
            "cancelled" or "canceled" or "interrupted" or "stopped" => "cancelled",
            "inProgress" or "in_progress" or "running" or "pending" or "started" => "running",
            "unknown" => "unknown", _ => fallback,
        };
        if (result is "denied" or "cancelled") return result;
        var code = FirstValue(value, "exitCode", "exit_code");
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var exit) && exit != 0 ||
            P(value, "is_error").ValueKind == JsonValueKind.True || P(value, "isError").ValueKind == JsonValueKind.True ||
            P(value, "success").ValueKind == JsonValueKind.False || Present(P(value, "error")) ||
            P(P(value, "result"), "isError").ValueKind == JsonValueKind.True) return "failed";
        return result;
    }

    internal static string Preview(string text, int maxLength = 16000)
    {
        // Below this minimum a valid schema envelope cannot be represented.
        if (maxLength < 64) throw new ArgumentOutOfRangeException(nameof(maxLength));
        try {
            using var document = JsonDocument.Parse(text);
            var normalized = Normalize(document.RootElement);
            if (normalized.Length <= maxLength) return normalized;
            using var parsed = JsonDocument.Parse(normalized);
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            var cut = false; ReadNormalized(parsed.RootElement, fields, ref cut);
            fields["schema"] = Schema; fields["truncated"] = true;
            return Bounded(fields, maxLength);
        } catch (JsonException) {
            return Bounded(new() { ["schema"] = Schema, ["kind"] = "tool", ["raw"] = Redact(text), ["truncated"] = true }, maxLength);
        }
    }

    // Keep upstream messages/data out of errors: they often contain credentials,
    // complete HTTP requests or private URLs. Codes are deliberately allowlisted.
    internal static string ErrorCode(JsonElement failure, string fallback = "AGENT_REPORTED_ERROR")
    {
        var code = P(failure, "code");
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)) {
            if (number == -32601) return "AGENT_METHOD_NOT_FOUND";
            if (number == -32602) return "AGENT_INVALID_PARAMS";
            if (number is -32700 or -32600) return "AGENT_PROTOCOL_INCOMPATIBLE";
        }
        if (S(failure, "code") == "CONFIGURATION_ERROR") return "AGENT_MODEL_REQUIRED";
        var info = P(failure, "codexErrorInfo");
        var tag = info.ValueKind == JsonValueKind.String ? info.GetString() : info.ValueKind == JsonValueKind.Object ? info.EnumerateObject().FirstOrDefault().Name : "";
        if (tag == "contextWindowExceeded") return "AGENT_CONTEXT_LIMIT";
        if (tag is "usageLimitExceeded" or "rateLimitExceeded") return "AGENT_RATE_LIMITED";
        if (tag is "responseStreamConnectionFailed" or "responseStreamDisconnected" or "httpConnectionFailed") return "AGENT_NETWORK_ERROR";
        if (S(failure, "message").Contains("Authentication required", StringComparison.OrdinalIgnoreCase)) return "AGENT_AUTH_REQUIRED";
        return fallback;
    }

    internal static string ErrorText(string code, bool retryable = false) => JsonSerializer.Serialize(new {
        schema = "termlink.error.v1", code, retryable,
        message = code switch {
            "AGENT_METHOD_NOT_FOUND" => "当前 Agent 不支持该方法。", "AGENT_INVALID_PARAMS" => "当前 Agent 不接受该请求参数。",
            "AGENT_AUTH_REQUIRED" => "请在主机完成 Agent 登录。", "AGENT_MODEL_REQUIRED" => "请在主机配置可用模型。",
            "AGENT_RATE_LIMITED" => "Agent 已达到用量或速率限制。", "AGENT_CONTEXT_LIMIT" => "会话已达到上下文限制。",
            "AGENT_NETWORK_ERROR" or "AGENT_NETWORK_RETRY" => "Agent 连接失败，正在等待连接恢复。",
            _ => "Agent 未能完成请求。",
        },
    });

    private static string Classify(JsonElement value, string hint, string name, JsonElement input)
    {
        var type = FirstText(value, "type", "kind");
        foreach (var candidate in new[] { type, hint, name.ToLowerInvariant() }) {
            var mapped = candidate switch {
                "command" or "commandExecution" or "item/commandExecution/requestApproval" or "execute" or "bash" or "shell" or "shell_command" or "exec_command" => "command",
                "file" or "fileChange" or "item/fileChange/requestApproval" or "read" or "edit" or "delete" or "move" or "write" or "read_file" or "write_file" or "multiedit" or "apply_patch" => "file",
                "mcp" or "mcpToolCall" => "mcp", "search" or "webSearch" or "websearch" or "webfetch" or "glob" or "grep" or "fetch" => "search",
                "agent" or "collabToolCall" or "collabAgentToolCall" or "task" => "agent",
                "image" or "imageView" or "imageGeneration" or "view_image" => "image", _ => "",
            };
            if (mapped.Length > 0) return mapped;
        }
        if (name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return "mcp";
        if (FirstText(value, "command", "cmd").Length > 0 || FirstText(input, "command", "cmd").Length > 0) return "command";
        return "tool";
    }

    private static void ReadNormalized(JsonElement value, Dictionary<string, object> fields, ref bool cut)
    {
        fields["schema"] = Schema;
        var kind = S(value, "kind");
        if (kind is "command" or "file" or "mcp" or "search" or "agent" or "image" or "tool") fields["kind"] = kind;
        foreach (var key in TextFields) if (P(value, key).ValueKind == JsonValueKind.String)
            SetText(fields, key, S(value, key), key == "output" ? OutputLimit : key == "raw" ? 16384 : key == "input" ? 32768 : 8192, ref cut);
        CopyNumber(fields, "exitCode", P(value, "exitCode")); CopyNumber(fields, "durationMs", P(value, "durationMs"), true);
        foreach (var key in new[] { "files", "children", "urls" }) {
            var rows = P(value, key); if (rows.ValueKind != JsonValueKind.Array) continue;
            var list = new List<Dictionary<string, object>>();
            foreach (var row in rows.EnumerateArray().Take(64)) {
                var entry = new Dictionary<string, object>();
                var keys = key == "files" ? new[] { "path", "diff" } : key == "children" ? new[] { "id", "status", "prompt" } : new[] { "title", "url" };
                foreach (var field in keys) if (P(row, field).ValueKind == JsonValueKind.String) SetText(entry, field, S(row, field), field == "diff" ? 8192 : 4096, ref cut);
                if (entry.ContainsKey(keys[0]) || key == "urls" && entry.ContainsKey("url")) list.Add(entry);
            }
            cut |= rows.GetArrayLength() > 64; fields[key] = list;
        }
        if (P(value, "truncated").ValueKind == JsonValueKind.True) cut = true;
    }

    private static void ReadFiles(JsonElement value, JsonElement input, Dictionary<string, object> fields, ref bool cut)
    {
        var rows = FirstValue(value, "files", "changes", "locations");
        var list = new List<Dictionary<string, object>>();
        if (rows.ValueKind == JsonValueKind.Array) {
            foreach (var row in rows.EnumerateArray().Take(64)) {
                var path = FirstText(row, "path", "file_path", "filePath"); if (path.Length == 0) continue;
                var entry = new Dictionary<string, object>(); SetText(entry, "path", path, 4096, ref cut);
                if (P(row, "diff").ValueKind == JsonValueKind.String) SetText(entry, "diff", S(row, "diff"), 8192, ref cut);
                list.Add(entry);
            }
            cut |= rows.GetArrayLength() > 64;
        }
        var content = P(value, "content");
        if (content.ValueKind == JsonValueKind.Array) foreach (var row in content.EnumerateArray().Take(64)) {
            if (S(row, "type") != "diff" || S(row, "path").Length == 0) continue;
            var entry = new Dictionary<string, object>(); SetText(entry, "path", S(row, "path"), 4096, ref cut);
            // ACP supplies old/new text, not a unified diff. Label it explicitly.
            SetText(entry, "diff", "--- old\n" + S(row, "oldText") + "\n+++ new\n" + S(row, "newText"), 8192, ref cut);
            list.RemoveAll(f => (string)f["path"] == (string)entry["path"]); list.Add(entry);
        }
        if (list.Count == 0 && rows.ValueKind != JsonValueKind.Array) {
            var path = FirstText(value, "path", "file_path", "filePath");
            if (path.Length == 0) path = FirstText(input, "path", "file_path", "filePath");
            if (path.Length > 0) { var entry = new Dictionary<string, object>(); SetText(entry, "path", path, 4096, ref cut); list.Add(entry); }
        }
        if (rows.ValueKind == JsonValueKind.Array || list.Count > 0) { cut |= list.Count > 64; fields["files"] = list.Take(64).ToList(); }
    }

    private static void ReadChildren(JsonElement value, Dictionary<string, object> fields, ref bool cut)
    {
        var list = new List<Dictionary<string, object>>();
        var ids = P(value, "receiverThreadIds");
        if (ids.ValueKind == JsonValueKind.Array) foreach (var id in ids.EnumerateArray().Take(64)) {
            if (id.ValueKind == JsonValueKind.String) list.Add(new() { ["id"] = Clean(id.GetString(), 512, ref cut) });
        }
        foreach (var name in new[] { "receiverThreadId", "newThreadId" }) if (S(value, name).Length > 0)
            list.Add(new() { ["id"] = Clean(S(value, name), 512, ref cut) });
        var states = P(value, "agentsStates");
        if (states.ValueKind == JsonValueKind.Object) foreach (var state in states.EnumerateObject().Take(64)) {
            var id = Clean(state.Name, 512, ref cut); list.RemoveAll(c => (string)c["id"] == id);
            list.Add(new() { ["id"] = id, ["status"] = Status(state.Value) });
        }
        foreach (var child in list) {
            if (S(value, "prompt").Length > 0) SetText(child, "prompt", S(value, "prompt"), 4096, ref cut);
            if (P(value, "agentStatus").ValueKind == JsonValueKind.Object) child["status"] = Status(P(value, "agentStatus"));
            else if (P(value, "agentStatus").ValueKind == JsonValueKind.String) child["status"] = S(value, "agentStatus");
        }
        if (list.Count > 0 || ids.ValueKind == JsonValueKind.Array) { cut |= list.Count > 64; fields["children"] = list.Take(64).ToList(); }
    }

    private static void ReadUrls(JsonElement value, Dictionary<string, object> fields, ref bool cut)
    {
        var url = FirstText(value, "url"); if (url.Length == 0) url = S(P(value, "action"), "url");
        if (url.Length == 0) return;
        fields["urls"] = new List<Dictionary<string, object>> { new() { ["url"] = Clean(url, 4096, ref cut) } };
    }

    private static string ContentText(JsonElement value, ref bool cut)
    {
        if (value.ValueKind == JsonValueKind.Array) {
            var parts = new List<string>(); var size = 0;
            foreach (var part in value.EnumerateArray().Take(64)) {
                var text = ContentText(part, ref cut); size += text.Length;
                parts.Add(text); if (size > OutputLimit) { cut = true; break; }
            }
            cut |= value.GetArrayLength() > 64; return string.Join("\n", parts);
        }
        if (value.ValueKind == JsonValueKind.Object) {
            if (P(value, "text").ValueKind == JsonValueKind.String) return S(value, "text");
            if (Present(P(value, "content"))) return ContentText(P(value, "content"), ref cut);
            if (S(value, "type") is "image" or "audio" or "image_url") return "[" + S(value, "type") + "]";
        }
        return Render(value, ref cut);
    }

    private static void SetFrom(Dictionary<string, object> fields, string key, JsonElement value, JsonElement input, string[] names, int limit, ref bool cut)
    {
        var source = FirstValue(value, names); if (!Present(source)) source = FirstValue(input, names);
        if (Present(source)) SetText(fields, key, Render(source, ref cut), limit, ref cut);
    }
    private static void SetText(Dictionary<string, object> fields, string key, string text, int limit, ref bool cut) => fields[key] = Clean(text, limit, ref cut);
    private static string Clean(string text, int limit, ref bool cut) {
        text = Redact(text ?? ""); if (text.Length <= limit) return text;
        cut = true; return Prefix(text, limit);
    }
    internal static string Prefix(string text, int limit) {
        if (text.Length <= limit) return text;
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }
    private static string Short(string value, int limit) => Prefix(value.Replace('\r', ' ').Replace('\n', ' ').Trim(), limit);
    private static string Redact(string value) {
        try { return Secrets.Replace(value, m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : "") + "[redacted]"); }
        catch (RegexMatchTimeoutException) { return "[redacted: content exceeded inspection limit]"; }
    }
    private static bool SecretKey(string key) {
        var normalized = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
        return normalized is "authorization" or "headers" or "cookie" or "setcookie" or "apikey" or "accesstoken" or "refreshtoken" or "idtoken" or "token" or "password" or "secret" or "clientsecret" or "credentials" or "privatekey";
    }
    private static object SafeValue(JsonElement value, int depth, ref bool cut) {
        if (depth > 12) { cut = true; return "[depth limit]"; }
        if (value.ValueKind == JsonValueKind.Object) {
            var output = new Dictionary<string, object>(); var count = 0;
            foreach (var entry in value.EnumerateObject()) {
                if (++count > 64) { cut = true; break; }
                var key = Clean(entry.Name, 256, ref cut);
                output[key] = SecretKey(key) || key is "data" or "base64" or "signature" or "additionalDetails" || key == "error" ? "[redacted]" : SafeValue(entry.Value, depth + 1, ref cut);
            }
            return output;
        }
        if (value.ValueKind == JsonValueKind.Array) {
            var output = new List<object>();
            foreach (var entry in value.EnumerateArray().Take(64)) output.Add(SafeValue(entry, depth + 1, ref cut));
            cut |= value.GetArrayLength() > 64; return output;
        }
        if (value.ValueKind == JsonValueKind.String) return Clean(value.GetString(), OutputLimit, ref cut);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : value.Clone();
    }
    private static string Render(JsonElement value, ref bool cut) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : JsonSerializer.Serialize(SafeValue(value, 0, ref cut));
    private static void CopyNumber(Dictionary<string, object> fields, string name, JsonElement value, bool nonnegative = false) {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && (!nonnegative || number >= 0) && Math.Abs(number) <= 9007199254740991d) fields[name] = number;
    }
    private static string Bounded(Dictionary<string, object> fields, int limit) {
        var text = JsonSerializer.Serialize(fields);
        if (text.Length <= limit) return text;
        fields["truncated"] = true;
        // Serialize after reducing individual values; never substring serialized JSON.
        for (var budget = Math.Max(16, limit / 12); ; budget = Math.Max(1, budget / 2)) {
            foreach (var key in TextFields) if (fields.TryGetValue(key, out var value) && value is string str) fields[key] = Prefix(str, budget);
            foreach (var key in new[] { "files", "children", "urls" }) if (fields.TryGetValue(key, out var value) && value is List<Dictionary<string, object>> rows) {
                if (rows.Count > 4) { rows = rows.Take(4).ToList(); fields[key] = rows; }
                foreach (var row in rows) foreach (var name in row.Keys.ToArray()) if (row[name] is string str) row[name] = Prefix(str, budget);
            }
            text = JsonSerializer.Serialize(fields); if (text.Length <= limit) return text;
            if (budget != 1) continue;
            foreach (var key in fields.Keys.Where(k => k is not ("schema" or "kind" or "truncated")).ToArray()) fields.Remove(key);
            text = JsonSerializer.Serialize(fields);
            if (text.Length > limit) { fields["kind"] = "tool"; text = JsonSerializer.Serialize(fields); }
            return text;
        }
    }
    private static bool Present(JsonElement value) => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    private static JsonElement FirstValue(JsonElement value, params string[] keys) { foreach (var key in keys) { var result = P(value, key); if (Present(result)) return result; } return default; }
    private static string FirstText(JsonElement value, params string[] keys) { foreach (var key in keys) { var text = S(value, key); if (text.Length > 0) return text; } return ""; }
    private static JsonElement P(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;
    private static string S(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.String ? P(value, key).GetString() ?? "" : "";
}
