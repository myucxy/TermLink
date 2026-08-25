# TermLink 0.1.28（build 34）

- 修复 Android 断线重连后会话内容不能安全恢复的问题：恢复失败时不再把实时差量应用到空白终端，也不错误推进 ACK。
- 完善 Windows Codex CLI 输出兼容：支持其实际使用的退格/制表、256 色、RGB 真彩色、光标样式、bracketed paste、focus reporting 与 synchronized output。
- 新增跨端终端兼容核心接口、source event/presentation revision 元数据和 Snapshot/Delta/Resync 标准 schema，为后续 Windows Terminal TerminalCore 或第三方实现保留稳定二开边界。
- 新增 typed input 双轨协议；新旧客户端仍通过 legacy bytes 互通，新 Windows 在写入 ConPTY 前校验语义输入与实际执行字节完全一致。
- 增加 capability 灰度、shadow 验证与进程级回滚开关；本版本继续使用已验证的 ConPTY/SessionHost 管道，不宣称已切换未纳入可追溯构建的 Windows Terminal 原生内核。
