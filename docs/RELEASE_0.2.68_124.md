# Android 0.2.68 / build 124

## 范围与行为

仅发布 Android 到 AppShip stable：右侧滚轮外观与底部 Dock 统一。

- 可见面板从 48×144 dp 缩至约 36×108 dp，采用半透明黑底、小圆角矩形。
- 上下箭头采用扁平线条和与 Dock 一致的 30 dp 方形按键、5 dp 圆角、细灰边框；
  上下按钮各保留 48×48 dp 触摸区域，面板右缘与 Dock 对齐。
- 中间手柄改为矢量双横线，减少高度和视觉位移；拖动加速、回中暂停和中断停止逻辑保留。
- 鼠标模式显隐条件、普通终端手势、控制租约及输入协议不变。

Windows 保持 0.2.66.122，Server/Docker 保持 v0.2.61。本次不推送 Docker 触发 tag。

## 发布预检

2026-09-25 查询 AppShip：Android stable 0.2.67/123，nextBuildNumber=124；Windows stable 0.2.66/122。
远端不存在 v0.2.68，Docker latest 与 v0.2.61 digest 相同。AppShip readiness=migration 8，
发布能力齐全，既有账号为 release_manager，无强制改密状态。
既有 Android keystore 为 PrivateKeyEntry，有效至 2053-12-03，指纹与线上 signer 一致：
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。

## 验证与发布状态

开发阶段滚轮回归测试通过，覆盖点按、拖动、反向、回中、限速、多指、取消、后台/失焦、移除及输入拒绝停止。
Debug 构建和 Lint 通过，0 error，既有 191 warning / 4 hint。模拟器已核对缩小后的面板、
Dock 对齐、上下 48 dp 点击范围及键盘开关；未向用户终端发送鼠标或命令。
证据：ignored 的 `artifacts/validation/wheel-compact-*`。

发布前以 0.2.68/124 重新运行 app 全量 JVM 测试：322 项通过，零失败/错误/跳过；同版本 Debug 构建通过。
日志：`artifacts/validation/release-124-tests-debug.log`。此次没有改动 native/JNI，前版协议测试不冒充本轮重跑。

源码和本说明提交推送后，使用 Build-Release / Verify-LocalRelease / Publish-AppShipProduction 完成正式发布。
构建时工作树干净，HEAD 与 origin/main 均为 `6136721ab5f0467ca8b96d07d7632a3b35620a86`。

## 正式产物与上线证据

Production APK 构建、独立签名校验、AppShip stable 发布及公开下载复核均已完成。

- APK：`artifacts/release/0.2.68-124/TermLink-Android-0.2.68-124.apk`，38,062,422 字节。
- SHA-256：`bb3899538bf0ac37bd461ee1702f7a6b08db25c7589088368864874547475a52`。
- APK 唯一 signer 与上述连续性指纹一致，v2 签名通过；applicationId、版本 0.2.68、build 124 均通过独立核验。
- application ID：`4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`。
- release ID：`58062d56-d678-4aca-b9bd-07fc4bb4e11c`，状态 `published`。
- artifact ID：`6bd5957f-d712-4e4d-8d0e-2e597bc4a618`，状态 `ready`、`manual_approved`。
- Android build 123 返回 `optional` 指向 0.2.68/124；build 124 返回 `no_update`。
  公开下载 APK 的哈希及大小与本地 manifest 完全一致。Windows build 122 仍为 `no_update`。
- 发布恢复状态：`.appship/termlink-android-stable-0.2.68-build124-signature-approved.json`。
- 构建、上传、公开更新接口和下载核对证据：ignored 的 `artifacts/validation/release-124-*`。

模拟器原装为 Debug signer，已原地安装同源码 0.2.68/124 Debug APK；appId=10224、
firstInstallTime=`2026-09-19 10:16:29` 保持。账号和数据保留，HOME-PC 既有 Codex 会话恢复为“控制中”，
紧凑滚轮、Dock 和键盘按钮显示正常；未向用户终端发送命令或鼠标操作。本机 Windows、Server/Docker 未部署。

## 回滚与限制

保留 0.2.67/123 的源码、APK 和 AppShip 历史。需要恢复旧样式时，以旧源码发布更高 build；
不卸载、不清数据、不强制降级，以保留登录和 Android Keystore。
真实手机及多种输入法的人工验收仍需用户环境补充；此次外观优化没有重新声称已覆盖弱网或跨 NAT 验收。
