using System;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    /// <summary>
    /// Marks a failure that was observed at the physical realtime WebSocket
    /// boundary. Located session fault handling must never consume this signal.
    /// </summary>
    internal sealed class ProductionPhysicalRealtimeException : IOException
    {
        public ProductionPhysicalRealtimeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Separates a safely located session route from the independently owned
    /// device WebSocket. Peer payload, crypto, REST-authority, renderer and
    /// local IPC failures retire only that route. Physical I/O, account
    /// reauthentication, shared cancellation and process-fatal failures escape
    /// to their existing owners.
    /// </summary>
    internal static class ProductionSessionRouteFaultBoundary
    {
        public static async Task ExecuteAsync(
            Func<Task> sessionWork,
            Action<Exception> onSessionFault,
            CancellationToken sharedConnectionToken)
        {
            if (sessionWork == null) throw new ArgumentNullException(nameof(sessionWork));
            if (onSessionFault == null) throw new ArgumentNullException(nameof(onSessionFault));
            try
            {
                await sessionWork().ConfigureAwait(false);
            }
            catch (Exception error) when (IsSessionLocal(error, sharedConnectionToken))
            {
                onSessionFault(error);
            }
        }

        internal static bool IsSessionLocal(
            Exception error,
            CancellationToken sharedConnectionToken)
        {
            if (error == null ||
                error is ProductionPhysicalRealtimeException ||
                error is ProductionReauthenticationRequiredException ||
                error is OutOfMemoryException ||
                error is StackOverflowException ||
                error is AccessViolationException ||
                error is AppDomainUnloadedException ||
                error is BadImageFormatException ||
                error is AuthenticationException)
            {
                return false;
            }
            if (error is OperationCanceledException && sharedConnectionToken.IsCancellationRequested)
                return false;
            return true;
        }
    }
}
