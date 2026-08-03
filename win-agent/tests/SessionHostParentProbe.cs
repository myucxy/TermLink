using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace TermLink.Validation
{
    internal static class SessionHostParentProbe
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 3) throw new ArgumentException("Expected Host path, encoded command, and iterations.");
                int iterations;
                if (!int.TryParse(args[2], out iterations) || iterations < 1 || iterations > 200)
                {
                    throw new ArgumentException("Iterations must be from 1 through 200.");
                }

                RunOnce(args[0], args[1]);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
                var before = CurrentHandleCount();
                var samples = new List<int>();
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    RunOnce(args[0], args[1]);
                    samples.Add(CurrentHandleCount());
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200);
                var after = CurrentHandleCount();
                Console.WriteLine(
                    "__PARENT_PROBE__:before=" + before +
                    ";after=" + after +
                    ";delta=" + (after - before) +
                    ";samples=" + string.Join(",", samples));
                return after - before <= 8 ? 0 : 125;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Session Host parent probe failed: " + error.Message);
                return 125;
            }
        }

        private static void RunOnce(string hostExecutable, string encodedCommand)
        {
            var start = new ProcessStartInfo
            {
                FileName = hostExecutable,
                Arguments = "--command-base64 " + encodedCommand + " --timeout-ms 5000 --diagnostics",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var process = Process.Start(start))
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                process.StandardInput.Close();
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new TimeoutException("Session Host did not stop within 10 seconds.");
                }
                var output = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult();
                outputTask.Dispose();
                errorTask.Dispose();
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                if (process.ExitCode != 0 || !output.Contains("__LOOP_OK__") ||
                    !error.Contains("__SESSION_HOST_RESULT__:reason=exited"))
                {
                    throw new InvalidOperationException(
                        "Session Host lifecycle failed: exit=" + process.ExitCode + ", output=" + output + error);
                }
            }
        }

        private static int CurrentHandleCount()
        {
            using (var process = Process.GetCurrentProcess()) return process.HandleCount;
        }
    }
}
