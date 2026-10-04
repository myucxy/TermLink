using System.Text;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class ArtifactValidation
{
    internal static void Run(string directory)
    {
        var root = Path.Combine(directory, "artifact-workspace"); Directory.CreateDirectory(root);
        using var session = new AiAgentSession(new AiAgentInstallation("codex", "Codex", Environment.ProcessPath!, "", "1", "codex", "available"), root, Guid.NewGuid());
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><p>安全预览</p>");
        var expected = Encoding.UTF8.GetBytes(new string('中', 50000));
        File.WriteAllBytes(Path.Combine(root, "large.txt"), expected);
        Directory.CreateDirectory(Path.Combine(root, ".git")); File.WriteAllText(Path.Combine(root, ".git", "secret"), "not visible");
        var files = JsonSerializer.SerializeToElement(session.Artifacts(""), AiSessionManager.Json).GetProperty("files");
        Check(files.GetArrayLength() == 2 && files.EnumerateArray().Any(f => f.GetProperty("mime").GetString() == "text/html"), "Artifact listing scope/MIME");
        Check(JsonSerializer.SerializeToElement(session.Artifacts("LARGE"), AiSessionManager.Json).GetProperty("files").GetArrayLength() == 1, "Artifact search");
        var first = Read(session, "large.txt", 0, ""); var version = first.GetProperty("version").GetString()!;
        using var output = new MemoryStream(); long offset = 0;
        do {
            var block = Read(session, "large.txt", offset, version);
            var bytes = Convert.FromBase64String(block.GetProperty("data").GetString()!);
            Check(bytes.Length <= 65536 && block.GetProperty("offset").GetInt64() == offset, "Chunk bounds");
            output.Write(bytes); offset = block.GetProperty("nextOffset").GetInt64();
        } while (offset < expected.Length);
        Check(output.ToArray().SequenceEqual(expected), "Artifact bytes changed across chunks");
        Reject(() => session.ReadArtifact("large.txt", -1, version));
        Reject(() => session.ReadArtifact("large.txt", expected.Length + 1, version));
        Reject(() => session.ReadArtifact("../outside.txt", 0, ""));
        Reject(() => session.ReadArtifact(Path.Combine(root, "index.html"), 0, ""));
        Reject(() => session.ReadArtifact("index.html:stream", 0, ""));
        Reject(() => session.ReadArtifact(".git/secret", 0, ""));
        File.AppendAllText(Path.Combine(root, "large.txt"), "changed");
        Reject(() => session.ReadArtifact("large.txt", 65536, version));
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
        Check(Read(session, "empty.txt", 0, "").GetProperty("data").GetString() == "", "Empty artifact");
        using (var large = File.Create(Path.Combine(root, "too-large.bin"))) large.SetLength(50L * 1024 * 1024 + 1);
        Reject(() => session.ReadArtifact("too-large.bin", 0, ""));
        var outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "private.txt"), "private");
        try {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            Reject(() => session.ReadArtifact("linked/private.txt", 0, ""));
            Check(!JsonSerializer.Serialize(session.Artifacts(""), AiSessionManager.Json).Contains("private.txt"), "Linked file listed");
            Console.WriteLine("PASS artifact symlink/reparse exclusion");
        } catch (UnauthorizedAccessException) { Console.WriteLine("SKIP artifact symlink creation requires Windows developer privilege"); }
        Console.WriteLine("PASS artifacts: workspace scope, MIME, chunk integrity, version changes, traversal/ADS, size and empty files");
    }
    private static JsonElement Read(AiAgentSession s, string id, long offset, string version) => JsonSerializer.SerializeToElement(s.ReadArtifact(id, offset, version), AiSessionManager.Json);
    private static void Reject(Action action) { try { action(); } catch (AiOperationException) { return; } throw new Exception("Unsafe artifact accepted"); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
