# Kitty 文件捕获与有序重放 V1

状态：底层存储、绑定、编解码及双端产品接线已实现；完整真实产品验收仍需按接线门禁完成。仅适用于持久 profile 为 `terminal_state_2_22_v1` 或 `terminal_state_2_22_v2` 的 Graphics 会话；后者使用 Unicode16 宽度规则。对象 wire 名称中的 V1 不代表会话只能使用 v1 state contract。

## 传输与持久化

一次原始 Output 仍只占一个 source sequence。含外部图像请求时使用 BrokerOutput 引用不可变捕获对象；无请求时仍为普通 Output。对象通过 Host History 的 512 KiB 独立 ACK 分片传输，不占终端窗口，分片 ACK 不推进终端 durable ACK。

会话内解析器给出捕获身份。文件读取只允许会话授权根句柄之下的常规文件；拒绝重解析、硬链接、跨卷、设备路径、ADS、路径逃逸。引用事件和对象接口不接受文件系统路径。原始 Output 本来包含程序发出的转义序列，因此它本身仍作为敏感终端内容加密，不能打印到遥测。

Host 在发布引用事件之前持久化捕获对象；Agent 在日志持久确认之前按 session、sequence、UUID、长度和 SHA-256 重新读取校验。读取失败明确停止该事件，不忽略捕获或重新读取原路径。捕获一经引用，保留到该会话历史删除；删除前取消资源请求，不能为腾出配额删除仍被引用的对象。DPAPI 存储原子 rename 已验证，断电目录元数据顺序尚未验证，因此缺失对象必须报告明确恢复错误。

发生存储配额不足时取消第一次 native 预处理，然后重新解析同一原始 Output，把捕获全部固定为 OverLimit。全失败的小对象允许直接内联到引用事件，无需新建 sidecar；不能把失败改成无图像的普通 Output。

临时文件只在 capture 已持久化、事件已 durable、native 已提交后，用原先保留的同一文件句柄删除。不能用后来重新解析的路径删除。

Windows `t=s` 使用合作式共享内存 broker：子程序通过 `TERMLINK_KITTY_SHM_PIPE` 指定的当前用户管道申请映射，Host 根据内核客户端 PID 核验其属于当前会话 Job，生成一次性随机名称并保留映射句柄。子程序写入后以该名称作为标准 Kitty `t=s` 的 Base64 payload；Host 仅查本会话注册表，验证 O/S 范围及配额后复制为私有字节，再沿同一 TLCAPV1 持久化链路处理。固定管道申请协议见 `win-agent/src/SessionHost/KittySharedMemoryBroker.cs` 的头部说明。

任意未登记 Win32 名称和 WSL/POSIX 共享内存不透明兼容；未知、过期、跨会话或读取范围无效返回 Unavailable，不能使用终端提供的名称调用 `OpenFileMapping`。私有副本是重放的不可变依据，原映射不能保证停止并发写入，合作子程序应先完成写入再发 APC。外部不可读资源的 PTY 错误统一，内部仍保留失败分类；图形内存额度与协议不支持错误另行区分。本段为开发中协议实现，产品验收与开关状态见整合记录。

## TLCAPV1 捕获对象

所有整数为 little endian。对象最大 32 MiB（包括头和记录）；每条成功捕获最大 16 MiB，最多 64 条。先扫描完整结构及累计长度，再分配/复制捕获数据。所有所有权对象可关闭并清零，解析失败也释放已分配的数据。

| 偏移 | 长度 | 值 |
| --- | --- | --- |
| 0 | 8 | `TLCAPV1\0` |
| 8 | 4 | header bytes = 32 |
| 12 | 4 | record count，1..64 |
| 16 | 8 | source sequence，0 < S < Int64.MaxValue |
| 24 | 4 | 对象完整长度 |
| 28 | 4 | 保留，必须为 0 |

每条记录为 96-byte redacted core identity、u32 outcome、u32 data length、data bytes。outcome 为 0 Bytes、1 Denied、2 Unavailable、3 UnsupportedMedium、4 OverLimit。失败结果长度必须为 0；成功可为空文件。

identity 使用 C ABI V1 的固定 little-endian 96 字节布局；ABI=1、struct size=96、has request=1、medium 为 ASCII `f/t/s`，source sequence 等于对象序号，ordinal 为 0..count-1，source offset >0，has read length 为 0/1，保留字段为零。read offset/length 原样保留。实际回放还必须逐项匹配真实 parser request 的完整身份，包括控制序列 digest；缺项、多项、身份变化均原子取消。

## TLBROV1 引用事件

所有整数为 little endian，UUID 为 RFC4122 网络字节序。原始 Output 最大 64 KiB，与 Host 的实际读取批量一致。头后紧跟 output，再跟可选 inline captures；不得有尾随数据。

| 偏移 | 长度 | 值 |
| --- | --- | --- |
| 0 | 8 | `TLBROV1\0` |
| 8 | 4 | header bytes = 96 |
| 12 | 4 | flags：0 外部对象，1 内联失败对象 |
| 16 | 8 | source sequence，必须与所属事件一致 |
| 24 | 16 | 外部 capture UUID；内联时全零 |
| 40 | 4 | canonical capture bytes，136..32 MiB |
| 44 | 4 | output bytes，1..65536 |
| 48 | 32 | canonical capture SHA-256 |
| 80 | 16 | 保留，全零 |

内联最多 6688 bytes，只允许所有 outcome 均失败且 data length 为 0。即使成功数据为零字节，也必须使用外部对象，避免两个数据路径对成功捕获产生不同保留语义。

## TLBCSEA1 加密对象

独立于 snapshot、Delta 和 frozen page 的加密域。AES-256-GCM、12-byte 随机 nonce、16-byte tag。最大密封对象为 32 MiB + 208 bytes，最多 65 个 512 KiB 分片。调用方在缓存/分配前预留完整预算。

固定头 192 bytes，头内整数为 big endian，UUID 为 RFC4122：magic `TLBCSEA1` @0；u16 version=1 @8；u16 header bytes=192 @10；session @12；PC @28；recipient @44；capture UUID @60；u64 source sequence @76；i64 key epoch @84；u32 canonical length @92；canonical SHA-256 @96；nonce @128；ASCII 会话持久 state contract @140（`terminal_state_2_22_v1` 或 `terminal_state_2_22_v2`，均为 22 字节），其后直到 191 全零。头后为 canonical ciphertext 和 GCM tag。打开对象时必须由已协商的会话 profile 指定预期 contract；对象头不能自行改变会话 profile。

HKDF-SHA256：IKM=现有 pairwise 32-byte key，salt=session UUID，输出 32 bytes。其 info、AAD 与 Key ID 随精确 state contract 分域：

| 会话 state contract | HKDF info 前缀（后接 PC UUID、recipient UUID、BE64(keyEpoch)） | AAD 前缀（后接完整 192-byte header） | Key ID 前缀 |
| --- | --- | --- | --- |
| `terminal_state_2_22_v1` | `TermLink broker capture key V1\n` | `TermLink broker capture AAD V1\n` | `broker_capture_v1:` |
| `terminal_state_2_22_v2` | `TermLink broker capture key V2\n` | `TermLink broker capture AAD V2\n` | `broker_capture_v2:` |

Key ID 后缀均为 `{recipient UUID 的32位小写hex}:{epoch十进制}`。v1 的 TLCAPV1/TLBROV1/TLBCSEA1 布局、密钥域与既有向量不变；v2 使用相同对象布局但不能复用 v1 的 Key ID、HKDF info 或 AAD。跨 profile 解密失败必须报告不兼容，不得按另一种 profile 重试。

Open 必须由调用方传入从有序引用事件取得的完整 expected identity、length、hash；认证后重新核验 canonical SHA-256 与 TLCAPV1 头。不能相信对象自己声称的身份。测试固定 nonce 仅用于向量，实际 Seal 默认随机。

## 接线门禁

新增 wire `MessageType.BROKER_OUTPUT=74`，payload 只含 `reference_v1`；Local IPC 对应 `LocalFrameType.BrokerOutput=24`。Host History request kind=8、object kind=6，request field 11 / response field 28 为 `BrokerCaptureSelectionV1`，包含对象 UUID、事件序号、canonical 长度和摘要。响应 `start_seq=end_seq=事件序号`，`snapshot_seq`、geometry、transcript cursor 均为 0；object ID 与 capture UUID 相同，format=`TERMLINK_BROKER_CAPTURE_V1`、version=1，外层 object SHA-256 对密封对象计算。错误不能携带对象，不能触发不带捕获的 fallback。

- `terminal_broker_captures_v1` 必须由 Relay 和双端协商；Host attach 返回该能力后才允许 BrokerOutput。Host 按持久 session profile 加密，Android 按已接受的同一个 profile 处理 live replay、历史 suffix replay 和缓存；旧客户端拒绝新消息，不能把引用重放为没有捕获的 Output。
- Android 接收循环遇到缺少对象的 BrokerOutput 时，有界排队并让出收件处理锁，由独立任务请求资源；不得在串行收件或 RemoteTerminalSession mutex 中等待同一 socket 的资源响应。
- 只在对象完整认证、密封缓存 durable、core 按序应用后推进该事件终端 ACK。关闭会话、掉线及取消必须释放所有预算和 pending 请求。
- Host 若在 native transaction 内等待 Agent durable ACK，ACK 读线程不能反过来等待同一个 transaction 或同步执行会发布事件的输入处理；接线前必须验证锁顺序与停止路径。
- 按 session/recipient 限制并发资源请求；删除会话时先取消传输再删资源，重连重放不得重新执行 PTY 回复、剪贴板或临时文件删除。

实际阶段证据见 core 仓 `INTEGRATION_STATUS_20261010.md`，既有 v1 合成向量见 `protocol/test-vectors/broker-captures-v1.json` 和 `broker-capture-sealed-v1.json`；它们仍只说明 v1 对象兼容性，不能代替 v2 或完整产品验收。
