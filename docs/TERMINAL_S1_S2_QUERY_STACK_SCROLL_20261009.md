# 终端查询、属性栈与备用屏幕滚轮接入

2026-10-09，DEV，未发布。完整范围、状态格式与验证记录见
[core 开发记录](../../terminal-core/S1_S2_QUERY_STACK_SCROLL_20261009.md)。全部 22 组能力仍在实施。

Windows/Kotlin HostEffect V2 严格解码新增查询回复。Windows 外层回复按 DSR 请求族、checksum request ID
匹配，校验字段数、数值、十六进制，错误/无关输入不吞掉，消费一次即退休，不抢控制租约。
SGR 栈和 1007 经同一 native core 运行和快照恢复，未给 Android 按钮新增独立编码。

最新验证：Rust 557；Android API/native 单元149；API36 emulator-5556 JNI27；.NET 核心与 SessionHost 通过。
窗口鼠标输入测试覆盖主屏无方向键、备用屏箭头、应用光标、鼠标优先和模式关闭。
JNI 覆盖新报告、真实 snapshot V2.11 栈恢复、1007 不启用触摸鼠标状态及单次方向键。

`ProtocolTransportValidation --protocol-transport-probe` 使用新建隔离子进程检测真实 ConPTY：
bundled 后端完整保留 SIXEL/Kitty/DRCS；本机 Windows10 19045 system 后端丢弃图形控制序列。
只使用合成测试输出，Base64 证据不包含用户会话。当前只证明传输，图形显示尚未实现。

DEV 基线现场查询为 Android/Windows 0.3.3/132，证据
`artifacts/validation/s1-extended-query-dev-baseline-20261009.json`。
DLL/四 ABI SO 与 lock 已替换并校验，来源 dirty。主 Windows 与主 Android DEV 运行包未替换；
只更新并运行独立 instrumentation 包。后续必须补产品 Relay/界面/跨版本和压力验收，不能据此发布。
