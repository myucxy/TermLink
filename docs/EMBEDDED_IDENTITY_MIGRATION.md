# TermLink 内置身份服务

TermLink Server 使用 Ory Fosite 提供内嵌 OAuth 2.0 / OpenID Connect 服务。生产
单机拓扑只需要 `TermLink Server + PostgreSQL`，不需要独立身份服务容器。Web、
Android 和 Windows 继续使用原有 issuer、client ID、redirect URI、claims 与页面
路径，因此客户端登录协议和 Web 页面地址不变。

## 开源组件与安全边界

- OAuth 2.0 / OIDC 协议核心使用 Apache-2.0 许可的 Ory Fosite。
- 只注册固定的 TermLink 第一方客户端，不提供动态客户端注册。
- Authorization Code 强制 PKCE S256；刷新令牌旋转，并在重放后撤销 token family。
- 密码使用 Argon2id（64 MiB、3 次迭代、并行度 2）和每账号随机 salt。
- TOTP secret、OIDC 签名私钥使用由 Server 主密钥派生的独立 AES-GCM 密钥加密；
  恢复码和密码重置 token 只保存摘要。
- RS256 签名密钥提前七天轮换，旧公钥保留 30 天验证窗口。
- 密码、邮箱、MFA 或恢复码变化会提升 `auth_epoch`，使旧访问令牌、授权码和
  刷新令牌失效。
- Web TOTP 继续用于登录和管理操作的 MFA；Android 与 Windows 保持当前授权流程。

## 兼容契约

| 项目 | 值 |
| --- | --- |
| Issuer | `https://<host>/realms/termlink` |
| Android client | `termlink-android`，`dev.termlink:/oauth2redirect` |
| Windows client | `termlink-windows`，RFC 8252 loopback 随机端口 |
| Web client | `termlink-web` |
| Web step-up client | `termlink-web-step-up` |
| API audience | `termlink-server` |
| Web paths | `/login`、`/register`、`/account`、`/admin` |

## 全新部署

首次启动必须通过 `TERMLINK_INITIAL_ADMIN_PASSWORD_FILE` 提供至少 16 字节的高熵
口令。Server 在事务锁保护下，仅当 `identity_accounts` 为空时创建 `admin`；已有
任何身份账号时都不会创建或覆盖凭据。初始账号包含 `SUPER_ADMIN`、`ADMIN`、
`USER` 和部署引导标记。

生产验收部署命令：

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 `
  -Reset -SingleNode -EmbeddedIdentity `
  -LocalServerImage 'myucxy/termlink:<version>-local-production'
```

`-Reset` 会删除且仅删除 `termlink-production-acceptance` Compose 项目的容器、网络
和命名卷，并重新生成受管 secret。旧账号、设备、会话和设置不会保留。

部署验证成功后，初始管理员口令只能读取一次：

```powershell
.\server\ops\Get-InitialAdminPassword.ps1
```

读取工具会先轮换主机引导文件并清除服务私有卷中的原始明文，再输出口令。需要
显式重置时运行 `Reset-InitialAdminPassword.ps1`；它调用当前运行 Server 的
`identity-reset-initial-admin` 命令，不依赖外部身份管理 API。

## 验收项目

至少验证 `/healthz`、`/readyz`、OIDC Discovery、JWKS、Web 登录、Windows
loopback 回调、Android 内嵌登录、注册与邀请码、密码重置、MFA、设备列表、目录
读取、终端会话和文件传输。Compose 展开结果应只有一次性
`runtime-secrets-init`、`postgres` 与 `server`；初始化任务移除后只保留后两项。

## 旧数据导入工具

仓库暂时保留 `identity-import-keycloak` 作为离线兼容工具，但它不参与运行时，也
不会在内嵌 Compose 中创建服务、卷、secret、健康检查或网络依赖。导入账号不含
旧密码与 MFA，必须先通过密码重置完成身份接管。全新部署不需要执行该工具。
