# Production Runtime Foundation

`compose/core.compose.yml` is the minimal production foundation. Its steady
state is TermLink Server, PostgreSQL, and Valkey; `migrate` is a required
successful one-shot job. The optional `edge` profile adds the only published
port (HTTPS 443). Core contains no MinIO, history proxy route, metrics stack,
acceptance probe, or test service.

OIDC is a required external dependency, not a service hidden inside core. Set
`TERMLINK_OIDC_ISSUER` to a separately operated Keycloak realm or another
compatible issuer. The server verifies the issuer during startup and will not
become ready when it is unavailable. A self-hosted Keycloak needs its own
lifecycle, database, backup, and upgrade plan; do not attach an acceptance
Keycloak container to the production core stack.

PostgreSQL and Valkey have no host-published ports and share only the isolated
backend network with the application. The server alone joins an egress network
for OIDC and optional external S3 calls. With `history.compose.yml`, the server
reaches MinIO over `object-control`, and the edge reaches only MinIO's `object`
network for presigned traffic. The edge never joins the backend network.
Services have bounded CPU, memory, PID counts, health checks, restart policies,
and rotated JSON logs. Limits are conservative defaults and can be overridden
with their documented `TERMLINK_*_LIMIT` variables.

## Compose combinations

Run these commands from `server`. Always render the exact combination before
starting it:

```powershell
# Core: external OIDC, history disabled.
docker compose --env-file .env `
  -f compose\core.compose.yml `
  --profile edge config
docker compose --env-file .env `
  -f compose\core.compose.yml `
  --profile edge up --detach --wait

# Core + local encrypted-history storage and history HTTPS route.
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\history.compose.yml `
  --profile edge up --detach --wait

# Core + provider-managed S3. OIDC remains external in every combination.
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\external-services.compose.yml `
  --profile edge up --detach --wait

# Enable controlled account creation and crash-safe invitation recovery for a Keycloak realm.
$env:TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE = 'D:\TermLink\secrets\keycloak-registration-recovery-secret'
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\registration-recovery.compose.yml `
  --profile edge up --detach --wait

# Optionally bootstrap Cloudflare Turnstile for both registration e-mail codes
# and account creation. SUPER_ADMIN can later replace or disable these values
# from the management page without restarting the server.
$env:TERMLINK_HUMAN_VERIFICATION_SITE_KEY = '0x4AAAA-public-site-key'
$env:TERMLINK_HUMAN_VERIFICATION_SECRET_FILE = 'D:\TermLink\secrets\human-verification-secret'
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\registration-recovery.compose.yml `
  -f compose\human-verification.compose.yml `
  --profile edge up --detach --wait

# Add the final account-deletion processor. One History/S3 provider is
# mandatory; this example uses local MinIO.
$env:TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE = 'D:\TermLink\secrets\account-deletion-identity-key'
$env:TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION = '1'
$env:TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE = 'D:\TermLink\secrets\keycloak-registration-recovery-secret'
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\history.compose.yml `
  -f compose\account-deletion.compose.yml `
  --profile edge up --detach --wait

# Add monitoring to either history choice (shown with local MinIO).
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\history.compose.yml `
  -f compose\observability.compose.yml `
  --profile edge up --detach --wait
```

`history.compose.yml` and `external-services.compose.yml` are mutually
exclusive history choices. `observability.compose.yml` is independent and
optional. `registration-recovery.compose.yml` is required when a Keycloak-backed
deployment offers account registration; it supplies the bounded account-creation
and recovery authority and can be combined with either history choice. Keycloak
native self-registration must remain disabled. `human-verification.compose.yml`
optionally bootstraps Cloudflare Turnstile for both registration stages and can
be combined with any registration deployment. After the first management-page
save, the encrypted database settings become the runtime source.
`account-deletion.compose.yml`
requires either local history or external S3 and a Keycloak service account.
Files named `acceptance`,
`integration`, or `lan-test` are disposable test stacks and must not be
included in a production Compose command. After a successful startup, `docker
compose` reports `migrate` as exited with status 0; it can be removed with the
same `-f` arguments followed by `rm --force migrate`.

The supported production layers are intentionally small:

| Layer | Services added | Required inputs | When to keep it |
| --- | --- | --- | --- |
| `core` | `server`, PostgreSQL, Valkey; one-shot `migrate` | OIDC, database/Valkey/token secrets | Always |
| `edge` profile | Caddy | public host, production ACME email | Only when TermLink terminates public TLS itself |
| `commerce` | none; read-only payment-secret mount | protected existing host directory | Only for locally configured Alipay/WeChat channels |
| `history` | MinIO and isolated object networks | history host plus S3 credentials | Only for locally managed encrypted objects |
| `external-services` | none | external HTTPS S3 origin and credentials | Instead of local `history` |
| `observability` | Prometheus, Blackbox, Alertmanager, Grafana | Grafana secret and explicit reviewed Alertmanager receiver configuration | Only when not supplied by platform monitoring |
| `registration-recovery` | none; enables controlled registration and a bounded recovery runner | Keycloak service-account secret | Whenever a Keycloak-backed deployment offers registration |
| `human-verification` | none; bootstraps Turnstile on the unified registration page and API | public site key and file-backed provider secret | Optional compatibility/bootstrap source before the first management-page save |
| `account-deletion` | none; enables the final deletion processor | History/S3, Keycloak secret, versioned AES identity key | Only when final account deletion is offered |

Do not add every overlay by default. A headless core behind an existing ingress
has three steady-state containers; `migrate` exits successfully and can then be
removed. Commerce, recovery, and account deletion reuse the server process and
do not justify extra worker containers. Choose exactly one history provider,
and omit the bundled observability stack when the hosting platform already
provides equivalent private metrics, alerts, and dashboards.

The Web portal uses origin-relative paths for all navigation within TermLink.
It does not turn a proxy-rewritten request Host into an absolute
`TERMLINK_PUBLIC_URL` redirect, so an ingress or alternate entry does not cause
the browser to bounce between addresses. `TERMLINK_PUBLIC_URL` remains the
security authority for the configured OIDC callback and provider-bound checks;
OIDC/Keycloak, payment-provider, Turnstile, and artifact-download URLs are
intentionally absolute because those protocols cross origins. Reverse proxies
should preserve the browser Host or set one unambiguous `X-Forwarded-Host`.

When observability is selected, `TERMLINK_ALERTMANAGER_CONFIG_FILE` must point
to an explicit, permission-restricted production receiver configuration and
`TERMLINK_GRAFANA_ADMIN_PASSWORD_FILE` must exist. The deployment policy rejects
the repository's no-delivery Alertmanager example, so a green container cannot
be mistaken for working alert delivery.

## Local validation

Copy `.env.example` to `.env` and create the referenced fixtures outside
source control. The files must be non-empty and owner-readable only:

```powershell
New-Item -ItemType Directory -Force secrets | Out-Null
'local-oidc-secret' | Set-Content secrets\oidc-client-secret -NoNewline
'postgres://termlink:local-password@postgres:5432/termlink?sslmode=disable' | Set-Content secrets\database-url -NoNewline
'local-valkey-password' | Set-Content secrets\valkey-password -NoNewline
'redis://default:local-valkey-password@valkey:6379/0' | Set-Content secrets\redis-url -NoNewline
'local-token-signing-key-at-least-32-bytes' | Set-Content secrets\token-signing-key -NoNewline
'local-s3-access' | Set-Content secrets\s3-access-key -NoNewline
'local-s3-secret' | Set-Content secrets\s3-secret-key -NoNewline
'local-password' | Set-Content secrets\postgres-password -NoNewline

docker compose --env-file .env -f compose\core.compose.yml config
```

On Unix hosts, run `chmod 700 secrets` and `chmod 600 secrets/*` (or `chmod
400` for read-only fixtures). On Windows, disable
ACL inheritance on both the directory and files and grant access only to the
deploying account, `SYSTEM`, and local Administrators. The production policy
validator rejects group/world Unix bits, inherited Windows ACLs, and any
unreviewed allow ACE. Do not enable history until the S3 endpoint and both S3
secret fixtures are configured. Secret contents are never supplied as Compose
environment values.

Valkey authentication is file-backed. Compose reads
`TERMLINK_VALKEY_PASSWORD_FILE` at startup, writes a short-lived ACL file on
the private `/run/valkey` tmpfs, and starts Valkey with the default user
protected by that password. Keep the password in the separate
`valkey-password` fixture and include the matching `default:<password>@` user
info in the `redis-url` fixture; neither value belongs in `.env` or a rendered
Compose environment.

Connection controls are non-secret and validated before startup. Existing
deployments remain compatible because every setting has a default:

| Setting | Default | Valid range |
| --- | ---: | --- |
| `TERMLINK_POSTGRES_MAX_CONNS` / `MIN_CONNS` | `20` / `2` | 1-1000 / 0-max |
| `TERMLINK_POSTGRES_MAX_CONN_LIFETIME` | `30m` | 1m-24h |
| `TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME` | `5m` | 1m-24h |
| `TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD` | `1m` | 5s-10m |
| `TERMLINK_POSTGRES_CONNECT_TIMEOUT` | `5s` | 100ms-2m |
| `TERMLINK_REDIS_POOL_SIZE` / `MIN_IDLE_CONNS` | `20` / `2` | 1-1000 / 0-pool size |
| `TERMLINK_REDIS_DIAL_TIMEOUT` | `5s` | 100ms-2m |
| `TERMLINK_REDIS_READ_TIMEOUT` / `WRITE_TIMEOUT` | `3s` / `3s` | 100ms-2m |
| `TERMLINK_REDIS_POOL_TIMEOUT` | `4s` | 100ms-2m |
| `TERMLINK_LOG_LEVEL` | `info` | `debug`, `info`, `warn`, `error` |
| `TERMLINK_APPSHIP_BASE_URL` | `https://appship.kget.cc:8910` | Credential-free HTTPS origin |
| `TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS` | empty | Up to 8 exact RFC1918/ULA addresses; no CIDRs |

Durations use Go duration syntax, such as `500ms`, `5s`, or `30m`. Invalid
values fail with the environment field name only; connection URLs, passwords,
and secret contents are never echoed. `TERMLINK_PAYMENT_SECRETS_DIR` defaults
to `/run/secrets` in the binary. It is set to `/run/secrets/payment` only by the
commerce overlay and must be an absolute, cleaned, non-root directory.
Configuration stores this base path but does not enumerate or load
payment-provider secrets.

Keycloak identity administration is disabled by default in the binary so
deployments using a non-Keycloak issuer remain compatible. The recovery overlay
enables the bounded client only for the server process and mounts the file named by
`TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE` as a mode-0400 Docker secret. The
client ID defaults to `termlink-registration-recovery` and can be changed with
`TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID`. Configure that Keycloak client as a
confidential service account with only the realm-management permissions needed
to create, query, update, and delete users (`query-users` and `manage-users`).
Add `manage-realm` only when the same bounded client is used to reconcile realm
mail settings; never supply a
realm administrator password. The issuer must be the realm URL ending in
`/realms/<realm>`.

The server derives a dedicated registration-attempt proof key from the token
key master and supplies it to the bounded identity administration client.
All account creation passes through the TermLink registration API; Keycloak's
native registration endpoint stays closed. Recovery accepts only identities carrying a matching HMAC-backed
attempt attribute, and its bounded runner starts only when the overlay is
enabled. Its client secret remains file-backed and is redacted from
diagnostics. Payment reconciliation starts in every server process but remains
idle until PostgreSQL contains a pending run. It uses the same real, versioned
payment-channel resolver as live commerce and the provider-reference digest
domain required to compare existing local hashes; provider keys continue to
come only from `TERMLINK_PAYMENT_SECRETS_DIR`.

Human verification is disabled by default. A `SUPER_ADMIN` can configure the
Cloudflare Turnstile Site Key, write-only Secret, and enable switch on the
management page. Saving takes effect immediately without restarting the server.
The Secret is AES-GCM encrypted with a dedicated key derived from the server
master key and is never returned through HTML, JavaScript, API responses,
diagnostics, or audit metadata. The `human-verification.compose.yml` overlay is
retained as a compatibility/bootstrap source: it sets
`TERMLINK_HUMAN_VERIFICATION_PROVIDER=turnstile`, provides the public Site Key,
and mounts the file named by `TERMLINK_HUMAN_VERIFICATION_SECRET_FILE` as a
mode-0400 Docker secret. Those deployment values remain effective only while
the database row is still at its untouched initial state; the first successful
management-page save makes the encrypted database configuration authoritative.

Once enabled, both `POST /v1/auth/register/email-code` and
`POST /v1/auth/register` fail closed unless their single-use challenge token is
valid for the configured public hostname and the fixed `registration` action.
The browser resets the challenge after either request, so a token accepted for
an e-mail code cannot be reused to create the account. Both the server and end
users' browsers must be able to reach `https://challenges.cloudflare.com`.
Provider or settings-store outages return a temporary-unavailable result; they
never bypass the challenge.

Payment provider secrets are opt-in and do not belong in the Compose
environment or database. Put each private key, public key/certificate, or API
v3 key in a locked-down host directory, then add the commerce overlay:

```powershell
$env:TERMLINK_PAYMENT_SECRETS_HOST_DIR = 'D:\TermLink\payment-secrets'
docker compose --env-file .env.production `
  -f compose/core.compose.yml -f compose/commerce.compose.yml `
  --profile edge up -d
```

The overlay bind-mounts that existing directory read-only at
`/run/secrets/payment`; `create_host_path: false` prevents a misspelled path
from silently creating an empty directory. Payment channel versions store only
references such as `docker-secret://alipay-merchant-private-key`. The service
loads the referenced file only for local validation or a payment operation,
and never returns its contents through API or Web pages. Selecting the overlay
does not invent or activate a payment channel: absent channel records or absent
referenced files remain `payment unavailable`, rather than a false healthy
provider.

Final account deletion is also opt-in. The identity-key file must contain
standard base64 or base64url encoding of exactly 32 random bytes (an AES-256
key), or a bounded JSON rotation ring such as
`[{"version":1,"key":"<base64>"},{"version":2,"key":"<base64>"}]`.
Raw text, hexadecimal keys, duplicate versions, unknown fields, more than 32
entries, and shorter decoded values are rejected. Set
`TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION` to the positive current
version; that version must exist in the ring. Mount the file through
`TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE`. The
deployment validator rejects account deletion without one selected History/S3
provider or without the Keycloak admin secret, and never grants either secret
to `migrate`. Treat key-version changes as a separate reviewed rotation and
retain the old protected key material according to the data-retention and
recovery procedure until no record depends on that version.

Resource limit overrides use the service prefix plus `MEMORY_LIMIT`,
`MEMORY_RESERVATION`, `CPU_LIMIT`, or `PIDS_LIMIT` (for example,
`TERMLINK_SERVER_MEMORY_LIMIT`). Core defaults are edge `256m/0.50 CPU`, migrate
`256m/0.50`, server `512m/1.00`, PostgreSQL `1g/1.50`, and Valkey `512m/0.50`;
the default PID limits are 128, 128, 256, 256, and 128 respectively. MinIO,
Prometheus, Blackbox, Alertmanager, and Grafana use the same naming scheme in
their overlays. `TERMLINK_POSTGRES_SHM_SIZE` defaults to `128m`,
`TERMLINK_VALKEY_MAXMEMORY` to `384mb` with `noeviction`, and container logs
rotate at `10m` x 5 via `TERMLINK_CONTAINER_LOG_MAX_SIZE` and
`TERMLINK_CONTAINER_LOG_MAX_FILES`.

`TERMLINK_S3_ENDPOINT` is the server-side S3/MinIO endpoint used for bucket
creation and object verification/deletion. `TERMLINK_S3_PUBLIC_ENDPOINT` is
used only in client-facing presigned upload/download URLs and defaults to the
internal endpoint when omitted. For the disposable integration Compose stack,
the internal endpoint remains `http://minio:9000` while the host-reachable
presigned endpoint is `http://127.0.0.1:59000`.

## Provider-managed S3

`core.compose.yml` always expects an external OIDC issuer. When an external S3
provider is also available, add `external-services.compose.yml` instead of
`history.compose.yml`. This keeps the required PostgreSQL and Valkey services,
but does not create Keycloak or MinIO. Its edge configuration serves only the
TermLink application host, so Caddy does not request a certificate for or proxy
the external object-storage host.

```powershell
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\external-services.compose.yml `
  --profile edge config

docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\external-services.compose.yml `
  --profile edge up --detach --wait

# Migration is a successful one-shot dependency; remove only its stopped
# container after startup. This does not remove images or named data volumes.
docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\external-services.compose.yml `
  rm --force migrate
```

Set `TERMLINK_S3_ENDPOINT`, `TERMLINK_S3_PUBLIC_ENDPOINT`,
`TERMLINK_S3_ACCESS_KEY_FILE`, and `TERMLINK_S3_SECRET_KEY_FILE` for that
provider. After removing the completed migration job, the steady state is three
containers without the optional edge profile (`server`, PostgreSQL, and
Valkey), or four with the edge. OIDC remains a required external capability;
this shape externalizes S3 rather than disabling encrypted history.

The Go config package fails fast for missing, empty, or over-permissive secret
files. Its `Secret` values redact themselves in ordinary and `%#v` formatting;
startup errors identify only the configuration field, never the secret value.

## 开发项 1-7 完成矩阵

| 开发项 | 范围 | 状态 | 验证与边界 |
| --- | --- | --- | --- |
| 1 | 生产账号/OIDC、同账号设备直连与撤销 | **已实现并验证** | 生产 server 启用账号级自动设备注册/路由；store 与 E2E 门禁覆盖同 OIDC subject 的 PC/Android 无 Grant 发现、Session 路由、Lease、Realtime 输入输出与跨账号拒绝。`production-e2e-probe` 已切到无自授信/无 Grant 时序；旧 pairing/grant/trust 表/API 仅作兼容。 |
| 2 | 生产 Protobuf 实时协议、X25519 + ChaCha20-Poly1305、Relay | **已实现并验证** | `protocol/protobuf/termlink_realtime.proto`、Go `realtimewire`/Relay、Android `core-network`、Windows `src/Protocol`；向量、协议和 Relay 测试通过。 |
| 3 | Windows ConPTY 到 Android 真实输入输出、ACK/Gap/重连、租约与本地输入优先 | **已实现并验证** | Windows `ProductionAgentRuntime`、Android 生产 Session Client、服务端租约/撤销；生产路径自动化覆盖有界重放、ACK/Gap、输入去重、租约和本地输入 epoch。 |
| 4 | 每 Android 设备单独加密的有界历史、S3/MinIO 预签名上传/下载/校验/删除 | **已实现并验证** | Android `core-crypto`/`feature-history`、Windows History Uploader、Go history store；SHA-256、密文元数据、预签名和删除集成测试通过。 |
| 5 | 服务端 Catalog、计费验证边界、权益与配额 | **核心已实现并验证（Provider 待配置）** | Go `billing`/Catalog/entitlement API、可注入 verified-purchase 契约与 Android `feature-billing` 已测试；真实 Google/Web verifier 未配置时生产入口返回 `BILLING_UNAVAILABLE`。 |
| 6 | Android 产品页面、Windows Tray、Compose PostgreSQL/Valkey/MinIO/OIDC 接线及自动化验收 | **已实现并验证** | Android product UI、Windows Account/Devices Tray、`core/history/integration.compose.yml` 与生产运行时；Go/Android/.NET/Compose 自动化验收通过。 |
| 7 | 生产 TLS/受信发布证书真实安装更新、备份恢复演练、镜像签名、监控告警、生产 CA/发布加固 | **已实现并本地验证（外部发布待验收）** | Caddy/ACME、内部 Prometheus、Alertmanager/Grafana、真实 PostgreSQL+MinIO 恢复演练、摘要固定、SBOM/provenance/Cosign workflow 与部署策略已验证；生产域名签发、告警接收器和 GitHub OIDC release tag 仍需外部执行。 |

统一边界：`LAN Replay V1`、`TLT1`（Phase 1 Test Session V1）和 Android Compose
Harness 都是测试夹具，不是生产协议；Android 历史客户端只接受 HTTPS。Integration
Compose 中 MinIO 的 HTTP 端口仅供本机 loopback 集成测试，不能当作生产历史传输。1-7
的本地自动化结果不代表 arm64 真机、厂商 IME、外接键鼠、平板/折叠屏或外部生产发布已经验证。

首版运行时以 OIDC issuer + subject 对应的账号作为发现和路由边界：Windows Agent 与 Android
登录同一账号后直接使用该账号的受管 PC/Session，不要求配对、自授信或逐 PC Grant。每台设备
仍生成独立密钥并证明私钥持有，跨账号请求必须拒绝；远程输入仍要求短时 Lease，本地输入优先；
显式设备撤销会断开连接并停止新 Key Epoch 分发。普通客户端退出登录不是服务端设备撤销或解绑。

运行时配置仍要求真实 OIDC/Google/Web Billing Provider 在部署前注入；本地默认配置只验证
契约和 `BILLING_UNAVAILABLE` 失败边界，不冒充线上购买或公网部署结果。

## TLS, observability, backup, and release checks

Core uses `compose/external-services.Caddyfile` for the application host only,
with TLS 1.2/1.3, HSTS, a production Let's Encrypt ACME endpoint, and public
denial of `/metrics` and `/debug`. The local-history overlay replaces that mount
with `Caddyfile` and adds the separate history host. It accepts only signed
`GET`/`PUT` requests for canonical `termlink-history/accounts/...` ciphertext
object keys; MinIO admin, bucket enumeration, multipart, and delete APIs fail
closed at the edge. Access logging remains disabled to avoid recording SigV4
query credentials. Metrics listen on the internal-only server port 9090.
The observability layer adds Prometheus, Blackbox readiness probing,
Alertmanager, and a provisioned Grafana dashboard; Grafana and Alertmanager
bind to loopback by default, while Prometheus has no host port.

Set `TERMLINK_IMAGE` to an immutable `tag@sha256:<digest>` reference and run:

```powershell
.\ops\Test-ProductionDeployment.ps1 `
  -EnvFile .\.env `
  -ComposeFile @(
    '.\compose\core.compose.yml',
    '.\compose\history.compose.yml',
    '.\compose\observability.compose.yml'
  )

docker compose --env-file .env `
  -f compose\core.compose.yml `
  -f compose\history.compose.yml `
  -f compose\observability.compose.yml `
  --profile edge config
```

The policy check requires the complete reviewed service set, the TermLink image
from `docker.io/myucxy/termlink` at an immutable digest, exact reviewed
third-party images, fixed network membership, approved environment keys,
`/run/secrets` references, and no declared secret value anywhere in the rendered
configuration. It also rejects unexpected ports, non-loopback management ports,
missing or reparse-point secret paths, reserved test domains, and any ACME CA
other than the production Let's Encrypt endpoint.

Backups contain a PostgreSQL custom-format dump and an exact MinIO mirror with
per-file sizes and SHA-256 values. Valkey is explicitly reconstructable state.
Production backup output must be outside the repository on an encrypted-at-rest
destination. The operator must keep a separate random 32-byte-or-longer manifest
authentication key in protected storage. Restore authenticates the manifest with
HMAC-SHA-256, validates every payload, copies each payload to isolated container
staging, rechecks its SHA-256 there, and only then performs destructive work. It
keeps application services stopped if restoration fails:

```powershell
.\ops\Backup-TermLink.ps1 `
  -Destination 'E:\TermLinkEncryptedBackups\2026-08-04' `
  -ManifestAuthenticationKeyFile 'E:\TermLinkProtectedKeys\backup-manifest.key' `
  -DestinationEncryptionConfirmed

.\ops\Restore-TermLink.ps1 `
  -BackupPath 'E:\TermLinkEncryptedBackups\2026-08-04' `
  -ManifestAuthenticationKeyFile 'E:\TermLinkProtectedKeys\backup-manifest.key' `
  -ConfirmDestructiveRestore

.\ops\Test-BackupRestore.ps1
```

The release workflow builds `linux/amd64` and `linux/arm64`, emits SPDX and
CycloneDX SBOMs plus maximum BuildKit provenance, and signs the immutable Docker Hub
digest plus SLSA provenance and both SBOM attestations with GitHub OIDC/Cosign. All third-party Actions and
container bases are pinned. Local policy and post-release verification entry
points are:

Reproducible builds must pass the same immutable metadata twice, including
`SOURCE_DATE_EPOCH` (the source commit Unix timestamp), `CREATED`, `VERSION`,
and `REVISION`. The Dockerfile retains a deterministic compatibility default,
but signed release automation should always pass the actual source timestamp.
After two `--no-cache` builds, `docker image inspect <tag> --format '{{.Id}}'`
must return the same image ID. The Dockerfile-specific ignore file restricts
BuildKit input to Server source and migrations, avoiding transfer of
Android/Windows outputs or repository-local artifacts.

```powershell
.\release\Test-ReleasePolicy.ps1
.\release\Verify-ImageRelease.ps1 `
  -Image 'docker.io/myucxy/termlink' `
  -Digest 'sha256:<64-lowercase-hex>' `
  -Tag 'v1.0.0'
```

After the signed workflow has completed, upgrade the local production-acceptance
stack with the exact published digest (without resetting persistent data):

```powershell
.\ops\Start-ProductionAcceptance.ps1 `
  -LuckyBindAddress '0.0.0.0' `
  -LuckyPort 8080 `
  -ServerImage 'docker.io/myucxy/termlink:1.0.0@sha256:<64-lowercase-hex>'
```

The published-image overlay removes the local server build definition, uses the
same immutable reference for `migrate` and `server`, and rejects images whose OCI
version, source, creation time, or Git revision metadata is missing. Do not pass
`-Reset` for an in-place upgrade.

Local validation cannot substitute for real DNS/ACME issuance, an approved
Alertmanager receiver, or execution of a signed GitHub release tag.

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
