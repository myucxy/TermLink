# Production Protocol Acceptance Stack

This disposable stack runs the production TermLink `serve` command behind TLS.
The default distributed topology uses Keycloak, PostgreSQL, and Valkey. The
explicit single-node topology keeps Keycloak and PostgreSQL while embedding
coordination, Direct signalling, migrations, and public Keycloak route
forwarding into Server. Neither topology registers the LAN Replay or `TLT1`
routes. The embedded identity database and local CA remain disposable
acceptance fixtures; they are not production data stores or trust anchors.

The acceptance Keycloak image is augmented at image-build time and then starts
with `--optimized` on a read-only root filesystem. Runtime startup therefore
does not use Keycloak's development mode or mutate the image layer. The small
acceptance realm also runs with a 768 MiB container limit, a 45% maximum JVM
heap, and two CPU cores; these are acceptance guardrails, not production sizing
recommendations.

Start and validate the complete stack from the repository root:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1
```

To validate the consolidated one-Server topology behind the configured
external TLS ingress:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -SingleNode
```

Its steady state is exactly `server`, `keycloak`, and `postgres`.
`runtime-secrets-init`, `keycloak-init`, and `server-secrets-init` are bounded
one-shot jobs and are removed after the probe. `valkey`, `direct-valkey`,
`migrate`, and `lucky-upstream` are not started. Direct/NAT signalling is
enabled locally and exercised by the acceptance probe, but Relay remains the
authoritative terminal path. Re-running without `-SingleNode` is the immediate
distributed rollback; no data-volume conversion is required.

The script generates random credentials under the ignored
`server/secrets/acceptance` directory, starts the containers, and runs a
cross-container probe. It exports a disposable local CA only when
`-LanAddress` explicitly exposes the private TLS listener. The current product
acceptance contract is:

To add the isolated Direct signaling/NAT-discovery lane for cross-client shadow
tests, opt in explicitly:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -EnableDirectTransportShadow
```

This creates a separate Direct Valkey and separate generated credential, warms
that service before the TermLink Server starts, and includes it in the health
gate. A network-isolated one-shot initializer stages the Direct Valkey password
into its own private named volume as mode `0400`, owned by UID `999` and GID
`1000`; Direct Valkey cannot start unless that initializer succeeds. The
acceptance-only overlay separately stages the Direct URL into the server's
existing private `/run/secrets` volume; it does not create a nested mount below
that read-only volume. Relay remains the authoritative connection and startup
path, and Direct still cannot carry terminal input, output, ACK, History,
Snapshot, or Session lifecycle traffic.

1. register a Windows PC device and an Android device with access tokens carrying the same OIDC subject;
2. let Android discover the PC and its managed session immediately, without pairing, self-trust confirmation, or a per-PC Grant;
3. open both Realtime roles, route opaque production Protobuf Envelopes, and acquire a short-lived control lease before sending input;
4. release the lease as the PC and verify that explicit device revocation disconnects the Android socket;
5. repeat the discovery/routing checks with another OIDC subject and require cross-account denial.

The checked-in `production-e2e-probe` follows this same-account path without
calling the legacy self-trust, pairing, or per-PC Grant endpoints. A successful
stack run therefore proves the deployment-level REST/Realtime/lease sequence;
the separate `Invoke-SameAccountE2EGate.ps1 -RequireInfrastructure` gate also
asserts that another OIDC subject cannot discover, subscribe to, or lease the
session. No credential or token is printed.

The managed secret directory rejects reparse points and does not retain
inherited host permissions. On Windows its ACL is limited to the invoking
account, `SYSTEM`, and local Administrators; on Unix the directory is `0700`
and each file is `0600`.

The acceptance Valkey password is generated into the managed
`valkey-password` file. The companion `redis-url` file contains only the
matching authenticated Redis URI; Compose never renders either value as an
environment variable. Valkey creates its ACL on a private tmpfs, runs as the
non-root Valkey UID, and exposes no host port.

Docker Desktop does not reliably apply Compose `uid`, `gid`, and `mode` to
file-backed secrets. Before any long-running service starts, an isolated
one-shot container copies each credential into a service-private named volume,
sets the exact runtime owner, and applies mode `0400`. The start script then
checks the metadata seen inside Keycloak, PostgreSQL, and Valkey and
fails the deployment if any credential is group- or world-readable. Secret
contents are never included in that check or its output. When Direct shadow is
enabled, the same gate additionally requires its password to be exactly
`400:999:1000` inside Direct Valkey. After all dependency conditions have
succeeded, the start script removes the four base completed one-shot containers
(`runtime-secrets-init`, `keycloak-init`, `server-secrets-init`, and `migrate`)
plus `direct-valkey-secrets-init` when Direct is enabled. The shared
`server-secrets-init` copies the private OIDC-loopback CA only after
`lucky-upstream` has created it. This post-probe cleanup does not delete named
volumes, images, or data.

The default public-production steady state contains `lucky-upstream`,
`keycloak`, `postgres`, `valkey`, and `server`. PostgreSQL and Valkey provide
application state and realtime coordination; Keycloak supplies OIDC.
`lucky-upstream` owns both the public HTTP
upstream and a Docker-internal TLS listener used for issuer-exact OIDC
discovery, eliminating the former second Caddy `edge` service. `-LanAddress`
uses `acceptance.local-edge.compose.yml` only to expose that same TLS listener
on host port 9443. In both modes, `lucky-upstream` remains stopped until
`keycloak-init` has safely reconciled the realm and enabled registration.

On a clean deployment, `keycloak-init` also creates the product account
`admin`, assigns the realm bootstrap role `SUPER_ADMIN`, and installs a random
24-character temporary password. Retrieve that password exactly once from a
trusted local terminal, then complete Keycloak's mandatory password change:

```powershell
.\server\ops\Get-InitialAdminPassword.ps1
```

The retrieval command rotates the host bootstrap input before printing the
original and records that it was claimed. Do not redirect or log its output.
If it was already claimed and then lost, issue and retrieve a new temporary
credential without exposing the Keycloak bootstrap credential:

```powershell
.\server\ops\Reset-InitialAdminPassword.ps1
.\server\ops\Get-InitialAdminPassword.ps1
```

If Docker Desktop cannot pull Quay layers directly, the start script downloads
the exact official platform manifest through the OCI registry API, verifies the
manifest and every blob SHA-256, and loads it under an acceptance-only local
tag. It never falls back to an unpinned image.

The default public endpoints are:

- Public product home and account portal: `https://termlink.kget.cc:8910/`
- Public API/OIDC through Lucky: `https://termlink.kget.cc:8910`

The internal-CA product endpoint is exposed on port 9443 only during an
explicit private-LAN acceptance run.

## Private-LAN product E2E / 私有局域网产品 E2E

Use only this repository checkout for the following flow; do not copy artifacts
from or depend on a sibling repository. 以下流程只使用当前仓库中的命令和产物，不复制、
也不依赖任何同级仓库。

### 1. Start the LAN stack / 启动 LAN 验收栈

From the repository root, run / 在仓库根目录运行：

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -LanAddress 192.168.9.28
```

`-LanAddress` must be an IPv4 address assigned to the host. For a selected
`<LAN-IP>`, the product, API, and OIDC origin is
`https://termlink.<LAN-IP>.sslip.io:9443`. The example therefore uses
`https://termlink.192.168.9.28.sslip.io:9443`.

`-LanAddress` 必须是本机网卡实际拥有的 IPv4 地址。对于指定的 `<LAN-IP>`，产品、API
和 OIDC Origin 均为 `https://termlink.<LAN-IP>.sslip.io:9443`；上面的示例因此使用
`https://termlink.192.168.9.28.sslip.io:9443`。

The script exposes Lucky's TLS listener to the LAN and exports its disposable CA to
`server\secrets\acceptance\acceptance-root-ca.crt`. It also runs the production
protocol probe automatically. 脚本会把 Lucky TLS 监听器暴露到局域网，把一次性 CA 导出到上述
路径，并自动运行生产协议探测。

### 2. Configure TLS trust / 配置信任

Configure the Android test device or emulator to trust only the exported CA.
The app does not install that CA or bypass certificate validation automatically.
在 Android 测试手机或模拟器中显式配置对该 CA 的信任；App 不会自动安装 CA，也不会绕过
证书校验。

For the Windows test processes, set the process-scoped CA path from the
repository root / 对 Windows 测试进程，在仓库根目录设置进程级 CA 路径：

```powershell
$env:TERMLINK_ACCEPTANCE_CA_FILE = (Resolve-Path `
  '.\server\secrets\acceptance\acceptance-root-ca.crt').Path
```

This disposable CA is for Debug/E2E only and must never become a production
trust anchor. 一次性 CA 只用于 Debug/E2E，不能作为生产信任锚。

### 3. Build Android Debug / 构建 Android Debug

Build from the `android` directory with the LAN origin / 在 `android` 目录使用 LAN
Origin 构建：

```powershell
cd android
.\gradlew.bat :app:assembleDebug `
  -PtermlinkServerUrl=https://termlink.192.168.9.28.sslip.io:9443
cd ..
```

`termlinkServerUrl` affects Debug builds only. Android Release builds ignore
the LAN injection and use the public origin `https://termlink.kget.cc:8910`.
`termlinkServerUrl` 仅影响 Debug 构建；Android Release 会忽略 LAN 注入并继续使用上述
公网 Origin。配置好测试设备的 CA 信任后，安装生成的
`android/app/build/outputs/apk/debug/app-debug.apk`；after configuring CA trust
on the test device, install that generated APK.

### 4. Point Windows at the LAN origin / 配置 Windows LAN Origin

Run the built Tray with / 启动已构建的 Tray：

```powershell
TermLink.Tray.exe `
  --server-url https://termlink.192.168.9.28.sslip.io:9443
```

The direct Agent account-link form is / Agent 直接关联账号的命令形式为：

```powershell
TermLink.Agent.exe --link-account `
  --server-url https://termlink.192.168.9.28.sslip.io:9443 `
  --pc-name <PC-NAME>
```

Use the actual built executable paths if they are not on `PATH`. Without
`--server-url`, Windows clients use the public origin
`https://termlink.kget.cc:8910`. 如果可执行文件不在 `PATH`，请使用实际构建路径；未传入
`--server-url` 时，Windows 客户端仍使用公网 Origin。

### 5. Exercise the product flow / 执行产品 E2E

Sign in to Windows and Android with the same OIDC account. Create or open a
TermLink-managed session on Windows, discover that PC/session on Android, open
the production Realtime/E2EE path, acquire the short-lived control lease, and
then verify output and input. Windows 与 Android 登录同一 OIDC 账号；在 Windows 创建或
打开 TermLink 受管 Session，再从 Android 发现 PC/Session，建立生产 Realtime/E2EE
链路，取得短时控制租约后验证输出和输入。该主流程不需要配对、自授信或逐 PC Grant。

The automatic stack probe proves the server-side REST/Realtime/lease
contract, but it does not replace this real Android + Windows managed-session
test. Only sessions created and managed by TermLink are forwarded; TermLink
cannot attach to or take over arbitrary existing cmd, PowerShell, or Windows
Terminal tabs. 自动探测只证明服务端协议，不等于完成真实 Android + Windows 受管会话
E2E；TermLink 不会转发或接管任意已经打开的终端标签页。

The phone and PC must resolve `sslip.io` and reach host TCP port `9443`; add a
host firewall rule scoped to the test LAN when required. The physical-device CA
installation/trust step is platform-dependent and remains manual. 手机与 PC 必须能够解析
`sslip.io` 并访问主机 TCP 9443；如有需要，由操作者添加仅限测试局域网的防火墙规则。
真机 CA 安装与信任步骤因平台而异，当前仍需手工完成。

### 6. Recorded live result (2026-08-21) / 已记录的实机结果（2026-08-21）

The live origin was `https://termlink.192.168.9.28.sslip.io:9443`. A Windows
Agent and an Android 16 emulator both signed in as the disposable
`acceptance-probe` account. Android input executed in a TermLink-managed
PowerShell 7 session and created this Windows marker:

```text
D:\Dev\Temp\TLANDROID1787315819158
```

The terminal output returned to Android. Direct acceptance-database checks
reported `0` Device Grant rows and `0` Pairing Challenge rows. This is evidence
for same-account discovery and routing without pairing, self-trust, or a per-PC
Grant; no credential or token was captured in the record.

本次真实 Origin 为 `https://termlink.192.168.9.28.sslip.io:9443`。Windows Agent 与
Android 16 模拟器均登录一次性账号 `acceptance-probe`。Android 输入在 TermLink 受管的
PowerShell 7 Session 中执行，并创建上述 Windows marker；终端输出随后返回 Android。验收
数据库直接查询得到 Device Grant `0` 行、Pairing Challenge `0` 行，因此此链路没有依赖配对、
自授信或逐 PC Grant；记录中不包含凭据或 Token。

In the earlier run, Android was force-stopped and a cold launch replayed the
previous terminal output. A manual `Devices -> Sessions` transition reacquired
control, after which the next Android input created:

```text
D:\Dev\Temp\TLRECONNECT1787316577617
```

Sanitized Windows status reported Relay Connected, `LastSequence = 239`, and
`ViewerCount = 1`. That earlier manual-tab limitation has since been superseded.
After same-holder lease resume was implemented, Android PID `8234` was
force-stopped, disappeared, and cold-started as PID `8360`. With no tab
transition, the restored screen reported `SessionsSelected=True`,
`SessionTitle=True`, and `EnterPresent=True`; both historical markers
`TLANDROID1787315819158` and `TLRECONNECT1787316577617` were restored
automatically. Valkey lease TTL observations during this sequence were `27882 ms`,
`25145 ms`, and `20007 ms`. Cold-start Session/UI, history, and same-holder lease
restoration are therefore verified. The local evidence screenshot is
`D:\Dev\Temp\termlink-fixed-cold-start-20260821-221034.png`.

早期复验中，Android 强停、冷启动并重放历史输出后，需要人工切换
`Devices -> Sessions` 才重新取得租约，随后输入创建了上述第二个 marker。同持有者租约恢复实现
后的后续复验已取代这一限制：Android PID 从 `8234` 经强停变为空，再冷启动为 `8360`；无需
切换 Tab，页面语义即为 `SessionsSelected=True`、`SessionTitle=True`、`EnterPresent=True`，
两个历史 marker 均自动恢复；同一过程中 Valkey 租约 TTL 依次观测为 `27882 ms`、`25145 ms`、
`20007 ms`。因此强停/冷启后的 Session 页面、历史和同持有者租约恢复已经验证，证据截图如上。

The Android 16 IME fix and standard command input field are implemented.
However, successful Windows execution of the newest command marker
`tlcold20260821223551` has not been confirmed, so fresh command execution after
cold start remains pending and is not recorded as passed. Android 16 IME 修复与
标准命令输入框已经实现，但最新命令 marker `tlcold20260821223551` 尚未确认在 Windows 执行
成功；冷启后的新命令执行仍待真实 E2E 验证，不能标为通过。

The Windows run used `TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT` with the isolated
root `D:\Dev\Temp\TermLink-MainE2E-mainmt2xowb8`. The Agent honors this override
only when `TERMLINK_ACCEPTANCE_CA_FILE` is set at the same time. Normal
`%LOCALAPPDATA%\TermLink` configuration and CurrentUser DPAPI credential paths
therefore remain unchanged; their before/after hashes were unchanged in this
run. Windows 验收数据根覆盖只在同时设置显式 acceptance CA 时启用，不会把普通运行默认迁移
到该目录。

This host also had a user system proxy at `127.0.0.1:7897`. REST/OIDC already
bypassed it for LAN acceptance, but `ClientWebSocket` initially inherited the
proxy and failed with WSS TLS EOF. When an explicit acceptance CA is loaded,
the restricted LAN-acceptance WebSocket path now sets `Proxy = null` while
retaining TLS hostname validation. Relay connected successfully after that
change. 本机用户代理曾导致 WSS TLS EOF；受限的 LAN acceptance WebSocket 现在显式绕过
该代理，同时继续校验 TLS 主机名。

The two screenshots below are local acceptance artifacts and are not checked
into this repository / 以下截图仅为本机验收产物，未进仓：

```text
D:\Dev\Temp\termlink-TLANDROID1787315819158-output.png
D:\Dev\Temp\termlink-TLRECONNECT1787316577617-reconnect.png
D:\Dev\Temp\termlink-fixed-cold-start-20260821-221034.png
```

The public root is a product introduction page. Selecting **Login** starts an
OIDC Authorization Code + PKCE flow through the `termlink-web` public client,
then returns every role to `/account`; authorized console roles can continue to
`/admin` from there. The portal stores only an encrypted, Secure,
HttpOnly, SameSite browser session; access and ID tokens are not persisted in
the browser cookie. Signing out is protected by an origin check and CSRF token.
The Keycloak account console uses the TermLink account theme, preserves the
same `termlink-theme` preference, and exposes a validated return link to
`/account`. The portal's allowlisted `/account/identity` bridge first renews the
`termlink-web` OIDC session and then redirects to the configured realm Account
Console. This keeps the product navigation and theme available even when the
shorter Keycloak SSO session expired while the portal session remained valid.

Self-service registration is enabled after `keycloak-init` has created or
verified the managed bootstrap administrator. The public Lucky upstream remains
stopped during that reconciliation, and an existing unmarked `admin` account
fails the deployment closed instead of receiving `SUPER_ADMIN`. The first
version does not require email verification. Account creation alone does not
grant access to another account's PC. Once Windows Agent and Android authenticate
as the same OIDC subject, their independently keyed, non-revoked devices are
registered into that account and can use its managed PC/session routes without
pairing or a per-PC Grant.

Keycloak serves the responsive TermLink account theme from
`acceptance/keycloak-theme/termlink`. The realm import selects the theme for a
clean deployment, and `keycloak-init` applies the same setting idempotently to
an existing realm. Self-service registration is enabled and can be completed
from the login page. This disposable stack does not configure SMTP, so email
verification is explicitly disabled instead of leaving newly registered users
in an action they cannot complete. A production operator must configure a
working SMTP provider before enabling Keycloak email verification.

The cross-process deployment probe uses the dedicated `acceptance-probe` OIDC
user and a persistent probe PC identity. Interactive Windows and browser
acceptance uses `acceptance-user`, so probe devices and interactive devices
remain in separate account scopes. Both users are disposable and exist only in
this acceptance realm.

Terminal history is host-owned in the current protocol. The online Windows
Agent answers bounded E2EE History/Snapshot requests, while the server routes
opaque envelopes and never stores terminal bodies. File transfers use
`REALTIME_V2`: the server stores task and routing metadata in PostgreSQL, while
bytes move between Windows and Android over realtime transport. The acceptance
stack therefore contains no object store, object network, presigned URL, or
history-object proxy route.

In public mode, the acceptance stack binds an HTTP-only Lucky upstream on host
port `8080` by default. Configure Lucky to forward the public
`https://termlink.kget.cc:8910` origin to
`http://<DOCKER-HOST-LAN-IP>:8080` (for this host,
`http://192.168.9.28:8080`); TLS continues to terminate at Lucky. Identity
paths are routed to Keycloak and all other paths are routed to the TermLink
Server.
Override the HTTP upstream bind only when needed with `-LuckyBindAddress` and
`-LuckyPort`; `-LanAddress` does not change that separate binding implicitly.

The Caddy CA is disposable and is not a production trust anchor. Local browser
or emulator testing must explicitly trust only the exported acceptance CA;
production clients continue to require a publicly trusted HTTPS chain.
The Windows Agent test path can instead set `TERMLINK_ACCEPTANCE_CA_FILE` to
the exported certificate. That override is accepted only for
loopback acceptance hosts and private-LAN hosts of the form
`termlink.<RFC1918-IP>.sslip.io`; it still enforces the TLS hostname.

Stop the stack while retaining data:

```powershell
.\server\ops\Stop-ProductionAcceptance.ps1
```

Remove the disposable volumes:

```powershell
.\server\ops\Stop-ProductionAcceptance.ps1 -RemoveVolumes
```

Stop always composes the Direct overlays after an opt-in run, so its Direct
container, initializer, and isolated network are removed with the base stack.
`-RemoveVolumes` additionally removes both the Direct data volume and its
private password volume; a reset always performs that full volume cleanup.

For a first-version clean-room deployment test, reset only the managed
`termlink-production-acceptance` project, regenerate its disposable secrets,
rebuild, and run the production protocol probe:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -Reset
```

The reset path checks the acceptance marker before deleting its ignored secret
directory. The next run still enables the Direct topology only when
`-EnableDirectTransportShadow` is present, but the cleanup model always includes
the Direct overlays so a previous opt-in run cannot leave its isolated Direct
Valkey volume behind. It does not remove containers or volumes owned by other
Docker projects.
