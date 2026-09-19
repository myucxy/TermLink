# 05 · 当前未提交变更全景（WIP）

> 快照时间：2026-09-17。来源：`git status --short`。提交主题依据 `git log`。

## 规模与分布

共 **92 处**未提交变更，几乎全部集中在 `server/`：

- **55 个修改（M）**：约 40 个在 `server/internal`（auth / identity / realtime / store / webportal / api / config 等），其余在 `server/cmd`、`server/deploy`、`server/ops`、README 类文档。
- **27 个删除（D）**：23 个在 `server/internal`（配合重构），含根 `server/Caddyfile`（对应"去掉 Caddy 容器"的架构收敛）及一批被裁剪的旧迁移/历史测试（transcript_history、object_storage、refund_approval 等）。
- **10 个新增（??）**：
  - `server/compose.yml`（单机 compose 新入口）
  - `server/internal/presence/`（新模块：`registry.go` + `registry_test.go`，内存在线状态）
  - `server/internal/store/runtime_secrets.go`（运行时密钥入库）
  - 迁移 `000046_device_disable`、`000047_server_runtime_secrets`、`000048_default_registration_closed`
  - 构建产物 `termlink-server.exe`

另有 `android/`、`docs/`、`.gitignore` 各 1 处小改；根目录大量 `.tmp-*` 截图/XML/日志为此前模拟器 UI 测试残留（应保持 gitignore，勿入库）。

## 主题归纳：一轮"运行时简化 + 安全加固"

1. **身份内置收尾**：Fosite OIDC 深度整合，移除 Keycloak 导入路径（`internal/identity/keycloak_import.go` 删除）。
2. **运行时密钥入库**：`runtime_secrets.go` + 迁移 000047，密钥不再依赖环境变量/文件。
3. **默认关闭注册**：迁移 000048 将新部署 `registration_policies.mode` 置 `CLOSED`（管理员可在管理页改；不自动恢复旧策略）。
4. **设备禁用能力**：迁移 000046 给 `devices` 加 `disabled_at/disabled_reason`。
5. **内存 Presence**：`internal/presence` 新模块 + `ListSessionsUsingPresence`，Relay 在线状态改走内存注册（⚠ 引出 [03](03-history-sessions-analysis.md) 风险 2/3）。
6. **部署收敛**：删除 Caddyfile、单节点 compose 收敛（`compose/single-node.compose.yml` 更新、`deploy/single-node-production/` 调整）。

## 与最近提交的连续性

```text
7dec0e4 feat(server): default PC agents to collaborative control   ← HEAD
b22455b fix(android): keep terminal selection handles usable
35de4a1 feat: finalize direct transport and client release changes
2573fe0 fix(android): recover from invalid persisted auth state
b62ac72 feat(android): refine sign-in server and carrier UI
```

更早（09 上旬）：`c4b7d65` 内置身份 provider、`689c941`/`4249f2e` 单节点部署简化、`86fe4a5` 历史迁移出对象存储。**当前 WIP 是这条"身份内置 + 单节点简化"主线的延续，尚未落成提交。**

## 风险交叉提示

- Presence WIP 与"历史会话"风险的关联见 [03](03-history-sessions-analysis.md) 风险 2、3——提交前建议先补会话级可达性语义与门户一致性。
- 000048（默认关注册）是 [04](04-session-log-2026-09-16.md) 中未完成的"模拟器登录失败"诊断的候选原因之一（若本地库为新初始化）。
- `server/README-runtime.md`、`server/README.md`、`docs/EMBEDDED_IDENTITY_MIGRATION.md` 已同步修改，提交时应一并核对文档与实现一致。

## NAS 部署（192.168.9.11，2026-09-17 修复）

绿联 DXP4800PLUS 上 `/volume1/docker/termlink/docker-compose.yaml` 的崩溃循环（`exited with code 1 (restarting)`，359 次）根因与修复：

- **根因**：手工改编 compose 时把 `TERMLINK_PUBLIC_URL`/`TERMLINK_OIDC_ISSUER` 两行的插值默认值改成了 `https://termlink.kget.cc:8910`，但 `TERMLINK_WEB_OIDC_REDIRECT_URI` 的默认值漏改（仍是 `https://localhost`）。NAS 上没有 `.env`，三行各自落到不一致的默认值；server 的 `config.Load` 校验 web redirect host 必须等于 public URL host（`config.go:1006-1010`）→ 配置加载失败 → exit 1。因为失败发生在结构化日志配置之前，日志只有一行无细节的文本错误。
- **修复（compose 层，已生效）**：三个 URL 直接硬编码为一致的 kget 值（不再用 `${}` 插值）；host 端口用 8914（8910 被 cloud-album-app 占用）。`docker compose up -d --wait` 后 healthy、`restarts=0`，`/v1/client-config` 的 issuer 与 web redirect 一致。原文件备份为 `docker-compose.yaml.bak-20260917`。
- **代码改进（已实施，未提交）**：`main.go` 的 `runProduction` 在 `config.Load` 失败时输出 `production config rejected, reason=...`（校验消息只含变量名、无密钥值，可安全记录），避免同类故障再变成无日志黑洞。
- **拓扑注意**：kget 网关（192.168.9.250:8910，TLS 终结）当前 upstream 指向本机 192.168.9.28:8080，**不指向 NAS**。两台机器现在跑着相同 issuer 的 server；若要以 NAS 为生产，需要改网关 upstream 到 `192.168.9.11:8914`。NAS 自身 8914 是纯 HTTP，仅供局域网管理访问。
- **教训**：compose 里派生值（issuer、redirect）绝不要各写各的插值默认值——要么全走一个 `TERMLINK_PUBLIC_URL` 且落在 `.env`/shell，要么全部硬编码一致；混合写法必然漂移。

### compose environment 精简（2026-09-17，已实施并部署）

server 早已内置全部自动适配（`config.go:517` issuer 从 `TERMLINK_PUBLIC_URL` 派生；runtime mode 默认 single-node、身份默认 embedded、各端 client ID/redirect/allowed parties 均有默认；`TestLoadFromEnvDefaultsToSingleNodeEmbeddedIdentity` 锁定该行为），compose 里的显式配置反而是漂移源头（NAS 事故即显式矛盾值覆盖了正确派生）。已把 `server/compose.yml` 的 environment 从 26 行精简到 8 行，只保留部署特有值：`PUBLIC_URL`、`DATABASE_URL`、三个功能开关（registration-recovery / account-deletion / direct-transport）、STUN、AppShip 两项。NAS 同步精简（`PUBLIC_URL` 硬编码 kget、端口 8914）。

- 新契约测试 `server/internal/config/compose_auto_adapt_contract_test.go`：断言 compose 必须包含 PUBLIC_URL/DATABASE_URL/三个开关，且**不得**再出现任何 server 可自动适配的变量（防回归）。
- 新镜像 `v0.2.45`（digest `sha256:83d4e28e…`，含 main.go 配置错误诊断日志改进）已推送 Hub（`latest` 同步；tag 命名遵循 CI 校验格式 `^v\d+\.\d+\.\d+$`，不加日期/后缀）；NAS 直接从 Hub 拉取成功。
- 两地部署均验证：容器 healthy、restarts=0、`/v1/client-config` 的 issuer/web redirect 全部由 server 正确派生。本机部署容器环境变量从 26 个降到 8 个。
