using System;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    /// <summary>
    /// Starts REST workers only after the PC registration transaction has
    /// completed. The gate is retry-safe and can be retired concurrently with
    /// registration during account switching or Agent shutdown.
    /// </summary>
    internal sealed class PostRegistrationWorkerStartGate
    {
        private readonly object sync = new object();
        private readonly Action[] starters;
        private int nextStarter;
        private bool retired;

        internal PostRegistrationWorkerStartGate(params Action[] starters)
        {
            if (starters == null || starters.Length == 0)
                throw new ArgumentException("At least one post-registration worker is required.", nameof(starters));
            if (Array.Exists(starters, value => value == null))
                throw new ArgumentException("A post-registration worker starter is missing.", nameof(starters));
            this.starters = (Action[])starters.Clone();
        }

        internal async Task EnsureRegisteredThenStartAsync(
            Func<CancellationToken, Task> ensureRegistered,
            CancellationToken token)
        {
            if (ensureRegistered == null) throw new ArgumentNullException(nameof(ensureRegistered));
            await ensureRegistered(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            StartOnce();
        }

        private void StartOnce()
        {
            lock (sync)
            {
                if (retired) return;
                while (nextStarter < starters.Length)
                {
                    starters[nextStarter]();
                    nextStarter++;
                }
            }
        }

        internal void Retire()
        {
            lock (sync) retired = true;
        }
    }
}
