# TermLink 0.3.5 / 135 发布结果

2026-10-16：Android、Windows 和 Docker Server 已发布。源码固定为应用 `8503acb3fed355a6d53571504873588de54438ae`，core `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。客户端与镜像发布没有修改本批产品源码或重建候选。

| 平台 | 发布结果 | 下载 / 引用 |
| --- | --- | --- |
| Android | AppShip stable，published / ready，0.3.5 / 135 | [APK](https://appship.kget.cc:8910/api/v1/artifacts/691a03fd-87ff-43a2-8d0a-eda1f9ede70f/download) |
| Windows x64 | AppShip stable，published / ready，0.3.5 / 135 | [Setup](https://appship.kget.cc:8910/api/v1/artifacts/663a0cea-cd3c-4365-9234-d3a8882810f0/download) |
| Docker Server | 本机打包后直接推送 Docker Hub，amd64 / arm64 | `docker.io/myucxy/termlink:0.3.5-build.135`；`0.3.5`、`latest` 同 digest |
| 对应源码 | AppShip unlisted，匿名下载哈希核验通过 | [完整对应源码](https://appship.kget.cc:8910/api/v1/artifacts/ca7763b8-ae84-4a24-9c78-d0e5698bbca6/download) |

客户端两个下载均实际匿名下载并核对 SHA-256。公开更新接口：Android build133、Windows build134 返回 `optional`，新 build135 均返回 `no_update`。签名保持既有 Android signer 和 Windows 证书，Windows RFC3161 及 17 个载荷签名通过。

- APK SHA-256：`c472c1616e75c76f1805ed1acdcd30f768fe47c15b61ec083705124010a2c92d`
- Setup SHA-256：`f3f45230bdab5968b0fb4b3766c7b83d6e2dbedf643010326fe7d0dd67eada8c`
- Android 唯一签名者证书 SHA-256：`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。
- Windows Authenticode 签名者 SHA-1：`91AE60BC06D866628B8EB2C62F32BDE9DAB56466`；本机验签为 Valid，不等于公开 CA 信任。
- 对应源码 ZIP SHA-256：`a8ad2d74e415f427704368f494b0535b720a37473f9b569e5c7b87c79f028e84`
- Android release：`09fa9c38-76a4-428d-821f-3708cf48477e`；Windows release：`995d71d8-6fad-4bb6-b20f-debe1ac6fd54`。

## Docker 构建与签名

从固定 commit 导出仅包含 server、LICENSE、NOTICE 的构建上下文，使用仓库原 Dockerfile 和固定基础镜像，在已有本机 BuildKit 构建器构建双架构 OCI，保留 BuildKit SLSA v1 provenance 与 SPDX SBOM。通过代理直接上传同一份 OCI 内容，未使用 GitHub Actions、GHCR 或 GitHub Release，也没有重新编译后替换本地已验证镜像。

```text
index  sha256:3660cd6252c21acdd93286ebf4f6e9176d3b0b798705a4613aaec503f0044104
amd64  sha256:9b93c1caebe7b120a6bea3a749ad5eaa7a8ea51b7021fb71772fb346f8585b7c
arm64  sha256:b74ce09e9e89064a2b67683431117c05f5d18737bbe843f2da43e719d0fdea63
```

Docker 使用独立 Cosign 签名身份；Android/Windows 继续使用既有签名者。私钥、密码和本机凭据不随源码分发。

- [Docker 签名公钥](../release/keys/termlink-server-20261016.pub)随仓库提供；只含公钥，指纹见下方。
- 公钥 SPKI SHA-256：`ccc091b13fe0ec349e233a3bef829734fb47a5839fb77a1bc59679ce0c90fd92`。
- Cosign 3.1.3 官方 Linux 包及可执行文件 SHA-256 均已核验，使用固定 Docker 工具镜像运行。ORAS 1.3.4 同样固定版本并核验官方哈希；工具记录于维护者清单，无系统 PATH 修改。

本地 OCI 归档签名通过；远端 index、amd64、arm64 的镜像签名，以及每种架构的 SLSA v1 / SPDX 签名，共 7 项验证通过，包含透明日志验证。没有跳过签名或透明日志检查，不冒称 GitHub OIDC 身份。版本验证后才同步 `latest`，旧 latest digest `dd39557370f80d62ca560aaa9f5f73eaaf9bc6729668377ae812eec7a027a2c1` 保留为记录。

网络使用现有 `127.0.0.1:7897` 代理；容器访问为 `host.docker.internal:7897`。Sigstore 配置站点采用可达路径，Docker Hub 的签名访问采用实际 `registry-1.docker.io` 入口；未更改系统代理、DNS 或 Docker 服务设置。历史下载/连接失败保留在本机记录，不计为成功。

使用 Cosign 3.1.3 和上述公钥，可验证：

```powershell
cosign verify --key termlink-server-20261016.pub registry-1.docker.io/myucxy/termlink@sha256:3660cd6252c21acdd93286ebf4f6e9176d3b0b798705a4613aaec503f0044104
```

## 安装与边界

MI 6 已从正式 0.3.3/132 覆盖升级到正式 0.3.5/135，UID 和首次安装时间不变，保留登录，设备目录可见。Windows 从已装 0.3.4/134 覆盖升级到正式 0.3.5/135，升级前无活动 SessionHost；升级后客户端恢复隐藏启动，Agent 版本与 Relay Connected、账户关联检查通过，连接仍为 termlink2。

正式包本轮只做覆盖升级与上述冒烟；完整终端输入、控制、通知等证据仍按维护者本机测试汇总中的受测包身份解释，不冒充正式包全矩阵复验。MI 6 实际发声按用户要求排除。

Docker 已发布到 Hub，本轮未升级运行中的服务器或执行数据库迁移。SINGLE/TAKEOVER、Shell 命令定位、Kitty shared-s、多端主题、完整 OEM / 省电及性能等剩余范围仍保留。

详细发布证据：维护者本机受限发布记录（不进入 Git）。本页保留可公开的下载地址、源码 revision、产物哈希与签名结论。

## 原候选验证记录

## 修复内容

- Android 断网目录轮询失败保留 live/frozen 会话，成功的权威目录继续负责回收；认证失败原有拒绝策略保留。
- Android 9 的 Delta、Frozen、Broker Capture AES-GCM 改用数组偏移接口，修复 ByteBuffer 适配器 ShortBufferException；身份、AAD、认证标签、长度、摘要和失败清零检查保留。
- Android 10 及以下启用 adjustResize，使 IME Insets 和终端操作栏正确避让键盘；现代 Android 布局策略保留。
- Windows 登录设备状态响应仅有界读取一次，保留服务端错误诊断并释放响应；历史初始化避免重复扫描兄弟机器历史，保留迁移前祖先路径校验、NULL DACL、重解析点/硬链接等安全检查。
- 验收脚本使用实际 Relay 后继连接判定恢复，保留原 capture 分页及 WAL 文件落盘/ACK 断言；修正图片清屏丢失测试标记，补充有界失败取证。
- 对应源码打包修复空文件/单字节文件处理和敏感路径过滤；Server Dockerfile 的运行时版本仅在链接时移除 build 后缀，OCI 标签保留完整版本。

## 独立验证

| 检查 | 结果 |
| --- | --- |
| Frozen termlink2 真实生命周期 | `review-20261016-frozen-r6`，44/44：断网保留、Relay 新代次订阅、原 capture PAGE、历史边界、释放、5 分钟 TTL、过期后重建与输入恢复通过；Host 已清理，模拟器网络已恢复。 |
| Broker WAL 故障恢复 | `review-20261015-wal-r2`：故障 ACK=45，恢复 WAL/source/ACK=46、capture=1，6804 个新增洋红像素，Host exit 0。 |
| MI 6/Android 9 可见 UI | `mi6-relay-r5`：输出、IME 稳定布局、输入往返通过，ACK 46→94。 |
| 设备仪器测试 | MI 6 与 API36 模拟器各 6/6，含真实 JNI Delta、Frozen/Broker Capture Windows 向量及篡改拒绝。 |
| Android 单元测试 | 最终 app 474 + core-crypto 37，零失败/错误。 |
| Windows 回归 | ProductionTerminalHistoryValidation、AgentUpdateValidation 通过；含 Frozen 72 断言、ACL/迁移/长路径/扫描边界与设备状态 13 用例。 |
| 发布/源码/验收脚本 | Build-Release、CorrespondingSourceArchive、TailFirst、Broker、WAL 自检通过；原生锁五库与 core revision 均通过。 |

本机完整证据：工作区 `.workspace-validation/device-validation-20261015/` 与 `.workspace-validation/frozen-relay-20261016/`。本轮测试使用 termlink2、本地 Docker Relay 与独立账号；仅 Relay，不安排 LAN/NAT。
