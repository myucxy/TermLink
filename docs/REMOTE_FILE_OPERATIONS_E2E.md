# Android ↔ Server ↔ Windows 文件操作 E2E

本文记录当前已经落入源码的远程文件操作契约、端到端数据路径、Android 下载中心生命周期、Docker 部署取舍与验收边界。中文是产品主说明，首次出现的主要界面词同时给出英文，便于核对中英文资源。

实现基线：2026-08-25。协议真值以 `server/internal/api/file_operations.go`、`server/internal/store/file_operations.go`、数据库 Migration 17/18/19、`win-agent/src/Agent/ProductionFileOperationWorker.cs`、`win-agent/src/Agent/WindowsFileOperations.cs`、`android/app/src/main/kotlin/dev/termlink/product/ProductionProductRepository.kt` 和 `RemoteFileModels.kt` 为准。

## 已实现的产品行为

- Android 设备页只展示在线 Windows PC；每台 PC 的右上状态区域提供“浏览文件 / Browse files”，设备页右上角提供跨 PC 聚合的“下载中心 / Download center”。
- 文件浏览从空路径开始列出 Windows 逻辑驱动器。进入目录后支持返回上一级、刷新和“加载更多 / Load more”。目录优先，再按名称排序。
- 文件和目录都可下载。文件按原始字节传输；目录先在 Windows 端生成包含所选根目录的 ZIP，保留空目录，不跟随 junction、symlink 等 reparse point。
- Android 先通过 Storage Access Framework（SAF）选择保存位置，再创建服务端 `DOWNLOAD` 任务。文件内容不经过 TermLink API 进程中转：Windows Agent 使用预签名 `PUT` 直传 S3/MinIO，Android 使用预签名 `GET` 直下对象。
- Android 下载时流式写入 SAF 文档，同时检查声明长度和 SHA-256；支持零字节文件。任何截断、超长或摘要不符都转为失败，并删除或清空不完整目标。
- 文件/目录行右侧统一使用操作菜单；目录菜单提供 ZIP 下载、收藏/取消收藏，以及该 PC Agent 实际上报可用的命令行。收藏按服务端与账号隔离，并记录 PC 与绝对路径，可从收藏目录跨 PC 直达。
- 目录页面只在 PC 的 `supported_shells` 中包含相应值时，才允许启动 CMD（`cmd.exe`）或 Windows PowerShell（`powershell.exe -NoLogo -NoProfile`）。新窗口由 TermLink SessionHost 托管，随后按返回的准确 `session_id` 出现在 Android 会话页。
- 下载中心显示准备/压缩、下载、校验、完成、失败和取消状态以及字节进度；支持取消、失败或取消后的重试、终态记录删除。删除记录只删 Android 元数据，不删除已经保存的 SAF 文档。
- Android 和 Windows Agent 使用同一账号时直接发现并处理任务，不要求用户执行逐 PC 配对或 Grant。文件操作仍只会路由到所选 PC 自己的 Agent。
- 文件浏览、下载和远程开 Shell 都受 Windows 的“远程访问已启用”开关约束；关闭后 Agent 不再领取新任务，并取消正在处理的长任务。

主要中英文 UX 对应为：`浏览文件 / Browse files`、`下载中心 / Download center`、`正在 Windows 上准备 / Preparing on Windows`、`正在 Windows 上压缩 ZIP / Compressing ZIP on Windows`、`正在下载 / Downloading`、`正在校验大小与 SHA-256 / Verifying size and SHA-256`、`已完成 / Completed`、`失败 / Failed`、`已取消 / Cancelled`、`重试 / Retry` 和 `移除记录 / Remove record`。两套资源使用相同 key，系统语言切换不改变状态机或协议值。

## 总体流程图

```mermaid
flowchart LR
    A[Android 产品界面] -->|创建及查询任务| S[TermLink Server API]
    S -->|任务与状态| P[(PostgreSQL)]
    W[Windows Agent] -->|claim renew complete| S
    W -->|LIST 本地枚举| FS[Windows 文件系统]
    W -->|OPEN_TERMINAL| H[SessionHost]
    H --> T[Windows Terminal 或可见控制台]
    W -->|预签名 PUT 文件或 ZIP| O[(S3 或 MinIO)]
    O -->|预签名 GET| A
    A -->|流式写入| D[Android SAF 文档]
    H -->|会话注册与实时终端流| R[TermLink Realtime]
    R --> A
```

控制面是 Android/Windows 与 Server API 之间的小型 JSON 请求；下载数据面是 Windows → 对象存储 → Android。Realtime 只承载打开后的终端会话，不承载文件字节。

## 服务端任务协议

### 端点

| 调用方 | 方法和路径 | 请求重点 | 成功响应 |
| --- | --- | --- | --- |
| Android | `POST /v1/pcs/{pcId}/file-operations` | `operation`、`path`，可选 `offset` 或 `shell_type` | `201`，`file_operation` |
| Windows | `POST /v1/pcs/{pcId}/file-operations/claim` | 空正文 | `200`，`file_operation` + 顶层 `lease_token`；无任务时 `204` |
| Android | `GET /v1/pcs/{pcId}/file-operations/{operationId}` | 无 | `file_operation`；成功下载在顶层附带 `download` |
| Android | `DELETE /v1/pcs/{pcId}/file-operations/{operationId}` | 无 | `file_operation.status=CANCELLED`；已取消时幂等 |
| Windows | `POST /v1/pcs/{pcId}/file-operations/{operationId}/upload` | 租约、文件名、大小、SHA-256、是否目录 ZIP | 顶层 `upload` 预签名请求 |
| Windows | `POST /v1/pcs/{pcId}/file-operations/{operationId}/renew` | `lease_token` | 更新后的 `file_operation.lease_expires_at` |
| Windows | `POST /v1/pcs/{pcId}/file-operations/{operationId}/complete` | 租约、`SUCCEEDED` + 可选结果，或 `FAILED` + `error` | 终态 `file_operation` |

Android 创建请求只有三种合法形状：

```json
{"operation":"LIST","path":"","offset":0}
```

```json
{"operation":"DOWNLOAD","path":"C:\\Users\\me\\report.txt"}
```

```json
{"operation":"OPEN_TERMINAL","path":"C:\\work","shell_type":"windows-powershell"}
```

`LIST` 的根目录请求可省略 `offset`；`DOWNLOAD` 不允许 `offset` 或 `shell_type`；`OPEN_TERMINAL` 只接受 `command-prompt` 和 `windows-powershell`。路径必须是有效 UTF-8、不得含 NUL，服务端上限是 4096 字节；Windows 进一步要求文件/目录操作使用已存在的绝对路径，并拒绝 `\\?\`、`\\.\` 设备命名空间。

公共任务视图包含以下字段中的适用部分：

```json
{
  "file_operation": {
    "id": "operation UUID",
    "pc_id": "PC UUID",
    "operation": "LIST | DOWNLOAD | OPEN_TERMINAL",
    "path": "C:\\work",
    "offset": 0,
    "shell_type": "windows-powershell",
    "status": "PENDING | PROCESSING | SUCCEEDED | FAILED | CANCELLED | EXPIRED",
    "attempt_count": 1,
    "lease_expires_at": "RFC 3339 timestamp",
    "result": {},
    "error": "stable-code: readable message",
    "expires_at": "RFC 3339 timestamp",
    "completed_at": "RFC 3339 timestamp",
    "cancelled_at": "RFC 3339 timestamp",
    "created_at": "RFC 3339 timestamp",
    "updated_at": "RFC 3339 timestamp"
  }
}
```

对象存储 key、租约摘要、账号 ID 和请求设备 ID 不出现在 Android 响应中。原始租约只在 claim 响应顶层返回给对应 Windows Agent。

### 状态与时限

```mermaid
stateDiagram-v2
    [*] --> PENDING: Android 创建
    PENDING --> PROCESSING: Windows 原子 claim
    PROCESSING --> PROCESSING: 租约过期后重新 claim
    PROCESSING --> SUCCEEDED: 完成且结果校验通过
    PROCESSING --> FAILED: Windows 报告稳定错误
    PENDING --> CANCELLED: Android 取消
    PROCESSING --> CANCELLED: Android 取消
    PENDING --> EXPIRED: 24 小时到期
    PROCESSING --> EXPIRED: 24 小时到期
    SUCCEEDED --> [*]
    FAILED --> [*]
    CANCELLED --> [*]
    EXPIRED --> [*]
```

- 任务有效期：创建后 24 小时。
- claim 租约：1 小时，且 `lease_expires_at` 永不超过任务 `expires_at`。Windows 处理任务时默认每 5 秒续租。
- Agent 空闲 claim 间隔和 Android 状态轮询间隔：均为 750 ms；瞬态连接错误采用最高 15 秒的退避。
- Windows 的目录与文件 Worker 独立于 Agent 心跳和实时会话运行。HTTP 超时不会结束 Worker；未分类异常由内部监督循环按最高 15 秒退避重新启动该 Worker，不重启 Agent，也不停止现有本地会话。每个故障周期只记录一次中英双语降级事件，恢复后记录一次恢复事件，诊断位于 `%LOCALAPPDATA%\TermLink\logs\TermLink-file-operations.jsonl`。
- 只有持有当前 32 字节 Base64URL 租约的 Windows Agent 能上传元数据、续租或完成。
- 多 Agent poller 通过 PostgreSQL `FOR UPDATE SKIP LOCKED` 原子领取；租约丢失后当前 ZIP/上传/启动工作会被取消，任务可按服务端状态决定是否重领。
- 所有修改状态的 API 请求携带 `Idempotency-Key`。Android 对一次 create 尝试保持同一 key；Windows 对结果不明确的 claim 重试复用 key，upload/renew/complete 的 key 绑定 operation、动作和租约摘要。任务 UUID 是后续轮询、取消和结果去重的唯一标识。
- `complete` 支持同一租约、同一终态正文的幂等重放；内容不同的重复完成会冲突。

## 目录浏览时序

```mermaid
sequenceDiagram
    autonumber
    participant A as Android
    participant S as Server API
    participant P as PostgreSQL
    participant W as Windows Agent
    participant F as Windows FS

    A->>S: POST file-operations LIST path and offset
    S->>P: INSERT PENDING with 24h expiry
    S-->>A: 201 file_operation
    loop Agent poll
        W->>S: POST claim
        S->>P: Atomic oldest eligible claim
    end
    S-->>W: file_operation plus lease_token
    W->>F: Enumerate roots or directory
    F-->>W: Entries
    W->>S: POST complete SUCCEEDED with LIST result
    S->>P: Persist result and completed_at
    loop Android poll every 750 ms
        A->>S: GET operation
        S-->>A: PENDING or PROCESSING or SUCCEEDED
    end
    A->>A: Render page and keep next_offset
```

成功 `LIST` 结果形状如下：

```json
{
  "current_path": "C:\\work",
  "parent_path": "C:\\",
  "entries": [
    {
      "name": "src",
      "path": "C:\\work\\src",
      "kind": "directory",
      "size_bytes": null,
      "modified_at": "2026-08-24T01:02:03Z",
      "attributes": ["directory"]
    }
  ],
  "next_offset": 500
}
```

每页最多 500 项；Windows 还把序列化结果限制在 900 KiB，给服务端 1 MiB 结果上限留出余量。Android 使用 `next_offset` 追加下一页，并按不区分大小写的绝对路径去重。切换目录时，迟到的旧请求不能覆盖新页面；新目录加载失败时恢复上一个已成功页面。

## 文件和目录下载时序

```mermaid
sequenceDiagram
    autonumber
    participant A as Android
    participant SAF as Android SAF
    participant S as Server API
    participant P as PostgreSQL
    participant W as Windows Agent
    participant O as S3 or MinIO

    A->>SAF: ACTION_CREATE_DOCUMENT suggested filename
    SAF-->>A: content URI and persistable grant
    A->>S: POST file-operations DOWNLOAD path
    S->>P: INSERT PENDING
    S-->>A: operation ID
    W->>S: POST claim
    S-->>W: operation plus lease_token
    alt Selected path is a directory
        W->>W: Create temporary ZIP and preserve empty directories
    else Selected path is a file
        W->>W: Open source as a stream
    end
    W->>W: Enforce 5 GiB and calculate SHA-256
    W->>S: POST upload metadata
    S->>P: Bind opaque object key and metadata
    S-->>W: Presigned PUT valid for 1 hour
    W->>O: PUT stream with checksum metadata
    W->>S: POST complete SUCCEEDED
    S->>O: HEAD size and checksum
    S->>P: Persist SUCCEEDED and schedule deletion
    A->>S: GET completed operation
    S-->>A: Download descriptor and presigned GET
    A->>O: GET stream
    O-->>A: File or ZIP bytes
    A->>SAF: Stream write while hashing
    A->>A: Verify exact size and SHA-256
```

下载 descriptor 位于 `GET` 响应顶层，而不是 `file_operation.result`：

```json
{
  "download": {
    "filename": "work.zip",
    "size_bytes": 12345,
    "sha256": "Base64URL without padding",
    "directory_zip": true,
    "request": {
      "url": "presigned HTTPS URL",
      "method": "GET",
      "headers": {"header-name": ["value"]},
      "expires_at": "RFC 3339 timestamp"
    }
  }
}
```

### 大小、校验和对象生命周期

- 文件和压缩后 ZIP 的共同硬上限是 5 GiB，即 `5,368,709,120` 字节。当前使用 S3 单次 `PUT`，未实现 multipart，因此不能宣称支持更大对象。
- 零字节普通文件合法；目录 ZIP 不会是零字节。
- Windows 在上传前计算 SHA-256；预签名 `PUT` 把摘要绑定为对象校验和。服务端完成任务时使用 `HEAD` 校验对象长度和 S3 checksum 元数据，不同步回读整个大对象。Android 下载后再独立计算 SHA-256。
- Windows 临时 ZIP 位于系统临时目录下的 TermLink 专用子目录，上传结束、失败或取消后释放流并删除。
- 上传 URL 有效 1 小时。任务成功后，Android 可在 `completed_at + 55 分钟` 之前请求新的 10 分钟下载 URL。
- 下载对象在终态后延迟 65 分钟通过持久化 outbox 删除。这个时间覆盖最后一个 10 分钟 GET，也覆盖取消或失败后仍可能到达的 1 小时 PUT。
- 终态记录仍保留在 PostgreSQL；对象被删除或 55 分钟下载宽限结束后，`GET file_operation` 仍返回任务，但不再附带 `download`。

## 取消、失败、恢复和重试

```mermaid
stateDiagram-v2
    [*] --> PREPARING: SAF 目标已选择
    PREPARING --> DOWNLOADING: Windows 完成上传并返回 GET
    DOWNLOADING --> VERIFYING: 已写满声明字节数
    VERIFYING --> COMPLETED: SHA-256 匹配
    PREPARING --> FAILED: 准备或远端任务失败
    DOWNLOADING --> FAILED: HTTP 长度或写入失败
    VERIFYING --> FAILED: 摘要不匹配
    PREPARING --> CANCELLED: 用户取消
    DOWNLOADING --> CANCELLED: 用户取消
    VERIFYING --> CANCELLED: 用户取消
    PREPARING --> FAILED: Android 进程重启
    DOWNLOADING --> FAILED: Android 进程重启
    VERIFYING --> FAILED: Android 进程重启
    FAILED --> PREPARING: 重试并重新选择 SAF 目标
    CANCELLED --> PREPARING: 重试并重新选择 SAF 目标
    COMPLETED --> [*]: 移除记录
    FAILED --> [*]: 移除记录
    CANCELLED --> [*]: 移除记录
```

Android 下载记录字段包括本地记录 ID、PC、远端路径、文件名、是否 ZIP、SAF URI、服务端 operation ID、总字节数、已下载字节数、状态、时间和错误；最多保存最近 100 条。进度最多每秒持久化一次，避免每个数据块都写 SharedPreferences。

取消执行以下动作：取消 Android coroutine 和 OkHttp call；删除或截断未完成 SAF 文档；若已得到 operation ID，则尽力调用服务端 `DELETE`。Windows 的下一次续租看到租约冲突后会中断压缩或上传。即使取消请求遇到瞬态网络错误，Android 本地仍显示已取消，24 小时服务端到期是最终兜底。

Android 进程被杀后，原 `PREPARING`、`DOWNLOADING` 或 `VERIFYING` 记录在下次启动时变为可重试的 `FAILED`；应用清理未完成文档、释放持久 URI 权限并尽力取消已知服务端任务。失败或取消的重试复用本地记录 ID，但清空旧 operation ID 和进度，重新选择 SAF 目标并创建新服务端任务。

“移除记录 / Remove record”只允许终态记录，并且不会调用 `DocumentsContract.deleteDocument` 删除已完成文件。退出登录或切换服务配置会停止活动下载并清空属于原账号上下文的下载元数据。

## PC 命令行能力协商

Windows Agent 在每次 PC upsert/心跳元数据中发送实际探测结果：

```json
{
  "name": "Office PC",
  "agent_version": "0.1.29.35",
  "key_epoch": 1,
  "session_heartbeat_capable": true,
  "supported_shells": ["command-prompt", "windows-powershell"]
}
```

能力成立需要 `TermLink.SessionHost.exe` 与对应 Shell 可执行文件同时存在。服务端仅接受
`command-prompt`、`windows-powershell`，去重后按固定顺序写入 `pcs.supported_shells` 并从
`GET /v1/pcs` 原样返回。旧 Agent 不发送字段时保存空数组；Android 对字段缺失、空数组或未知值
均不猜测，不显示命令行选项。该字段是 Agent → Server → Client 的能力协商接口；新增 Shell 必须
同时扩展 Agent 探测/启动白名单、服务端输入与数据库约束、客户端映射和协议测试，不能只改 UI。

## 在所选目录打开终端

```mermaid
sequenceDiagram
    autonumber
    participant A as Android
    participant S as Server API
    participant W as Windows Agent
    participant H as SessionHost
    participant C as Session catalog

    A->>S: POST OPEN_TERMINAL path and shell_type
    W->>S: POST claim
    S-->>W: operation plus lease_token
    W->>W: Derive stable session UUID from operation UUID
    W->>H: Launch with managed session ID and base64 cwd
    H->>C: Register the exact managed session
    W->>W: Wait until locally visible, up to 17 seconds
    W->>S: complete with session_id shell_type cwd
    S->>S: Strictly validate all three fields
    S-->>A: SUCCEEDED result
    loop Up to 30 seconds
        A->>C: Refresh selected PC session catalog
    end
    C-->>A: Same session_id is online
    A->>A: Navigate to that session
```

成功结果必须且只能包含：

```json
{
  "session_id": "canonical non-zero UUID",
  "shell_type": "command-prompt",
  "cwd": "C:\\work"
}
```

服务端要求 `session_id` 为规范 UUID，`shell_type` 与请求完全相同，`cwd` 与请求路径字符串完全相同。Windows 实际启动前会把目录规范化，并把同一个 ID 传给 `TermLink.SessionHost.exe --managed-session-id`，把目录以 `--working-directory-base64` 传入。Agent 对 operation ID 派生稳定 session ID；任务因租约重领而重放时，如会话已注册则不会再开第二个 Shell。优先用 Windows Terminal 新标签页，找不到或启动失败时回退到可见 SessionHost 控制台。

## Docker 拓扑与精简取舍

```mermaid
flowchart TB
    subgraph M[最小生产拓扑：外部 OIDC 和 S3]
        ME[可选 Caddy edge profile] --> MS[TermLink Server]
        MS --> MP[(PostgreSQL)]
        MS --> MV[(Valkey)]
        MS --> MO[外部 OIDC]
        MS --> MX[外部 S3]
    end

    subgraph A[自包含 acceptance 拓扑]
        PUB[公网 Lucky 映射] --> AL[Lucky upstream]
        LAN[本地或 LAN 内部 CA] --> AE[Caddy TLS edge]
        AL --> AS[TermLink Server]
        AL --> AK[Keycloak]
        AL --> AM[MinIO object route]
        AE --> AS
        AE --> AK
        AE --> AM
        AS --> AP[(PostgreSQL)]
        AS --> AV[(Valkey)]
        AS --> AK
        AS --> AM
    end
```

| 方案 | 常驻组件 | 适用场景与取舍 |
| --- | --- | --- |
| `core.compose.yml` + `external-services.compose.yml` | Server、PostgreSQL、Valkey；`--profile edge` 可选 Caddy | 推荐的精简生产方式。OIDC 与 S3 由外部服务提供，不启动 Keycloak/MinIO；已有反向代理时也可不启 Caddy。登录、任务持久化、协调和对象存储能力没有被关闭。 |
| `core.compose.yml` + `history.compose.yml` | 上述组件 + MinIO；可选 Caddy | 自托管对象存储。文件下载依赖 history/S3 开关，但文件传输仍使用独立 5 GiB object path 和限制。OIDC 仍是外部服务。 |
| `acceptance.compose.yml` | Server、PostgreSQL、Valkey、MinIO、Keycloak、edge、lucky-upstream | 可重复的本地/公网映射联调栈，资源更重。Compose 声明 11 个服务，其中 `runtime-secrets-init`、`keycloak-init`、`server-secrets-init` 和 `migrate` 是 4 个一次性任务，成功后退出；稳态为表中 7 个服务。两个 Caddy 在此栈中职责不同：TLS/内部验收入口与 Lucky 的明文上游边界。 |

PostgreSQL 保存任务和 outbox，Valkey 支撑当前认证/实时协调，OIDC 提供账号登录，S3/MinIO 保存待下载对象，因此不能在保留当前 E2E 能力的同时直接删除这些逻辑依赖。精简的正确方向是外置 OIDC/S3、让一次性容器退出、复用同一个 Server 镜像以及按需启用 edge，而不是把功能依赖删掉。

自托管入口必须允许严格匹配的对象路径：

```text
/termlink-history/accounts/{account UUID}/file-transfers/{pc UUID}/{operation UUID}.bin
```

只需代理带完整 S3 SigV4 查询参数的 `GET` 和 `PUT`，并为文件传输单独配置 `5,368,709,120` 字节请求体上限；不要误用终端历史对象的 64 MiB 上限。外部 S3 模式不应把对象流量再代理回 TermLink Caddy。

## 部署与 E2E 验收清单

1. 把数据库迁移到 Migration 19：Migration 17 创建 `pc_file_operations` 和 claim 索引，Migration 18 把 `size_bytes` 收紧到 5 GiB 并固化 OPEN_TERMINAL 约束，Migration 19 增加白名单约束的 `pcs.supported_shells`。
2. 确认 `TERMLINK_HISTORY_ENABLED=true`，S3 internal/public endpoint、bucket、region 和凭据可用；公网客户端拿到的预签名 URL 必须从 Android 和 Windows 都可达。
3. 若使用仓库 Caddy，确认 file-transfer matcher、`GET PUT`、5 GiB 限制和对象存储 upstream 生效。若使用外部 ingress，等价复制这些约束。
4. 访问 `/readyz` 应为 `200`；访问 `/v1/client-config`，OIDC issuer 和客户端看到的 public origin 必须是实际公网域名，而不是 Docker hostname 或 LAN 临时地址。
5. Android 与 Windows Agent 登录同一账号；所选 PC 必须在线且 remote routing/remote access 已启用。
6. 浏览根驱动器和含超过 500 项的目录，确认分页无遗漏、无重复；覆盖中文、空格和 Unicode 文件名。
7. 下载普通文件、零字节文件和目录 ZIP，逐一核对文件名、长度、SHA-256、ZIP 根目录、空目录和内容。
8. 在 ZIP 准备阶段及实际下载阶段各取消一次，确认 Android 变为 `CANCELLED`、不完整 SAF 文档被清理，服务端任务最终为 `CANCELLED` 或按竞态已经终态。
9. 对失败和取消记录执行重试；删除完成记录后确认 Android 文件仍存在。
10. 先确认 PC 列表只回显 Agent 实际探测到的 `supported_shells`；再分别打开已上报的 CMD 和 Windows PowerShell，确认返回 `session_id` 唯一、`cwd` 正确、Android 会话目录出现同一 ID，并能进入实时终端。对缺失 Shell 或旧 Agent，确认 Android 不显示对应命令。
11. 检查终态 download 对象已创建延迟删除 outbox；用自动化时钟测试验证 55 分钟 descriptor 宽限、10 分钟 GET 和 65 分钟删除关系，不建议人工等待。

仓库中的协议级真实链路脚本见 [File operations end-to-end validation](FILE_OPERATIONS_E2E.md)。它验证生产 REST、对象传输和 Windows Agent 路径，但不代替 Android UI/SAF 验收。

## 验证状态与设备边界

### 已验证的自动化覆盖

- Server：Go 全量测试与 `go vet` 已通过；文件操作 API、角色边界、零字节、5 GiB 边界、SHA-256 元数据、严格 Shell 结果、幂等完成、租约轮换、取消/到期、延迟对象删除，以及真实 PostgreSQL Migration 17/18 与任务集成均有测试覆盖。
- Windows：Release `FileOperationsValidation` 和完整 `Build-Windows.ps1` 已通过；覆盖目录排序/分页/UTF-8 字节预算、ZIP 空目录、reparse point、临时文件清理、流式上传、取消、续租丢失、remote access 关闭、HTTP 超时继续领取、未分类异常独立恢复、中英双语诊断、Shell allowlist、cwd/session 参数和 SessionHost 注册超时。
- Android：`:app:testDebugUnitTest`、`assembleDebug` 和 `lintDebug` 已通过；覆盖文件协议解析、5 GiB 边界、零字节/截断/SHA 错误、浏览竞态和分页去重、SAF 清理、下载元数据恢复、进程重启转失败、重试前状态以及“删记录不删文件”。
- Docker/入口：生产配置检查覆盖独立 file-transfer matcher 和 5 GiB request-body 限制；Compose 配置可以分别生成自托管对象存储与外部 OIDC/S3 拓扑。

这些结果证明分层实现和服务端真实数据库契约，不自动等同于一次物理 Android 设备到真实 Windows 文件系统的完整用户操作已经通过。

### 仍需物理设备或发布环境确认

- Android 真机上的 OEM DocumentsProvider、外置存储/云盘 SAF provider、权限回收、低存储空间和后台进程限制。
- 真实移动网络切换、超大文件长时间下载、接近 5 GiB 上限的端到端耗时与厂商省电策略；5 GiB 是协议上限，不是已经用真机搬运 5 GiB 的性能结论。
- 真机触控与中英文布局、屏幕旋转、Activity 重建后的系统文件选择器体验，以及下载中心的人工可用性。
- 不同 Windows 版本、企业 ACL、锁定文件、网络盘和实际 reparse point 目录的人工行为；自动化只覆盖可控夹具。
- 公网 ingress、证书链、外部 S3 的单次 PUT 兼容性和对象生命周期任务需要在最终部署环境再做一次验收。

正式验收记录应分别注明 `protocol_e2e`、`android_ui_emulator` 和 `android_physical_device`，未执行的层级不得由其他层级的成功结果代替。
