using System.Reflection;

namespace TermLink.Desktop;

public static class DesktopProductInfo
{
    public const string Author = "清风惹尘丶";

    public static string Version { get; } = ResolveVersion();

    public static string Title => $"TermLink PC · v{Version} · {Author}";

    private static string ResolveVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version ??
            typeof(DesktopProductInfo).Assembly.GetName().Version;
        return version?.ToString(4) ?? "1.0.0.0";
    }
}
