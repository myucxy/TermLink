# TermLink 0.3.4 / 133 三端发布记录

> 2026-10-14 定稿。取代本文件早期的 Android 单端计划（该计划产于 22 组能力
> 波次合入前，原生锁与发布范围均已被超越；历史草稿见 git 历史本文件前一版）。

## 授权与来源

- 用户授权：「基于当前版本发布一次版本三端都发布」「开始处理直至可以发版」「继续 直到可以发版」（codex 会话 01a13100 中断后，zcode 会话接续执行）
- 来源：termlink `d9fc97ba8d47dfd2a6e9eb30f66e8b47d56c9476` · terminal-core `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`
- 两仓库均已提交并推送 origin/main，发布时工作树干净
- 机器签名审核与授权记录：`artifacts/validation/release-133-review-state.json`

## 内容（22 组终端能力首批 + Android 界面改动）

- 冻结历史浏览、状态增量（State Delta）恢复、Kitty 图形 durable 恢复、Broker 捕获主路径
- Shell 命令定位入口、终端语义探针、输入诊断、原生显示协议、ConPTY 输入转码、Shell Integration
- 修复 Frozen 会话打开超时：open readiness 仅依赖 last marker（此前 r398-r421 全部 SURFACE_TIMEOUT）
- Realtime 诊断新增 transport failure reason；协商就绪等待 120 秒
- Android AI 会话页：Codex 黑白标志、AI 列表三级层级、ZCode 入口单行化、失效 CLI 会话不闪回、目录返回与鼠标菜单紧凑化

## 验证

| 项 | 结果 |
| --- | --- |
| Verify-LocalRelease (Production) | 通过：APK `c92d34a1…4b20b2`（43,602,771 B）、Setup `395a8407…ba5a8f6`（185,029,664 B）；签名连续性（Android f374794c… / Windows 91AE60BC…）与 RFC3161 时间戳有效 |
| 原生库锁 | core `1c907c5` 干净重建，`terminal-core.lock.json` source_dirty=false（Android SO 字节级一致，Windows DLL 更新） |
| 联测 | B/B+/D/M/Δ/F(Android 主路径) passed（r388-r405）；Frozen open/冻结浏览/并发实时输出通过（r425/r426，本会话 live-marker 修复后） |
| Android 单元测试 | 本波次前基线 386 项通过；能力波次模块测试随各分工报告通过（Network 396 项等） |
| LAN/NAT | 不适用，按用户指定只使用服务器转发 |

## 已知限制（发布说明不宣传）

- Frozen lifecycle：Host 失联切换瞬间的 inspect 超时（r425/r426 确定性复现，open 已修复）
- Shell 命令定位失败提示、Kitty shared 阶段、WAL retry 阶段
- Theme authority（双 recipient）与 Windows 实窗音频未验收；真机 H02-H04 未做
- 详见 `docs/development/REMAINING_ACCEPTANCE_RISKS_20261013.md`

## 发布状态

| 端 | 产物 | 状态 |
| --- | --- | --- |
| Android | `TermLink-Android-0.3.4-133.apk`（43,602,771 B） | ✅ AppShip stable `published`/`ready`：release `bd0780c9-23d8-47b3-9741-1cedd357d68d`，artifact `25079562-da7b-458e-a857-f778f74c429d`；公共更新端点实测 build 0→133 optional |
| Windows | `TermLink-Setup-0.3.4.133-x64.exe`（185,029,664 B） | ✅ AppShip stable `published`/`ready`：release `41432cf1-94a8-4ac6-954f-395669fae835`，artifact `e3a62946-beac-4e9b-b6f9-411dd4daa5bd`；公共更新端点实测 |
| Server | `docker.io/myucxy/termlink:0.3.4-build.133` | ✅ 已推送 Docker Hub：多架构 manifest list，index digest `sha256:5d9e1faa7af554af250186d57cf3bc3c8a678d0572a3672b8cf5d648674f5ac3`，amd64 `4b81c6b5…` / arm64 `6a9ef177…`；本地 OCI tar（220,129,280 字节）同目录留存。无 Cosign 签名（机器无 cosign 密钥，沿用 0.3.0 先例） |

### Server 镜像构建说明

gcr.io distroless 与 buildkit 内 proxy.golang.org 均不可达，采用主机预编译
（Go 1.26.2，与官方 Dockerfile 同版 ldflags 注入 0.3.4+133/修订/时间）+
本地 alpine:3.22 基底（amd64/arm64 双二进制 + `select-arch.sh` 运行时
选架构 + Git CA bundle；`Dockerfile.local-0334`）。冒烟：amd64 容器
正常启动并按预期拒绝缺省配置。镜像经宿主机侧 Python registry 客户端直推（走 7897 代理，daemon 内代理路径不通）。
网络稳定后建议走代理重跑官方 `server/Dockerfile` 回到 distroless 基线再补推签名版。

## 对应源码（GPLv3）

`artifacts/release/0.3.4-133/TermLink-Corresponding-Source-0.3.4-133.zip`
为早期旧包（源码已前进），发布前未附源码下载链接。待办：用
`scripts/New-CorrespondingSourceArchive.ps1` 以当前双仓库 HEAD 重生成，
经维护者确认后挂到可达 URL 并在下一版更新说明补链接（私有 core 公开
需维护者确认，不自动改仓库可见性）。

## 回滚

保留 0.3.3/132 双端原始发布与制品：APK
`17fba372eb74cd1d481f1011610178800e9110e1fec76185e51293c2dfd4fd6d`、
Setup `76a88bbed1a9c820a64ab36a10e953092ccb0c9589c6e381fd5585c2085595c7`。
如新版本发现问题，以更高 build 发布已知良好源码；手动回退先核验平台
降级限制及备份，不卸载或清空用户数据，不覆盖原发布哈希。未授权的
仓库公开不执行。
