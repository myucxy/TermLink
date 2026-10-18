# TermLink 0.3.9 / 139

2026-10-18 已发布至 [GitHub v0.3.9](https://github.com/myucxy/TermLink/releases/tag/v0.3.9) 和 AppShip stable。
本轮从现场核验的 0.3.8 / 138 递增，包含 Android APK、Windows x64 Setup 和完整对应源码。

| 制品 | AppShip 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/d25686fb-5c4e-4f89-a425-1cc254c906d1/download) | `8b7ae8d644def17a0de6c1ab57ec67da77d8d546ed4263a2eba43a4326d158b8` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/c7f7a7b1-0650-488f-9aa4-a18c8d09eb68/download) | `a8ac6c07d1ad2594cd22f6f8ebdd7a14c80b978aadab1f8e76ab7ddf6666f1e4` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/f6878370-8904-4271-8e67-57f57e561aa6/download) | `c67e2f4eecefaf137c2f29605308a764fcc54d1ab22aa8c582537daee2356643` |

三个 AppShip 制品均为 published / ready，匿名流式下载的大小和哈希与本地一致。
两个客户端 build 138 返回 `optional`，build 139 返回 `no_update`。

- 应用构建及 tag `v0.3.9`：`1e8a5d2030a6c07bc782c726bd2d847e83511bd6`。
- [源码检查](https://github.com/myucxy/TermLink/actions/runs/39134132934)和[GitHub Windows 独立验签](https://github.com/myucxy/TermLink/actions/runs/39135938027)通过；Release ID `416486151`，七个附件哈希一致。
- AppShip Android release：`721c9b3d-ce28-4fac-86d7-5a4a4e4f487a`；Windows release：`72068746-3150-4c65-802d-88da06b41b20`；源码 release：`b598455a-b804-49e2-8678-004f2dc3e3fa`。
- APK 唯一 signer、v2 及版本通过；Windows 发布者身份、RFC3161 和 17 个自有载荷签名通过，新包及篡改拒绝回归通过。
- 正式 APK 在专用模拟器覆盖安装、启动及横竖屏登录页检查通过；未覆盖 MI 6 或用户的 Windows 开发版安装。
- GitHub 大文件直连缓慢后改用代理恢复相同草稿和字节；仅移除本轮取消上传的未完成源码占位，保留已完成附件、tag 和原制品。
- 匿名下载校验采用流式哈希，避免在空间不足的工作盘保存重复制品；未完成副本已转移保留到其他盘。

本节结果在构建后补记，不改变已发布制品及对应源码 revision。

## 更新内容

- Codex 支持读取桌面原生历史并在同一会话继续对话。商店包阻止直接启动时，验证已安装引擎的受信任 OpenAI 签名，将引擎及必要助手按内容哈希保存到当前用户专用缓存后运行，无需另装 CLI。拒绝签名失败、哈希异常和目录重定向，不修改 WindowsApps 权限。
- Claude Code 支持桌面引擎的版本／哈希安装目录和桌面专用历史目录，通过原生 transcript 恢复同一会话，保留历史授权及加密归档中的路径；沿用引擎的认证配置，不复制账号凭据。
- 新增 OpenCode ACP 接入，支持原生历史、`ses_…` 会话 ID、历史正文回放、续聊、审批、中断及归档恢复。恢复已有归档不重复回放，权限沿用原生规则。
- Android 增加 OpenCode 识别，历史导入／恢复请求允许等待 120 秒；Codex 原生恢复允许等待 90 秒。普通请求超时不变。
- 修复 ACP 初始化阶段模型能力误报，并补齐安装包严格还原所需的依赖锁文件。
- 网络层请求动作解析统一使用模块现有 JSON 解析器，补充完整请求正文及无效正文的超时选择回归。

## 验证

- Android 模拟器通过正常产品界面连接本地服务的 HTTPS 测试地址，经 Relay 完成 Codex、Claude Code 桌面引擎、OpenCode 的历史加载和上下文续聊；原生会话 ID 和测试上下文保持一致。
- 单独排除测试进程 PATH 中的 Codex CLI 后，桌面签名引擎仍可在手机恢复归档并继续对话；系统 PATH 未修改。
- Agent 重启后，三种 Agent 的加密归档均可恢复；OpenCode 恢复前后对话正文数量一致，恢复后再次发送通过；Claude 桌面 transcript 路径与计划模式保留。
- AI 验证套件通过，覆盖签名缓存、取消、篡改和目录重定向、权限边界、原生历史及 ACP 交互；Windows adapter 验证通过。
- Android 应用 480 项、网络层 424 项单元测试通过，包含导入／恢复超时及加密请求交互回归。正式 APK 和 Setup 的版本、哈希、原生签名、Windows 载荷及 RFC3161 均通过本地独立校验和 GitHub 发布管道复核。

## 已知边界

- 主测模拟器首次安装启动出现一次超时，同时系统桌面与 InputManager 无响应，根因未确认；后续 3 次冷启动约 4.5–4.7 秒、第二模拟器首次启动约 13.5 秒，均成功且无新增 App ANR。保留该启动性能问题，未宣称已根治。
- 目前支持 Claude Code 桌面编码历史；普通 Claude Chat、Cowork、SSH／WSL 和云端独立会话不在本轮范围。OpenCode 仅接本机 CLI，未验证其桌面 GUI、运行中的 TUI 接管或外部 HTTP Server。
- 本轮联测使用模拟器；没有重新测试 MI 6 或完整折叠屏矩阵。当前 CLI 与 AI 仅使用 Relay，不安排 LAN/NAT 验收。

## 制品与来源

- `TermLink-Android-0.3.9-139.apk`
- `TermLink-Setup-0.3.9.139-x64.exe`
- `TermLink-0.3.9-139-source.zip` 及 `.sha256.json`
- `artifact-manifest.json`、`github-release.json`、`SHA256SUMS`

终端核心继续使用锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` 的五个预编译原生库。
完整对应源码包含精确应用／core revision、Rust vendor 和构建资料，按 GPL-3.0-only 提供。
Android 与 Windows 沿用原签名；Windows 为既有自签名身份，不代表公开 CA 信任或 SmartScreen 信誉。
本轮不发布 Docker、不部署服务，GitHub 仓库保持原可见性。最终 revision、哈希、下载入口与发布管道结果见本文记录及随附清单。
