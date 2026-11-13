// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;

namespace TermLink.Pc.Contracts;

/// <summary>Literal POSIX command data; this class never starts an executable.</summary>
public static class PosixTerminalCommandRules
{
    public static IReadOnlyList<string> BuiltInIds { get; } = Array.AsReadOnly(new[] { "bash", "zsh", "fish", "pwsh", "sh" });
    public const int MaximumArguments = 32;
    public const int MaximumArgumentBytes = 4096;
    public const int MaximumCombinedArgumentBytes = 16384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static TerminalCommandCapability Validate(TerminalCommandCapability command)
    {
        if (command is null || command.Arguments is null) throw Invalid();
        Text(command.Id, 64, false);
        Text(command.DisplayName, 80, false);
        Text(command.Executable, PcContractLimits.MaximumDetailBytes, false);
        if (command.Id[0] is < 'a' or > 'z' || command.Id.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not ('-' or '_' or '.')) ||
            (command.Detected ? !BuiltInIds.Contains(command.Id, StringComparer.Ordinal) : !command.Id.StartsWith("custom-", StringComparison.Ordinal)) ||
            !command.Executable.StartsWith("/", StringComparison.Ordinal) || command.Executable.StartsWith("//", StringComparison.Ordinal) ||
            command.Executable.Split('/').Any(segment => segment is "." or "..") ||
            command.DisplayName.Trim() != command.DisplayName || command.DisplayName.Any(char.IsControl) || command.Executable.Any(char.IsControl) || command.RunAsAdministrator)
            throw Invalid();
        ValidateArguments(command.Arguments);
        return command with { Arguments = Array.AsReadOnly(command.Arguments.ToArray()), RunAsAdministrator = false };
    }

    public static IReadOnlyList<string> ParseArguments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        Text(text, 65536, true);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid();
            var values = new List<string>();
            foreach (var value in document.RootElement.EnumerateArray())
            {
                if (values.Count >= MaximumArguments || value.ValueKind != JsonValueKind.String) throw Invalid();
                values.Add(value.GetString()!);
            }
            ValidateArguments(values);
            return Array.AsReadOnly(values.ToArray());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new InvalidDataException("TERMINAL_COMMAND_ARGUMENTS_INVALID", error); }
    }

    public static string FormatArguments(IReadOnlyList<string> arguments)
    {
        ValidateArguments(arguments);
        return JsonSerializer.Serialize(arguments);
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        if (arguments is null || arguments.Count > MaximumArguments) throw Invalid();
        var bytes = 0;
        foreach (var argument in arguments) bytes += Text(argument, MaximumArgumentBytes, true);
        if (bytes > MaximumCombinedArgumentBytes) throw Invalid();
    }

    private static int Text(string? value, int maximum, bool empty)
    {
        if (value is null || (!empty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0')) throw Invalid();
        try { var bytes = StrictUtf8.GetByteCount(value); if (bytes > maximum) throw Invalid(); return bytes; }
        catch (EncoderFallbackException) { throw Invalid(); }
    }
    private static InvalidDataException Invalid() => new("TERMINAL_COMMAND_INVALID");
}
