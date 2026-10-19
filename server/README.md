# TermLink Server

TermLink Server 的生产部署只保留两个容器：

```text
外部 HTTPS 反向代理
        |
TermLink Server
        |
PostgreSQL
```

Server 内置以下能力：

- OIDC 登录、注册、邀请码、邮箱验证码和可选人机验证；
- Android、Windows Agent、PC 和会话管理；
- Relay 实时转发、ACK、租约和多端协同；
- 单节点 Direct/NAT 信令；
- 文件传输任务和 Windows Agent 升级任务；
- 个人中心、管理后台、备份恢复和健康检查。

Server 不执行 Windows 命令，不创建 Windows ConPTY，不保存完整终端历史，
也不保存文件对象。命令和终端历史由 Windows Agent 负责，文件字节在 Android
与 Windows 之间通过实时链路传输。

客户端版本与下载信息固定从 `https://appship.kget.cc:8910` 读取，管理页面不提供分发源配置。

## 部署

直接使用仓库内的完整 Compose 文件，不需要 `.env` 文件。将部署参数
作为本次 Docker 命令的环境变量传入：

```bash
TERMLINK_IMAGE='docker.io/myucxy/termlink:TAG@sha256:DIGEST' \
TERMLINK_PUBLIC_URL='https://termlink.example.com' \
TERMLINK_POSTGRES_PASSWORD='URL_SAFE_DATABASE_PASSWORD' \
TERMLINK_HTTP_PORT=8080 \
docker compose -f server/compose.yml up -d --wait
```

首次启动时 Server 会在数据库中生成并保存运行时主密钥，内置身份服务会创建
`admin` 用户。初始密码在首次启动时随机生成并仅打印一次到启动日志（检索
`generated one-time initial administrator password`）；也可通过
`TERMLINK_INITIAL_ADMIN_PASSWORD_FILE` 显式注入，不存在公开的固定默认密码。
首次部署的注册策略默认为关闭，管理员可在“注册与邀请码”页面按需开启或改为仅邀请码。
数据库密码通过 `TERMLINK_POSTGRES_PASSWORD` 传入；该值只能使用 URL 安全字符，
并应通过受保护的部署环境注入，避免提交到版本库。

TermLink 不负责安装 TLS 反向代理。需要在宿主机或云平台配置 Nginx、Traefik、
Lucky 或其他支持 WebSocket 的 HTTPS 入口，并将请求转发到 Server。

## 必需配置

直接使用 `server/compose.yml` 时只需要在本次命令中传入：

```text
TERMLINK_IMAGE
TERMLINK_PUBLIC_URL
TERMLINK_POSTGRES_PASSWORD
```

PostgreSQL 数据目录必须使用持久化卷。Server 首次启动会自动迁移数据库，并将
Token 签名主密钥及其派生密钥的根密钥保存到 PostgreSQL；不需要 `.env`、Secret
文件、`secrets-init`、独立 migrate 容器、Keycloak、Valkey、MinIO 或 Caddy。

## 备份（compose 路径最小方案）

生产/长期运行建议使用 `server/deploy/single-node-production/termlink-deploy backup`
（age 加密归档 + SHA-256 清单 + 受保护的恢复路径）。compose 直用路径的最小等价命令：

```bash
docker compose -f server/compose.yml exec postgres   pg_dump -U termlink -d termlink > backup-$(date +%Y%m%d).sql
```

恢复：`cat backup-YYYYMMDD.sql | docker compose -f server/compose.yml exec -T postgres   psql -U termlink -d termlink`。请定期演练恢复并把备份送到非本机存储；
`server/ops/Backup-TermLink.ps1` / `Restore-TermLink.ps1` 面向已淘汰的旧多容器栈，勿用。

## 健康检查

```text
GET /healthz   进程存活检查
GET /readyz    PostgreSQL 和运行时依赖检查
```

`/metrics` 只有在显式开启内部指标监听时才提供，不属于最小部署要求。

## 生产边界

| 内容 | 负责方 |
|---|---|
| 账号和 Token | TermLink Server 内置身份服务 |
| 设备、PC、会话元数据 | TermLink Server + PostgreSQL |
| 命令执行和 Windows ConPTY | Windows Agent |
| 终端历史 | 在线 Windows Agent |
| 实时消息路由 | TermLink Server Relay |
| 文件字节 | Android 与 Windows Agent |
| 文件任务、租约和权限 | TermLink Server + PostgreSQL |
| HTTPS 证书和反向代理 | 宿主机或云平台 |

## 测试和验收

LAN Replay、Phase 1、Keycloak 验收栈、Valkey 集成栈和监控栈属于开发验证内容，
不属于生产部署。相关文件应从生产部署文档和发布包中排除；需要运行测试时，
请使用仓库的 `technical-validation` 文档和 CI 流程。
