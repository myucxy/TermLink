# 按手机窗口调整会话

安卓会话页云朵菜单增加“按手机窗口调整会话”和“恢复电脑窗口尺寸”。仅在线且持有控制权时可点击。
菜单打开时读取 native 终端 View 的实际可视区域，扣除内边距，按当前字体、SP 字号、系统字体缩放
及行距计算能完整容纳的列数和行数；预览显示行列及内容区像素。0.2.62 起按输入法收起后的高度
计算：只补回页面实际避让输入法的高度（已扣除底部导航重叠），不改变输入法显隐。
当前一行、两行或折叠快捷键栏仍按真实 View 布局扣除，横竖屏和字号也按当前值计算。
调整为一次性操作，后续字号、方向或快捷键布局变化需重新点击。

这调整的是终端字符网格与 Windows 终端窗口，不修改显示器分辨率、Windows DPI 或用户字体设置。
Windows 外层终端按自身字体和 DPI 换算物理窗口大小。菜单原有标题栏高度保持不变。

## 执行与结果

1. Android 通过现有 E2EE Relay 发送带 UUID、控制租约、本地输入 epoch、10 秒到期时间的显式请求。
   双端时钟须同步；请求不自动重试。原自动 `sendResize` 仍不改变电脑尺寸。
2. Agent 校验控制租约，同一会话一次只执行一个请求。旧 SessionHost 缺少能力时返回升级提示。
3. SessionHost 将 `CSI 8;rows;columns t` 写到外层终端输出，不写入子进程输入或终端历史。
   原有尺寸轮询/稳定等待流程调整真实 ConPTY，发布有序 `RESIZE_APPLIED`。
4. 外层和内层尺寸均达到目标才发送 `RESIZE_RESPONSE`；手机还会等待其 authoritative grid 达到
   返回尺寸，之后显示成功。超时、失去控制、本地输入优先、窗口拒绝均显示明确反馈。
5. 第一次成功调整保存原电脑网格；连续调整保留该恢复点。恢复成功即清除；电脑本地调整窗口
   会使恢复点失效，避免随后恢复覆盖用户本地选择。

`RESIZE_RESPONSE=42` 是带 UUID 的控制回复，不进入输出 replay/ACK 游标。LAN/NAT 数据白名单不变。
窗口调整协议需要 Server、Windows 和 Android 0.2.61 或更高版本，并使用新版 SessionHost 新开的会话。
0.2.62 的输入法高度计算优化不改变协议，无需再次升级 Server。
发布复核补充：Android 按当前 Relay 连接协商 `terminal_window_resize_v1`；旧 Server 未回显能力时
直接提示升级服务端，不发送请求，保留既有会话连接和输入能力。

## 参考与边界

- Termux：[`termux/termux-app` pinned `3df69d1da197dd9bd71a3bafd902dffd720576b4`](https://github.com/termux/termux-app/tree/3df69d1da197dd9bd71a3bafd902dffd720576b4)，
  `terminal-view/src/main/java/com/termux/view/TerminalView.java::updateSize` 与 `TerminalRenderer`。
  参考“可视像素 / 字体度量”的行为；不复用其本地 PTY/Shell 调整路径。
  已核对本仓库 `UPSTREAM.md`、`termux-patches/`、`Sync-TermuxTerminal.ps1` 及 `terminal-engine-termux/`。
  上游 GPLv3-only 及组件例外只作为行为研究，本改动没有复制 Termux 代码/资源。
- Windows Terminal 1.24.11911.0：`src/cascadia/TerminalCore/TerminalApi.cpp::ResizeWindow`、
  `TerminalControl/TermControl.cpp::_bubbleWindowSizeChanged`、
  `TerminalApp/TerminalPage.cpp::_WindowSizeChanged`。
  当前外层仅允许单标签、单窗格、非全屏、非 quake 的窗口调整；屏幕/最小窗口尺寸也可能限制目标。
  被拒绝时保留原机制和真实失败反馈，不强行制造与外层不一致的内层尺寸。

## 可重复验证

从仓库根使用 `D:\Dev` 清单指定的工具：

```powershell
gradle -p android :app:testDebugUnitTest :core-network:testDebugUnitTest :terminal-engine-native:testDebugUnitTest
gradle -p android :terminal-engine-native:connectedDebugAndroidTest
dotnet run --project win-agent/tests/SessionHostValidation/TermLink.SessionHost.Validation.csproj --runtime win-x64
dotnet run --project win-agent/tests/ProductionTerminalHistoryValidation/TermLink.ProductionTerminalHistory.Validation.csproj
# 在 server 目录：
go test ./internal/realtime ./internal/realtimewire
```

Windows 集成用测试自建的外层 ConPTY 承载真实 SessionHost/内层 ConPTY，模拟 renderer 接受及
ResetSize 拒绝，覆盖调整、恢复、本地调整覆盖、输入 epoch、过期和真实有序事件；不操作用户会话。
Android 模拟器在独立测试包验证真实字体/系统字体缩放/行距/缩小可视区的计算及 JNI，确保测量不
改变权威网格。网络测试覆盖权限、重复点击、UUID 匹配、恢复失败和回复不推进 ACK；服务端覆盖
方向 ACL 和控制回复不推进 replay 游标。

证据位于 `artifacts/validation/resize-*.log`。这些测试不等同于已对正式版本完成跨设备菜单点击验收；
后续发布验收仍需实际 Windows Terminal 单窗格成功、多窗格/全屏拒绝及手机 Relay/LAN/NAT 会话验证。

本轮结果：Android app 318、core-network 330、native engine 118 项单元测试全部通过；
Android 16 模拟器 15 项 instrumentation 测试通过；Windows SessionHost（含真实嵌套 ConPTY）和
历史 WAL/producer/outbox 验证通过；Server realtime/realtimewire 测试通过。
