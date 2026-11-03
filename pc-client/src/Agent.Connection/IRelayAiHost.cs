namespace TermLink.Agent.Connection;

public interface IRelayAiHost : IDisposable
{
    Task<string> ExecuteAsync(Guid caller, string request, CancellationToken token, Func<bool> authorized);
}
