# TermLink 0.3.18 / 148

本批准备 Windows x64 Setup、Android APK 和完整对应源码。macOS 桌面客户端后续沿用 `0.3.18 / 148` 和本批应用提交，从各自架构的预编译核心依赖构建。

## Windows 与 Android

- 终端核心支持光标闪烁模式，切换时保留光标形状。识别固定 UTF-8 Meta 模式，未知模式仍以受限诊断安全中止。
- Android 在会话链路保留终端协议诊断，补齐相关在机回归。打包保留四种 Android ABI。
- 便携电脑端收紧 Relay 请求超时、AI 操作回执和 CLI 清理重试，补充传输与控制回归用例。
- Windows x64 采用 MSVC 原生编译的 terminal-core DLL。Windows 与 Android 核心源码均固定到 `0d5b8d9fbdfc9c6cbb21588b729eefa916d9d221`。

## macOS 同版交接

- 从本说明所在的发布提交构建，使用 `0.3.18`、build `148` 以及 `terminal-core.macos.lock.json` 中固定的 arm64 与 x64 库。
- 分架构完成 Developer ID 签名、公证与 stapler 校验。按正式轨道要求确认新装、覆盖升级、回滚和 Relay 验收后，发布到对应的 macOS 渠道。
- 若修改发布提交中的产品源文件或依赖，先与发布协调者安排新的候选。Mac 侧以签名包和线上核验结果确认补发完成。
