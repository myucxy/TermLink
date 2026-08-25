# Windows Agent 远程自动升级

本文定义 TermLink Windows Agent 远程自动升级 v1 的权限、协议、事务和恢复边界。手动从 Windows
Tray 检查更新仍走既有交互流程；远程自动升级只能由已信任的 Android 设备发起。

## 1. 产品与权限边界

- Android 在设备行长按菜单中检查、创建、查看和取消自动升级。
- Server 对检查、创建、查询活动任务、查询任务和取消接口强制要求 `ANDROID` 设备身份。
- Windows Agent 只能领取和执行其自身 PC 的任务，不能创建远程升级任务。
- Windows Tray 的手动安装与 Android 自动安装共用同一个签名 Setup。普通安装使用现有交互或
  `--install-dir` 参数；自动安装额外使用内部参数 `--remote-update-transaction`，由外部 Updater 调用。
- 自动升级会终止该 PC 上的受管终端会话。Android 必须在创建任务前显示明确警告和活动会话数量。

## 2. Bootstrap 与版本语义

只有已安装版本同时满足以下条件时才允许创建任务：

- PC 在线、远程路由开启且 Agent 上报 `remote_agent_update_v1`；
- PC 元数据包含 `remote_agent_update_capable=true`；
- `agent_version` 可解析为受支持的 Windows assembly 版本；
- AppShip 返回更大的稳定通道 `buildNumber` 和完整的固定产物元数据。

显示版本与升级身份必须分开：

| 用途 | 示例 | 规则 |
| --- | --- | --- |
| AppShip `versionName` | `0.1.31` | 面向用户的显示信息 |
| AppShip `buildNumber` | `37` | 升级、成功和回滚的唯一比较身份 |
| Windows assembly | `0.1.31.37` | 第四段必须等于 AppShip build |

旧三段 assembly 只为兼容已有安装，以第三段作为 build。新发布必须使用四段版本。不得仅按
`versionName` 判断新旧，也不得用相同 build 覆盖不同二进制。

## 3. 服务端协议

Android 使用：

```text
GET    /v1/pcs/{pcId}/agent-update-eligibility
POST   /v1/pcs/{pcId}/agent-updates
GET    /v1/pcs/{pcId}/agent-updates/active
GET    /v1/pcs/{pcId}/agent-updates/{updateId}
DELETE /v1/pcs/{pcId}/agent-updates/{updateId}
```

Windows Agent 使用：

```text
POST /v1/pcs/{pcId}/agent-updates/claim
POST /v1/pcs/{pcId}/agent-updates/{updateId}/renew
POST /v1/pcs/{pcId}/agent-updates/{updateId}/progress
POST /v1/pcs/{pcId}/agent-updates/{updateId}/complete
```

每个 PC 同时最多有一个 `PENDING` 或 `PROCESSING` 任务。领取返回 32 字节随机 lease token；Server
只保存 SHA-256。领取、续租、进度和完成操作均受设备签名、任务归属、有效期与 lease 约束。进度幂等键包含
阶段和百分比，续租使用唯一幂等键，完成键由最终状态和 observed build 确定。

`active` 只返回原发起 Android 设备的未完成任务，不返回下载 URL、哈希或签名人信息。Android 进程重启后
先调用该接口恢复轮询，避免重复创建任务。

## 4. 状态机与取消边界

```text
REQUESTED -> PRECHECKING -> DOWNLOADING -> VERIFIED
          -> DRAINING_SESSIONS -> INSTALLING -> STARTING_NEW_AGENT
          -> VERIFYING_CONNECTION -> SUCCEEDED
                                      |
                                      v
                                ROLLING_BACK
                                  |       |
                                  v       v
                            ROLLED_BACK  ROLLBACK_FAILED
```

`REQUESTED`、`PRECHECKING`、`DOWNLOADING` 和 `VERIFIED` 可以由 Android 取消。从
`DRAINING_SESSIONS` 开始不可取消，因为会话终止和安装事务已经跨过安全回退边界。安装前错误进入
`FAILED`；安装开始后的错误必须进入回滚路径，不能伪装为普通失败。

## 5. 产物信任与路径边界

Server 只接受 AppShip 返回的 HTTPS、Windows x64、ready EXE，并固定大小、SHA-256 和可选签名证书指纹。
Agent 下载时禁止自动跨协议重定向，只允许最多三次无凭据 HTTPS 重定向，并在落盘前流式限制大小和校验哈希。

执行前后均校验 Setup Authenticode。期望发布者绑定到当前安装根中已签名 Tray 的证书；AppShip 提供指纹时
还必须与之相同。事务目录固定在 `%LOCALAPPDATA%\TermLink\remote-updates\transaction-{updateId}`，Setup、
Updater、journal 和 receipt 都必须位于该目录。备份只能是授权安装根同级的 `.termlink-backup-*` 目录。

## 6. 安装、健康确认与提交

1. Agent 下载和验证 Setup，关闭所有受管终端会话，写入 `Prepared` journal。
2. 事务目录中的外部 Updater 注册当前用户启动恢复项，停止旧 Tray，并调用同一 Setup 的自动事务参数。
3. Setup 验证 journal、安装根和自身 assembly build，原子替换安装目录并保留旧版本备份。
4. 新 Tray 启动后，Agent 先完成设备注册和 PC upsert，再建立 Relay 连接。
5. 新 Agent 只有在 Relay 已连接后才提交 `SUCCEEDED`。Server 还会在同一数据库事务中确认：
   observed version/build 一致、build 等于目标 build，且 `pcs.agent_version` 已经是该 observed version。
6. Server 接受成功后，Agent 写入本地 receipt；外部 Updater 看到 receipt 后才删除旧版本备份并清除启动恢复项。

因此仅“进程启动”或“WebSocket 建立”都不足以提交升级成功。

## 7. 中断恢复与回滚

- Android 进程重启：通过 `active` 恢复任务和轮询。
- Agent/Tray 重启：扫描最新的有效本地 journal。已领取的新 Server 任务即使与遗留 journal 不同，也会直接
  交给正常处理，遗留日志不会吞掉新 lease。
- Updater 在写启动恢复项前中断：仍在旧 build 的 Agent 会重新拉起一次事务目录中的监督器。
- Setup 中断：监督器按已安装 build 和唯一备份恢复到 `AwaitingNewAgent`，否则进入回滚。
- 新 Agent 未在期限内确认 Server 与 Relay：监督器停止目标进程、隔离失败版本、恢复旧目录并启动旧 Tray。
- 旧 Agent 重新连接后提交 `ROLLED_BACK`，Server 同样要求 `pcs.agent_version` 已恢复为 observed version。
- 自动回滚本身失败时 journal 记录 `ROLLBACK_FAILED`。只要任一受信 Agent 仍能运行，它会把详细稳定错误码和
  安全截断后的诊断回报 Server；已回报的失败 journal 不再阻塞后续任务。

如果安装根和备份同时不可读取，自动流程无法凭空恢复可执行文件。这一最终故障必须保留事务目录和错误信息，
在 Windows 本机使用已验证的同发布者 Setup 修复，不能删除账户或 Android 设备身份来规避。

## 8. 发布要求

- 远程升级 bootstrap 版本必须先用常规本机安装方式部署；更老版本不会显示为可远程升级。
- AppShip Windows artifact 的 `buildNumber`、Setup/Agent/Tray assembly revision 和发布 manifest build 必须一致。
- Setup SHA-256、大小、Authenticode 状态、发布者连续性指纹和 RFC 3161 时间戳必须通过本地发布门禁。
- migration 22 必须先于 Android 入口启用；Server、Android 和 Windows 应来自同一已复核源码提交。
- 发布前执行 Server 全量测试、Windows Release solution 和验证项目、Android unit/lint/assemble，并遵循
  [发布与部署运行手册](RELEASE_RUNBOOK.md)。
