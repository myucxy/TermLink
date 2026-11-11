// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class AiWorkspaceFilesValidation
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Require(AiWorkspaceFiles.Available, "Packaged native AI file access is unavailable.");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "outside");
        var workspace = Path.Combine(root, "work"); Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "中文 files"));
        File.WriteAllText(Path.Combine(workspace, "中文 files", "result.md"), "result 中文");
        File.WriteAllText(Path.Combine(workspace, "literal\\name:part.txt"), "POSIX name");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "escape"), outside);
        File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), Path.Combine(outside, "private.txt"));
        foreach (var protocol in new[] { "codex", "claude", "zcode", "acp" }) {
            var owner = Guid.NewGuid();
            var installation = new AiAgentInstallation(protocol, protocol, "fixture", "", "1.0", protocol, "available");
            using var session = new AiAgentSession(installation, workspace, owner);
            var files = JsonSerializer.SerializeToElement(session.Artifacts(""));
            Require(files.GetProperty("files").EnumerateArray().Any(x => x.GetProperty("id").GetString() == "中文 files/result.md"), "Workspace file missing.");
            _ = session.ReadArtifact("literal\\name:part.txt", 0, "");
            var value = JsonSerializer.SerializeToElement(session.ReadArtifact("中文 files/result.md", 0, ""));
            Require(Encoding.UTF8.GetString(Convert.FromBase64String(value.GetProperty("data").GetString()!)) == "result 中文", "Artifact bytes differ.");
            foreach (var path in new[] { "../outside/private.txt", "escape/private.txt", "link.txt", "/etc/passwd", "中文 files/../../outside/private.txt", "中文 files\\..\\private.txt", "result.md:secret" })
                Reject(() => session.ReadArtifact(path, 0, ""));
            var payload = Encoding.UTF8.GetBytes("attachment " + protocol); var id = Guid.NewGuid().ToString();
            await session.ApplyAsync(owner, new("attachment.begin", AttachmentId:id, FileName:"note.txt", Mime:"text/plain", Size:payload.Length,
                Sha256:Convert.ToHexString(SHA256.HashData(payload)), ControlVersion:1));
            await session.ApplyAsync(owner, new("attachment.chunk", AttachmentId:id, Data:Convert.ToBase64String(payload), Offset:0, ControlVersion:1));
            // Idempotent retry must not append twice.
            await session.ApplyAsync(owner, new("attachment.chunk", AttachmentId:id, Data:Convert.ToBase64String(payload), Offset:0, ControlVersion:1));
            await session.ApplyAsync(owner, new("attachment.commit", AttachmentId:id, ControlVersion:1));
            var snapshot = JsonSerializer.SerializeToElement(session.AttachmentSnapshot(id));
            Require(snapshot.GetProperty("ready").GetBoolean(), "Attachment did not commit.");
            var ready = Path.Combine(workspace,".termlink","attachments",session.Id,id+".ready");
            Require(File.ReadAllBytes(ready).SequenceEqual(payload), "Attachment content changed.");
            Require((File.GetUnixFileMode(ready) & (UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite)) == 0, "Attachment permissions not private.");
            await session.ApplyAsync(owner, new("attachment.remove", AttachmentId:id, ControlVersion:1));
            Require(!File.Exists(ready), "Attachment removal failed.");
        }
        using (var lost = new AiAgentSession(new("codex","Codex","fixture","","1.0","codex","available"), workspace, Guid.Empty)) {
            var id=Guid.NewGuid().ToString();var bytes=Encoding.UTF8.GetBytes("lost");
            await lost.ApplyAsync(Guid.Empty,new("attachment.begin",AttachmentId:id,FileName:"lost.txt",Mime:"text/plain",Size:bytes.Length,Sha256:Convert.ToHexString(SHA256.HashData(bytes)),ControlVersion:1));
            Directory.Delete(Path.Combine(workspace,".termlink","attachments",lost.Id),true);
            await lost.ApplyAsync(Guid.Empty,new("attachment.remove",AttachmentId:id,ControlVersion:1));
            Require(lost.AttachmentSnapshot(id)==null,"Missing parent prevented cleanup");
        }
        // Existing untrusted attachment directories fail instead of being chmod'ed or followed.
        var unsafeWorkspace = Path.Combine(root,"unsafe"); Directory.CreateDirectory(unsafeWorkspace);
        Directory.CreateSymbolicLink(Path.Combine(unsafeWorkspace,".termlink"), outside);
        Reject(() => AiWorkspaceFiles.CreateAttachmentDirectory(unsafeWorkspace, Guid.NewGuid().ToString()));
        Console.WriteLine("PASS AI workspace files: all protocol families, POSIX artifacts, private/replayed attachments, traversal and symlink rejection");
    }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is AiOperationException or IOException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Unsafe path was accepted."); }
    private static void Require(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); }
}
