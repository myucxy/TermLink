# Android 0.2.69 / build 125

## 范围与行为

仅发布 Android 到 AppShip stable：会话云朵菜单增加全局“滚轮位置：左侧 / 右侧”。

- 默认右侧，所有会话共用设置，重启后保留；关闭会话标签不会清除位置偏好。
- 菜单项在只读、非鼠标模式也可设置；实际滚轮仍仅在可输入、鼠标模式开启、
  触摸作为鼠标关闭且历史覆盖层隐藏时出现。
- 采用紧凑单行：标题与提示上下排列，左右选择总宽 120 dp，按钮可见高度 36 dp，
  行与触摸高度保留 48 dp。物理左右边缘不随 RTL 文字方向反转。
- 切换边缘通过移除旧滚轮 View 取消当前拖动和定时发送，新 View 不恢复旧手势。
  远端坐标仍由终端最近触摸位置或终端中心决定，不使用浮层按钮坐标。
- 复用现有偏好存储、Material3 控件和滚轮取消逻辑，无新增依赖及 native/JNI/输入协议变更。

Windows 保持 0.2.66/122；Server/Docker 保持 v0.2.61；本次不推送 Docker 触发 tag。

## 发布预检

2026-09-25 实时查询：Android stable 0.2.68/124，nextBuildNumber=125；Windows stable 0.2.66/122；
Docker latest 与 v0.2.61 digest 一致。远端 Git tag 未占用 v0.2.69。
AppShip readiness=ready、migrationVersion=8，发布能力齐全，现有账号为 release_manager，
passwordMustChange=false。凭据从 Credential Manager 当次读取，未写入源码或日志。

现场核验既有 Android keystore 为 PrivateKeyEntry，有效至 2053-12-03，
SHA-256 与线上唯一 signer 一致：
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。
Ensure-DevEnv 核验已安装 JDK 21.0.7 / Android SDK；Build-Release -Plan 仅包含 Android APK。

## 测试与模拟器

- 最终 0.2.69/125 源码的 app JVM 测试 323 项，零失败/错误/跳过；Debug 构建通过。
  新增全局默认值、跨 Context 持久化、关闭所有标签后仍保留偏好的回归。
- 既有滚轮测试通过：点按、拖动加速、反向、回中、多指/取消、失焦、隐藏、后台、
  输入拒绝和 View 移除后停止，没有定时发送积压。位置切换复用该移除逻辑。
- Lint 通过：0 error，既有 191 warning / 4 hint；本次未新增警告。
- Android 16 模拟器从现有 Debug 签名包原地覆盖到同源码同版本 Debug APK。
  appId=10224、firstInstallTime=2026-09-19 10:16:29 不变，账号与既有 HOME-PC Codex 会话保留。
- 实际操作菜单左右切换、强制停止 App 后恢复位置、只读时仍可设置且滚轮隐藏、
  恢复控制后使用已选位置。最终紧凑布局已查看截图，并实际点击可见按钮上下边界外的
  扩展点击区域，两侧均生效：左侧 x=0..126、右侧 x=954..1080，纵向范围均为 y=950..1265。
- 未向用户的 Codex 终端发送命令或滚轮事件；滚轮事件与取消由隔离的 JVM 回归验证。

证据在 ignored 的 `artifacts/validation/release-125-*` 与 `wheel-side-*`；
本次没有重跑 native/JNI、真实手机、跨 NAT 或弱网验收，不用历史结果替代本次测试。

## 发布状态

源码及本说明固定提交后，按 Build-Release / Verify-LocalRelease / Publish-AppShipProduction
执行 Production 构建、独立核验、AppShip stable 发布和公开更新/下载哈希复核。
实际 revision、制品和发布 ID 将在完成后补录；本节当前不代表上传完成。

## 回滚

保留 0.2.68/124 的源码、APK 和 AppShip 历史。若需回退功能，以旧源码发布更高 build；
不卸载、不清数据、不强制降级，以保留账号与 Android Keystore。
