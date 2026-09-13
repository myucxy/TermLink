# TermLink 网络连接层与直连传输

## 目标与不可破坏边界

连接稳定性优先于直连命中率。TermLink 把“账户/设备在线”“终端会话语义”“数据走哪条路径”
拆成三个彼此隔离的状态机：

| 层 | 长期通道 | 允许失败后的动作 | 禁止动作 |
| --- | --- | --- | --- |
| 连接监督 | `/v1/realtime` WSS、心跳、重连 | 带抖动退避重建 socket，恢复订阅 | 因渲染、STUN 或直连失败断开账户状态 |
| 会话协议 | 现有 E2EE `Envelope`、seq、ACK、Gap、租约 | 单会话 Snapshot/有界后缀恢复 | 重置其他会话或把传输 receipt 当持久 ACK |
| 路径管理 | Relay、LAN/公网 QUIC、独立信令 | make-before-break，立即回退 Relay | 在直连未认证/未 commit 时停止 Relay 可用性 |

`termlink_realtime.proto` 仍是终端语义的唯一事实来源。直连传输的应用数据只是原样承载
序列化后的 `Envelope`，不得解析再序列化，不改变 sequence、ACK、E2EE、方向 ACL、控制租约、
Flow Control、Gap 或 Snapshot 规则。terminal-core/渲染器错误属于单会话数据消费故障，网络
控制面、其他会话和 Relay socket 必须继续工作。

### 发布与启用门禁

直连是可选的传输优化，不是登录、在线状态或远程控制成立的前提。代码进入安装包不等于生产启用：

- 服务端只有显式设置 `TERMLINK_DIRECT_TRANSPORT_ENABLED=true` 才注册 transport config、ticket
  和 signalling 路由；未设置或为 `false` 时不广告能力。
- Android Release 的 `TERMLINK_DIRECT_TRANSPORT_SHADOW` 固定为 `false`；只有本地 Debug 构建可用
  `-PtermlinkEnableDirectTransportShadow=true` 做不切流的验证。
- Windows signalling 和 native network-core 也必须由独立、默认关闭的运行时门禁创建。仅仅成功加载
  DLL、拿到 ticket、收到 candidate 或完成 UDP probe，都不得改变 Relay 路由。
- 生产开关必须支持服务端紧急关闭。客户端观察到配置撤回、未知能力、票据失败或连续三次直连失败时，
  同步回到 `RELAY_ONLY`，而不是继续使用缓存的授权。

当前落地状态是 **shadow foundation**：设备级 Relay supervisor、独立 signalling、票据、host/STUN
candidate、认证 UDP probe、同 socket QUIC/TLS exporter、bounded FFI、terminal-core/renderer 故障隔离，
以及多副本 signalling presence/route state 的独立 Valkey 共享背板已经有实现与自动测试。共享背板故障
采用 fail-closed，仅关闭 Direct 能力，不能阻止 Relay 服务启动。应用数据切流、双向 barrier/receipt、
Android/Windows 产品运行时完整接线和真实跨 NAT 矩阵仍是生产启用门禁，当前不得把功能描述为
“已启用直连”。

Realtime WSS generation 只有在真实 socket connect/read/write/close 或已认证心跳探测失败时才
允许替换。不可解析 protobuf、错误 Envelope 元数据/密文、单会话队列溢出、History/Snapshot
恢复失败、renderer/terminal-core 异常、信令/STUN/Probe/QUIC 失败都只丢弃或退休对应应用帧、
会话或 direct epoch；不得关闭健康 WSS、重启 Agent/SessionHost，也不得改变 durable ACK。

## 通道模型

每个已登录设备维护以下逻辑通道：

1. **Realtime Relay**：`wss /v1/realtime` 始终连接并持续心跳。Relay 模式下传输全部
   Envelope；直连模式下仍承载心跳、回退重发及未切换的会话。
2. **Transport Signalling**：`wss /v1/transport/signals`，只传输
   `TransportSignalEnvelopeV1`。它使用独立 seq/ACK、密钥派生和 nonce 域；断开不影响
   Realtime Relay，恢复后可重新协商更高 path epoch。
3. **Direct Data**：通过候选探测建立的可靠 QUIC。只有短期服务器票据、设备签名、E2EE
   交换的临时证书指纹和 QUIC TLS exporter 全部匹配后，才接受 `DirectPathFrameV1`。

信令 WebSocket 使用与 Realtime 相同的 OIDC、设备 ID、timestamp 和一次性 nonce 头，但设备
签名明文严格改为：

```text
TERMLINK-TRANSPORT-SIGNAL-PROOF-V1\n
GET\n
/v1/transport/signals\n
<timestamp>\n
<nonce>\n
<lowercase SHA-256 of empty body>
```

服务端独立消费该 nonce，并校验 source/recipient 是同账户内的已批准设备。终端 Envelope 不得
进入该端点，TransportSignalEnvelope 也不得进入 `/v1/realtime`。

升级后客户端先发送不超过 4 KiB 的 UTF-8 JSON：

```json
{"type":"CLIENT_HELLO","protocol_versions":[1],"capabilities":["direct_transport_v1","direct_transport_shadow_multipair_v2","direct_transport_data_v1","direct_session_cutover_v1"],"role":"android"}
```

`role` 只能是 `pc` 或 `android` 并与注册设备一致。服务端返回：

```json
{"type":"SERVER_WELCOME","protocol_version":1,"capabilities":["direct_transport_shadow_v1","direct_transport_shadow_multipair_v2","direct_transport_data_v1","direct_session_cutover_v1"],"heartbeat_interval_ms":15000,"max_signal_frame_bytes":65536}
```

之后只接受 binary `TransportSignalEnvelopeV1`；信令存活使用 WebSocket ping/pong，连续 45 秒没有
有效 pong 才重建信令 socket。服务端不生成 signal ACK，outer `signal_ack` 是两个 E2EE peer
之间的内存游标。JSON 未知字段忽略，缺少上述必填字段、协商不到 version 1，或 Welcome 没有
`direct_transport_shadow_v1` 就只关闭信令 lane。配置端点还必须同时发布 `direct_signaling_v1` 和
`direct_transport_shadow_v1`。生产数据切流还要求配置端点的 `direct_routing_permitted=true`，以及
配置、Realtime Welcome 和 Transport Signalling Welcome 同时协商 `direct_transport_data_v1` 与
`direct_session_cutover_v1`；缺少任一能力都必须继续使用 Relay。客户端 Hello 中的
`direct_transport_v1` 仅是协议协商请求，不能单独解释为服务端允许生产数据切流。

`direct_transport_shadow_multipair_v2` 是独立的双端能力：服务端只有在当前发送端与接收端的
presence 都协商成功时才转发 signal type 11..14。Valkey presence 的 schema/capability 也参与
generation fence；旧 schema 或任一不支持的 peer 只能继续 V1。拒绝 V2 信令不能消费该方向的
signal sequence/nonce，也不得影响 Realtime Relay。

`direct_transport_data_v1` 表示能够承载 Direct Envelope，`direct_session_cutover_v1` 表示能够按
会话、方向和 barrier 做增量切换/注销/回退。两者必须成对协商，避免旧的方向级实现把晚加入会话
误报为 Direct，或在单会话关闭时退休同方向的其他会话。服务端只在显式 routing gate 打开且当前
两端的 signalling presence 都声明二者时转发 signal type 6..9；legacy data-only peer 保持 Relay。

连接监督必须使用独立控制执行器处理 socket 生命周期、ping/pong、认证刷新与订阅。各会话的
Envelope 应用在有界串行队列中运行；单会话队列饱和时发送现有 Flow Control 或暂停该会话，
不得阻塞心跳和其他会话。网络在线、路径状态和 renderer 状态分别上报，UI 不得把
`renderer_failed` 显示成服务器离线。

## 候选收集与 NAT 打洞

V1 是受限的 ICE-like UDP/QUIC 流程，不宣称完整实现 RFC 8445：

完整的候选并发检查协商 `direct_transport_shadow_multipair_v2`，并要求 native ABI v4；冻结的
ABI v3 仅作为单候选 shadow 兼容层，不能宣称完成下述 multipair 流程，也不得作为 v4 失败时
的生产回退。

ABI v4 handle 销毁必须先同步取消并等待其唯一 native worker 完全退出，之后平台才可卸载
native library。不能让 detached worker 在 `destroy` 返回后继续执行 DLL/SO 中的 Gather、检查、
QUIC 或签名等待代码；该生命周期错误可能使整个 Agent/Android 进程崩溃，并间接中断原本独立的
Relay。

1. 从当前活跃、可路由接口收集 host 候选；排除 loopback、unspecified、multicast、虚拟断开
   接口及 IPv6 link-local。候选只含 4/16 字节 IP，不接受 hostname、URL 或任意 scheme。
2. host 候选立即通过成对 E2EE 信令交换，LAN 路径获得最高 priority，并有 700 ms 的优先
   探测窗口。Wi-Fi/以太网同网段通常在此阶段完成，不等待 STUN 超时。
3. 并行向服务端下发的 allowlist STUN 端点发送 RFC 8489 Binding 请求，严格校验 magic
   cookie、transaction ID、响应源和 XOR-MAPPED-ADDRESS，再生成 server-reflexive 候选。
4. 发起方先用 `DirectConnectivityCheckBatchV2` 下发有序检查计划；双方从候选所表示的同一个
   UDP socket/port 发送 `TLD2\n` + `DirectConnectivityDatagramV2`，完成 simultaneous open。
   去重后最多 32 对、最多同时探测四对；PROBE 与 PROBE_ACK 都认证且不得放大响应。只有双方
   都验证同一 pair 后，才以 `DirectNominationV2`/`DirectNominationAckV2` 提交唯一 winning pair。
5. 双向 UDP 可达后，ticket initiator 是 QUIC client，responder 是 QUIC listener；QUIC 必须
   复用探测/STUN 的本地 socket 与端口，否则 NAT mapping 无效。平台高级 QUIC API 不能接管该
   socket 时，应使用受审计的 ICE/QUIC binding，不得静默换端口后宣称打洞成功。
6. QUIC 建立后完成双向 peer proof；双方根据同一候选 pair、证书指纹、proof 和 TLS exporter
   计算 path binding，匹配后返回 nomination result。
7. `RELAY` candidate 与 `TURN_UDP_QUIC` 只预留字段。没有
   `direct_transport_turn_v1`、短期 TURN 凭据轮换和故障测试时不得生成或尝试；探测失败直接
   使用现有 WSS Relay。

网络变化会使当前候选集失效。切换 Wi-Fi、移动网络、VPN 或接口地址后，立即回退 Relay、退休
旧 epoch，再请求更高 epoch 的新票据；不得在旧 QUIC 上继续发送新 Envelope。

## 票据与点对点认证

服务端仅向同一账户内、未撤销且策略允许远程连接的两个设备签发 `DirectPathTicketV1`。发起端
通过已认证的 `POST /v1/transport/tickets` 提交 target device ID、随机 connection ID 和请求的
carrier；服务端从认证身份确定 initiator，并在该账户的 canonical device pair（两个 16 字节 ID
按 unsigned byte lexicographic 排序）上原子分配递增 epoch。客户端不得自报 account、initiator
或 epoch。V1 票据约束如下：

```json
{"target_device_id":"10213243-5465-7687-98a9-bacbdcedfe0f","connection_id":"00112233-4455-4677-8899-aabbccddeeff","requested_carriers":["UDP_QUIC"]}
```

成功响应只需返回签名后的规范对象，避免 JSON 与 protobuf 出现两份 claims：

```json
{"protocol_version":1,"ticket_protobuf":"<unpadded base64url DirectPathTicketV1>"}
```

JSON UUID 为 lowercase canonical RFC 4122 文本，proto 为对应的 16 个 network-order bytes；请求
总计不超过 4 KiB、carrier 最多两个且不重复。服务端的 STUN allowlist 和 Ed25519 验证公钥由
已认证 `GET /v1/transport/config` 返回；配置最多两个 UDP STUN endpoint、最多四个当前/轮换
signing key，主机名在配置刷新时解析并固定响应源，peer 信令不能覆盖配置。400/401/403/409/
429 只使本次直连尝试失败，客户端继续 Relay，且响应不得回显设备密钥或内部异常。

- `ticket_id`、`account_id`、两个 device ID、`connection_id` 和 `server_nonce` 均为 16 字节；
- `path_epoch` 在 canonical device pair 内单调递增，范围 `1..Int64.MaxValue`；
- `issued_at_ms <= expires_at_ms`，有效期不超过 120 秒，时钟容差不超过 30 秒；
- `server_signing_key_id` 为 1..64 字节，Ed25519 signature 为 64 字节；
- 允许 carrier 不重复、最多 2 个；当前只允许 `UDP_QUIC`；
- 票据签名输入为 UTF-8 域 `TERMLINK-DIRECT-TICKET-V1\n` 加字段 1..12 的确定性 protobuf；
  验证使用从已认证 HTTPS 配置获得并固定 key ID 的服务端公钥；
- 票据不是登录 token，不能调用 REST、注册设备或改变控制策略。它必须与双方设备私钥证明和
  当前 QUIC TLS exporter 一起使用，泄露票据本身不能冒充设备。

QUIC 临时证书 SHA-256 指纹经信令 E2EE 交换，必须为 32 字节。双方随后签名
`TERMLINK-DIRECT-PEER-PROOF-V1\n`、ticket SHA-256、角色、双方 device ID、connection ID、
path epoch、16..32 字节随机 proof nonce 及 32 字节 TLS exporter SHA-256。公钥以服务端已认证
设备清单为准，不信任 proof 自带公钥。信令只交换临时 QUIC leaf certificate DER 的 SHA-256，
不得传 raw DER、certificate chain 或私钥；QUIC TLS handshake 提供 leaf DER，接收端对其原始
DER 求 SHA-256 并与 E2EE offer/answer 中按设备角色绑定的指纹做 constant-time 比较。任一不匹配
只关闭当前 QUIC 和 path attempt。

V1 QUIC application wire 只允许 4 字节 unsigned big-endian 长度前缀加
`DirectPathFrameV1`。每个方向的第 1 帧必须是 `peer_proof`，第 2 帧必须是
`peer_proof_result`；双方都验证 ticket、角色、设备签名、证书 pin、TLS exporter 和收到的
peer-proof transcript SHA-256 后，才允许从第 3 帧开始发送 ping、receipt 或 Envelope。
`TLPP1`、裸 proof bytes 或任何平台私有 proof record 都不属于 V1 wire，必须在 native/runtime
正式接线前删除。proof/result 失败、乱序、重复或超限只退休当前 QUIC/path epoch，Relay 不变。

信令 AEAD 从现有批准设备对的 32 字节 pairwise secret 派生，但不能复用 Session key。V1 使用
HKDF-SHA256：salt 为 `SHA256("TERMLINK-TRANSPORT-SIGNAL-AEAD-V1\0")`；info 依次拼接同一
ASCII domain、connection ID、8 字节 big-endian path epoch、sender device ID、recipient device
ID 和 key ID；输出 32 字节方向密钥。nonce 为 12 个随机字节，同一派生密钥下不得重复。
同一次 path attempt 的两个方向必须使用同一个 key ID；该值固定为 ticket responder 设备在服务端
设备记录中的当前 `key_id`（不是 initiator/Android recipient 的 `/grants` 条目，也不能由 key epoch
临时拼接）。发起方从已认证设备清单取得该值，响应方使用自己的持久化设备 key ID；值必须为
1..64 字节严格 UTF-8，二者不一致时只拒绝当前 Direct attempt，不能影响 Relay。
`signal_seq` 按 connection/epoch/direction 从 1 递增，`signal_ack` 是内存中的最高连续已认证
信令 seq，重连后可重新发送当前完整协商状态；两者均不得写入 terminal ACK 存储。

UDP probe 使用另一派生 key：salt 为 `SHA256("TERMLINK-DIRECT-PROBE-HMAC-V1\0")`，info
依次为该 domain、connection ID、path epoch、nomination ID、sender 和 recipient device ID；
HMAC 输入是该 domain 加 `DirectConnectivityDatagramV1` 字段 1..12 的确定性 protobuf，取
  SHA-256 tag 前 16 字节。PROBE 的 `response_nonce` 为空并携带 16 字节随机
  `anti_amplification_padding`；ACK 回显 16 字节 probe nonce、加入 16 字节 response nonce，并将
  padding 置空，使 ACK 的编码线长不大于 PROBE。packet 必须来自声明的 remote candidate endpoint，时间偏差不超过
30 秒，`TLD1\n` 加 protobuf 总长不超过 512 字节。

Multipair V2 使用完全隔离的派生域 `TERMLINK-DIRECT-PROBE-HMAC-V2`：salt 为
`SHA256("TERMLINK-DIRECT-PROBE-HMAC-V2\0")`，info 依次拼接不含结尾 NUL 的同一 ASCII domain、
connection ID、8 字节 big-endian path epoch、check round ID、sender device ID 和 recipient
device ID。每个方向各自派生 32 字节 key。每对的 16 字节 probe nonce 是：

```text
HMAC-SHA256(direction_key,
  "TERMLINK-DIRECT-PAIR-PROBE-NONCE-V2\0" ||
  connectivity_nonce || initiator_candidate_id || responder_candidate_id)[0..16]
```

TLD2 HMAC 使用 `TERMLINK-DIRECT-PROBE-HMAC-V2\0`，且 protobuf 字段 6 是 check round ID。
V1/V2 magic、protocol version、HKDF/HMAC domain 和 nonce 均不可互换；候选对必须始终按 ticket
initiator/responder 排列，不能按本机角色翻转。

`direct_path_binding` 是 SHA-256：domain `TERMLINK-DIRECT-PATH-BINDING-V1\0`，随后拼接完整
signed ticket SHA-256、initiator/responder candidate ID、两个临时证书指纹、双方 peer-proof
transcript SHA-256 及 QUIC TLS exporter SHA-256。这里的 peer-proof hash 明确定义为对
`TERMLINK-DIRECT-PEER-PROOF-V1\n` 开始、截至 TLS exporter SHA-256 的完整签名 transcript 求
SHA-256；不包含设备签名，也不包含 `DirectPeerProofV1`、`TLPP1` 或其他平台传输记录的 framing。
所有成对字段都严格按 initiator 后 responder 排列：initiator candidate、responder candidate、
initiator certificate、responder certificate、initiator proof、responder proof；不得按本地/远端
角色动态交换顺序。TLS exporter label 是 ASCII `EXPORTER-TERMLINK-DIRECT-V1`，context 是
ticket SHA-256、connection ID、8 字节 big-endian path epoch 的直接拼接；binding 中放 exporter
输出的 SHA-256，而不是原始 exporter secret。
cutover proposal digest 是
`SHA256("TERMLINK-DIRECT-CUTOVER-V1\0" || connection_id || epoch_u64_be ||
deterministic_proposal)`；字段顺序和 session barrier 顺序必须 canonical，session ID 按 unsigned
byte lexicographic 递增且不重复。

## 信令与候选的规范上限

接收端必须先检查 wire 大小，再分配或解析；违反任一限制时退休当前 path attempt，并保持 Relay：

| 对象 | 上限/校验 |
| --- | --- |
| `TransportSignalEnvelopeV1` | 总计 64 KiB；ID 各 16 字节；nonce 12 字节；key ID 1..64 字节；seq/ack/epoch 不超过 `Int64.MaxValue` |
| 信令 AEAD | ChaCha20-Poly1305；HKDF info/domain `TERMLINK-TRANSPORT-SIGNAL-AEAD-V1`；AAD 为除 ciphertext 外所有 outer fields 的确定性编码 |
| 候选总数 | 每设备每 epoch 最多 16 个；每次 offer/answer/update 最多 8 个；candidate ID 16 字节且不可变 |
| 地址/端口 | IP 恰为 4 或 16 字节；port `1024..65535`；禁止 loopback/unspecified/multicast/broadcast/reserved；V1 禁止 IPv6 link-local |
| 候选时效 | 不晚于票据过期，且从接收时算不超过 120 秒；过期候选不探测 |
| 候选字段 | priority `1..2^31-1`；network cost `0..999`；HOST 的 related ID 为空，SRFLX 必须引用本 epoch 的 HOST |
| pair 探测 | 去重后最多 32 对，同时最多 4 对；每对 1500 ms，总预算 10 秒；指数退避有随机抖动 |
| UDP probe | V1 为 `TLD1\n`，multipair 为 `TLD2\n`；protobuf 总计最多 512 字节；nonce/padding 各 16 字节；每 pair 每方向最多 4 次；ACK 编码线长不得大于 PROBE |
| connectivity check | round ID 与 connectivity nonce 各 16 字节；batch seq 从 1 严格连续；每批最多 8 对、累计最多 32 对、同时最多 4 对 |
| nomination | ID 16 字节；只能引用本 epoch、本 check round 已双向认证的候选；最多一个 committed pair；QUIC 前必须收到对端 nomination ACK |
| 切换栅栏 | 每条消息最多 32 个 session；session/barrier ID 16 字节；每方向 first seq 为 `1..Int64.MaxValue`；重复宽限 `0..5000 ms` |
| Direct frame | 4 字节 unsigned big-endian 长度加 protobuf 总计不超过 1,048,640 字节；其中 realtime Envelope 仍不超过 1 MiB |
| peer proof/result | 每方向严格为 frame seq 1/2；proof frame（含 4 字节长度前缀）最多 2640 字节，result frame 最多 256 字节；完整 ticket 必须在 `DirectPeerProofV1.ticket` 中，不能改用省略 claims 的私有记录 |
| receipt ranges | 最多 8 个、升序、不重叠，`start <= end <= Int64.MaxValue`；只确认 direct frame seq |
| reason code | ASCII `[A-Z0-9_]{1,64}`，不得携带异常文本、IP、终端内容或凭据 |

候选 IP 是敏感网络元数据：仅存在于 pairwise E2EE ciphertext 和进程内短期状态，不进入 Relay
日志、analytics、崩溃上报或持久数据库。STUN 响应地址不能用于 HTTP、文件或 DNS 请求，避免把
候选机制变成 SSRF。HOST 仅探测由本机活跃路由可达的 peer subnet，所有探测使用高位临时端口、
固定小包和硬速率上限。服务端对 ticket、信令速率和并发 path attempt 按账户及设备双重限流。

### 未知字段与规范编码

V1 按边界采用三种明确策略，平台不得自行选择：

- `CLIENT_HELLO`、`SERVER_WELCOME` 和 HTTPS config 的有界 JSON 可忽略未知字段，但已知必填字段、
  version/capability、类型和大小仍严格校验；未知字段不得改变已知字段语义。
- ticket、signal outer/payload、candidate、probe、peer proof、path frame、barrier 和 receipt 都是
  安全敏感 transport protobuf。V1 在校验/认证前必须按“只复制已知字段到新 message 后确定性
  serialize，并与收到的完整 wire 逐字节相等”的方式拒绝 unknown field、duplicate singular field、
  非最短 varint、非规范字段顺序和未知 enum。拒绝只终止当前 signalling/direct attempt，并同步
  回 `RELAY_ONLY`，不得关闭 Realtime WSS。新增 transport 字段必须通过新的 capability/version
  协商，不能依赖旧 V1 接收端静默保留。
- `DirectPathFrameV1.realtime_envelope` 是唯一例外：transport 把它当 opaque bytes，必须逐字节转发、
  缓存、duplicate-grace 和 Relay fallback，不在 transport 层 parse/re-serialize。因此现有
  Realtime Envelope 的 unknown fields 也被完整保留。

## 路径状态机

```text
RELAY_ONLY
  -> GATHERING -> PROBING -> AUTHENTICATING -> NOMINATED
  -> CUTOVER_PENDING -> DIRECT_ACTIVE

任意直连状态 --失败/超时/网络变化--> RELAY_ONLY
DIRECT_ACTIVE --重新优化--> RELAY_ONLY -> 更高 epoch 的 GATHERING
```

核心不变量：

- `RELAY_ONLY` 是启动状态和所有异常的同步恢复状态；进入它不关闭 `/v1/realtime`，不重新登录，
  不重建 renderer，也不改变 durable ACK。
- 直连控制面只允许发布 `state_changed`、`direct_failed`、`shadow_ready/path_ready` 和 stale-ignore
  类事件。它的 API、FFI command/event、异常类型和回调中不得存在 Relay disconnect/reconnect、
  account logout/login、Agent/SessionHost stop/restart、renderer reset、session close 或 durable ACK
  advance 动作；这是一条需要用穷举事件 surface 和故障注入自动验证的架构不变量。
- 对信令解析、候选、STUN、UDP probe、QUIC、peer proof、队列饱和、renderer、terminal-core、
  History/Snapshot 和单会话 dispatcher 错误一律归类为可降级故障，同步退休对应 direct epoch、
  renderer 或 session work item，并保持/回到 `RELAY_ONLY`。只有 Relay 自身 socket connect/read/
  write/close、已认证 Relay 心跳失败，或账户凭据被服务端明确拒绝，才允许连接监督器改变 Relay
  generation；这些硬故障也不能由直连控制器代为判断。
- offer/answer/candidate 只能追加到当前 epoch；同 candidate ID 字段变化、旧 epoch 消息、重复
  signal seq 或 ticket 不匹配均丢弃并记录安全计数。
- 只有双方 peer proof、同一个 nomination pair 和 `direct_path_binding` 全部成功，才能提出
  cutover。路径 ready 不等于会话已切换。
- 每个 proposal 只能声明提议方自己的一个 outbound Envelope direction，且每个 session 从
  提议时已预留的 next seq 开始。两方向用独立 barrier ID/事务切换，不能替对方选择 seq。
- proposal 发出后仍通过 Relay 正常发送，同时从 barrier 起在每方向最多缓存 256 个或 8 MiB
  原始 Envelope（先到者为限）。缓存越界就放弃切换，不暂停会话。accept 回显 proposal 的
  确定性 SHA-256。接收方必须先等待每个 session 的公共连续应用游标达到
  `first_direct_seq - 1` 才能 accept；超时整笔拒绝并保持 Relay，避免 Direct 越过尚在途的
  Relay 前缀而误触发 History Gap。提议方随后发 commit，对端先安装接收 barrier，再回发完全
  相同的 commit。提议方收到 echo 前，Relay 保持权威路径。
- `barrier_sha256` 由 native FFI 再次权威校验，不只依赖平台 codec：输入为
  `SHA256("TERMLINK-DIRECT-CUTOVER-V1\0" || connection_id || path_epoch_be64 ||
  deterministic DirectCutoverProposalV1)`；direction 决定每个 session 仅编码字段 2 或字段 3，session
  必须按无符号 16 字节 ID 排序，未使用的 FFI session 槽不进入 protobuf。
- commit echo 后先在 direct 重放 barrier 以来的缓存，再在 `duplicate_grace_ms` 内把同一
  序列化 Envelope 同时发给 Relay 和 direct；接收端使用既有 session/direction seq 去重。
  宽限结束才进入该方向 direct-only，WSS 心跳仍继续。另一方向可仍是 Relay。
- 首个生产版本使用最小、双端完全相同的显式白名单：Android→PC 仅 `INPUT_EVENT`，PC→Android
  仅 `OUTPUT_CHUNK`。`GAP`、`INPUT_ACCEPTED`/`INPUT_REJECTED`、Resize、Lease、History、终止、
  lifecycle、`HEARTBEAT`、`GOODBYE`、`SESSION_UPSERT`、`SESSION_REMOVED`、`SUBSCRIBE`、
  `UNSUBSCRIBE`、`OUTPUT_ACK`、`FLOW_CONTROL`、未指定和未知类型全部走 Relay。分类只读取已经
  解析的 Envelope 元数据，真正发送、缓存和回退仍使用第一次序列化的原始 Envelope bytes，
  禁止 parse 后重新序列化。以后扩大白名单必须两端同版、逐类型增加授权和故障测试。
- Direct `INPUT_EVENT` 虽不经过 Relay 的逐帧路由检查，Windows Agent 在写入 SessionHost 前仍须
  向服务端查询绑定到 `(session_id, sender_device_id)` 的最新控制授权，并同时验证发送设备仍有
  CONTROL/MANAGE 权限、lease ID/version/expiry、policy version 和控制模式。`SINGLE`/`TAKEOVER`
  只允许当前 holder；`COLLABORATIVE` 才允许同一已订阅会话中的其他已授权设备。该决定只缓存
  1 秒；查询失败、字段缺失或策略变化均拒绝输入但不终止 Relay。PC 本地持久化的 input ID +
  exact Envelope digest fence 仍是最终 at-most-once 边界。
- Relay 控制帧与 Direct 数据帧共享同一会话 Envelope seq，因此两种载波必须先进入按 peer
  隔离的单写者公共入口，再按 `(direction, session_id)` 重排后交给现有会话处理器。宽限期内
  Direct 副本先到时先暂存，等 Relay 同序列副本对照或宽限到期；同 seq 同 bytes 只应用一次，
  在 native 仍保留该 seq 的入站摘要时发现同 seq 不同 bytes，立即退休该 Direct epoch，并以
  Relay 与现有 durable ACK 恢复为准。平台把 application cursor 推进到该 seq 后，native 可释放
  对应 bytes/摘要；之后到达的旧 seq 必须先经过平台现有 journal/durable-ACK digest fence，再交给
  native 标记为 DROP，冲突仍由平台保留权威 Relay bytes 并触发既有恢复流程。
- `DirectTransportReceiptV1` 只用于释放发送端的有界直连重发缓存。它不表示 terminal-core 已
  应用，不可写入 ACK 数据库，也不可生成 `OUTPUT_ACK`。每个 epoch/direction 的 direct
  `frame_seq` 独立从 1 递增；receipt 只确认对端方向的 frame seq。PC authoritative journal 仍保留
  到现有 cumulative application ACK；Android 的 input/resize/lease 等语义 outbox 仍保留到
  对应 result 或既有幂等完成条件，transport receipt 不得删除它们。V1 是单一可靠有序 stream，
  因此 `missing` 必须为空；receipt 本身不产生 receipt，禁止 receipt-of-receipt 循环。
- direct 异常时，本地路由立刻选择 Relay。所有尚未收到 transport receipt 的 Envelope data frame 按
  原始 Envelope bytes 通过 WSS 重发；未来 Envelope 继续使用已有 next seq。重复由现有协议
  去除。已经收到 transport receipt 但尚未获得应用确认的数据，从上述 journal/semantic outbox
  继续按现有 ACK、input ID 和结果状态机恢复；缺口继续走现有 Gap/Snapshot 规则。
- 方向从 `DIRECT_ACTIVE` 回退后进入当前 epoch 内部的 drain-only 状态：已经分配并进入可靠流发送
  future 的迟到 Direct Envelope 只消耗该方向 transport `frame_seq`，不交付应用、也不生成 receipt；
  每方向最多容忍与发送缓存硬上限一致的 256 帧，第 257 帧按协议违规拒绝。该方向保持 Relay 且
  在更高 path epoch 前不得再次切流。初始 `RELAY_ONLY` 或仍在协商阶段收到 Direct Envelope 必须
  fail closed，不能借 drain 规则静默吞掉。native path 进入 failed/cancelled/
  network-changed 终态后拒绝接管所有新的 Relay/Direct ingress，只允许平台排空此前已归 native
  所有的 route actions、incoming bytes 和事件。
- fallback notice 只帮助对端快速收敛；信令断开、notice 丢失或对端较慢均不得阻塞本地回退。

## 稳定性、退避与资源隔离

- Realtime socket 失败采用 full-jitter 指数退避：250 ms 起步、30 秒封顶；有效收发 30 秒后
  清零 backoff。认证失效走单飞 token refresh，禁止多个会话各自重建 socket。
- Transport Signalling 独立退避，失败不增加 Realtime backoff。直连尝试同一设备对至少间隔
  30 秒；连续三次失败冷却 5 分钟，但 Relay 始终可用。
- QUIC keepalive 只在 direct-active 时运行；移动端进入后台且平台不允许保活时主动回退 Relay，
  不伪装成服务器断线。恢复前台先恢复 WSS 会话，再异步重新打洞。
- 每会话 ordered apply queue、直连发送缓存和历史恢复各自有硬上限。渲染队列失败只退休该
  renderer 实例，通过 Snapshot + bounded suffix 恢复；连接监督线程不可等待 renderer join。
- 心跳与 transport control 使用控制 lane；大 Envelope、历史和 renderer 工作使用数据 lane，
  避免大量重绘或 resize 阻塞 ping/pong。

## 分阶段完成定义

阶段边界按可回滚能力定义，不按平台分别交付：

1. **连接隔离完成**：一个账户级 Realtime supervisor、每会话有界 dispatcher、renderer 故障
   不断 socket；锁屏、网络切换、坏 Envelope 和多会话压力故障注入通过。
2. **直连基础完成**：独立信令、票据、host/STUN candidates、QUIC peer proof、LAN-first 与
   srflx hole punching 互操作通过；能力仍可服务端一键关闭。
3. **无损切换完成**：双向 per-session barrier、重复宽限、transport receipt、Relay 原字节回退
   重发通过；对每个切换点证明无丢失、无重复应用、durable ACK 不越界。
4. **生产启用完成**：Android/Windows 在同 LAN、常见 cone NAT、对称 NAT 失败、VPN、Wi-Fi/5G
   切换、锁屏恢复和 Relay/信令/QUIC 分别断开的矩阵通过，才默认开放
   `direct_transport_v1`。TURN 保持后续独立能力。

## Termux 参考边界

已复核仓库固定的 `terminal-forks/termux-terminal/UPSTREAM.md`（上游
`termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`）、patch queue 和
`android/terminal-engine-termux/`。固定来源明确排除了 Termux `TerminalSession`、本地 PTY、
应用服务和网络认证边界；所含 terminal-emulator/terminal-view 也没有 WebSocket、STUN、TURN、
ICE 或 QUIC 实现。因此本设计没有可直接复用的 Termux 网络方案，只保留其“终端渲染不拥有远程
transport/auth”的边界作为行为参考。NAT、E2EE 信令、Relay 回退和 durable ACK 均由 TermLink
自行实现，不向 GPLv3 terminal fork 引入或复制网络代码。

## 必测矩阵与可观测性

自动测试至少覆盖：proto 恶意边界、ticket/signature/expiry/replay、候选去重与私网过滤、host
优先、STUN transaction spoof、32-pair/10 秒预算、旧 epoch fencing、barrier digest 不一致、
commit 前断线、duplicate grace、direct frame 截断/超限、receipt 不推进 durable ACK、QUIC
中断后 Relay 原字节重发，以及一个 renderer 抛错时 socket 和其他会话仍健康。

跨平台密码学测试必须共同读取 `protocol/test-vectors/direct-transport-crypto-v1.json`，至少验证
RFC 4122 UUID network-order bytes、big-endian epoch、signalling HKDF + ChaCha20-Poly1305 AAD、
probe HKDF + HMAC、peer-proof transcript、TLS exporter context 和 path binding。任何平台只验证
自己的自生成向量不计为互操作通过。

端到端测试至少执行：同一 Wi-Fi、Windows 有线 + Android Wi-Fi、不同 NAT、对称 NAT 必然回退、
IPv4/IPv6、VPN 开关、Windows 防火墙拒绝 UDP、Android 锁屏/唤醒、连续 resize/OpenCode 重绘及
两个移动端同时观察。验收不是“直连必须成功”，而是“可直连时无损切换；不可直连时在预算内
稳定留在 Relay，用户会话不断”。

指标只记录枚举和时延：`relay_connected`、`signal_connected`、`path_state`、候选类型计数、
probe/认证/cutover 时延、fallback reason、重发字节数、seq gap 和 queue depth。禁止记录候选
地址、ticket、key/nonce、Envelope bytes、终端文本或完整异常；UI 的绿色在线点只由 Realtime
Relay 的已认证心跳决定，直连状态另列“局域网直连/公网直连/Relay”。
