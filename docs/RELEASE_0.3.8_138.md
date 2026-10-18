# TermLink 0.3.8 / 138

2026-10-18 已发布至 [GitHub v0.3.8](https://github.com/myucxy/TermLink/releases/tag/v0.3.8) 和 AppShip stable。
本轮从现场核验的 0.3.7 / 137 递增为 0.3.8 / 138，包含 Android APK、Windows x64 安装包和完整对应源码。

| 制品 | AppShip 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/48ae51aa-bdac-4ea7-a78c-50b1d89ab3ec/download) | `2f0fb179697e045e8cf8a0b59f7bfd367ec90fad313bf7ea3116644cec095170` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/211d4669-7d2c-4627-b931-ff0e41c2ff52/download) | `2db9f0c551804784faf18685ed8672d8dba8ce88f944a922aa749e0b30183527` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/3d32f98e-2d48-4ac2-9c4f-295e66269daa/download) | `8351787dce274267f9be54f70eb5385feb23ab7aeee0e1551ccdc0cc8a6bcc3f` |

三个 AppShip 制品均为 published / ready，匿名下载哈希与本地一致。两个客户端的
build 137 返回 `optional`，build 138 返回 `no_update`。

- 应用构建与 Git tag `v0.3.8`：`a46a7dadb93aad47c67671bc2555f530e33eca6b`。
- [源码检查](https://github.com/myucxy/TermLink/actions/runs/39118909333)与[远端独立验签](https://github.com/myucxy/TermLink/actions/runs/39120215168)通过；GitHub Release ID `416382499`，七个附件哈希核对一致。
- AppShip Android release：`83f5ee6e-0c49-4d37-b039-14eea9c981b9`；Windows release：`6012950a-c6d2-4fc0-805f-c1a05635ea7e`；源码 release：`4fd3b2b3-e9c3-43cc-a289-6dc586e07b45`。
- APK 唯一 signer、v2 签名及 0.3.8 / 138 版本通过；Windows 发布者身份、RFC3161 和 17 个自有载荷签名通过。
- 正式 APK 已在专用模拟器覆盖安装并验证启动及横竖屏登录页；没有覆盖 MI 6 或本机 Windows 正式安装。
- 代理上传中断后通过 HTTP/2 直连恢复相同字节，只清理本轮未完成的源码上传占位记录；已完成附件、构建、tag 和对应源码 revision 保持一致。

本节发布结果在构建后补记，不改变已发布制品及对应源码的来源。

## 更新内容

- Windows AI 安装检测增加 Codex / ChatGPT 和 Claude 桌面版发现，覆盖当前用户商店注册、卸载注册、运行进程及常见安装目录。
- 优先使用可用 CLI；CLI 失效时继续探测桌面内置编码引擎。Claude Desktop 下载的 Code 引擎按数字版本顺序选择，无需添加到 PATH。
- 仅编码引擎通过帮助和版本探测后标为“协议可用”；图形客户端存在但没有接口时显示“桌面版已安装”，Windows 拒绝启动引擎时显示“系统阻止启动”。
- Windows 和 Android 同步展示这些状态，补充对应历史读取边界说明。检测保留超时、输出上限和取消处理。

## 验证

- AI 全量验证通过，包含新发现逻辑、CLI 优先级、失效 CLI 回退、桌面独立安装、Claude 引擎版本排序及取消。
- 实际本机商店版 OpenAI.Codex 能被识别，包内引擎拒绝启动时返回 runtime_blocked；没有将文件存在记为协议可用。
- Windows 客户端与 Agent 构建无警告、无错误；桌面 Headless 回归通过。
- Android 编译和应用单元测试 479 项通过；本轮仅增加状态文案，没有重新执行上一版本的完整折叠屏矩阵。
- 正式 APK 与 Windows 安装包须通过本地独立验签、时间戳及原生载荷检查，再经 GitHub Windows runner 复核；最终 revision 和哈希记录在随附清单。

## 文件与边界

- `TermLink-Android-0.3.8-138.apk`
- `TermLink-Setup-0.3.8.138-x64.exe`
- `TermLink-0.3.8-138-source.zip` 及 `.sha256.json`
- `artifact-manifest.json`、`github-release.json`、`SHA256SUMS`

终端核心继续消费锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` 的五个预编译原生库；
完整对应源码包含精确应用／core revision、Rust vendor 与构建资料。Android、Windows 沿用原签名身份。
Windows 为既有自签名身份，不代表公开 CA 信任或 SmartScreen 信誉。

本轮没有修复 Windows 商店包内引擎的执行限制，不复制引擎、不修改 WindowsApps 权限、不自动安装 CLI。
Codex 历史读取需要可用引擎与相同用户／CODEX_HOME；Claude 普通聊天及桌面独立会话目录未新增支持。
当前 CLI 和 AI 继续使用 Relay Only；本轮不发布 Docker、不部署服务、不覆盖用户现有客户端安装。
GitHub 仓库保持原可见性；AppShip 客户端发布说明须提供匿名可下载的完整对应源码。
