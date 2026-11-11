# iOS 客户端接入

服务端已具备独立 iOS 公共 OIDC 客户端、设备身份及移动端 Relay／文件权限路径。本轮补齐部署配置、注册后继续授权及 schema 版本接线。默认仍需显式开启 iOS，保持已有 Android、Windows 和 Web 身份边界。

本说明是代码与部署配置说明，不表示在线服务已升级。使用包含这些改动的新构建；旧镜像不会因为添加环境变量就获得新能力。

## 必需配置

```dotenv
TERMLINK_IOS_OIDC_CLIENT_ID=termlink-ios
TERMLINK_IOS_OIDC_REDIRECT_URI=dev.termlink.ios:/oauth2redirect
```

`TERMLINK_IOS_OIDC_CLIENT_ID` 为空时不创建 iOS 客户端；非空时必须与其他客户端不同。回调严格固定，不允许 Android 回调、域名、query 或 fragment 替代。iOS 使用授权码 + S256 PKCE 和刷新令牌，不开放密码 grant。

若不显式设置 `TERMLINK_OIDC_ALLOWED_PARTIES`，Server 自动加入 Android、Windows 和已启用的 iOS ID。手工配置该列表时，必须包含每个已启用的原生客户端：

```dotenv
TERMLINK_OIDC_ALLOWED_PARTIES=termlink-android,termlink-windows,termlink-ios
```

不要把 iOS ID 加入 `TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES`。iOS token 只允许注册 `IOS` 设备，不能注册 `ANDROID`、`WINDOWS_PC` 或 `PC_AGENT` 身份。

## 不同部署入口

| 入口 | iOS 配置方式 |
| --- | --- |
| `server/compose.yml` | 在该 Compose 命令环境或其 `.env` 中设置 iOS ID；模板显式透传两项 iOS 字段，allowed parties 由 Server 推导。 |
| `server/compose/core.compose.yml` 及 single-node overlay | 同时传入 iOS ID；这里的默认 allowed parties 是原 Android/Windows 列表，开启 iOS 时须显式追加其 ID。 |
| 单文件生产安装器 | 使用包含本轮改动的新安装器升级；iOS ID、固定回调和可选 allowed parties 存入受管 `config.env`。后续升级未传覆盖值时保留。 |

Compose 的 `.env` 不会把任意变量自动注入容器，因此模板必须有明确映射。本轮三个 Compose 入口和单文件安装器渲染的 Compose 均已补齐。环境示例见 [`.env.example`](.env.example)。

受管旧部署必须先使用**新版安装器和新版不可变镜像**执行升级，才能重渲染 iOS 字段。仅运行旧安装器的 `config` 不生效；新版脚本也会在保存前拒绝缺少 iOS 映射的旧 runtime，并提示先升级。后续操作按[生产部署说明](deploy/single-node-production/README.md#optional-ios-identity)及[发布运行手册](../docs/RELEASE_RUNBOOK.md)执行。本轮没有运行升级命令、改写数据库或启用在线配置。

## 数据库与身份提供者

- 需要 migration `000051_ios_mobile_devices.up.sql`，允许真实 `IOS` 设备。当前运行时 `LatestVersion` 为 **55**（包含 iOS 迁移51及后续 Mac 兼容迁移）；否则迁移后 schema 校验会拒绝启动。
- `single-node` 模式在启动时执行已有迁移流程；分布式部署按原有显式 migrate 流程执行，不手工改历史迁移或 schema 版本。
- 回滚迁移会在仍有 `IOS` 身份时拒绝执行，避免将真实 iOS 设备改名或删除后伪装回滚成功。
- 内置身份服务在配置 iOS ID 后创建公共客户端，授权页 CSP 支持固定的 iOS 回调。
- 外部 OIDC 提供者必须另外注册同名公共客户端、精确回调、S256 PKCE、授权码／refresh grants 和 Server audience。不要启用 password grant。仓库附带的 Keycloak 主题已为默认 `termlink-ios` 提供注册入口；自定义 client ID 时还需相应调整外部主题配置。

## 接口与 wire 兼容

| 功能 | 行为 |
| --- | --- |
| `GET /v1/client-config` | 返回 `ios_oidc_client_id`、`ios_redirect_uri`；旧字段保持原客户端含义。 |
| `POST /v1/devices/register` | iOS 使用 `type: "IOS"` 和自身客户端 token，继续验证设备签名、nonce、时间窗与账号边界。 |
| 注册页继续授权 | 仅允许已配置的 Web／Android／iOS／Windows client ID，保留原 `state`、PKCE challenge 和 callback；域名、路径和重复 client_id 检查保留。 |
| `GET /v1/realtime` | 子协议 `termlink.realtime.v1`；iOS 使用既有移动 wire role `android`，数据库身份仍是 `IOS`。 |
| `GET /v1/file-transfers` | 子协议 **`termlink.file-transfer.v2`**；复用移动 role 与 E2EE／resume 路径。客户端之前漏写连字符的名称已修正。 |
| 文件操作／升级状态／控制租约 | 继续使用受信移动身份权限，保留账户、设备、电脑与操作归属校验。iOS 不能以 `pc` role 连接。 |
| V1 设备列表和加密接收者列表 | 将 iOS 投影为既有 `ANDROID` wire enum，兼容已发布主机；真实设备类型和授权主体不改写。 |

保持 Relay Only。服务端只转发有界密文与元数据，iOS 支持不等于新增直连或放开终端内容访问。

## 本轮验证与限制

- 使用 Go **1.26.2** 在 Mac 本地副本执行全服务端测试：**1102 项通过，0 失败，57 项跳过**（含子测试）。跳过项需要 PostgreSQL／Valkey 等外部集成环境或专用构建条件；不能视为已通过。
- 身份、Relay、文件及 store 既有测试通过 race 检查；本轮修改涉及的 API／config／webportal／migrate 包另外通过 race 检查，`go vet ./...` 通过。
- 已编译 Darwin arm64 和 Linux amd64／arm64 服务端二进制；未构建或发布 Docker 镜像。
- 新增测试使用真正设备签名中间件与 HTTP/WebSocket handler，验证 iOS public config、文件操作、升级状态、移动 Relay／文件握手及拒绝冒充 PC。其身份与 route fixture 在内存中，不替代真实数据库、OIDC 或加密内容端到端验收。
- Compose 配置、旧 runtime 拒绝和渲染测试在临时目录执行，没有启动容器。PowerShell 部署验证脚本已更新，但本 Mac 未执行 Windows PowerShell 门禁。
- 尚未运行真实 iOS 账号登录、migration 51 的数据库集成、电脑 attach／租约／文件续传联测或 APNs。后台推送 token 注册和 APNs 发送仍属于后续开发，不因前台 Relay 支持而自动具备。

可复现的源码检查：

```sh
cd server
go test ./...
go test -race ./internal/api ./internal/config ./internal/webportal ./internal/migrate
go vet ./...
```

应使用专用测试数据库运行集成测试，不把本机跳过结果或模拟器单测当作在线接入已验收。

## 2026-11-03 后台通知源码补充

迁移55和服务端APNs模块已实现，提供设备Token登记、受限AI状态事件、升级结果原子入队、投递重试和失效清理。默认关闭；此前“尚无APNs”的段落为历史验收记录。当前仍未完成iOS系统Token回调、电脑事件上报和真实iPhone送达验证。接口与配置见 [后台通知契约](PUSH_NOTIFICATIONS.md)。
