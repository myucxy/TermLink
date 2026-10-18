# TermLink 0.3.8 / 138

本轮范围为 GitHub Release 与 AppShip stable 的 Android APK、Windows x64 安装包和完整对应源码。
2026-10-18 现场核对客户端及源码最新版本均为 0.3.7 / 137，固定递增为 0.3.8 / 138。
本文件随构建源码提交，最终发布结果以渠道状态、下载哈希和独立验签记录为准。

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
