# 边缘网关（TLS Entry）运行档案

> 状态：2026-09-18 由全项目审计（KB06 #14）补建。本页是**生产 TLS 入口的唯一正式记录**，
> 之前的拓扑知识只存在于排障记录（KB04/05）。信息缺口以「待补录」标注——运维该网关时请随手补全。

## 网关在链路中的位置

```text
Android / PC 客户端
        │  https
        ▼
termlink.kget.cc:8910  ──►  本档案描述的 TLS 网关（192.168.9.250）
                                │  TLS 终结（Let's Encrypt *.kget.cc 通配符）
                                │  HTTP 反向代理
                                ▼
                        TERMLINK server 容器（upstream）
                                ▼
                        PostgreSQL（容器内网）
```

- **域名与 DNS**：`termlink.kget.cc` 在局域网 DNS 解析到 **192.168.9.250**（网关机）。
  公网解析同样指向可达地址，客户端（Android 内置默认地址 `https://termlink.kget.cc:8910`，
  见 `android/app/build.gradle.kts` 的 `publicTermLinkServerUrl`）因此无需任何配置。
- **软件**：Lucky（192.168.9.250 上运行；版本/配置界面入口待补录）。
- **证书**：Let's Encrypt 通配符 `*.kget.cc`，2026-09-17 实测有效。续期方式待补录
  （推测为 Lucky 内置 ACME DNS/HTTP 验证；续期失败 = 全端 8910 拒连）。
- **upstream 现状（2026-09-17）**：指向 **HOME-PC（192.168.9.28:8080，Docker Desktop）**。

## 同网关承载的服务（注意联动影响）

| 域名:端口 | 用途 | 后端 |
|---|---|---|
| `termlink.kget.cc:8910` | TermLink 实时终端 + OIDC + 文件传输 | HOME-PC:8080（Docker `termlink-local-server-1`） |
| `appship.kget.cc:8910` | **Windows Agent 自动升级分发**（AppShip） | NAS（192.168.9.11，`app-ship-appship-1`，端口 18080） |

网关宕机影响的不仅是终端连接，**Windows Agent 的升级检查也会失败**（Agent 的
分发地址内置为 `https://appship.kget.cc:8910`）。这两件事没有独立兜底。

## 强制代理要求（server 侧契约，见 server/README-runtime.md）

1. 保留 `Host` 头；
2. 设置 `X-Forwarded-Proto: https`；
3. 允许 WebSocket 升级（`/v1/realtime`、`/v1/file-transfers`、`/v1/transport/signals` 全是 WS）；
4. 不得缓冲/截断长连接。

## 故障影响与恢复

| 故障 | 症状 | 恢复 |
|---|---|---|
| 网关进程宕机 | 所有客户端失联（登录/终端/文件全挂） | 登录 192.168.9.250 重启 Lucky |
| upstream 指错/后端宕机 | 客户端 TLS 正常但请求 5xx/超时 | 核对 upstream 指向（当前应指 192.168.9.28:8080） |
| 证书过期 | 全端 TLS 拒连 | 手动/自动续期 `*.kget.cc`（ACME） |
| DNS 记录丢失 | 域名解析失败 | 恢复 LAN DNS 的 `termlink.kget.cc → 192.168.9.250` |

**注意（2026-09-17 起）**：NAS（192.168.9.11:8914）上存在第二个 TermLink server 实例，
**数据库与本机实例独立、不得切换 upstream 指向它**——切换会造成「账号/设备/会话全部消失」
的假象（数据在另一台机的 PostgreSQL 里）。NAS 实例仅作试验用途。

## 黑盒探针建议

对 `https://termlink.kget.cc:8910/readyz` 做周期拨测（1 分钟级），失败即告警。
当前无任何自动拨测（审计发现 #26）——接入任一现有监控前，至少保留人工巡检。

## 待补录清单

- [ ] Lucky 的具体版本、配置文件路径与备份方式
- [ ] 证书续期机制（ACME 提供商、验证方式、自动续期计划）
- [ ] 网关的超时/缓冲/WS 升级配置快照
- [ ] 网关机的登录方式与责任人
- [ ] `appship.kget.cc` upstream 的精确指向
