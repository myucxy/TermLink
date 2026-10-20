using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TermLink.UpdateCore
{
    public static class InstallRootLock
    {
        public static IDisposable Acquire(string installRoot)
        {
            var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
            var mutex = new Mutex(false, "Local\\TermLink.Install." + key);
            try {
                try {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("Another TermLink installer is modifying this installation.");
                } catch (AbandonedMutexException) { }
                return new Held(mutex);
            } catch { mutex.Dispose(); throw; }
        }
        private sealed class Held : IDisposable
        {
            private Mutex mutex;
            public Held(Mutex value) { mutex = value; }
            public void Dispose() { var owned = Interlocked.Exchange(ref mutex, null); if (owned != null) { owned.ReleaseMutex(); owned.Dispose(); } }
        }
    }
}
