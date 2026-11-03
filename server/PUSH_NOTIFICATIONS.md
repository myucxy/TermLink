# iOS 后台通知服务端契约

本实现提供 APNs 注册、状态事件、持久化投递队列及升级结果通知。默认关闭；尚未接入 iOS 真机的系统 Token 注册回调，也尚未由电脑 AI Owner 上报状态事件。服务端测试使用本机 HTTP/2 TLS 夹具，没有向 Apple 发送推送，不代表真机送达已经验收。

## 配置

启用时需要独立 iOS OIDC 客户端和实际 Apple 推送身份：

```dotenv
TERMLINK_IOS_OIDC_CLIENT_ID=termlink-ios
TERMLINK_APNS_ENABLED=true
TERMLINK_APNS_TEAM_ID=<10位Apple Team ID>
TERMLINK_APNS_KEY_ID=<10位APNs Key ID>
TERMLINK_APNS_TOPIC=dev.termlink.ios
TERMLINK_APNS_ENVIRONMENT=sandbox
TERMLINK_APNS_PRIVATE_KEY_FILE=/run/secrets/apns_auth_key
```

`.p8` 仅从私有文件读取，配置日志不输出私钥或其路径；非 Windows 服务上要求普通私有文件、无组 / 其他用户权限。推送关闭时不读取私钥；启用但身份 / topic / environment / PKCS8 P256 私钥不合法则启动失败。此处配置属于推送身份，不使用 Mac 安装包的 Team ID 配置代替它。

HTTP/2 TLS 连接固定使用 Apple 的 sandbox / production 端点；没有自定义服务地址。实际签名 topic 和环境由服务配置固定，客户端不能通过请求切换。ES256 Provider JWT 缓存30分钟，响应仅保留固定错误码，不记录 Token-bearing URL、响应正文或凭据。

Compose 用户可显式叠加 `compose/apns.compose.yml`。该覆盖文件只影响 `server`，不启用 migrate 容器的推送。Host 的 `TERMLINK_APNS_PRIVATE_KEY_FILE` 指向仓库外 `.p8`，容器内映射为固定私钥路径；文件需让容器UID65532读取并保持0400/0600。普通本机 Docker Compose 对 file secret 的 uid/gid/mode 支持可能不同，需验证实际挂载权限，不能放宽密钥权限来绕过配置检查。当前受管单文件安装器不会自动导入或备份 `.p8`；推送覆盖文件和密钥需要由运维显式管理，不会因升级服务而自动启用。

## iOS 注册 / 轮换 / 注销

沿用现有 Bearer、设备签名和 `Idempotency-Key`；仅受信、未禁用的 IOS 设备可以访问自己账户和设备的注册。服务端只保存使用独立派生密钥加密的 Token，HMAC用于重复端点识别。

- `GET /v1/devices/self/push`：返回 `registration` 或 null，不返回 Token / 密文 / Hash。
- `PUT /v1/devices/self/push`：正文 `{"token":"<hex device token>","sound_enabled":false}`；Token 支持32～1024位、偶数长度的hex，不假定固定长度。
- 已存在注册时，更换 Token 或重新启用须带 `expected_generation`（GET 返回的UUID）。同一 Token 的重试保留 generation；轮换会取消旧 generation 的待发任务。声音默认关闭，可显式设置 `sound_enabled=true`。
- `DELETE /v1/devices/self/push`：正文 `{"expected_generation":"<UUID>"}`；注销本机当前 generation 并取消其待发任务。即使服务端暂时关闭投递，也保留此清理接口；旧 generation 不能注销更新后的注册。

活跃端点不能被其他账户 / 设备覆盖。原设备注销、撤销、禁用或账户失效后，新签名身份可以重新注册相同系统端点，旧账户的注册 / 任务会移除。手机必须在退出登录前注销其注册，并在 Token 变化后读取当前 generation、重新登记。遇到409时重新读取状态，不无限重试旧 Token。

## 电脑 AI 状态事件

`POST /v1/pcs/{pcId}/notification-events`，示例正文：

```json
{
  "event_id":"<stable event UUID>",
  "kind":"ai.needs_input",
  "session_id":"<AI session UUID>",
  "recipient_device_ids":["<IOS device UUID>"],
  "occurred_at":"<RFC3339 timestamp>"
}
```

允许 `ai.needs_input` / `ai.completed`，不接受正文、标题、路径、模型回答或任意通知文案。发送者只能是此电脑所属、同账号且当前受信的 PC Agent；电脑必须有 `ai_agent_v1`，远程路由已开启。所有显式接收者均须是同账号受信IOS设备，并有有效MANAGE或现有自动信任策略。任何非法接收者导致整体拒绝，不默默扩大受众。

每次最多16个接收者；时间限于过去5分钟至未来30秒；同PC每分钟最多60个新事件，账户已有大量待发任务时拒绝继续积压。`event_id` 在该PC去重，内容变化返回409；新事件返回202及本次入队数量。若手机没有开启推送，事件可以成功但入队0条。电脑端需在接收者权限及同意范围内选择对象，不能把状态接口用作会话正文通道。

## 升级结果

升级状态变为 SUCCEEDED、FAILED、ROLLED_BACK、ROLLBACK_FAILED 时，由数据库触发器在原状态事务内给原请求设备入队通用通知（设备已注册时）。正常领取、手动较高版本对账和重试都不重复通知。升级和AI事件使用不同来源命名空间，Agent不能通过AI事件伪造升级完成。

触发器不接受电脑自行声明升级结果的新接口，沿用既有升级租约 / 完成校验。升级推送入队失败会使原状态变化回滚并可重试。只有AI通知关联会话ID；升级结果不附带无关会话内容。

## 投递和恢复

- 使用 SKIP LOCKED 和有期限的独立租约领取，每批最多10条；重试次数上限10。
- 每条发送前重新检查账户、设备、注册 generation、PC / AI 能力、远程路由和MANAGE；失效或过期任务取消。已经在网络发送中的通知无法撤回，接收后仍需重新查询当前状态。
- 429按 Retry-After / 退避；5xx至少15分钟后重试；网络错误有界重试。AI事件有效期1小时，升级结果24小时，过期不再补发。
- 410 Unregistered 仅在对应 generation 且注册时间不晚于 Apple 失效时间时禁用端点。旧回执不会关闭轮换或重新注册后的 Token。BadDeviceToken 同样受 generation 约束。
- 通知Payload只有固定通用文案、job事件ID、电脑ID、类型和可选会话ID。声音遵循显式偏好。通知不能直接批准操作；点击后手机必须重新登录 / 校验权限和控制权，再查询最新待办。
- 事件 / 结果记录保留7天后分批清理。服务关闭推送时不运行投递worker，不能据此声称后台提醒已启用。

## 数据库与验收

新增迁移55 `device_push_notifications`，运行时schema55，旧迁移保持原字节。数据库降级会移除该功能的注册和队列，执行前应关闭worker并确认不再需要待发通知；没有改写既有身份和Relay协议。

覆盖本机HTTP/2 TLS与JWT验签、令牌保密、配置拒绝、Token AAD绑定 / 篡改、签名HTTP、真实PostgreSQL迁移、跨账号 / 权限矩阵、去重、并发领取、轮换 / 注销与失效竞争、第十次投递、重试 / 过期、升级状态与通知事务回滚。没有使用真实Apple凭据或发送通知。

下一步客户端接入：iOS申请系统通知权限、取得APNs Token并登记 / 注销，通知点击后恢复并检查会话；电脑AI Owner产生稳定事件ID和最小状态上报。正式启用还需匹配App的推送 entitlement、证书身份和真实iPhone验收。

## 连接错误诊断

原 `error.code`、`error.message_key` 和 `error.trace_id` 保持，元数据API在明确连接环节新增可选 `error.stage`：configuration、device_registration、device_identity、pc_metadata、push_registration。预留Relay / 文件握手阶段标识；其实际认证及WebSocket错误仍由各自模块处理，不能把本次API补充当作所有断线原因已覆盖。客户端需读取这些字段后展示友好文案，不能继续只显示HTTP状态码。
