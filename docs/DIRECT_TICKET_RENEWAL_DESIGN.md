# 直连票据续期设计（#2 · 审计 P1-2）

> 状态：设计稿 v1（待评审）
> 关联：06-project-audit #2/#16/#25；`server/internal/transport/tickets.go`；`network-core/crates/termlink-direct-ffi/src/v5.rs`；`win-agent/src/Agent/ProductionDirectTransport.cs`

## 一、问题

直连票据当前 `ticketTTL = 120s`，且数据面 data-loop 把 `ticket_expires_at_ms` 作为硬性终止分支（v5.rs：`ticket_expiry` sleep 触发即拆直连）。后果：

- 一切正常时直连每约 2 分钟被强制拆回 Relay，再走完整 gather→probe→QUIC→proof→cutover；
- "NAT 可直连则优先直连"的产品意图实际退化为"每 2 分钟内优先直连"；
- 重连有节流（同对 30s 间隔、连败 3 次冷却 5 分钟），高频拆建放大抖动。

## 二、目标 / 非目标

**目标**

1. 直连建立后可持续运行，不因固定 TTL 被动拆除；
2. 会话/票据仍可在服务端**紧急撤销**（立即失效）；
3. 20 天无交互登录生命周期的会话级策略不受影响；
4. 向后兼容：不支持续期的对端保持现行为（TTL 内直连、到期回 Relay）。

**非目标**

- 不改变票据签发时的身份验证强度（设备证明、epoch 分配、nonce 单次有效均保持）；
- 不解决对称 NAT 零命中（#45）与 MTU 协商（#48）；
- 不引入与 Relay 的第二条控制依赖（续期失败一律回落现行为）。

## 三、方案概述

**核心：已认证连接上的"续期票据"（renewal ticket）双向交换 + 服务端签发。**

票据仍然是服务端 Ed25519 签名的短期凭证，但：

1. 单票据 TTL 120s → **20 分钟**（`ticketTTL = 20 * time.Minute`）；
2. 新增续期消息：任一 peer 在票据剩余寿命 ≤ 1/2 时，经 Relay 向 Server 请求续期；
3. Server 校验（会话未撤销、双方设备仍受信、epoch 未前进、限速未超）后签发**绑定同一
   `connection_id + path_epoch`** 的新票据；
4. 持有方通过已建立的 QUIC 数据面专用控制流把新票据（含 server 签名）转发给对端；
5. 对端以 server 公钥验证签名与绑定字段后，双方原子更新本地 `ticket_expires_at_ms`
   与重置 expiry timer——数据循环**不中断**。

### 3.1 为什么安全

- 新票据由 Server 私钥签发，peer 只持有 server 公钥：伪造续期在计算上不可行；
- 续期票据绑定 `connection_id + path_epoch + 双方设备 ID`：不能移植到其他连接/会话；
- Server 是续期的必经点：**拒绝续期 = 有界撤销**（当前票据到期即拆，最长滞后一个 TTL/2 的续期提前量）；
- 数据面 in-band 交换不引入新攻击面：票据经既有 AEAD 会话加密传输，重放由
  `renewal_sequence` 单调序号防护（见 3.3）。

### 3.2 续期时序

```
Android                          Server                           PC (peer)
  |                                 |                                |
  |  RenewTicket(session, conn,     |                                |
  |    epoch, last_renewal_seq) --> |                                |
  |                                 |-- 校验: 会话 RUNNING/未撤销      |
  |                                 |-- 校验: 双方设备受信/在线        |
  |                                 |-- 校验: epoch 未前进            |
  |  <-- new_ticket(seq=N+1) ------ |                                |
  |  [数据面] RENEW(new_ticket) ------------------------------------> |
  |                                 |                          验签+绑定校验
  |                                 |                    双方 expiry 更新
```

- 任一侧可发起续期（建议：数据持有方 PC 侧在剩余 ≤ 1/2 时发起；Android 侧兜底）；
- 双方各自维护 `renewal_sequence`（单调递增，初始为票据签发序号）；收到
  `renewal_sequence` 非严格递增的续期帧即拒绝并保持现票据。

### 3.3 服务端状态

复用现有 ticket state（`maxTicketStates` LRU 不变），新增：

- `last_renewal_sequence map[connectionID]uint64`（随 ticket state 同生命周期，
  连接结束随 `connectionReplayTTL` 清理）；
- 续期限速：每连接每分钟 ≤ 2 次续期请求（正常 20 分钟 TTL 下几乎不触发）。

### 3.4 数据面变更（termlink-direct-ffi v5/v6）

- `data_loop` 的 `ticket_expiry` sleep 改为"续期检查 tick"：
  到达 `expires_at - renewal_lead`（默认 TTL/2）→ 发出 `EVENT_RENEWAL_DUE`
  事件给宿主（Win/Android），**不拆除**；
- 宿主调用新增 FFI `direct_renew_apply(session, new_ticket_bytes)` 原子更新
  票据与 expiry timer；
- 续期帧类型复用既有控制流帧（新增 `frame_type = RENEW`），带版本号；对端不支持
  时回复 NOT_SUPPORTED → 本端标记 `renewal_unavailable` → 保持现行为（到期拆直连）。

### 3.5 宿主（Win/Android）变更

- Win：`ProductionDirectTransport` 新增续期请求协程（Relay REST，复用现有
  票据端点 + `renew=1` 参数），收到新票据后 `direct_renew_apply`；
- Android：`ProductionDirectDataRuntime`/`TerminalCoreShadowObserver` 同构实现；
- 两端均需处理：续期失败 N 次（如 3 次）→ 主动拆直连回 Relay（保守回退，与现状一致）。

### 3.6 兼容与降级矩阵

| 本端 | 对端 | Server | 行为 |
|------|------|--------|------|
| 支持 | 支持 | 在线 | 20 分钟滑动续期，不拆 |
| 支持 | 支持 | 拒绝续期 | 到期拆直连（紧急撤销生效） |
| 支持 | 不支持 | — | 到期拆直连（等价现状） |
| 不支持 | 任意 | — | 现状（120s TTL） |

## 四、实施分解

| 步骤 | 内容 | 验证 |
|------|------|------|
| S1 | Server：ticketTTL 20 分钟 + `RenewTicket` 端点 + 状态/限速/单测 | 单测：续期签名、序号重放拒绝、撤销后拒绝 |
| S2 | FFI：RENEW 帧解析 + `direct_renew_apply` + expiry 重置 + NOT_SUPPORTED 协商标识 | direct-e2e 内存模型用例：续期后数据循环跨原 TTL 边界持续 |
| S3 | Win/Android 宿主：续期请求协程 + apply 接线 + 失败回退 | 本地双端实测：直连存活 > 原 TTL×3 |
| S4 | 观测：续期成功/失败/撤销计数指标进 /metrics | 指标存在性断言 |

## 五、开放问题（评审点）

1. TTL 数值：20 分钟 vs 1 小时（越长，撤销滞后越大；20 分钟为确认过的量级）；
2. 续期提前量：TTL/2 固定，或按 RTT 自适应；
3. Android 侧续期发起权（移动端省电考虑是否只允许 PC 侧发起，Android 仅被动 apply）；
4. `RenewTicket` 是否要求请求方持有原票据（防第三方代替请求）——建议要求，与设备证明绑定。
