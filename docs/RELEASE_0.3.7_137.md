# TermLink 0.3.7 / 137

本轮范围：GitHub Release 与 AppShip stable 的 Android APK、Windows x64 安装包及完整对应源码。
2026-10-18 现场核对两个客户端均为 0.3.6 / 136，固定递增为 0.3.7 / 137。
本文件随构建源码提交；发布结果以两个渠道的最终状态、下载哈希和独立验签结果为准。

## 更新内容

- Android 按实际窗口适配平板、内折、外折和小折叠屏，支持横竖屏与自由窗口尺寸变化。
- 使用 AndroidX WindowManager 识别分隔折痕，书本姿态选择连续单面、悬停姿态使用上半区域，避免终端跨铰链；高度不足时侧边导航可滚动。
- 输入法只避让与当前操作区重叠的高度；窗口重建保留终端引擎、会话、AI 草稿和用户名草稿。
- Windows 主窗口默认以最小允许尺寸 820×600 逻辑像素启动，支持手动缩放及最大化。
- Windows 自动更新默认验证固定发布者身份与实际文件签名，兼容现有自签名发布者；仍拒绝篡改、无签名和发布者不匹配的文件，显式 strict 策略继续要求系统信任。
- 补充 WindowManager 原始许可、依赖清单、适配说明与自动化验收。

## 验证

- Android 应用单元测试 479 项通过；Lint 无错误，既有警告未宣称清零。
- 4 类 Android 36 模拟器的 17 项设备窗口用例、8 项界面用例及 1 项自由窗口缩放用例通过。
- 覆盖铰链避让、真实键盘弹出、Activity 重建、原生引擎／会话／AI 草稿保持和 80×24 网格保持。
- 半折模拟器使用 AVD hinge sensor 与 AOSP display_features 设置提供系统几何；这些结果不等于厂商折叠真机测试。
- Windows 构建及桌面 Headless 回归通过；更新安全回归涵盖旧正式签名包、错误发布者和保留证书的篡改文件。
- 正式制品须再通过本地独立验签与 GitHub Windows runner 验证，具体哈希、应用 revision 和 core revision 见随附清单。

## 文件与边界

- `TermLink-Android-0.3.7-137.apk`
- `TermLink-Setup-0.3.7.137-x64.exe`
- `TermLink-0.3.7-137-source.zip` 及 `.sha256.json`
- `artifact-manifest.json`、`github-release.json`、`SHA256SUMS`

核心继续消费锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` 的五个预编译原生库；
完整对应源码包含该精确 revision 与 Rust vendor。Android 和 Windows 沿用既有签名身份。
Windows 当前自签名证书保证已配置发布者的更新连续性，不代表公开 CA 信任或 SmartScreen 信誉。

当前 CLI 和 AI 继续使用 Relay Only。本轮未更改服务器、发布 Docker 镜像或自动升级现有安装。
布局测试使用本地产品 fixture 和真实 JNI，不代替真实 Relay 联调；厂商折叠真机、外屏启动政策及
真实蓝牙／USB 键鼠未在本轮验收。GitHub 仓库可见性保持不变；AppShip 客户端发布说明提供对应源码下载入口。
