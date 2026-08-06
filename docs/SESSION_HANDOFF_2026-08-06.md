# TermLink 开发会话交接记录

日期：2026-08-06
工作区：`E:\WorkSpace\AICodeProject\TermLink`
分支：`main`

## 1. 当前结论

TermLink 首版生产三端闭环尚未完成，当前工程完成度仍约为 70%。服务端生产验收栈、账号与
管理门户、Windows 生产 Realtime/E2EE 基础、Android App 内登录/首次授信和 Windows 统一
MSIX 已取得真实证据；但 Windows Terminal TermLink Profile → 真实 PowerShell/Codex →
公网 Relay → Android 的完整生产双向闭环仍不能宣称通过。

不得使用 Mock、UI 原型、单元测试或临时明文 `TLT1` 代替剩余跨进程验收。

## 2. 本轮已完成并提交

### 2.1 服务端门户与管理体验

- `ab67cf4 fix(server): invalidate unified portal styles`
  - 修复门户 HTML 与旧 CSS 缓存版本不一致；
  - 公网重新部署；
  - 个人中心、管理台和身份中心可相互返回；
  - 深色/明亮主题跨首页、登录、个人中心、身份中心和管理台保持一致；
  - 管理员身份显示 `超级管理员 · SUPER_ADMIN`；
  - `go test ./...` 通过。

公网入口：`https://termlink.kget.cc:8910/`。

### 2.2 Windows 给可信 Android 直接授权

- `ea09519 feat(windows): grant trusted Android PC access`
  - Windows 托盘可向已 `TRUSTED` Android 直接授予当前 PC 的 `VIEW/CONTROL/MANAGE`；
  - `PENDING` Android 继续使用短码配对，两条路径不混用；
  - 每次授权操作使用独立幂等 ID，同一次网络重试复用；
  - 修复撤销后重新授权被旧幂等响应误判的问题；
  - Windows Release 构建 0 warning / 0 error，Agent Control Pipe V4 验证通过。

### 2.3 Windows 单一统一 MSIX

- `203ea59 feat(windows): ship unified terminal msix`
  - 一个 x64 自包含 MSIX 包含 Tray、Agent、SessionHost、Updater 和 .NET Runtime；
  - 注册 `termlink-session.exe` AppExecutionAlias，固定解析到包内
    `TermLink.SessionHost.exe`；
  - 注册 `com.microsoft.windows.terminal.settings` App Extension，包内提供四个固定
    Windows Terminal Profile：
    - TermLink CMD；
    - TermLink Windows PowerShell；
    - TermLink PowerShell 7；
    - TermLink WSL；
  - Profile 不包含工作区路径或版本化 `WindowsApps` 路径；
  - 生命周期测试使用隔离包身份，不删除用户数据；
  - 统一包阶段的 Alias 真实启动包内 SessionHost，Session Registry 按 Host PID 发现会话，
    本地输入桥使测试 Shell 自然以预期退出码结束；
  - 测试没有启动或关闭用户现有 Windows Terminal 窗口；
  - 测试结束后隔离包、Alias、测试进程和临时开发证书均为零残留；
  - 同时修复共享 AgentControl 的 C# 5 兼容性，恢复旧系统 SessionHost 验证入口。

这证明单包结构和本机真实 AppX 生命周期，不证明生产发布证书链、RFC 3161 时间戳、正式
注销/登录 StartupTask，也不证明可见 Windows Terminal Profile 到 Android 的生产闭环。

### 2.4 Windows 安装验证安全收口

- `585d0db fix(windows): isolate installer validation`
  - 解包验证的 Tray/Agent 改用每次运行唯一的控制 Pipe 和临时 BufferRoot，不再可能连接
    当前用户真实 Agent 的默认 Pipe 或 `%LOCALAPPDATA%\TermLink\Agent`；
  - 生命周期先安装不含 Alias/App Extension 的真实旧版迁移夹具，再由包内 Updater 升级为
    统一 0.2 包，验证 Alias、四个 Profile 和 SessionHost 运行时确实由升级新增；
  - 再降级旧包，验证 Alias/App Extension 被正确撤销；
  - 用户开发 Fragment 只做文件集合和内容指纹检查，全程未修改；
  - 成功证据延迟到包、测试进程、临时开发证书和临时目录全部清理并复查为零之后写入；
  - 新一轮开始先把旧证据原子标记为 `passed=false`，中断或清理失败不会遗留可误认的成功证据。

## 3. Windows 本轮真实测试

执行并通过：

```powershell
<SDK-8.0.303-dotnet.exe> build .\win-agent\TermLink.Windows.sln -c Release --no-restore
.\win-agent\tests\Validate-AgentControl.ps1 -DotNetPath <SDK-8.0.303-dotnet.exe>
.\win-agent\tests\Test-SessionHost.ps1
.\win-agent\tests\Validate-WindowsInstaller.ps1
.\win-agent\tests\Invoke-DevelopmentMsixLifecycle.ps1
git diff --check
```

结果：

- Release 构建：0 warning / 0 error；
- Agent Control Pipe V4：通过；
- SessionHost：四种 Profile、双层 ConPTY、Unicode、控制键、resize、2 MiB 输出、10 秒任务、
  超时、强制结束和 20 次生命周期通过；
- 解包验收：自包含、9 个 TermLink 二进制签名、4 个 Profile、Tray 单实例和 Agent 健康通过；
- AppX 生命周期：安装无 Terminal 集成的旧版 0.1、包内更新器升级统一 0.2、真实
  Alias/SessionHost/Registry 探针、降级旧版 0.1、卸载和清理通过；
- 最终复查：隔离 AppX 包 0、测试进程 0、测试开发证书 0、凭据/生命周期临时目录 0；原有
  Windows Terminal PID `28752` 未关闭；
- 两份安装证据 UTF-8 无 BOM、源码 SHA-256 与当前提交一致，未发现 Password、Token、Secret
  或私钥模式。

机器可读证据：

- `docs/validation/phase0-session-host.json`；
- `docs/validation/phase1-windows-installer.json`；
- `docs/validation/item7-windows-msix-lifecycle.json`。

### 3.1 更新检查与 Windows EXE 首发路径

- Android 与 Windows Tray 已接入 AppShip 公共更新检查，固定 slug 分别为
  `termlink-android` 和 `termlink-win64`；只查询 stable 通道，不携带账号凭据，不自动下载或
  安装，也不会关闭当前 Windows Terminal、Agent 或 Shell 窗口。
- Windows 首发产物固定为 `TermLink-Setup-<version>.exe` 单文件安装器；`Build-Setup.ps1`
  已完成自包含 Agent/SessionHost/Tray/Updater 打包和开发证书签名构建。正式 AppShip 发布仍
  需要外部提供 HTTPS AppShip 地址、`release_manager` 凭据和受信代码签名证书/时间戳。
- Android release signing 现在只从 Gradle 属性或进程环境读取 keystore 路径、别名和密码，
  仓库不保存签名材料；当前工作区没有 TermLink 专用 release keystore，不能用其他产品证书
  代替生产签名。

### 3.2 AppShip 首版发布结果

- AppShip 地址：`https://appship.kget.cc:8910/`；两个公开应用均已由管理员创建并发布到
  `stable` 通道。
- Android build 1 首发 release `8d090760-cfb4-46e7-870e-19a780894984` 已保留；当前修复版
  slug `termlink-android` 的 release `99b0508f-29c5-4e39-858d-80e61b0e8fc0`、artifact
  `80aae773-3363-4a95-835a-74dafb91f4e0`，版本 `0.1.2` / build `2`，状态 `published`；
  APK SHA-256 为 `d9b3a730390f827c62945e37ef88b28e67fdde9034ac8eeed21e11f0a16c0489`。
- Windows build 1 首发 release `cc05481b-29f6-4bbb-8094-08c4e4a2f5e6` 已保留；当前修复版
  slug `termlink-win64` 的 release `f2141e9a-39ec-4e4e-9fad-2484c0864d3c`、artifact
  `e84434d2-b2ad-4ce0-9eb6-db09bc9566b4`，版本 `0.1.2` / build `2`，状态 `published`；
  EXE SHA-256 为 `6137d27da263bef5bd5567ed7c4ceb53c303f56e5146a52e0b1fbf73c064bdef`。
- 两个公开 updates 接口均返回 HTTP 200；传入 build 1 返回 `optional` 和 latest `0.1.2`/2，
  传入 build 2 返回 `no_update`。客户端已兼容 AppShip 实际的 `latest` 与根级 `artifact`
  响应结构。
  Windows 本次使用用户授权的一次性自生成 Code Signing 证书，签名根不属于系统受信生产 CA；
  不能把该签名视为正式商业发布证书。

## 4. 本机状态注意事项

- 本机 Android SDK 已确认位于 `D:\Dev\Env\Android\Sdk`（不是常见的 `D:\Android\Sdk`）；使用该路径设置本次构建进程的
  `ANDROID_SDK_ROOT`/`ANDROID_HOME` 后，`:core-network:compileDebugKotlin`、
  `:core-network:testDebugUnitTest`、`:core-session:testDebugUnitTest` 与 `:app:assembleDebug`
  均已通过。未修改系统级环境变量。
- 当前尚未提交的生产 FlowControl/ACK/Relay 字节预算改动已通过 Server 相关 Go 测试与
  `go vet`，Windows Release Solution 构建为 0 warning / 0 error；这些结果证明当前源码可构建，
  不证明公网部署或真实 Windows → Android 生产闭环已经通过。
- 用户原有 Windows Terminal 窗口未关闭；后续不得用结束 `WindowsTerminal.exe` 进程清理测试；
- 当前用户和 All Users 均没有注册 `TermLink.Terminal`；磁盘上仍有旧的 0.1 开发 MSIX
  构建产物，但它不是已安装包；
- `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\TermLink` 仍有开发 Fragment，指向
  工作区 SessionHost；真正进行可见 Profile 验收前必须安全备份，测试后原样恢复，不能直接
  删除用户文件；
- `termlink-session.exe` 当前不存在是预期结果，因为隔离生命周期测试最后完成了卸载；
- 本机已有开发发布者信任材料可能仍被其他开发包使用；未经依赖清点不得移除；
- 生产签名安装包仍需正式受信代码签名证书与时间戳，开发证书测试不能作为外部发布证据。

## 5. 服务端与账号当前状态

- 公开服务：`https://termlink.kget.cc:8910/`；
- Lucky 上游：`http://192.168.9.28:8080`，仅作本机映射，不写入客户端；
- OIDC、TLS、API、Realtime Probe 的生产验收栈已在不清空数据的情况下重建并通过；
- 注册功能已开放；
- Android 与 Windows 都支持应用内直接登录和注册，固定使用公开 HTTPS 服务，不跳转网页；
  首版同账号设备自动登记、信任并发现自己的 PC/Session，远程输入仍需短时控制租约；
- `admin` 是首次超级管理员。临时密码未写入仓库；README 已说明单次领取/重置途径；
- `admin` 当前处于首次登录强制改密状态，新的临时密码尚未领取；不得在日志、文档或提交中
  输出该密码。

## 6. 下一条生产主链

### 2026-08-06 现场 Relay 恢复证据与 Android 账号边界

- 已将旧 Windows 生产身份文件移动到本机带时间戳的备份目录并完成当前 OIDC 账号重新绑定；
  未删除或输出任何凭据内容。
- 受控重启已安装 Agent 后，控制管道 `List` 与 `RefreshProductionStatus` 均保持
  `RelayState=Connected`；生产状态返回 2 台设备、1 个本地会话。
- 既有 `TermLink.SessionHost`、PowerShell 和 Windows Terminal 进程均保持运行，未关闭用户窗口。
- 旧的 Windows Agent Grant 流程仍可用于协议兼容测试，但首版客户端不展示授信、配对、
  会员或配额入口；同账号 PC/Session 发现由服务端自动设备策略覆盖。
- 本机模拟器 `dev.termlink.harness` 虽显示“已登录；已有最近服务器同步”，但其设备名为
  `sdk_gphone64_x86_64`；当前 Windows 账号的设备名为 `25098PN5AC`。两者属于不同账号，
  模拟器页面的 `0/1 台 PC、0/3 个实时会话` 不能作为当前账号的实时连接证据。

因此，Windows → 公网 Relay 的生产连接已得到真实进程证据；Android 同账号实时观看/控制仍待
在模拟器或真机登录当前账号后重新验证，不能提前宣称三端闭环完成。

1. 使用包含 `ea09519`、`203ea59` 和 `585d0db` 的 0.2 Windows 构建，在独立 Windows
   验收机上通过普通 `Add-AppxPackage` 持久安装；禁止使用 `ForceApplicationShutdown`，
   不关闭验收机或本机已有窗口；
2. 验证已安装包、Alias、四个可见 Profile、Tray/Agent 和真实 SessionHost；连接信息和登录
   凭据只在会话内使用，不写入仓库或交接文档；
3. 在 Android/Windows 应用内使用同一账号完成原生登录或注册，确认设备自动登记并发现 PC/Session；
4. 在不关闭现有窗口的前提下，安全处理开发 Fragment，使用真实 Windows Terminal
   TermLink PowerShell Profile 启动受管 PowerShell；
5. 在该 Shell 中运行真实 Codex/其他 AI CLI；
6. Android 验证实时原始输出和英文、中文、Enter、Tab、方向键、Ctrl+C；
7. 验证本地键盘立即抢占、输入 ID 去重、ACK 恢复、Gap、resize、退出和断线重连；
8. 验证 Android 断线时本地 Shell/Codex 不退出；
9. 完成三端逐页 UI、主题、导航、管理员权限、冗余内容和图标终审；
10. 完整生产闭环通过后，再统一确认 01B 图标在 Windows、Android 和网页的最终接入。

## 7. 仍未完成的 P0/P1

- 生产 `FLOW_CONTROL`：Proto Payload、Android 高/低水位、Windows 按订阅设备独立窗口和
  Relay 字节硬预算仍未收口；
- Realtime 仍需冻结 Envelope ACK 与 `OutputAckPayload` 的权威关系，并增加角色、方向和
  MessageType allowlist；当前 Relay 的 128 帧队列不是生产字节预算；
- REST 创建的 Subscription Token 尚未由 Realtime 消费，多 Server 副本之间也没有跨实例
  路由；同序列恢复帧必须约束为同一密文，不能接受任意旧序列重发；
- PC 本地租约确认策略与当前服务端自动授予行为仍有设计/实现不一致；
- 现有生产 Probe 只转发随机 opaque ciphertext，不执行真实双方 E2EE 加解密，不能作为终端
  正文闭环证据；
- Android App 内 Password Grant 与旧权威文档 PKCE-only 不一致，必须同步权威文档和威胁模型；
- 正式 `core.compose.yml` 使用外部 OIDC 时，TermLink Keycloak 主题安装仍需明确校验/发布方案；
- 生产发布证书、外部 Billing Provider、真机 IME/键鼠、TalkBack、后台和长时间吞吐仍未验收。

任何后续“完成”声明都必须以真实生产 Protobuf/Realtime/E2EE 跨进程证据为准。
