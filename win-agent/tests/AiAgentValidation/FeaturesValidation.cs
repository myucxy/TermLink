using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class FeaturesValidation
{
    internal static async Task Real(AiAgentInstallation agent, string directory, string model, string image)
    {
        var caller = Guid.NewGuid();
        var archive = new AiSessionArchive(Path.Combine(directory, "real-archive"));
        AiSessionArchive.Record record;
        using (var session = new AiAgentSession(agent, directory, caller, model)) {
            session.AttachArchive(archive);
            await session.StartAsync();
            var effective = Snapshot(session, caller).GetProperty("modelId").GetString();
            var mode = agent.Protocol == "claude" ? "plan" : "read-only";
            await session.ApplyAsync(caller, new("configure", ControlVersion: 1, ModelId: effective, PermissionMode: mode, ExpectedSettingsVersion: 1));
            Console.WriteLine($"REAL {agent.Id}: native {mode} acknowledged");
            if (agent.Protocol == "claude" && model.Length > 0) {
                await session.ApplyAsync(caller, new("configure", ControlVersion: 1, ModelId: "", PermissionMode: mode, ExpectedSettingsVersion: 2));
                await session.ApplyAsync(caller, new("configure", ControlVersion: 1, ModelId: model, PermissionMode: mode, ExpectedSettingsVersion: 3));
                Console.WriteLine($"REAL {agent.Id}: default and explicit model switch acknowledged");
            }
            string attachment = null;
            if (image.Length > 0) {
                attachment = Guid.NewGuid().ToString(); var bytes = File.ReadAllBytes(image);
                await session.ApplyAsync(caller, new("attachment.begin", ControlVersion: 1, AttachmentId: attachment, FileName: "test-red-square.png", Mime: "image/png", Size: bytes.Length, Sha256: Convert.ToHexString(SHA256.HashData(bytes))));
                for (var offset = 0; offset < bytes.Length; offset += 24576) await session.ApplyAsync(caller, new("attachment.chunk", ControlVersion: 1, AttachmentId: attachment, Offset: offset, Data: Convert.ToBase64String(bytes, offset, Math.Min(24576, bytes.Length - offset))));
                await session.ApplyAsync(caller, new("attachment.commit", ControlVersion: 1, AttachmentId: attachment));
            }
            await session.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply exactly TERMLINK_AI_FEATURES_OK" + (image.Length > 0 ? " followed by the main color in the attached image." : ".") + " Do not use tools.", AttachmentIds: attachment == null ? null : new[] { attachment }));
            await RealIdle(session, caller);
            var result = Snapshot(session, caller);
            var reply = string.Join("\n", result.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "assistant").Select(m => m.GetProperty("text").GetString()));
            Require(reply.Contains("TERMLINK_AI_FEATURES_OK"), "Real marker response missing: " + result.GetProperty("error"));
            if (image.Length > 0) Require(reply.Contains("red", StringComparison.OrdinalIgnoreCase) || reply.Contains("红"), "Image not recognized");
            record = session.ArchiveRecord();
            Console.WriteLine($"REAL {agent.Id}: reply verified, image={image.Length > 0}");
        }
        using var recovered = AiAgentSession.Restore(agent, record, archive, caller);
        await recovered.ResumeAsync();
        await recovered.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Repeat only the exact TERMLINK marker from my previous message. Do not use tools."));
        await RealIdle(recovered, caller);
        Require(Snapshot(recovered, caller).GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "assistant").GetProperty("text").GetString().Contains("TERMLINK_AI_FEATURES_OK"), "Native resume lost context");
        Console.WriteLine($"REAL {agent.Id}: process restart and native context resume verified");
    }
    private static async Task RealIdle(AiAgentSession session, Guid caller) {
        for (var i = 0; i < 900; i++) {
            var value = Snapshot(session, caller);
            if (value.GetProperty("state").GetString() != "running") return;
            if (value.GetProperty("pendingCount").GetInt32() > 0) throw new Exception("Unexpected provider interaction");
            await Task.Delay(100);
        }
        throw new TimeoutException("Real provider turn");
    }
    internal static async Task Run(string directory)
    {
        var actor = Guid.NewGuid();
        foreach (var protocol in new[] { "codex", "claude" }) {
            Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", protocol);
            var installation = new AiAgentInstallation(protocol, protocol, Environment.ProcessPath, "", "1", protocol, "available");
            using var session = new AiAgentSession(installation, directory, actor);
            await session.StartAsync();
            await Reject(() => session.ApplyAsync(actor, new("configure", ControlVersion: 1, ModelId: "model-b", ExpectedSettingsVersion: 4)), "SETTINGS_CHANGED");
            await Reject(() => session.ApplyAsync(Guid.NewGuid(), new("configure", ControlVersion: 1, ExpectedSettingsVersion: 1)), "CONTROL_CHANGED");
            await session.ApplyAsync(actor, new("configure", ControlVersion: 1, ModelId: "model-b", ExpectedSettingsVersion: 1));
            Require(Snapshot(session, actor).GetProperty("modelId").GetString() == "model-b", "configuration not acknowledged");
            await Reject(() => session.ApplyAsync(actor, new("configure", ControlVersion: 1, ModelId: "model-b", ExpectedSettingsVersion: 1)), "SETTINGS_CHANGED");
            var mode = protocol == "codex" ? "read-only" : "plan";
            await session.ApplyAsync(actor, new("configure", ControlVersion: 1, ModelId: "model-b", PermissionMode: mode, ExpectedSettingsVersion: 2));
            await Reject(() => session.ApplyAsync(actor, new("configure", ControlVersion: 1, ModelId: "model-b", PermissionMode: "bypass", ExpectedSettingsVersion: 3)), "INVALID_PERMISSION_MODE");

            var bytes = Encoding.UTF8.GetBytes("user document: safe input");
            var id = Guid.NewGuid().ToString();
            var begin = new AiSessionManager.Request("attachment.begin", ControlVersion: 1, AttachmentId: id, FileName: "document.txt", Mime: "text/plain", Size: bytes.Length, Sha256: Convert.ToHexString(SHA256.HashData(bytes)));
            await session.ApplyAsync(actor, begin); await session.ApplyAsync(actor, begin);
            await Reject(() => session.ApplyAsync(actor, begin with { Size = 1 }), "ATTACHMENT_CONFLICT");
            var chunk = new AiSessionManager.Request("attachment.chunk", ControlVersion: 1, AttachmentId: id, Data: Convert.ToBase64String(bytes));
            await Reject(() => session.ApplyAsync(actor, chunk with { Offset = 1 }), "ATTACHMENT_OFFSET");
            await session.ApplyAsync(actor, chunk); await session.ApplyAsync(actor, chunk);
            await Reject(() => session.ApplyAsync(actor, chunk with { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("different")) }), "ATTACHMENT_CONFLICT");
            await session.ApplyAsync(actor, new("attachment.commit", ControlVersion: 1, AttachmentId: id));
            await session.ApplyAsync(actor, new("attachment.commit", ControlVersion: 1, AttachmentId: id));
            Require(JsonSerializer.SerializeToElement(session.AttachmentSnapshot(id), AiSessionManager.Json).GetProperty("ready").GetBoolean(), "commit not ready");
            await Reject(() => session.ApplyAsync(actor, new("send", ControlVersion: 1, Text: "hello", AttachmentIds: new[] { Guid.NewGuid().ToString() })), "ATTACHMENT_NOT_READY");
            await session.ApplyAsync(actor, new("send", ControlVersion: 1, AttachmentIds: new[] { id }, OperationId: Guid.NewGuid().ToString()));
            await WaitIdle(session, actor);
            var user = Snapshot(session, actor).GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
            Require(user.GetProperty("text").GetString().Contains("document.txt") && user.GetProperty("model").GetString() == "model-b" && user.GetProperty("mode").GetString() == mode, "attachment/model turn metadata");
            await Reject(() => session.ApplyAsync(actor, new("attachment.remove", ControlVersion: 1, AttachmentId: id)), "ATTACHMENT_IN_USE");
            var bad = begin with { AttachmentId = Guid.NewGuid().ToString(), Sha256 = new string('0', 64) };
            await session.ApplyAsync(actor, bad); await session.ApplyAsync(actor, chunk with { AttachmentId = bad.AttachmentId });
            await Reject(() => session.ApplyAsync(actor, new("attachment.commit", ControlVersion: 1, AttachmentId: bad.AttachmentId)), "ATTACHMENT_HASH");
            await Reject(() => session.ApplyAsync(actor, begin with { AttachmentId = "../escape" }), "INVALID_ATTACHMENT");
            await Reject(() => session.ApplyAsync(actor, begin with { AttachmentId = Guid.NewGuid().ToString(), Size = 21 * 1024 * 1024 }), "INVALID_ATTACHMENT");
            await session.ApplyAsync(actor, new("send", ControlVersion: 1, Text: "approval", OperationId: Guid.NewGuid().ToString()));
            await Reject(() => session.ApplyAsync(actor, new("configure", ControlVersion: 1, ModelId: "model-b", ExpectedSettingsVersion: 3, PermissionMode: mode)), "AGENT_BUSY");
            Console.WriteLine("PASS feature adapter " + protocol + ": settings CAS/control, native modes, attachment replay/hash/ownership/limits, attachment-only send, per-turn configuration");
        }
        var store = new AiSessionArchive(Path.Combine(directory, "archive"));
        var native = new AiAgentInstallation("codex", "Codex", Environment.ProcessPath, "", "1", "codex", "available");
        var record = new AiSessionArchive.Record(Guid.NewGuid().ToString(), "codex", directory, "thread", "private-title", "model-a", "", "ask", 3,
            Enumerable.Range(0, 200).Select(i => new AiAgentSession.Message("message-" + i, "user", "private-text-" + i)).ToArray());
        store.Save(record);
        var longRecord = record with { Id = Guid.NewGuid().ToString(), Messages = new[] { new AiAgentSession.Message("long", "assistant", new string('长', 65000)) } };
        using (var longSession = AiAgentSession.Restore(native, longRecord, null)) {
            var preview = Snapshot(longSession, actor).GetProperty("messages")[0];
            Require(preview.GetProperty("fullLength").GetInt32() == 65000 && preview.GetProperty("text").GetString().Length <= 16001, "bounded preview");
            var part = JsonSerializer.SerializeToElement(longSession.MessagePage("long", 32768), AiSessionManager.Json);
            Require(part.GetProperty("text").GetString().Length == 32232, "full message paging lost content");
        }
        Require(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "archive", record.Id + ".bin"))).Contains("private-text"), "archive plaintext leak");
        Require(store.Read().Single().Messages.Length == 200, "durable transcript");
        using var restored = AiAgentSession.Restore(native, store.Read().Single(), store);
        Require(!restored.IsAlive && Snapshot(restored, actor).GetProperty("state").GetString() == "suspended", "restore must not report running");
        var page = JsonSerializer.SerializeToElement(restored.History(136), AiSessionManager.Json);
        Require(page.GetProperty("before").GetInt32() == 72 && page.GetProperty("messages")[0].GetProperty("id").GetString() == "message-72", "history paging boundaries");
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "codex");
        using var resumed = AiAgentSession.Restore(native, record, store, actor);
        resumed.AdvanceControlFrom(restored);
        await resumed.ResumeAsync();
        Require(Snapshot(resumed, actor).GetProperty("state").GetString() == "idle", "native resume");
        await Reject(() => resumed.ApplyAsync(actor, new("send", ControlVersion: 1, Text: "stale")), "CONTROL_CHANGED");
        await resumed.ApplyAsync(actor, new("rename", ControlVersion: 2, Text: "renamed"));
        Require(store.Read().Single().Title == "renamed", "rename durable");
        var fork = await resumed.ForkAsync(actor, 2);
        Require(fork.Id != record.Id && fork.Messages.Length == 200, "fork loses context");
        Console.WriteLine("PASS archive: DPAPI, atomic save, paged transcript, suspended state, native resume, old controller fencing, rename, fork");
    }
    private static JsonElement Snapshot(AiAgentSession session, Guid actor) => JsonSerializer.SerializeToElement(session.Snapshot(actor, true), AiSessionManager.Json);
    private static void Require(bool value, string detail) { if (!value) throw new Exception(detail); }
    private static async Task Reject(Func<Task> action, string code) { try { await action(); } catch (AiOperationException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    private static async Task WaitIdle(AiAgentSession session, Guid actor) {
        for (var i = 0; i < 500; i++) { if (Snapshot(session, actor).GetProperty("state").GetString() == "idle") return; await Task.Delay(10); }
        throw new TimeoutException();
    }
}
