# Android 0.2.67 / build 123

## 范围

仅发布 Android 到 AppShip stable。Windows 保持 0.2.66.122，Server/Docker 保持 v0.2.61；不推送镜像触发 tag。

- 终端已启用鼠标报告、手机持有控制权且关闭“触摸作为鼠标”时，右侧显示半透明辅助滚轮。
  上下箭头一次一个刻度，中间手柄拖得越远越快；回中暂停、松手停止，取消/多指/失焦/后台/失去控制停止。
- 普通终端继续使用原来的点击打开键盘、单指历史浏览、长按选择，不根据程序名称猜测鼠标模式。
- 一行/两行 Dock 的第一个按钮固定切换输入法，跟随真实 IME 可见状态，支持浮动键盘和返回键关闭。
- 云朵菜单压缩状态标题、鼠标开关和窗口尺寸预览，说明移入帮助；网络检测继续遵循服务端策略。
- 复用既有鼠标编码、输入及控制租约通道；不修改 native Rust core、E2EE、ACK、快照及 LAN/NAT 协议。

## 发布预检与测试

2026-09-25 现场查询 AppShip：双端 stable 均为 0.2.66/122，nextBuildNumber=123；
Android 采用未使用的 0.2.67/123。远端不存在 v0.2.67，Docker latest 与 v0.2.61 digest 相同。
AppShip readiness=migration 8，必需能力齐全，既有账号为 release_manager、无需改密。
现有 Android keystore 可用，SHA-256 与线上唯一 signer 一致：
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。

Android JVM：app 322、native 126、core-session 47、replay 5，共 500 项通过，零失败/跳过。
Android 16 模拟器真实 JNI/触摸/Canvas 测试 21 项通过；新增辅助滚轮用例验证 1006/1016 模式、
默认中心/最近触点坐标、只读/模式关闭拒绝，以及控件的限速、反转、回中和中断停止。
最终 Debug 构建、Lint 通过：0 error，既有 191 warning / 4 hint。

模拟器已实际检查两种 Dock 布局、浮动键盘打开/收起、系统返回键同步、菜单布局及鼠标开关显隐；
恢复两行 Dock、触摸鼠标关闭、键盘关闭。没有向用户 Codex 会话发送命令或鼠标输入。
日志和截图保存在 ignored 的 `artifacts/validation/wheel-*`。

源码和本说明提交、推送后才构建 Production，构建时工作树干净，HEAD 与 origin/main 均为
`27089f02ece9acf81541b99998edf9879efa9374`。发布操作使用仓库 Build-Release / Verify-LocalRelease /
Publish-AppShipProduction 入口。

## 正式发布结果

Production 构建、独立验证、AppShip stable 上传发布、更新决策和公开下载复核均已完成。

- APK：`artifacts/release/0.2.67-123/TermLink-Android-0.2.67-123.apk`，38,060,686 字节。
- SHA-256：`f953447f09630a655b373146fe5f65b9fb8048c62c356b3a816ccfc0e3f01d30`。
- 唯一 Android signer 与上述连续性指纹一致，v2 签名验证通过，证书有效至 2053-12-03。
  版本/applicationId 核验通过；四个 ABI 的 JNI 等原生库与 build 122 字节一致，DEX 未引用 Termux 类。
- application ID：`4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`。
- release ID：`aa010f2d-2399-4e81-9eb6-c46643852656`，状态 `published`。
- artifact ID：`83087ad4-cfb8-4250-83d7-67fa284a8ff5`，状态 `ready`，签名审核 `manual_approved`。
- Android build 122 返回 `optional` 指向 0.2.67/123；build 123 返回 `no_update`。
  公开下载 APK 的 SHA-256 和大小与本地 Production manifest 一致。Windows build 122 仍为 `no_update`。
- 恢复状态保存在 `.appship/termlink-android-stable-0.2.67-build123-signature-approved.json`。
  构建、上传、下载校验及模拟器证据在 ignored 的 `artifacts/validation/release-123-*`。

模拟器原装为 Debug signer，已从同一源码构建 0.2.67/123 Debug APK 并原地覆盖；
`appId=10224`、`firstInstallTime=2026-09-19 10:16:29` 保持，账号和数据未清理。
升级后 HOME-PC 既有 Codex 会话画面恢复，状态为 Controlling，辅助滚轮及键盘按钮显示正常。
本机 Windows、Server 和 Docker 未升级。

## 回滚与限制

保留 0.2.66/122 的已发布 APK、AppShip 历史和源码。需要代码回滚时以旧行为源码发布更高 build，
不卸载、不清数据、不强制降级，以保护登录和 Android Keystore。辅助滚轮可通过开启触摸鼠标隐藏。
真实手机、多种输入法及弱网人工验收仍未覆盖；模拟器测试不能替代这些环境。
