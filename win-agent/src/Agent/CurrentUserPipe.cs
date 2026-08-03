using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TermLink.Agent
{
    internal static class CurrentUserPipe
    {
        public static NamedPipeServerStream Create(
            string pipeName,
            int maximumInstances,
            PipeOptions options,
            int inputBufferBytes,
            int outputBufferBytes)
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity.User == null) throw new InvalidOperationException("Current Windows SID is unavailable.");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                identity.User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
#if NET8_0_OR_GREATER
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maximumInstances,
                PipeTransmissionMode.Byte,
                options,
                inputBufferBytes,
                outputBufferBytes,
                security);
#else
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maximumInstances,
                PipeTransmissionMode.Byte,
                options,
                inputBufferBytes,
                outputBufferBytes,
                security);
#endif
        }
    }
}
