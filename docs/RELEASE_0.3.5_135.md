# TermLink 0.3.5 / 135 发布候选

用户于 2026-10-16 授权测试完成、复核代码后发布新版本。版本根据 AppShip 当前 Android 0.3.4/133、Windows 0.3.4/134、next build 134/135 选择；构建前仍须确认未被占用。平台候选使用同一 0.3.5/135，失败重试不换号、不覆盖已发布制品。

## 修复内容

- Android 断网目录轮询失败保留 live/frozen 会话，成功的权威目录继续负责回收；认证失败原有拒绝策略保留。
- Android 9 的 Delta、Frozen、Broker Capture AES-GCM 改用数组偏移接口，修复 ByteBuffer 适配器 ShortBufferException；身份、AAD、认证标签、长度、摘要和失败清零检查保留。
- Android 10 及以下启用 adjustResize，使 IME Insets 和终端操作栏正确避让键盘；现代 Android 布局策略保留。
- Windows 登录设备状态响应仅有界读取一次，保留服务端错误诊断并释放响应；历史初始化避免重复扫描兄弟机器历史，保留迁移前祖先路径校验、NULL DACL、重解析点/硬链接等安全检查。
- 验收脚本使用实际 Relay 后继连接判定恢复，保留原 capture 分页及 WAL 文件落盘/ACK 断言；修正图片清屏丢失测试标记，补充有界失败取证。
- 对应源码打包修复空文件/单字节文件处理和敏感路径过滤；Server Dockerfile 的运行时版本仅在链接时移除 build 后缀，OCI 标签保留完整版本。

## 独立验证

| 检查 | 结果 |
| --- | --- |
| Frozen termlink2 真实生命周期 | `review-20261016-frozen-r6`，44/44：断网保留、Relay 新代次订阅、原 capture PAGE、历史边界、释放、5 分钟 TTL、过期后重建与输入恢复通过；Host 已清理，模拟器网络已恢复。 |
| Broker WAL 故障恢复 | `review-20261015-wal-r2`：故障 ACK=45，恢复 WAL/source/ACK=46、capture=1，6804 个新增洋红像素，Host exit 0。 |
| MI 6/Android 9 可见 UI | `mi6-relay-r5`：输出、IME 稳定布局、输入往返通过，ACK 46→94。 |
| 设备仪器测试 | MI 6 与 API36 模拟器各 6/6，含真实 JNI Delta、Frozen/Broker Capture Windows 向量及篡改拒绝。 |
| Android 单元测试 | 最终 app 474 + core-crypto 37，零失败/错误。 |
| Windows 回归 | ProductionTerminalHistoryValidation、AgentUpdateValidation 通过；含 Frozen 72 断言、ACL/迁移/长路径/扫描边界与设备状态 13 用例。 |
| 发布/源码/验收脚本 | Build-Release、CorrespondingSourceArchive、TailFirst、Broker、WAL 自检通过；原生锁五库与 core revision 均通过。 |

本机完整证据：工作区 `.workspace-validation/device-validation-20261015/` 与 `.workspace-validation/frozen-relay-20261016/`。本轮测试使用 termlink2、本地 Docker Relay 与独立账号；仅 Relay，不安排 LAN/NAT。

## 发布状态与边界

当前为候选，尚未构建/上传新正式制品。签名、精确源码 revision、对应源码下载 URL、制品哈希和最终 AppShip/Docker 状态必须在实际执行后记录，不能将本文件视为已发布证明。

Windows 普通客户端/Agent 未被测试进程替换。完整 H02–H04 通知、音频、剪贴板、触摸/OEM 矩阵，Shell 命令定位、Kitty shared-s 和多 recipient 主题仍有既存验收缺口；发布说明不宣称这些整组已关闭。旧 r412 inspect 静默未复现，本轮 WAL 成功不改写旧失败结论。

Server 生产构建要求现有 Cosign 工具及批准密钥；当前检查未找到，旧镜像未签名不能作为本次豁免。Docker Hub 当前直连也超时；这些门禁未满足时保留服务端本地验证并停止该平台外部发布，不绕过签名要求。
