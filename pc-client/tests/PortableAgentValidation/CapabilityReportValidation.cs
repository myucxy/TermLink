using System.Text.Json;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;

internal static class CapabilityReportValidation
{
    public static void Run()
    {
        var id = new PcAgentIdentity(Guid.NewGuid(), "Mac fixture", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS", "0.3.16.146");
        var state = AgentStatusSnapshot.Disconnected(id, "", "") with
        {
            AccountLinked = true, RemoteAccessEnabled = false,
            StatusKnown = true,
            TerminalCommands = new[] { new TerminalCommandCapability("zsh", "Z shell", "/bin/zsh", new[] { "-l" }, true, false) },
        };
        var codex = new AiAgentCapability("codex", "Codex", "/opt/homebrew/bin/codex", "1.2.3", "detected", "local_cli")
            { Protocol = "codex", ProtocolStatus = "available" };
        var report = CapabilityReportFactory.Create(state, new[] { codex }, false, false);
        Require(report.Checks.Single(x => x.Id == "local_shells").Status == CapabilityCheckStatus.Available, "Detected Shell was missing.");
        Require(report.Checks.Where(x => x.Id is "remote_cli" or "files" or "remote_update").All(x => x.Status == CapabilityCheckStatus.NotIntegrated),
            "Local detection invented remote capabilities.");
        Require(report.Checks.Single(x => x.Id == "ai_auth").Status == CapabilityCheckStatus.Unknown && !report.Agents[0].RemoteExecutionAvailable,
            "A protocol help probe implied model authorization or remote integration.");
        var filesReady = CapabilityReportFactory.Create(state, new[] { codex }, false, false, aiFileAccessAvailable: true);
        var filesMissing = CapabilityReportFactory.Create(state, new[] { codex }, false, false, aiFileAccessAvailable: false);
        Require(filesReady.Checks.Single(x => x.Id == "ai_attachments").Status == CapabilityCheckStatus.Available &&
            filesMissing.Checks.Single(x => x.Id == "ai_attachments").Status == CapabilityCheckStatus.NotIntegrated &&
            report.Checks.Single(x => x.Id == "ai_attachments").Status == CapabilityCheckStatus.Unknown, "Mac file export availability was hardcoded or an old response invented support.");
        var linux = CapabilityReportFactory.Create(state with { Identity = id with { Platform = PcPlatform.Linux } }, new[] { codex }, false, false, true);
        Require(linux.Checks.Single(x => x.Id == "ai_attachments").Status == CapabilityCheckStatus.NotIntegrated, "Linux gained an unimplemented AI file backend.");
        Require(filesReady.Checks.Any(x => x.Id == "ai_native_history") && filesReady.Checks.Single(x => x.Id == "ai_persistence").Detail.Contains("未接入"), "Native history and archive support were conflated.");
        var windows = CapabilityReportFactory.Create(state with { Identity = id with { Platform = PcPlatform.Windows } }, new[] { codex }, false, false);
        Require(windows.Checks.Single(x => x.Id == "ai_attachments").Status == CapabilityCheckStatus.Limited, "Windows file capability presentation changed");
        var unknown = CapabilityReportFactory.Create(state with { StatusKnown = false }, new[] { codex }, false, false);
        Require(unknown.Checks.Where(x => x.Id is "remote_cli" or "files" or "remote_update").All(x => x.Status == CapabilityCheckStatus.Unknown),
            "Unknown backend state was confused with a missing implementation.");
        var wire = JsonSerializer.Serialize(report, LocalAgentProtocol.JsonOptions);
        var restored = JsonSerializer.Deserialize<CapabilityDetectionReport>(wire, LocalAgentProtocol.JsonOptions)!;
        CapabilityDetectionValidator.Validate(restored);
        Require(restored.Agents[0].ProtocolStatus == "available" && restored.Checks.Count == report.Checks.Count, "Report IPC JSON lost additive fields.");
        var legacy = JsonSerializer.Deserialize<AiAgentCapability>("{\"id\":\"codex\",\"name\":\"Codex\",\"path\":\"/bin/codex\",\"version\":\"1\",\"status\":\"detected\",\"strategy\":\"local_cli\"}", LocalAgentProtocol.JsonOptions)!;
        Require(legacy.AuthenticationStatus == "not_checked" && legacy.ProtocolStatus == "not_checked" && !legacy.RemoteExecutionAvailable,
            "An older Agent response invented protocol or authentication results.");
        foreach (var invalid in new[]
        {
            report with { Platform = PcPlatform.Unknown },
            report with { Checks = report.Checks.Concat(report.Checks.Take(1)).ToArray() },
            report with { Checks = new[] { new CapabilityCheck("bad", "test", "bad", (CapabilityCheckStatus)100, "") } },
            report with { Agents = new[] { codex with { Path = "/bin\0/injected" } } },
        })
        {
            try { CapabilityDetectionValidator.Validate(invalid); throw new InvalidOperationException("Invalid report accepted."); }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("PASS capability report: no fabricated remote/auth states, legacy fallback, wire roundtrip and invalid-payload bounds");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
