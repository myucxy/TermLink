# TermLink Android

阶段 0/1 Android 工程包含：

- `terminal-engine-api`：不依赖 Termux、WebView 或业务 UI 的稳定引擎边界；
- `terminal-engine-replay`：严格校验 Golden Stream V1 和 LAN Replay V1；
- `terminal-engine-termux`：把远程原始 VT 字节、resize、主题和只读输入边界适配到
  Termux emulator/view；
- `core-session`：与 Termux、Compose 和具体网络实现解耦的 `RemoteTerminalSession`；
- `core-network`：只面向 Phase 1 临时双向测试 Relay 的 OkHttp WebSocket 适配；
- `app`：Compose Harness，以分段模式隔离本地 Bundle、只读 LAN Replay 和临时双向会话；
  三条路径均驱动真实 `TerminalView` 网格。

浏览器侧的固定 xterm.js 对照位于
[`terminal-reference/xterm-harness`](../terminal-reference/xterm-harness/README.md)。
`:terminal-engine-termux:testDebugUnitTest` 会把带 `capture_id` 和最终序号的 Termux 草案
网格写入 `terminal-engine-termux/build/termlink-reference/`；对照器会先严格校验两份网格
确实来自同一 Golden Stream，再比较可见行、样式段、光标和公共模式。

Termux 源码固定到 `termux/termux-app` 提交
`3df69d1da197dd9bd71a3bafd902dffd720576b4`。仓库只导入 emulator 核心、view、资源和
上游 emulator 测试，明确排除 `TerminalSession`、JNI、本地 PTY、Shell 和 Termux App。
`terminal-forks/termux-patches` 保存远程 Session Port 补丁；可用以下命令从精确提交重建
一份不覆盖检入源码的检查目录：

```powershell
.\terminal-forks\Sync-TermuxTerminal.ps1 `
  -Destination "$env:TEMP\termlink-termux-sync-check"
```

`DiagnosticTerminalEngine` 仍保留为字节诊断工具，但 Harness 已不再使用它渲染终端。

## 构建

```powershell
$env:JAVA_HOME = 'D:\Dev\Env\Jdk\jdk-17.0.17'
$env:ANDROID_HOME = 'D:\Dev\Env\Android\Sdk'
$env:GRADLE_USER_HOME = 'D:\Dev\Env\gradle\user-home'

.\gradlew.bat testDebugUnitTest assembleDebug lintDebug
.\Validate-Phase1Session.ps1
.\Validate-Phase1Network.ps1
```

Wrapper 固定 Gradle 8.13 及其 SHA-256，不依赖 `D:\Dev` 中带全局初始化脚本的 Gradle
发行目录。

Debug APK 位于：

```text
android/app/build/outputs/apk/debug/app-debug.apk
```

在运行中的 API 26+ 模拟器上安装 APK，并对 10 个用例逐个冷启动验证：

```powershell
.\Validate-Phase0Emulator.ps1 -DeviceSerial emulator-5554
```

脚本轮询 Compose 语义树中的 `Verified`，逐项核对 case、事件数、输出字节数、最终尺寸与
退出码，并扫描 crash/ANR。机器可读证据写入
`docs/validation/phase0-android-emulator.json`。

`RemoteTerminalSession` 当前实现严格连续序号、重复丢弃、`TerminalEngine.feed` 完成后再
推进累计 ACK、携带最后 ACK 的 reconnect、显式/隐式 History Gap、从序号 1 开始的有界
原始流重建、控制租约输入、唯一 `input_id`、禁止失败输入自动重试、退出只读以及
100～200 ms resize 防抖。11 个单元测试的机器可读证据位于
`docs/validation/phase1-android-session.json`。

`core-network` 串行处理 WebSocket 事件，在 `TerminalEngine.feed` 完成后才发送累计 ACK；
重连携带本地最高连续序号，输入只在收到匹配 UUID 的 `InputAccepted` 后完成，失败输入不会
自动重试。它还绑定测试租约、Flow Control 和明确的 History Gap。5 个单元测试的证据位于
`docs/validation/phase1-android-network.json`。

规范化 Snapshot 尚未冻结。因此状态机明确拒绝从中段清空并回放，不能用 Termux 私有网格
序列化冒充跨端 Snapshot。`core-network` 和“双向会话”页面只接入临时明文测试协议，仍未
接入生产 WebSocket、Crypto、设备授权或正式设备/Session 页面；LAN Replay 继续保持只读。

## 使用 Android 手机测试 Docker

1. 让手机和 PC 连接同一个允许设备互访的 Wi-Fi；不要使用启用了客户端隔离的访客网络。
2. 在仓库根目录执行 `ipconfig`，记录当前 Wi-Fi 或以太网 IPv4 地址。
3. 启动测试容器：

   ```powershell
   docker compose -f server\compose\lan-test.compose.yml up --build -d
   ```

4. 在手机浏览器打开 `http://<PC-LAN-IP>:8080/healthz`。预期看到
   `{"status":"ok","mode":"lan-replay-test"}`。
5. 把 Debug APK 传到手机安装；也可以启用 USB 调试后从仓库根目录执行：

   ```powershell
   D:\Dev\Env\Android\Sdk\platform-tools\adb.exe install -r `
     .\android\app\build\outputs\apk\debug\app-debug.apk
   ```

6. 打开 `TermLink Harness`，切换到“LAN 固定回放”，填写：

   - Docker 局域网地址：`ws://<PC-LAN-IP>:8080`；
   - 测试令牌：`termlink-lan-test`，或 Compose 启动时设置的自定义令牌。

7. 点击“连接 Docker”。固定测试向量的预期结果为：

   ```text
   Verified
   LAN Docker WebSocket
   14 events · 702 bytes · 100×30 · exit 17
   ```

   状态下方应同时看到真实 Termux 网格中的 PowerShell 提示符、ANSI 颜色、
   `__OUTPUT_CAPTURE_OK__` 和 `__INPUT_CAPTURED__:android-input-simulation`。

要验证阶段 1 双向测试闭环，切换到“双向会话”，填写同一地址和令牌，再选择场景：

- “断线一次并恢复”：首次在序号 6 后断开，恢复后预期到达 `ACK 13`；
- “History Gap”：预期停在 `ACK 4` 并显示 `missing 5..6`；
- “慢消费者窗口”：验证 Flow Control 与有界关闭；
- “已退出会话”：预期 `ACK 14 · exit 17`。

连接后页面会显示真实 `RemoteTerminalSession` 状态。取得测试控制权后可以发送固定测试
输入，并观察 `Input accepted` 计数；页面始终显示 `TEST RELAY · NO E2EE`。

API 36 模拟器可自动执行断线恢复、输入、Gap 和 exited 验收：

```powershell
.\Validate-Phase1RelayEmulator.ps1
```

机器可读证据写入 `docs/validation/phase1-android-relay-emulator.json`。

若手机浏览器无法访问健康检查，先确认 PC 地址没有变化、两台设备处于同一局域网，且路由器
未启用 AP/客户端隔离。Compose 不会自动修改 Windows 防火墙；不要在未评估前把 8080
暴露到公网。

`ws://` 明文访问仅在 Debug Manifest 中放行。LAN Replay 只传仓库内固定测试数据且不接收
手机输入；Phase 1 入口接收的也只能是 Harness 测试输入。双向测试通过只证明测试级 ACK、
背压和恢复链路，不代表生产认证、设备授权、E2EE、Protobuf 或 Snapshot 已完成。

## 当前验证记录

2026-08-03 在 `NuanYa_API36_Review`（API 36，x86_64）完成：

- 本地菜单完整展示全部 10 个固定 case；每个 case 都以 Intent 参数独立冷启动，结果
  `10/10 Verified`，case、事件数、字节数、100×30 和退出码 17 均与 Manifest 一致；
- 阶段 0 自动验收记录 `0 crash/ANR`；加入 Phase 1 双向闭环后的 APK SHA-256 为
  `1025fa916abb464df36cec864b571259a83671dd8c8187fec28f2e39e9195d4e`；
- `ws://192.168.9.28:8080` → Docker LAN Replay → Termux grid：`Verified`；
- LAN 路径仍固定为
  `smoke-powershell-v1 · 14 events · 702 bytes · 100×30 · exit 17`；
- ANSI 16/256 色、True Color、CJK、emoji、combining、Terminal Features、Vim、less、
  fzf 与 htop 均进入真实 `TerminalView`；最终截图非空，系统栏与页面没有重叠；
- `testDebugUnitTest assembleDebug lintDebug` 通过，Android/Termux 共 176 个测试、0 失败；
  其中 `core-session` 11 个和 `core-network` 5 个聚焦测试均通过；
- 进程级 logcat 无崩溃、ANR 或应用异常。模拟器仍会输出 x86 CPU variant、HWUI 和
  Android 平台弃用提示，不属于 Harness 失败。

同一模拟器还通过 Docker `ws://10.0.2.2:8080` 验证了 Phase 1 双向路径：

- `disconnect-once` 从断线恢复到 `ACK 13`，取得测试租约后进入 `Controlling`；
- 测试字符串按 code point 产生 13 个输入事件，均收到匹配 `InputAccepted`；
- `history-gap` 保留 `ACK 4` 并明确报告缺失 `5..6`；
- `exited` 到达 `ACK 14` 并保留退出码 17；
- 全部场景 0 crash/ANR，最终画面非空且控件与真实 Termux 终端没有重叠。

全部 10 个固定流均生成 Termux 草案网格，并由离线 xterm.js 6.0.0 + Unicode 11 Harness
对照为 122/122 checkpoint `Grid match`。程序输出中的 emoji/ZWJ 正确；Android 输入适配
单元测试也验证 CJK/emoji code point、Ctrl+C、Alt+B、方向键、bracketed paste、SGR
mouse、focus 和只读拒绝。它仍不能替代真机 IME 与外设验证，也不是 Golden Grid 冻结：
`expectations.json` 中的 `normalized_grid` 和 `reference_screenshot` 尚为空，Android
xterm.js WebView 回退和真机矩阵仍待后续完成。

当前环境只有 API 24 与 API 36 AVD，而 app `minSdk` 为 26，因此 API 24 无法安装且没有
API 26 AVD。arm64 真机、Gboard/Samsung/搜狗等厂商 IME、蓝牙键鼠、平板/折叠屏、
TalkBack、Doze 与耗电只能在对应硬件可用后补测；不得由 API 36 模拟器结果代替。
