# Claude CLI 断开及首批优先兼容修复

2026-10-09。本地 DEV 已验证，未发布、未替换运行中的 Windows/Android 主客户端；服务端无改动。

## 问题与改动

手机新建 CLI 后启动 Claude，程序输出 `CSI 16t` 查询字符单元像素。旧 core 返回 Unsupported，
Windows authority 随之失败，SessionHost 以 125 结束 shell；Android 回放同一事件出现 TL-H122。

独立 core 实现：

- `14/15/16/18/19t` 查询族安全处理，18t 回复实际权威字符行列；像素/屏幕查询不伪造信息，采用无回复回退。
- 精确 DECSTR (`CSI !p`) 软复位，不清空文本/历史或切换屏幕，保留 Win32/鼠标/焦点/粘贴协议。
- 2027/2031 的 DECRQM 返回不识别，避免错误声明完整字素/主题报告能力；保留旧快照位兼容。

本应用改动：

- `OuterTerminalReplySuppressor` 扩展 4/5/6/8/9 几何回复分类；18t 通过 source sequence/offset
  合并 authority 与投射期待，防止自动回复误作人工输入或留下重复期待。
- Windows .NET、Android API 的 Host Effect V2 校验接受严格的 `CSI 8;rows;columns t`，
  两个值必须为 1..512 的规范十进制。没有放开任意 PTY payload，V1 格式保持原限制。
- 消费 core 构建器统一导出的 Windows DLL/四 ABI SO，更新 `terminal-core.lock.json`。

未改网络协议、E2EE、控制租约、服务端、CLI 页面布局。默认输入操作不变。
完整 Unicode 字素算法及国旗宽度问题仍待独立实现；本轮只纠正能力声明，不将其标为已解决。

## 已完成验证

| 验证 | 结果 |
|---|---|
| Rust core / golden / FFI | 468 / 10 / 48 项通过；fmt、Clippy 通过 |
| .NET native core | 通过，覆盖 V2 payload 正反例及真实 DLL |
| Windows SessionHost | 回复分块/乱序/去重、source coordinate、普通键盘输入、nested ConPTY 鼠标和窗口调整通过 |
| 原故障 WAL | 修复版 DLL 36/36 事件回放成功，未移除 16t，未改原历史 |
| 实际 Claude CLI | 隔离 PowerShell + managed SessionHost + 测试 ACK 桥；看到真实 16t 后仍存活，Ctrl+C 后 shell 可继续执行命令，正常 exit 0 |
| Android 单元 | terminal-engine-api 21、terminal-engine-native 126 项通过 |
| API 36 模拟器 | 22 项 JNI/View instrumentation 通过，包括新查询逐字节回放、DECSTR、checkpoint 恢复 |
| 原生制品 | 1 DLL + 4 SO 均生成，锁文件哈希验证通过 |

实际 Claude 测试仅启动/退出，无模型任务。测试桥接为本机隔离 ACK，不冒充 Android App 经公网 Relay 的全链路验收。
模拟器仅运行独立 instrumentation 测试包；已有登录状态和用户会话未替换。
本轮工具按 `D:\Dev` 清单核验，DEV 版本经 AppShip 两端现场查询为 `0.3.3/132`。

本机证据在忽略目录 `artifacts/validation/`：

- `core-query-fix-tests-20261009.log`、`core-query-fix-clippy-20261009.log`、`core-query-fix-export-20261009.log`。
- `geometry-native-dotnet-tests-20261009.log`、`geometry-sessionhost-tests-20261009.log`。
- `geometry-android-tests-20261009.log`、`geometry-android-unit-summary-20261009.json`。
- `claude-cli-diagnosis-20261009-fixed.json`、`claude-real-startup-fixed-20261009.log`。

## 待升级/发布的门禁

本轮库为本地 DEV，锁文件 `source_dirty=true`，不能作正式发行库；没有提交或推送 core/application。
正式发布需固定源码、重新导出 clean 五库、同时重建 Windows/Android，完成真实 Relay 联测及发布审核。
旧 0.3.4/133 Android 候选包含旧核心，不能直接上传作为本次修复包。
已安装主客户端仍可能复现原故障，直到完成对应升级。当前不安排 LAN/NAT 测试。

核心实现记录位于独立 core 仓库 `QUERY_COMPATIBILITY_FIX_20261009.md`；核心源码和构建脚本仍只放在 core 仓库。
