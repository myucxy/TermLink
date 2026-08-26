using System.Security.Cryptography;
using TermLink.Agent.Local;
using TermLink.Agent.Host;
using TermLink.Platform;
using TermLink.Platform.Linux;
using TermLink.Platform.MacOS;

if (!args.Contains("--agent", StringComparer.Ordinal))
{
    Console.Error.WriteLine("TermLink.Agent is an independent background process and requires --agent.");
    return 2;
}

var endpoint = RequiredOption(args, "--ipc");
IPlatformServices platform;
IAsyncDisposable? platformLifetime;
if (OperatingSystem.IsLinux())
{
    var options = LinuxPlatformOptions.CreateDefault() with { IpcEndpoint = endpoint };
    var linux = new LinuxPlatformServices(options);
    platform = linux;
    platformLifetime = linux;
}
else if (OperatingSystem.IsMacOS())
{
    var options = MacPlatformOptions.CreateDefault() with { IpcEndpoint = endpoint };
    var mac = new MacPlatformServices(options);
    platform = mac;
    platformLifetime = mac;
}
else
{
    Console.Error.WriteLine("The portable Agent Host supports Linux and macOS. Windows uses the production Windows Agent.");
    return 3;
}

try
{
    if (!platform.SecretStore.IsAvailable)
    {
        Console.Error.WriteLine("A secure desktop credential store is required; plaintext Agent keys are forbidden.");
        return 12;
    }
    var key = await platform.SecretStore.RetrieveAsync("local-ipc-authentication-v1", CancellationToken.None);
    if (key is null || key.Length != LocalAgentProtocol.AuthenticationKeyBytes)
    {
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        Console.Error.WriteLine("The local Agent authentication key is missing or invalid. Start the TermLink UI once to provision it.");
        return 13;
    }
    try
    {
        var handler = await PortableAgentRuntime.CreateAsync(platform, CancellationToken.None);
        await using var server = new LocalAgentServer(platform.LocalIpc, endpoint, key, handler);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
        await server.RunAsync(shutdown.Token);
        return 0;
    }
    finally { CryptographicOperations.ZeroMemory(key); }
}
finally
{
    if (platformLifetime is not null) await platformLifetime.DisposeAsync();
}

static string RequiredOption(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    if (index < 0 || index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
        throw new ArgumentException($"The required option {name} is missing.");
    return Path.GetFullPath(arguments[index + 1]);
}
