# HOME-PC TL-H122：备用屏幕滚轮模式兼容

2026-09-25。此记录是代码修复与本地验证，不是发布记录。

## 原因与处理

HOME-PC 的失败会话在第 22 个事件输出
`ESC[?1049h ESC[?1007h ESC[1;1H ESC[J`（此处空格仅用于分隔指令）。
Windows 0.2.62.118 的 native core 对 `?1007h` 返回 `Unsupported`，
持久化 checkpoint 为 `SnapshotEligible=false / NATIVE_UNSUPPORTED`。
Android 回放同一事件时也会失败并映射成 TL-H122；单纯重打旧 core 无效。

共享 Rust dispatcher 现在兼容消费 `DECSET/DECRST 1007`，不再因此关闭 renderer。
1007 是前端滚轮转方向键策略；本次不新增该输入功能，不改变现有滚轮、触摸、控制租约、
Host Effect、ACK 或 Snapshot 格式。`DECRQM 1007` 仍报告未识别（0），不宣称已实现该能力。
其他未知模式（包括与 1007 混合的未知模式）继续失败，未改为全部忽略。

Windows 和 Android 的正式包都需包含修复后的 core；Server 无需为此更新。
现有 Windows producer 在重启后会从 WAL 重建，不必删除历史。已结束的命令行进程不会被恢复。

## Termux 参考边界

- 仓库：`termux/termux-app`，固定提交 `3df69d1da197dd9bd71a3bafd902dffd720576b4`。
- `terminal-emulator/.../TerminalEmulator.java` 的 `doDecSetOrReset` 未实现 1007，
  经 `unknownParameter` 消费指令后继续解析，不会关闭整个终端。
- `terminal-view/.../TerminalView.java` 的 `doScroll` 在备用屏幕将滚动转为上下键，
  但不受 1007 状态控制；该输入策略不能直接搬到 TermLink 的远程控制边界。
- 已核对 `UPSTREAM.md`、`termux-patches/`、`Sync-TermuxTerminal.ps1` 与
  `android/terminal-engine-termux/`。仅参考行为，未复制 GPL 源码或引入生产依赖。
  Android instrumentation 使用现有、仅测试依赖的固定 Termux emulator 对照显示结果。

## 验证

- 新增 core 回归在修复前稳定失败，原因是 `Unsupported("private mode")`。
- Rust workspace：462 core + 10 golden-stream + 48 FFI，共 520 项通过。
  包含任意字节切分、旧快照加后续事件恢复、退出备用屏幕后主屏保留、混合模式与查询。
- Clippy 全 workspace/all-targets/all-features 通过。修改的 Rust 文件 rustfmt 检查通过；
  全库 fmt 检查仍指出原有 `input.rs` 两处格式差异，本次未改动该文件。
- Windows Release native core 构建通过；生产历史 WAL/producer/outbox/uploader 验证通过，
  新用例验证旧 `NATIVE_UNSUPPORTED` checkpoint 可经 WAL 重建为有效 native Snapshot。
- HOME-PC 实际失败会话的 24 个 WAL 事件在独立进程用新 core 全部回放成功，
  导出/导入快照后 next sequence 为 25；未改写原历史、替换安装 DLL 或重启客户进程。
- Android 四种 ABI JNI 库构建通过；模拟器 16 项 JNI instrumentation 通过，
  新用例对照 Termux 显示结果并覆盖快照恢复及退出备用屏幕。

本地日志和真实会话回放摘要位于 `artifacts/validation/h122-*`，不包含终端正文或密钥。
客户端安装包尚未发布或升级。
