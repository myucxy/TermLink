# Production Runtime Foundation

The production Compose file is `server/compose/core.compose.yml`. It publishes
only the edge proxy on port 443; the TermLink server, PostgreSQL, and Valkey
services have no host-published ports and communicate on a private network.
The server also has an egress-only network for external OIDC and S3 endpoints;
PostgreSQL and Valkey remain on the isolated private network.
The server runs as UID/GID `65532`, with a read-only root filesystem, dropped
Linux capabilities, and a bounded `/tmp` tmpfs.

## Local validation

Copy `.env.example` to `server/.env` and create the referenced fixtures outside
source control. The files must be non-empty and owner-readable only:

```powershell
New-Item -ItemType Directory -Force server\secrets | Out-Null
'local-oidc-secret' | Set-Content server\secrets\oidc-client-secret -NoNewline
'postgres://termlink:local-password@postgres:5432/termlink?sslmode=disable' | Set-Content server\secrets\database-url -NoNewline
'redis://valkey:6379/0' | Set-Content server\secrets\redis-url -NoNewline
'local-token-signing-key' | Set-Content server\secrets\token-signing-key -NoNewline
'local-s3-access' | Set-Content server\secrets\s3-access-key -NoNewline
'local-s3-secret' | Set-Content server\secrets\s3-secret-key -NoNewline
'local-password' | Set-Content server\secrets\postgres-password -NoNewline

docker compose --env-file server\.env -f server\compose\core.compose.yml config
```

On Unix hosts, run `chmod 600 server/secrets/*` (or `chmod 400` for
read-only fixtures). Do not enable history until the S3 endpoint and both S3
secret fixtures are configured. Secret contents are never supplied as Compose
environment values.

`TERMLINK_S3_ENDPOINT` is the server-side S3/MinIO endpoint used for bucket
creation and object verification/deletion. `TERMLINK_S3_PUBLIC_ENDPOINT` is
used only in client-facing presigned upload/download URLs and defaults to the
internal endpoint when omitted. For the disposable integration Compose stack,
the internal endpoint remains `http://minio:9000` while the host-reachable
presigned endpoint is `http://127.0.0.1:59000`.

The Go config package fails fast for missing, empty, or over-permissive secret
files. Its `Secret` values redact themselves in ordinary and `%#v` formatting;
startup errors identify only the configuration field, never the secret value.

## 开发项 1-7 完成矩阵

| 开发项 | 范围 | 状态 | 验证与边界 |
| --- | --- | --- | --- |
| 1 | 生产账号/OIDC/设备注册与配对 | **已实现并验证** | Go `auth`、设备注册/配对 API、Android OIDC/API 客户端、Windows 配对流程；Go/Android/.NET 自动化验收通过。 |
| 2 | 生产 Protobuf 实时协议、X25519 + ChaCha20-Poly1305、Relay | **已实现并验证** | `protocol/protobuf/termlink_realtime.proto`、Go `realtimewire`/Relay、Android `core-network`、Windows `src/Protocol`；向量、协议和 Relay 测试通过。 |
| 3 | Windows ConPTY 到 Android 真实输入输出、ACK/Gap/重连、租约与本地输入优先 | **已实现并验证** | Windows `ProductionAgentRuntime`、Android 生产 Session Client、服务端租约/撤销；生产路径自动化覆盖有界重放、ACK/Gap、输入去重、租约和本地输入 epoch。 |
| 4 | 每 Android 设备单独加密的有界历史、S3/MinIO 预签名上传/下载/校验/删除 | **已实现并验证** | Android `core-crypto`/`feature-history`、Windows History Uploader、Go history store；SHA-256、密文元数据、预签名和删除集成测试通过。 |
| 5 | 服务端 Catalog、计费验证边界、权益与配额 | **核心已实现并验证（Provider 待配置）** | Go `billing`/Catalog/entitlement API、可注入 verified-purchase 契约与 Android `feature-billing` 已测试；真实 Google/Web verifier 未配置时生产入口返回 `BILLING_UNAVAILABLE`。 |
| 6 | Android 产品页面、Windows Tray、Compose PostgreSQL/Valkey/MinIO/OIDC 接线及自动化验收 | **已实现并验证** | Android product UI、Windows Account/Devices Tray、`core/history/integration.compose.yml` 与生产运行时；Go/Android/.NET/Compose 自动化验收通过。 |
| 7 | 生产 TLS/受信发布证书真实安装更新、备份恢复演练、镜像签名、监控告警、生产 CA/发布加固 | **未开始** | 仅有开发证书/故障注入和本地 Compose 验证；真实证书链、系统级安装更新、灾备演练、镜像签名与生产运维尚未验收。 |

统一边界：`LAN Replay V1`、`TLT1`（Phase 1 Test Session V1）和 Android Compose
Harness 都是测试夹具，不是生产协议；Android 历史客户端只接受 HTTPS。Integration
Compose 中 MinIO 的 HTTP 端口仅供本机 loopback 集成测试，不能当作生产历史传输。1-6
的自动化结果不代表 arm64 真机、厂商 IME、外接键鼠、平板/折叠屏或生产发布已经验证。

运行时配置仍要求真实 OIDC/Google/Web Billing Provider 在部署前注入；本地默认配置只验证
契约和 `BILLING_UNAVAILABLE` 失败边界，不冒充线上购买或公网部署结果。

## Production checks

The production binary uses `serve`, `migrate`, and `healthcheck` commands. Run the
following from `server` after preparing `.env` and the owner-readable secret
fixtures described above:

```powershell
D:\Dev\Env\Go\bin\go.exe test ./...
D:\Dev\Env\Go\bin\go.exe vet ./...
D:\Dev\Env\Go\bin\go.exe run ./cmd/termlink-server migrate
docker compose --env-file .env -f compose\core.compose.yml config
docker compose -f compose\integration.compose.yml up -d --wait postgres valkey minio
$env:TERMLINK_INTEGRATION_DATABASE_URL='postgres://termlink:integration-only-password@127.0.0.1:55432/termlink?sslmode=disable'
$env:TERMLINK_INTEGRATION_REDIS_URL='redis://127.0.0.1:56379/0'
$env:TERMLINK_INTEGRATION_S3_ENDPOINT='http://127.0.0.1:59000'
$env:TERMLINK_INTEGRATION_S3_PUBLIC_ENDPOINT='http://127.0.0.1:59000'
D:\Dev\Env\Go\bin\go.exe test ./internal/api -run TestProductionAccountRelayAndRevocation -count=1
docker compose -f compose\integration.compose.yml down -v
```

`integration.compose.yml` is a disposable PostgreSQL/Valkey/MinIO acceptance
stack. Its MinIO HTTP endpoint is bound to loopback for presigned URL tests only;
production Android history requests must use HTTPS and the production Compose
stack keeps database, Valkey, and object-store ports private.
