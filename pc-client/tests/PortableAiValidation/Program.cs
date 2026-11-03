// SPDX-License-Identifier: GPL-3.0-only
if (args.Contains("app-server", StringComparer.Ordinal))
{
    await PortableAiFixture.RunAsync();
    return;
}
if (args.Length == 2 && args[0] == "--real-isolated")
{
    await RealZcodeValidation.RunAsync(Path.GetFullPath(args[1]));
    return;
}
if (args.Length is 1 or 2 && args[0] == "--real-zcode")
{
    var evidence = args.Length == 2 ? args[1] : Path.Combine(Path.GetTempPath(), "termlink-real-zcode-" + Guid.NewGuid().ToString("N"));
    if (!Path.IsPathFullyQualified(evidence) || Directory.Exists(evidence) || File.Exists(evidence))
        throw new ArgumentException("Real runtime evidence requires a new absolute directory.");
    Directory.CreateDirectory(evidence);
    await RealZcodeValidation.RunSandboxedAsync(evidence);
    Console.WriteLine("Real runtime sanitized evidence directory: " + evidence);
    return;
}
if (args.Length != 0)
    throw new ArgumentException("Usage: TermLink.PortableAI.Validation [--real-zcode [new-absolute-evidence-directory]]");
var root = Path.Combine(Path.GetTempPath(), "termlink-portable-ai-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try { await PortableAiFixtureValidation.RunAsync(root); }
finally { Directory.Delete(root, recursive: true); }
