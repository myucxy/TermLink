# iOS 原生核心导入与继续迁移

2026-10-25 已将独立 core 构建的 `TermLinkTerminalCore.xcframework` 导入应用的 `native/terminal-core/ios/`。iOS 工程通过 Frameworks 阶段链接整个 XCFramework，由 Xcode 为设备/模拟器选择切片；静态 framework 不嵌入应用运行目录。

## 分平台来源

| 消费平台 | 应用锁文件 | core revision | 来源状态 |
| --- | --- | --- | --- |
| Windows / Android | `terminal-core.lock.json` | `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` | 保留原五库与锁文件，逐文件哈希未改变 |
| iOS / iPadOS | `terminal-core.ios.lock.json` | `f51936441dea83fa2f9459386fb6c35b90102979` | Rust 1.97.1，从干净源码构建 |

两份锁均保留 schema 2，各自描述对应产物的真实来源。iOS 不回退读取 Windows/Android 锁。锁内记录框架全部 9 个文件，包括头、模块、Info.plist 和两个静态二进制的大小与 SHA-256。设备为 arm64，模拟器为 arm64/x86_64，最低 iOS 17.0。

`native/terminal-core/ios/build-info.json` 保留 core 构建来源指纹、驱动哈希、工具版本与 SDK。该来源记录不替代二进制哈希校验。匹配的 core/GPL、Unicode、字符映射和 Rust 依赖许可已随 iOS 应用打包，并在“许可与致谢”页展示。

## 构建与验证

应用构建不需要同级 core 源码、Rust 或联网下载原生库。每次 Debug/Release 构建都会运行：

```sh
xcrun --sdk macosx swift ios/scripts/verify_native.swift
```

显式选择 macOS SDK 是因为此脚本在构建主机上运行，不能继承应用的 iOS `SDKROOT`。校验包括固定来源/工具链/clean 状态、逐文件流式 SHA-256/大小、无符号链接/路径穿越/重复/遗漏文件，以及两个切片的平台、架构、模块与最低系统版本。

防篡改回归测试在 Mac 本地临时目录编译一次独立 Swift 校验器：

```sh
python3 -B ios/scripts/test_verify_native.py
```

将应用检出/复制到 Mac 本地磁盘后运行测试，不直接从 NAS 编译：

```sh
xcodebuild -project ios/TermLink.xcodeproj -scheme TermLink \
  -configuration Debug \
  -destination 'platform=iOS Simulator,name=iPhone 18 Pro,OS=27.0' \
  -parallel-testing-enabled NO -derivedDataPath /tmp/termlink-ios-derived \
  CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- test
```

无需通过命令行覆盖 framework 搜索路径或链接参数。模拟器使用 ad-hoc 签名以支持 Keychain，真机签名仍由本地配置提供，不能将签名资料提交。

## 本轮继续迁移的终端能力

- `TLTerminalBridge` 接入原生有界 history capture、Render V4 viewport、行锚点定位及释放；Shell 状态直接读取原生 TLSHV1，没有把 VT 当文本解析来替代核心。
- `TerminalPresentation` 将实时状态和冻结显示分开。浏览期间事件照常应用、快照持久化后 ACK；滚回渲染帧永远不能决定 ACK。双指上下滑动或菜单进入历史，可以翻页并显式返回实时尾部。
- 冻结期间不发送输入或调整主机窗口；返回尾部只在控制租约仍有效、未撤销且连接已确认时恢复输入。290 秒提前释放五分钟捕获；断连、快照恢复、关闭视图和析构也释放捕获。
- Shell 菜单支持打开当前本地 Windows 目录、查看阶段/退出码和点击命令定位。元数据为不可信输入；目录操作只接受空/localhost authority 的绝对盘符路径，拒绝远程主机、UNC、穿越和非法编码。命令定位前重新读取当前命令 ID 与完整 prompt 身份，避免 RIS 重置后复用行号误定位。
- 当前仍只声明 legacy `terminal_state_2_21_v2`，以 Render V4 消费。core 自身可生成图形/Unicode16 不代表应用具备 Render V5 资源渲染、Broker 或对应恢复链路，仍不能扩大能力声明。

## 后续更新产物

在独立 core 仓库使用固定工具链构建新目录，先验证 clean revision、完整 iOS 包及哈希。导入应用时先在暂存目录复制并核对全部框架文件，替换成功后最后写 `terminal-core.ios.lock.json`；框架、平台锁与匹配的许可一起提交。不得覆盖 `terminal-core.lock.json` 或重标旧 DLL/SO 的来源。使用新包后重新执行校验器、实际桥接/快照/历史测试及设备构建。

## 验收边界

已通过 iPhone 和 iPad 模拟器各 48 项测试及 16 项防篡改测试，包括真正加载已导入核心后的 Unicode、输入、快照、不可变历史、捕获释放、Shell 元数据、旧命令硬重置防误定位和许可资源测试。正式工程设备 arm64 无签名 Release 编译通过，未安装或发布，详见 [iOS 对齐记录](../../ios/PARITY.md)。

尚未在真实主机上验证 Relay attach、租约、history gap、断网恢复、Shell 注解和跨设备控制，也未在 iPhone 真机运行。图形/Unicode16 profile、Render V5/Broker、完整硬件键鼠、主机副作用/信号、AI 附件恢复、后台推送和完整本地化仍见对齐清单。没有部署服务、上传签名包或推送 Git。
