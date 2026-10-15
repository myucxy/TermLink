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
为早期旧包，不能作为本轮已发布制品的对应源码，保留用于追溯；发布前未附
源码下载链接。下表的新候选包已按各已发布版本的精确应用/core revision
生成，不能用当前 HEAD 替换历史来源。后续交付复用已核验候选，并补可达
下载入口与实际下载哈希记录；不自动改 core 仓库可见性。

### 2026-10-15 本机候选源码包（prepared/local_verified；外部交付 pending）

以各已发布版本的精确干净 checkout 重新打包，仅本机留存于
`.workspace-validation/review-20261015/source-delivery/output/`，
尚未挂接任何可达 URL，未向外部交付：

| 包 | 应用 revision | core revision | 字节数 | SHA-256 |
| --- | --- | --- | --- | --- |
| `TermLink-Corresponding-Source-0.3.4-133.zip` | `d9fc97ba8d47dfd2a6e9eb30f66e8b47d56c9476` | `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` | 21,819,614 | `0ce09ced1bbbc0343450581cbf53717c4b765d77186387432d0e3a1dc79d7158` |
| `TermLink-Corresponding-Source-0.3.4-134.zip` | `9721f2ec6407815897a06d10740e7dd37901bcd1` | `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` | 21,824,099 | `46b16b4c08fe4c9ee0799f7e786b579867d43f7330d47c6a311227998fff30f7` |

- 133 包关联制品：APK `c92d34a1…4b20b2`、Setup `395a8407…ba5a8f6`；134 包关联
  Setup `99dd94df…7836fe6e0`（与上表发布记录哈希一致）。
- 核验状态：打包器自测通过；Codex 复审已对两包做全部跟踪文件的
  blob SHA-1/SHA-256/大小/mode 与 Git 来源独立核验
  （`.workspace-validation/review-20261015/rereview-source-verification.json`）。
- 2026-10-15 后续收口：已从原发布会话工具记录恢复最终 Dockerfile、选择脚本和
  成功的 Go 预编译命令；选择脚本与 CA bundle 另从原 OCI 逐层提取并匹配，
  CA 与本机 Git bundle 字节一致。从 `d9fc97b`、Go 1.26.2 复编的 amd64/arm64
  二进制与 OCI 中原文件 SHA-256 完全一致。
- Server 独立源码候选：`TermLink-Server-Corresponding-Source-0.3.4-133-v2.zip`，
  77,244,586 B，SHA-256 `6f862df345990911fd0e38cf0346b7b5dc53feb2cae5addfcb32958e32baac7a`。
  包含 Server/前端/协议源码、恢复的构建材料和 82 份依赖源码 ZIP；已验证解包后
  在空模块缓存中仅用本地 file proxy 复编，两架构仍与发布文件完全一致。
  首个无 v2 后缀的 Server 候选缺少一个拆分模块父包，已被 v2 取代，不交付旧候选。
- 原始镜像的实际基底为 amd64 Alpine 3.22.5、arm64 Alpine 3.22.6；旧 Dockerfile
  使用了可变 `alpine:3.22` 标签。精确 layer/config digest 和原始材料来源记录在本机
  `source-delivery/server-133-materials/RECOVERY-MANIFEST.json`，不能冒称重建整个镜像 digest。
- OCI 检查另发现旧镜像未带项目 LICENSE、NOTICE、THIRD_PARTY_NOTICES.md；本轮不覆盖
  旧镜像，下次授权发布须按现有官方 Dockerfile 的许可复制步骤核验。Alpine 基底的
  对应源码/分发资料仍须核对，Server 全镜像交付不能据此标为完成。
- 旧构建注入的 `0.3.4+133` 会被 Server 的三段数字版本解析器拒绝，导致运行时版本
  显示未知。本机官方 `server/Dockerfile` 已改为仅在 Go 链接时去掉 `+build` 后缀，
  三段 Version 满足运行时解析；OCI label、镜像 tag 和清单仍保留完整 build。
  实际 Dockerfile 的 shell 表达式、发布计划及 linker buildinfo 测试通过，不改变旧二进制。
- 本机交付入口与校验和：工作区 `.workspace-validation/review-20261015/source-delivery/index.html`、
  `DELIVERY-MANIFEST.json`、`SHA256SUMS.txt`。三个候选均未上传，外部源码交付仍 pending。

## 回滚

保留 0.3.3/132 双端原始发布与制品：APK
`17fba372eb74cd1d481f1011610178800e9110e1fec76185e51293c2dfd4fd6d`、
Setup `76a88bbed1a9c820a64ab36a10e953092ccb0c9589c6e381fd5585c2085595c7`。
如新版本发现问题，以更高 build 发布已知良好源码；手动回退先核验平台
降级限制及备份，不卸载或清空用户数据，不覆盖原发布哈希。未授权的
仓库公开不执行。


## 热修复 0.3.4 · Build 134（2026-10-14，Windows 专用）

133 发布后一台 Windows 机器报告：升级后 Agent 进程存在但 100% CPU、UI 状态异常、
后台运行时卡在 `loading local account, session, and history state` 反复重启。
受影响机器的 Agent 转储（`F:\TermLink.Agent.DMP`）抓到热点栈：
`PrivateHistoryDirectory.SetSecurityInfo ← ProtectContents`——22 组能力波次新增的
历史目录 ACL 加固在每次启动时对既有大历史树**逐文件无条件重写受保护 DACL**，
全树耗时超过客户端 watchdog 重启窗口，永远无法完成就绪（0.3.3 同机正常）。

**修复**（termlink `9721f2e`）：`PrivateHistoryDirectory.OpenAndProtect` 改为
先比对存储的 DACL 与期望描述符，一致即跳过写——每次启动只读校验（可跨重启
收敛），仅对缺失/未保护/陈旧的条目付费写。安全语义不变（所有者信任、
重解析/硬链接拒绝、受保护 DACL 校验原样保留）。历史验证套件全绿（继承 ACL
迁移、长路径幂等、真实 CreateFileW 拒绝校验）。

**发布**：`TermLink-Setup-0.3.4.134-x64.exe`（185,029,664 B，
SHA-256 `99dd94df…7836fe6e0`）已发 AppShip stable `published`/`ready`
（release `855a62de-6ce6-459e-9c04-52ab4b698df6`），公共更新端点实测
build 133→134 optional。仅 Windows 需更新；Android/Server 不受影响
（PrivateHistoryDirectory 仅存在于 win-agent）。

**受影响机器恢复步骤**：更新到 134 后 Agent 会完成收敛（首次启动可能仍需
一到两轮重启循环，每轮都有进展，数轮内就绪并稳定）；无需手动移动历史目录。
