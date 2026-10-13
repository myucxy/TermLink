using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Agent
{
    /// Local, user-protected failure evidence. Never included in server metadata or telemetry.
    public sealed class TerminalFailureStore
    {
        private const int MaximumTextBytes = 10240;
        private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("TermLink terminal diagnostic v1");
        private readonly string directory;

        public TerminalFailureStore(string directory = null)
        {
            this.directory = Path.GetFullPath(directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink", "TerminalFailures"));
        }

        public bool TryWrite(Guid session, string diagnostic)
        {
            if (session == Guid.Empty || !ValidDiagnostic(diagnostic)) return false;
            string temporary = null;
            byte[] plain = null;
            try
            {
                Directory.CreateDirectory(directory);
                Prune();
                var destination = Path.Combine(directory, session.ToString("N") + ".dpapi");
                if (File.Exists(destination)) return true; // Keep the first failure for this unique session.
                var text = "source=windows_authority\nsession=" + session.ToString("D") +
                    "\nobserved_at=" + DateTimeOffset.UtcNow.ToString("O") +
                    "\nhost_action=stopping_input_and_session_bridge\n\n" + diagnostic;
                plain = Encoding.UTF8.GetBytes(text);
                var encrypted = ProtectedData.Protect(plain, Purpose, DataProtectionScope.CurrentUser);
                temporary = Path.Combine(directory, session.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".pending");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(encrypted); output.Flush(true); }
                File.Move(temporary, destination);
                temporary = null;
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is CryptographicException)
            { return false; }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }

        public bool TryWriteInput(Guid session, string diagnostic)
        {
            if (session == Guid.Empty || !ValidInputDiagnostic(diagnostic)) return false;
            string temporary = null;
            byte[] plain = null;
            try
            {
                Directory.CreateDirectory(directory);
                Prune();
                var destination = Path.Combine(directory, session.ToString("N") + ".input.dpapi");
                var text = "source=windows_input\nsession=" + session.ToString("D") +
                    "\nobserved_at=" + DateTimeOffset.UtcNow.ToString("O") +
                    "\nhost_action=rejected_input_kept_session\n\n" + diagnostic;
                plain = Encoding.UTF8.GetBytes(text);
                var encrypted = ProtectedData.Protect(plain, Purpose, DataProtectionScope.CurrentUser);
                temporary = Path.Combine(directory, session.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".input.pending");
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                { output.Write(encrypted); output.Flush(true); }
                File.Move(temporary, destination, true);
                temporary = null;
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is CryptographicException)
            { return false; }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }

        public string Read(Guid session) => Read(session, true);

        private string Read(Guid session, bool includeInputFallback)
        {
            if (session == Guid.Empty) return null;
            byte[] plain = null;
            try
            {
                var file = new FileInfo(Path.Combine(directory, session.ToString("N") + ".dpapi"));
                if (!file.Exists || file.Length > 16 * 1024 || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                    return includeInputFallback ? ReadInput(session) : null;
                byte[] encrypted;
                using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > 16 * 1024) return null;
                    encrypted = new byte[checked((int)input.Length)];
                    input.ReadExactly(encrypted);
                }
                plain = ProtectedData.Unprotect(encrypted, Purpose, DataProtectionScope.CurrentUser);
                if (plain.Length > MaximumTextBytes) return null;
                var text = new UTF8Encoding(false, true).GetString(plain);
                var split = text.IndexOf("\n\n", StringComparison.Ordinal);
                return split > 0 && text.StartsWith("source=windows_authority\nsession=" + session.ToString("D") + "\n", StringComparison.Ordinal)
                    && text.All(c => c == '\n' || c >= 32 && c <= 126) && ValidDiagnostic(text.Substring(split + 2)) ? text : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is CryptographicException || error is DecoderFallbackException)
            { return null; }
            finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
        }

        public string ReadAuthorityBody(Guid session)
        {
            var text = Read(session, false);
            var prefix = "source=windows_authority\nsession=" + session.ToString("D") + "\n";
            if (text == null || !text.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            var body = split > 0 ? text.Substring(split + 2) : null;
            return body != null && body.StartsWith("TL-DIAG/", StringComparison.Ordinal) && ValidDiagnostic(body)
                ? body : null;
        }

        private string ReadInput(Guid session)
        {
            byte[] plain = null;
            try
            {
                var file = new FileInfo(Path.Combine(directory, session.ToString("N") + ".input.dpapi"));
                if (!file.Exists || file.Length > 16 * 1024 || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) return null;
                byte[] encrypted;
                using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > 16 * 1024) return null;
                    encrypted = new byte[checked((int)input.Length)];
                    input.ReadExactly(encrypted);
                }
                plain = ProtectedData.Unprotect(encrypted, Purpose, DataProtectionScope.CurrentUser);
                if (plain.Length > MaximumTextBytes) return null;
                var text = new UTF8Encoding(false, true).GetString(plain);
                var split = text.IndexOf("\n\n", StringComparison.Ordinal);
                return split > 0 && text.StartsWith("source=windows_input\nsession=" + session.ToString("D") + "\n", StringComparison.Ordinal) &&
                    text.All(c => c == '\n' || c >= 32 && c <= 126) && ValidInputDiagnostic(text.Substring(split + 2)) ? text : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is CryptographicException || error is DecoderFallbackException)
            { return null; }
            finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
        }

        private static bool ValidDiagnostic(string text) => text != null &&
            ((text.Length <= 8192 && text.StartsWith("TL-DIAG/3\n", StringComparison.Ordinal)) ||
            (text.Length <= 4096 && (text.StartsWith("TL-DIAG/1\n", StringComparison.Ordinal) || text.StartsWith("TL-DIAG/2\n", StringComparison.Ordinal))) ||
            (text.Length <= 2048 && text.StartsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n", StringComparison.Ordinal))) &&
            text.EndsWith("\n", StringComparison.Ordinal) && text.All(c => c == '\n' || c >= 32 && c <= 126);

        private void Prune()
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("*.dpapi")
                .Where(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _))
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            for (var i = 0; i < files.Length; i++)
                if (i >= 255 || files[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) files[i].Delete();
            var inputFiles = new DirectoryInfo(directory).EnumerateFiles("*.input.dpapi")
                .Where(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name).Replace(".input", "", StringComparison.Ordinal), "N", out _))
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            for (var i = 0; i < inputFiles.Length; i++)
                if (i >= 255 || inputFiles[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) inputFiles[i].Delete();
        }

        private static bool ValidInputDiagnostic(string text) => text != null && text.Length <= 2048 &&
            text.StartsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n", StringComparison.Ordinal) && text.EndsWith("\n", StringComparison.Ordinal) &&
            text.All(c => c == '\n' || c >= 32 && c <= 126);
    }
}
