using TermLink.Pc.Contracts;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent.Connection;

public sealed record RelayTerminalSession(Guid Id, string Shell, string Title, string State, int Columns, int Rows,
    ulong Sequence, DateTimeOffset CreatedAt, DateTimeOffset ActivityAt);
public sealed record RelayTerminalEvent(ulong Sequence, MessageType Type, byte[] Payload, bool RequiresSnapshot = false, Guid? Recipient = null);
public sealed record RelayTerminalCheckpoint(ulong Sequence, uint Columns, uint Rows, byte[] Bytes);

/// <summary>The authoritative PTY/core owner. Its lifetime is independent of a Relay connection.</summary>
public interface IRelayTerminalHost : IAsyncDisposable
{
    IReadOnlyList<TerminalCommandCapability> Commands { get; }
    IReadOnlyList<RelayTerminalSession> Sessions { get; }
    Task<Guid> StartAsync(string command, string directory, Guid operationId, CancellationToken token);
    Task StopAsync(Guid id, CancellationToken token);
    Task RecordInputResultAsync(Guid id, Guid recipient, InputResultPayload result, CancellationToken token);
    Task<RelayTerminalEvent?> RecoverAsync(Guid id, Guid recipient, ulong after, CancellationToken token);
    Task<RelayTerminalCheckpoint> CaptureAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<RelayTerminalEvent>?> ReadAsync(Guid id, ulong after, CancellationToken token);
    /// Replays retained source events through a committed native checkpoint.
    /// Intermediate parser/synchronized-output states are legitimate history.
    Task<IReadOnlyList<RelayTerminalEvent>?> ReadHistoryAsync(Guid id, ulong after, CancellationToken token);
    Task<string?> InputAsync(Guid id, Guid sender, InputEventPayload input, Func<Task<bool>> authority, CancellationToken token);
    Task<string?> ResizeAsync(Guid id, ResizePayload resize, Func<Task<bool>> authority, CancellationToken token);
}
