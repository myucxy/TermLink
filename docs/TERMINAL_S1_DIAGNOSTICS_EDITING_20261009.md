# 终端 S1 首批应用接入记录

日期：2026-10-09。首批 DEV 实现与本地验证通过；全量 22 组及整个 S1 均未完成。
协议定义、参考来源和核心证据见
[core 本批记录](../../terminal-core/S1_DIAGNOSTICS_EDITING_20261009.md)。

## 用户可见变化与链路

- Android 终端输出失败区显示转义的失败序列，增加紧凑的“协议错误详情”和“复制诊断”。
  复用原错误卡片，不改变 CLI 正常页面布局。详情为纯文本，包含原因、序列、hex、事件/字节坐标及隐藏/截断计数。
- 协议未支持继续用 TL-H122，格式损坏为 TL-H126，资源超限为 TL-H127；保留对应本地证据，避免统一显示成服务器故障。
- Windows NativeTerminalException 保留独立 Diagnostic 属性，SessionHost 输出转义详情到本地外层错误面，不送入 PTY 输入。
- 已停止的 native handle 保留首次根因；可重试错误不将上一次证据套在新错误上。镜像恢复失败保留原证据，同时保留恢复操作实际错误类型。
- 新字段不进入普通异常 Message、连接遥测或 Server 明文日志。没有新建服务端终端解释/存储链路。
- Android JNI 与 Windows DLL 同时新增 ESC 6/9、CSI ?5W 支持；共享核心，不在按钮各自处理协议。

`TL-DIAG/1` 的 Android 解码严格校验字段、ASCII、长度、hex/可读表示一致性、计数与坐标范围。
JNI 导出可选注册，旧库缺少此 API 时仍返回原错误码，不用诊断失败覆盖业务根因。

## 本批验证

| 验证 | 结果 |
|---|---|
| 核心 Rust / golden / FFI | 544 项通过，fmt/Clippy 通过 |
| .NET NativeTerminalCoreValidation / SessionHostValidation | 通过，含实际 ConPTY 输入、鼠标、查询、生命周期、resize |
| 隔离真实 Claude 启动/中断/回到 PowerShell | 通过，真实 16t 未导致退出，最终退出码 0 |
| Android API / native / network / app 单元 | 874 项通过，0 失败/跳过 |
| API 36 模拟器 emulator-5556 真实 JNI | 24 项通过，0 失败/跳过；新增诊断根因与编辑/快照测试 |
| 五个 DEV 原生库及锁文件 | 构建与哈希验证通过，source_dirty=true |

app HEAD 为 `d6fe9c6f4b21d9babc3c3b8f52ed6364455f5ab4`，core HEAD 为
`e6e849172c6fca0de01ae0799eae97ad9483e687`，二者均含本批及之前的未提交改动。
DEV 沿用经 AppShip 核验的 0.3.3 / 132，未递增版本。hash 以 `terminal-core.lock.json` 为准。

复现入口：

```powershell
./scripts/Verify-NativeArtifacts.ps1
dotnet run --project win-agent/tests/NativeTerminalCoreValidation/TermLink.NativeTerminalCore.Validation.csproj --no-restore -p:Version=0.3.3 -p:FileVersion=0.3.3.132
dotnet run --project win-agent/tests/SessionHostValidation/TermLink.SessionHost.Validation.csproj --no-restore -p:Version=0.3.3 -p:FileVersion=0.3.3.132
# android/；先设置 D:\Dev 的 JAVA_HOME / SDK / Gradle 用户目录，以及 ANDROID_SERIAL=emulator-5556
./gradlew.bat :terminal-engine-api:testDebugUnitTest :terminal-engine-native:testDebugUnitTest :core-network:testDebugUnitTest :app:testDebugUnitTest :terminal-engine-native:connectedDebugAndroidTest '-PtermlinkVersionCode=132' '-PtermlinkVersionName=0.3.3' --offline --console=plain
```

.NET 测试串行执行；版本参数只用于复现本批证据，下次新构建仍应现场核验。
本机日志：`artifacts/validation/diagnostic-editing-*-20261009.log`；单元测试统计保存为同前缀 JSON。

## 接续边界

1. C02 仍需补具体参数/码点定位、profile/revision/来源、实际影响、输入方向与完整可选查询分类。
2. 主 App 会话触发、详情查看与复制、公网 Relay 产品联测、Windows 持久错误展示仍待验证/完善。
3. 后续 C01/C07 和 S2–S8 继续执行全量计划；本批不代表全部终端功能已补齐。
4. 性能对比、soak、ARM64 真机、旧版混用和升级回滚未完成。只使用 Relay Only，不恢复 LAN/NAT 验收。

未安装/升级主 Windows 或 Android 客户端，模拟器只运行独立 JNI 测试包。Server 无改动，无 Git 推送、正式构建或 AppShip/Docker 发布。
