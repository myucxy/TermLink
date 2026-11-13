namespace TermLink.Pc.Contracts;

/// <summary>Displays installed-build facts without adding any server capability.</summary>
public static class CapabilityReportFactory
{
    public static CapabilityDetectionReport Create(AgentStatusSnapshot state, IReadOnlyList<AiAgentCapability> agents,
        bool remoteSessions, bool commandEditing, bool? aiFileAccessAvailable = null)
    {
        var portable = state.Identity.Platform is PcPlatform.MacOS or PcPlatform.Linux;
        var checks = new List<CapabilityCheck>();
        bool Has(string id) => state.StatusKnown && state.Capabilities.Contains(id);
        checks.Add(new("account", "连接", "TermLink 账号授权", !state.StatusKnown ? CapabilityCheckStatus.Unknown : state.AccountLinked && state.ConnectionState!=AgentConnectionState.ReauthenticationRequired ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NeedsAuthorization,
            !state.StatusKnown ? "后台状态无法确认，请重新检测。" : state.ConnectionState==AgentConnectionState.ReauthenticationRequired ? "登录授权已过期或失效，请在账号页重新授权。" : state.AccountLinked ? "当前后台已加载登录状态；令牌有效性由后续连接与刷新确认。" : "请先在账号页通过浏览器授权登录。", "account"));
        checks.Add(new("relay", "连接", "服务器 Relay", !state.StatusKnown ? CapabilityCheckStatus.Unknown : state.ConnectionState == AgentConnectionState.Connected ? CapabilityCheckStatus.Available : CapabilityCheckStatus.Limited,
            !state.StatusKnown ? "后台状态无法确认。" : !state.AccountLinked ? "登录后启用服务器连接。" : !state.RemoteAccessEnabled ? "已登录，连接未启用。" : state.ConnectionState == AgentConnectionState.Connected ? "当前 Relay 已连接；各项远程操作仍受服务器策略与设备权限限制。" : "连接尚未就绪，请查看账号页和诊断。"));
        checks.Add(new("local_shells", "终端", "本机 Shell", state.TerminalCommands.Any(x => x.Enabled && x.Detected) ? CapabilityCheckStatus.Available : state.TerminalCommands.Any(x=>x.Enabled) ? CapabilityCheckStatus.Limited : CapabilityCheckStatus.Missing,
            state.TerminalCommands.Any(x => x.Enabled) ? $"已配置 {state.TerminalCommands.Count(x => x.Enabled)} 项本机命令，其中 {state.TerminalCommands.Count(x=>x.Enabled && x.Detected)} 项由自动检测发现；自定义项仍需实际启动确认。" : "未发现可用的本机 Shell，可在命令页重新检测。", state.Identity.Platform==PcPlatform.MacOS ? "OpenTerminal" : ""));
        checks.Add(new("remote_cli", "终端", "手机远程 CLI", Has(PcCapabilityIds.RealtimeTerminalV1) && remoteSessions ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated,
            Has(PcCapabilityIds.RealtimeTerminalV1) && remoteSessions ? "此 Agent 已提供远程终端链路；具体会话仍需连接和控制租约。" : "当前构建尚未接通远程终端数据链路；本机 PTY 和 Shell 检测不能替代它。"));
        checks.Add(new("terminal_snapshots", "终端", "终端核心与断线恢复", Has(PcCapabilityIds.TerminalSnapshotV1) || Has("terminal_snapshot_v2") ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated,
            Has(PcCapabilityIds.TerminalSnapshotV1) || Has("terminal_snapshot_v2") ? "Agent 已声明快照恢复能力，端到端恢复需会话验证。" : "当前 Agent 未声明已接入的终端核心快照能力。"));
        checks.Add(new("command_editing", "终端", "自定义启动命令", commandEditing ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated,
            commandEditing ? "此平台提供命令编辑与保存入口。" : "此构建仅检测内置 Shell，尚未接入可持久化的自定义启动命令。"));
        checks.Add(new("files", "文件", "远程文件访问", Has(PcCapabilityIds.FileOperationsV1) ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated,
            Has(PcCapabilityIds.FileOperationsV1) ? "Agent 已声明远程文件能力；目录权限按实际访问结果确认。" : "当前构建未接入手机文件浏览、下载和传输。"));
        checks.Add(new("ai_remote", "AI", "原生 AI 远程链路", agents.Any(x => x.RemoteExecutionAvailable) ? CapabilityCheckStatus.Limited : CapabilityCheckStatus.NotIntegrated,
            agents.Any(x => x.RemoteExecutionAvailable) ? "部分 Agent 已接入协议链路；逐项查看下方远程状态，模型认证和真实调用仍需验证。" : "本机安装与协议检测独立于远程执行，当前没有检测到已接入且可启动的 Agent。"));
        checks.Add(new("ai_auth", "AI", "AI 模型授权", CapabilityCheckStatus.Unknown,
            "本次不读取 Codex、Claude、ZCode 等第三方凭据，也不调用模型；是否登录需在对应应用或首次会话确认。"));
        checks.Add(new("remote_update", "更新", "手机触发客户端升级", Has(PcCapabilityIds.RemoteAgentUpdateV1) ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated,
            Has(PcCapabilityIds.RemoteAgentUpdateV1) ? "Agent 已声明升级任务处理能力；安装仍受签名校验与系统授权限制。" : "服务端接口存在不代表此 Agent 已接入远程升级任务。"));
        checks.Add(new("device_management", "设备", "设备授权管理", portable ? CapabilityCheckStatus.NotIntegrated : CapabilityCheckStatus.Available,
            portable ? "此构建尚未接入 Grant / Revoke 操作，请在服务网页管理设备。" : "此客户端提供设备授权与撤销入口。"));
        checks.Add(new("terminal_graphics", "终端", "图形终端与 Broker 捕获", portable ? CapabilityCheckStatus.NotIntegrated : CapabilityCheckStatus.Unknown,
            portable ? "当前构建未接入 macOS / Linux 的图形核心、Broker 捕获和远程渲染链路。" : "此状态接口没有完整会话协商结果，需在具体终端会话中确认。"));
        checks.Add(new("semantic_input", "终端", "高级键鼠与终端副作用", portable ? CapabilityCheckStatus.NotIntegrated : CapabilityCheckStatus.Unknown,
            portable ? "远程键鼠语义、进度、提示音和剪贴板策略尚未接入本构建。" : "键鼠、提示音和剪贴板受会话能力与平台策略限制，需单独验证。"));
        var aiFiles = state.Identity.Platform == PcPlatform.Windows || state.Identity.Platform == PcPlatform.MacOS && aiFileAccessAvailable == true;
        var fileStatus = state.Identity.Platform == PcPlatform.Windows ? CapabilityCheckStatus.Limited :
            state.Identity.Platform == PcPlatform.MacOS && aiFileAccessAvailable is null ? CapabilityCheckStatus.Unknown :
            aiFiles ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated;
        checks.Add(new("ai_attachments", "AI", "AI 附件与产物文件", fileStatus,
            state.Identity.Platform == PcPlatform.Windows ? "Windows 已有文件接口，具体 Agent 和会话是否支持以实际能力为准。" :
            aiFiles ? "此构建已加载附件、电脑文件引用和产物文件后端；目录权限及具体 Agent 支持仍以会话能力和实际操作结果为准。" :
            fileStatus == CapabilityCheckStatus.Unknown ? "AI 文件后端状态未提供，请重新检测。" : "此构建没有可用的 AI 文件后端；原生文件库缺失或缺少所需导出时不会启用附件。"));
        checks.Add(new("ai_native_history", "AI", "Agent 原生历史与续聊", agents.Any(a => a.Strategy == "native" && a.Status == "available") ? CapabilityCheckStatus.Limited : CapabilityCheckStatus.Unknown,
            "按 Agent 原生协议和版本提供有界历史读取、导入或续聊；安装和协议检测不读取历史，也不保证每个 Agent 支持。"));
        checks.Add(new("ai_persistence", "AI", "TermLink AI 持久归档", portable ? CapabilityCheckStatus.NotIntegrated : CapabilityCheckStatus.Limited,
            portable ? "当前 Mac / Linux Host 未接入 TermLink 持久归档；原生 Agent 数据目录与手机缓存不等于 Host 重启后自动恢复。" : "Windows 已接入账号范围内的归档；保存、恢复及原生续聊仍需逐会话验证。"));
        if (portable)
            checks.Add(new("routing_policy", "连接", "本机开关与服务端路由", CapabilityCheckStatus.Limited,
                "本机开关控制后台连接，当前客户端尚未同步新的服务端路由开关接口；还需确认管理中心的远程路由与接收设备权限。"));
        if (!state.StatusKnown)
        {
            for (var index = 0; index < checks.Count; index++)
                if (checks[index].Id is "remote_cli" or "terminal_snapshots" or "files" or "remote_update")
                    checks[index] = checks[index] with { Status = CapabilityCheckStatus.Unknown, Detail = "后台状态暂时无法确认，请恢复连接后重新检测。" };
        }
        var report = new CapabilityDetectionReport(DateTimeOffset.UtcNow, state.Identity.Platform, checks, agents);
        CapabilityDetectionValidator.Validate(report);
        return report;
    }
}
