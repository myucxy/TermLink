using System.Text.Json;
namespace TermLink.Agent.Connection;
public interface IRelayFileHost : IAsyncDisposable
{
    Task<JsonElement> ListAsync(string path, long offset, string query, CancellationToken token);
    Task<RelayFileArtifact> PrepareAsync(Guid operationId, string path, CancellationToken token);
    Task CleanupAsync(Guid operationId, CancellationToken token);
}
public abstract class RelayFileArtifact : IAsyncDisposable
{
    public abstract string Filename { get; }
    public abstract bool DirectoryZip { get; }
    public abstract long SizeBytes { get; }
    public abstract byte[] Sha256 { get; }
    public abstract Stream Content { get; }
    public abstract ValueTask DisposeAsync();
}
public sealed class RelayFileException(string code) : IOException(code) { public string Code { get; } = code; }
