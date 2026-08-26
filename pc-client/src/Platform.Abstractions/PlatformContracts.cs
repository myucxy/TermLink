using TermLink.Pc.Contracts;

namespace TermLink.Platform;

public sealed record PlatformDescriptor(
    PcPlatform Platform,
    PcArchitecture Architecture,
    string PlatformVersion,
    string RuntimeIdentifier,
    bool IsPackaged);

public sealed record TerminalLaunchRequest(
    string CommandId,
    string WorkingDirectory,
    int Columns,
    int Rows,
    IReadOnlyDictionary<string, string> Environment);

public sealed record TerminalLaunchResult(Guid SessionId, int ProcessId, string ShellType);

public sealed record FileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModifiedAt);

public sealed record UpdatePackage(
    string Version,
    long BuildNumber,
    Uri DownloadUri,
    long Size,
    string Sha256,
    string SignatureIdentity,
    string PackageKind);

public interface ISecretStore
{
    bool IsAvailable { get; }
    ValueTask StoreAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken);
    ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken);
}
public interface ILocalIpcTransport
{
    ValueTask<Stream> ConnectAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken);
    ValueTask<ILocalIpcListener> ListenAsync(string endpoint, CancellationToken cancellationToken);
}

public interface ILocalIpcListener : IAsyncDisposable
{
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}

public interface ITerminalSessionHost
{
    ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken cancellationToken);
    ValueTask ResizeAsync(Guid sessionId, int columns, int rows, CancellationToken cancellationToken);
    ValueTask SendInputAsync(Guid sessionId, ReadOnlyMemory<byte> input, CancellationToken cancellationToken);
    ValueTask StopAsync(Guid sessionId, CancellationToken cancellationToken);
}

public interface IFileSystemOperations
{
    ValueTask<IReadOnlyList<FileSystemEntry>> ListAsync(string path, CancellationToken cancellationToken);
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
}

public interface ITerminalCommandDiscovery
{
    ValueTask<IReadOnlyList<TerminalCommandCapability>> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IAutostartManager
{
    ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken);
    ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

public interface IUpdateInstaller
{
    ValueTask VerifyAndStageAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken);
    ValueTask LaunchAsync(UpdatePackage package, string localPath, CancellationToken cancellationToken);
    ValueTask RollbackAsync(CancellationToken cancellationToken);
}

public interface IProcessSupervisor
{
    ValueTask EnsureAgentRunningAsync(CancellationToken cancellationToken);
    ValueTask<bool> IsAgentRunningAsync(CancellationToken cancellationToken);
}

public interface IPlatformNotification
{
    ValueTask ShowAsync(string title, string message, CancellationToken cancellationToken);
}

public interface IPlatformServices
{
    PlatformDescriptor Descriptor { get; }
    ISecretStore SecretStore { get; }
    ILocalIpcTransport LocalIpc { get; }
    ITerminalSessionHost TerminalHost { get; }
    IFileSystemOperations FileSystem { get; }
    ITerminalCommandDiscovery TerminalCommands { get; }
    IAutostartManager Autostart { get; }
    IUpdateInstaller Updates { get; }
    IProcessSupervisor Processes { get; }
    IPlatformNotification Notifications { get; }
}
