# 0.3.1 / 129 双端发布计划

2026-10-04：用户恢复网络代理并要求继续推送、上传 AppShip。本轮范围为 Android APK、Windows x64 Setup，顺序 Android → Windows。Server 无修改，不重编译或推送 Docker，不部署服务，不中断正在运行的 Windows 会话。

## 版本与源码

- 现场 AppShip 两端 stable 均为 `0.2.71/127`，release=`published`、artifact=`ready`，nextBuildNumber=128（含草稿占号）。
- 通过本机既有代理现场读取 Docker Hub 全部 17 个标签，最高版本 `0.3.0`；`latest` 与 `0.3.0` 均为 `sha256:dd39557370f80d62ca560aaa9f5f73eaaf9bc6729668377ae812eec7a027a2c1`。
- 远端 `v0.3*` 标签为空。已发布 Docker 和旧客户端候选使用 build 128，本轮按全平台已用最大值递增，固定为 **0.3.1 / 129**。
- 功能源码为 `fa54a512087c5a457c136121751c5941c8703159`；本计划提交仅修改发布文档。Production 制品绑定包含本计划的已提交、已推送且工作树干净的最终 revision，在精确制品审核记录中写出完整值。
- 选号过程中误建的本地 `0.3.1/128` 制品未发布，不作为候选，不上传。保留历史目录，不覆盖旧版本。

## 发布说明

Android 支持从 AI 文件及预览打开所在目录、定位文件并返回原对话；设备卡片保持 CLI、AI 聊天、文件顺序，显示 Agent 分组与产品图标，目录可新建 AI 对话并带入路径。改善草稿/队列恢复、跨设备操作隔离、返回导航、错误反馈和后台下载；修复文件保存返回应用时 Relay 重连导致旧请求等待的问题。

Windows 改善离线/未知状态、浏览器授权取消、确认框焦点与键盘操作、CLI 标题与已加载 AI 元数据。更新流程区分下载、验证与安装，安装前提示当前任务，稍后安装保留并重新验证缓存。

## 测试与已知边界

- 功能源码已通过 Android app 375 项单测、ProductionSessionCatalog 4 项 Relay 契约测试、Web 9 项 Playwright 回归。
- Windows Agent 解决方案、桌面 Windows 目标编译通过；Control、Update、Adapter、Headless 验证通过。
- 安装器隔离安装/升级/卸载自动化通过，恢复本机原安装注册信息；真实更新下载、缓存复用与篡改拒绝通过。
- 模拟器与 HOME-PC 已验证 AI 文件目录/预览跳转、高亮、返回、下载期间切页和系统分享入口。18072 字节文件双端 SHA-256 一致。
- 当前仅 Relay Only；LAN/NAT 测试不适用。CLI 正文与 Dock 布局保持原功能。
- 厂商离线推送尚无项目配置；高级诊断文案尚未全部双语；Windows AI 列表仅列出运行时已加载记录，不声称扫描所有原生历史。

## 签名与发布门禁

从上述最终 revision 运行统一 Production 构建，再独立运行 Verify-LocalRelease。APK 使用既有 signer SHA-256 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`；Windows 使用既有 signer SHA-1 `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`，核验 Authenticode、完整载荷和 RFC 3161 时间戳。Windows 是既有自签发布者连续性，不代表公开 CA 信任。

精确制品清单、完整哈希和验证结果写入 ignored 的 `artifacts/release/0.3.1-129/REVIEW.md`；人工签名审核仍需记录真实结论，不把验签自动通过冒充人工批准。只通过仓库 AppShip Production 包装器发布并保留幂等状态。

发布成功须确认两端 release=`published`、artifact=`ready`、build 127/128 更新为 optional、129 为 no_update，再补充 release/artifact ID 及核验时间。当前本计划不代表上传成功。

## 回滚证据

保留 AppShip 既有 `0.2.71/127` 发布及本地历史制品。Android 旧 APK SHA-256 `6bc7a8c3fdbebaee34ab6727d1886d6057d461681d2b412973b4431b192ce728`；Windows 旧 Setup SHA-256 `38f5f82ae0f2eac8b06a249220f4ab611aac3f8acf1ce118aff54c91f0b25184`，签名连续。

不删除用户数据或卸载 Android 来强行降级；正常更新按 build 递增，必要时从已知良好源码发布更高 build 的修复版。Windows 安装器失败回滚已测试；不移动已发布 tag 或改写发布历史。Server 未改动，不涉及数据库回滚。
