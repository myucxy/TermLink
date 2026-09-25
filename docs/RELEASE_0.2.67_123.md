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

源码和本说明提交、推送后才构建 Production。正式 APK、签名验证和 AppShip 上线证据将在完成后补记；
本段预检记录不代表已发布。发布操作使用仓库 Build-Release / Verify-LocalRelease / Publish-AppShipProduction 入口。

## 回滚与限制

保留 0.2.66/122 的已发布 APK、AppShip 历史和源码。需要代码回滚时以旧行为源码发布更高 build，
不卸载、不清数据、不强制降级，以保护登录和 Android Keystore。辅助滚轮可通过开启触摸鼠标隐藏。
真实手机、多种输入法及弱网人工验收仍未覆盖；模拟器测试不能替代这些环境。
