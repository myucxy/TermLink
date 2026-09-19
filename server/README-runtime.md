# TermLink 单节点运行时

当前支持的生产拓扑只有：

```text
外部 HTTPS 反向代理 → TermLink Server → PostgreSQL
```

Server 使用 `single-node` 模式，内置：

- OIDC 身份服务和账号管理；
- 数据库迁移；
- 内存协调器；
- Relay 实时转发；
- Direct/NAT 信令；
- 注册、邀请码、邮件、人机验证和账户注销任务。

PostgreSQL 是唯一需要长期运行的外部服务。生产运行时不启动 Keycloak、Valkey、
MinIO、Caddy 或独立迁移容器。

## 安装

直接使用仓库中的 `compose.yml` 即可部署，不需要 `.env`、Secret 文件或
`secrets-init`：

```bash
TERMLINK_IMAGE='docker.io/myucxy/termlink:TAG@sha256:DIGEST' \
TERMLINK_PUBLIC_URL='https://termlink.example.com' \
TERMLINK_POSTGRES_PASSWORD='URL_SAFE_DATABASE_PASSWORD' \
docker compose -f server/compose.yml up -d --wait
```

Server 负责首次迁移、生成并保存运行时主密钥，以及创建初始管理员。

部署完成后的稳定容器数量为两个：

```text
termlink-server
termlink-postgres
```

TLS 由宿主机或云平台的反向代理终止。反向代理必须保留 Host、设置
`X-Forwarded-Proto: https`，并允许 WebSocket 升级。

## 配置

Compose 部署的最小配置包括：

```text
TERMLINK_RUNTIME_MODE=single-node
TERMLINK_IDENTITY_PROVIDER=embedded
TERMLINK_PUBLIC_URL=https://termlink.example.com
TERMLINK_POSTGRES_PASSWORD=URL_SAFE_DATABASE_PASSWORD
```

`TERMLINK_POSTGRES_PASSWORD` 由 Compose 拼出数据库连接串。内置身份模式的初始
管理员密码默认在首次启动时随机生成，并在启动日志中以
`generated one-time initial administrator password` 打印一次（仅出现一次，请立即保存）；
也可通过挂载 `TERMLINK_INITIAL_ADMIN_PASSWORD_FILE`（compose 已接线）显式注入。
不存在公开的固定默认密码；Token、Cookie、注册、
人机验证和账户注销使用的密钥由数据库中的运行时主密钥派生，不需要额外挂载。
首次部署的注册策略为关闭，管理员可在“注册与邀请码”页面启用公开注册或邀请码注册。

Direct/NAT 信令可以在 Server 内部启用，但实际直连默认关闭：

```text
TERMLINK_DIRECT_TRANSPORT_ENABLED=true
TERMLINK_DIRECT_ROUTING_PERMITTED=false
```

Relay 始终是主链路和兜底链路。Direct 失败时不会影响 Relay 连接。

在线状态由 Server 进程内 Presence 缓存维护：认证连接注册、WebSocket Pong 刷新，
45 秒无心跳自动过期；同一设备的多条连接合并为一个在线设备。Realtime 和 NAT
连接共用该缓存，设备列表、PC 列表、会话列表及管理页面优先读取缓存。PostgreSQL
中的 `last_seen_at` 仅保留长期活动参考和旧客户端兼容值，PC 元数据上报最多每
30 秒刷新一次，正常连接心跳不会写库。

## 数据边界

PostgreSQL 保存账号、设备、PC、会话元数据、租约、邀请码、文件任务、升级任务、
管理设置和注销任务。它不保存完整终端输出，也不保存文件对象。

终端历史由在线 Windows Agent 保存和提供；文件内容在 Android 与 Windows Agent
之间传输。服务端只负责鉴权、路由、任务状态和租约。

## 备份和恢复

备份必须包含：

- PostgreSQL 自定义格式备份；
- 嵌入式身份数据；
- `server_runtime_secrets` 表（已包含在 PostgreSQL 备份中）；
- 校验清单。

Valkey 状态、终端历史缓存和客户端临时文件不属于 Server 备份边界。

使用部署工具执行备份和恢复：

```bash
sudo /opt/termlink/termlink-deploy backup --output /var/backups/termlink/latest.tar.age
sudo /opt/termlink/termlink-deploy restore \
  --input /var/backups/termlink/latest.tar.age \
  --identity /secure/off-host-copy/age-key.txt \
  --confirm-restore
```

## 健康检查

```text
/healthz   HTTP 进程存活
/readyz    PostgreSQL 和 Server 依赖就绪
```

指标监听和外部监控属于可选运维能力，不增加生产运行时服务数量。

## 不再使用的部署组件

以下内容不属于当前单节点生产部署：

```text
Keycloak
Valkey
MinIO
Caddy
独立 migrate 容器
独立 session cleanup 容器
独立文件对象存储
套餐、支付和公告服务
```

验收 Compose、LAN 测试、分布式 Compose 和外部监控 Compose 应从生产发布包中排除。
