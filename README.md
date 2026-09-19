# TermLink

TermLink 是一个由 Windows Agent、Android 客户端和 TermLink Server 组成的远程终端系统。
Android 与 Windows Agent 使用同一个账号登录后，可以查看和控制该账号下的 Windows
终端会话。

## 生产运行时

生产服务端只保留两个容器：

```text
外部 HTTPS 反向代理 → TermLink Server → PostgreSQL
```

TermLink Server 内置身份服务、账号管理、设备发现、会话控制、Relay、Direct/NAT 信令、
文件任务、Agent 升级任务和 Web 管理页面。

生产服务端不执行 Windows 命令、不创建 ConPTY、不保存完整终端历史、不保存文件对象。
这些能力分别由 Windows Agent 或客户端负责。

生产部署入口：

- [Server 部署说明](server/README.md)
- [单节点运行时](server/README-runtime.md)
- [单节点安装、升级、备份和恢复](server/deploy/single-node-production/README.md)

## 客户端

| 客户端 | 目录 | 作用 |
|---|---|---|
| Android | [`android`](android) | 查看会话、输入命令、管理设备和传输文件 |
| Windows Agent | [`win-agent`](win-agent) | 管理 Windows 会话、启动 Shell、保存历史和执行升级 |
| Terminal Core | [`terminal-core`](terminal-core) | 终端解析、状态重建和渲染核心 |
| 协议 | [`protocol`](protocol) | 实时协议、加密信封和测试向量 |

## 主要能力

- 内置 OIDC 登录、注册、邀请码、邮箱验证和可选人机校验；
- Android 与 Windows Agent 的设备和 PC 管理；
- Relay 实时连接、ACK、断线恢复、控制租约和多端协同；
- Direct/NAT 信令，失败时自动回到 Relay；
- 客户端之间的文件传输和断点续传；
- Windows Agent 远程升级；
- Windows 主机拥有的终端历史和分页恢复；
- Web 个人中心和管理后台。

## 服务端边界

| 能力 | 负责方 |
|---|---|
| 账号、Token、设备和会话元数据 | TermLink Server + PostgreSQL |
| 实时消息路由 | TermLink Server Relay |
| 命令执行和 ConPTY | Windows Agent |
| 终端历史 | Windows Agent 本地存储 |
| 文件内容 | Android 与 Windows Agent |
| 文件任务和权限 | TermLink Server + PostgreSQL |
| HTTPS 和证书 | 外部反向代理 |

## 开发检查

```powershell
cd server
D:\Dev\Env\Go\bin\go.exe test ./...
D:\Dev\Env\Go\bin\go.exe vet ./...
```

Android、Windows、协议和 Terminal Core 的构建说明分别位于对应目录的 README。
LAN Replay、Phase 1、验收 Compose 和外部监控栈属于测试或运维扩展，不属于生产运行时。

## 其他文档

- [完整系统设计](docs/COMPLETE_SYSTEM_DESIGN.md)
- [终端核心设计](docs/TERMLINK_TERMINAL_CORE_V2.md)
- [实时协议](protocol/protobuf/PRODUCTION_REALTIME_V1.md)
- [文件操作 E2E](docs/REMOTE_FILE_OPERATIONS_E2E.md)
- [Windows Agent](win-agent/README.md)
- [Android 客户端](android/README.md)

商业套餐、支付、公告、MinIO 对象存储和服务端终端历史存储已从当前产品运行时移除。
