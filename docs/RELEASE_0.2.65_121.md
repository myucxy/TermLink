# Android 0.2.65 / build 121

## 范围与行为

仅 Android。Windows 继续使用 AppShip 0.2.64/120；Server、Docker Hub 和 Git tag 不变。

- 云朵菜单新增按会话保存的“触摸作为鼠标”（默认关闭）和独立显示/收起键盘。
- 默认手指点击唤起输入法、滑动浏览、长按选择；显式开启鼠标后，同一手势完整发送
  press/move/release，取消时结束按键，不把孤立 UP 交给点击识别。物理鼠标单独路由。
- 只读或 native core 未启用鼠标报告时禁用开关并说明原因。鼠标状态来自 core 的纯编码查询，
  查询结果不进入输入队列，不在 Android 再解析一套 VT 模式；支持分片、checkpoint、reset。
- 菜单不抢走终端窗口焦点，浮动输入法依据可见状态显示对应菜单项；标题栏保持原高度。
- 初始化并复位本地 IME selection，覆盖首次连接后直接退格的边界。
- Android Gradle 构建图移除三个 Termux 模块；调试 Harness 改用 native engine，对照测试改为
  自有行为回归。AGENTS.md 不再要求 Termux 查询/差分检测。归档源码、许可证、署名仍保留。
  本次未复制外部实现，绘制算法未改动。

## 根因与复核

Codex 启用 `1003;1006` 后，旧 View 在 DOWN 即尝试远端鼠标并跳过 GestureDetector，
UP 又清除捕获标志，仅向 GestureDetector 传入 UP，无法形成完整点击、触发显示键盘。
新实现以用户开关决定手指用途，在 DOWN 固定整段手势的路由；控制租约仍由 engine 统一校验。

## 发布前验证

- native JVM 125、app JVM 320、core-session 47、replay 5，共 497 项通过，零跳过。
- emulator-5554 / Android 16：19 项真实 JNI/Canvas 仪器测试通过，零跳过；覆盖分片鼠标模式、
  checkpoint/reset、无探测输入泄漏、默认触摸/鼠标开关、取消、输入法 composition 与双向删除，
  以及字体、行距、宽字符、样式和只读历史。
- HOME-PC 真实 Codex 会话：默认点击显示输入法；开关开启后点击不弹输入法；独立菜单显示/收起，
  浮动输入法状态正确；重启开关保留；Dock 收起后仍可使用菜单。未向用户会话输入命令。
- 模拟器原地安装同源码 Debug 121，保留 appId 10224、firstInstallTime
  `2026-09-19 10:16:29` 和登录状态。模拟器 Debug signer 不用于 AppShip。
- `:app:lintDebug` 通过，0 error；报告包含 191 warning 和 4 hint（未抑制或新增 baseline）。
- Debug runtime dependency graph 无 Termux 模块。
- Debug APK DEX 无 `com.termux` 或 `dev.termlink.terminal.termux` 类引用。
- 现场 AppShip Android/Windows 均为 0.2.64/120；Android nextBuild 121；远端 v0.2.65 tag 不存在。
  Docker Hub latest 仍指向既有 v0.2.61 发布，本轮不操作镜像。
- 已从受保护凭据打开原 keystore，签名 SHA-256
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`，有效至 2053-12-03。

本地证据：`artifacts/validation/android-121-*.log`、`android-121-test-summary.json`、
`emulator-121-*.xml/png`、`release-121-appship-defaults.json`。制品、截图和凭据状态不提交 Git。

## 构建、上传与回滚

2026-09-25 已完成 Production 构建、独立校验、AppShip stable 发布和公开更新接口复核。
最终制品源码 revision：`7b7858d8b9b61ccd9c6164355cac6f07f11a30c5`；构建时工作树干净，
HEAD 与 origin/main 相同。后续仅补充本发布记录。

正式制品：`artifacts/release/0.2.65-121/TermLink-Android-0.2.65-121.apk`。

- 大小：38,059,930 bytes。
- SHA-256：`b574c0191bee4eaec4a6d47eeb0886d5fb799430dbaf672557961f98ffba003a`。
- apksigner 验证：唯一 signer、APK Signature Scheme v2；连续性指纹与上文一致。
- 正式 APK DEX 也无 Termux 类引用；四个 ABI 的 `libtermlink_terminal_jni.so` 与 0.2.64/120
  逐字节 SHA-256 相同，因此本次不需要 Windows core 或 Server 同步升级。
- AppShip application：`4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`。
- release：`c384332c-727f-43fb-9d7f-f740e306ff5c`，`published`。
- artifact：`f14652b2-d9da-4130-a85a-0f22a236611a`，`ready`、`manual_approved`。
- build 120 返回 `optional`，build 121 返回 `no_update`；公开 artifact 的 hash、大小和 signer
  与本地产物完全一致。发布状态保留于 `.appship/termlink-android-stable-0.2.65-build121-signature-approved.json`。
- 证据：`artifacts/validation/release-121-production-build.log`、`release-121-apksigner.txt`、
  `release-121-package-check.json`、`release-121-appship-publish.log`、`release-121-public-after.json`。

模拟器已覆盖升级同源码 Debug 0.2.65/121 并完成 HOME-PC 回归。未将 Debug 包上传 AppShip；
实体 Android 手机、真实物理鼠标和其他 IME 的现场测试没有在本轮执行。

回滚：可即时关闭触摸鼠标开关。0.2.64/120 制品和 AppShip 历史均保留；若代码回归，以该已验证
源码作基础发布更高 build 的修复版，避免 Android 降级或卸载丢失登录与设备密钥。本版没有迁移。
