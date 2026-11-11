using System.Net.WebSockets;
namespace TermLink.Agent;
internal sealed class FileOperationUploadTarget
{
    internal Guid TransferId { get; set; }
    internal Guid RequesterDeviceId { get; set; }
    internal string Protocol { get; set; } = "REALTIME_V2";
    internal string Filename { get; set; } = "";
    internal bool DirectoryZip { get; set; }
    internal long SizeBytes { get; set; }
    internal byte[] Sha256 { get; set; } = [];
}
internal interface IFileTransferSender { Task SendAsync(FileOperationUploadTarget target, Stream content, CancellationToken token); }
internal class FileOperationRemoteException(string message, bool retryable = true) : HttpRequestException(message)
{
    internal FileOperationRemoteException(string message, Exception inner, bool retryable) : this(message, retryable) { }
    internal bool Retryable { get; } = retryable;
}
internal sealed class ProductionReauthenticationRequiredException : Exception { }
