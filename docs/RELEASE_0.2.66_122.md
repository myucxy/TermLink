# Android / Windows 0.2.66 / build 122

## 范围与行为

本次统一发布 Android 0.2.66/122 与 Windows 0.2.66.122，Server 和 Docker 不变；不推送镜像触发 tag。

- Android 只有在 native core 已启用鼠标报告、用户打开触摸鼠标开关且有控制权时才启用新手势。
  轻点在抬起时发送单击，单指滑动发送滚轮，长按后拖动发送鼠标拖拽；快速连点完整转发。
- 未启用鼠标报告、关闭用户开关或只读时，沿用本地点击打开输入法、单指浏览历史、长按选择。
  不按 Codex/Claude 程序名猜测模式；不向普通终端发送模拟滚轮。
- 多指打断、取消、窗口失焦和移除 View 会结束鼠标捕获；剩余手势不会误唤起键盘。
  开关已开启时始终允许关闭，即使程序不再支持鼠标或手机处于只读。
- Windows SessionHost 补齐交互程序滚轮、点击、释放、拖动转发，并保持与键盘混合输入的顺序。
  托管会话复用 native core 协商的协议；悬停不抢手机控制权。
- native Rust core、快照格式、E2EE/ACK、Server、LAN/NAT 协议未改变。详细根因与边界见
  [鼠标桥接与手势说明](TERMINAL_MOUSE_SCROLL.md)。

## 发布现场预检

2026-09-25 实际查询 AppShip：Android stable 为 0.2.65/121，nextBuild=122；
Windows stable 为 0.2.64/120，nextBuild=121。本批统一使用未发布的 0.2.66/122。
远端不存在 v0.2.66；Docker Hub latest 仍是 v0.2.61，本次不操作。

已打开既有 Android keystore 与 Windows PFX 并核对证书：

- Android SHA-256：`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。
- Windows SHA-1：`91AE60BC06D866628B8EB2C62F32BDE9DAB56466`，有效至 2036-08-04，具备私钥。
  沿用自签开发证书的更新连续性，不声明公开 CA 信任。
- AppShip 凭据库中的既有账号为 release_manager，无强制改密状态。

## 验证与发布状态

Windows solution Release 构建通过，0 warning / 0 error；SessionHost 真实嵌套 ConPTY、
NativeTerminalCoreValidation、WindowsSetupValidation 均通过。
Android native JVM 126、app JVM 320、core-session 47、replay 5，共 498 项通过，零跳过；
emulator-5554 / Android 16 上 20 项真实 JNI/Canvas/触摸仪器测试通过，零跳过。
`:app:lintDebug` 通过，0 error，既有 191 warning / 4 hint，没有新增 baseline。

模拟器已原地升级同源码 Debug 0.2.66/122，appId 10224 和 firstInstallTime
`2026-09-19 10:16:29` 保持。HOME-PC 既有 Codex 会话恢复成功，菜单显示新手势说明，
开关切换正常，恢复关闭后轻点可以显示浮动输入法；没有向用户会话输入命令或远端鼠标操作。
非鼠标模式开关保持 ON 时的点击、滚动恢复以及取消、失焦、双击由自动化测试覆盖。

Production 构建、独立签名校验、双端 AppShip stable 发布及公开更新接口复核均已完成。

证据放在 ignored 的 `artifacts/validation/release-122-*`。源码、说明提交推送后才构建 Production。
APK/Setup、凭据和 `.appship` 状态不提交。最终制品源码 revision 为
`b10ebc11d7be0ac2a56f967fce14acb05026ac44`；构建时 HEAD 与 origin/main 相同，工作树干净。
Windows ProductVersion 为 `0.2.66.122+b10ebc11d7be0ac2a56f967fce14acb05026ac44`。

## 正式产物与上线证据

| 平台 | 产物 | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| Android | `artifacts/release/0.2.66-122/TermLink-Android-0.2.66-122.apk` | 38,059,958 | `8c38eb11f0fcf3084afd7f8314d3fad525c368622219f335fe3824682f676d02` |
| Windows x64 | `artifacts/release/0.2.66-122/TermLink-Setup-0.2.66.122-x64.exe` | 182,470,880 | `398960145999552148e27ef5bfc698dd7db6148a151f2aedfefb7a06aba292b1` |

APK 唯一 signer 与既有签名一致；Windows Setup 和内嵌 TermLink 程序均为有效原 signer，
具备 RFC 3161 时间戳。双端 `Verify-LocalRelease.ps1` 独立验证通过。
APK DEX 无 Termux 类引用，四个 ABI 的 terminal JNI 库与 0.2.65/121 完全相同。

- Windows application：`0ae760b3-294e-48e8-a793-95d162f685e2`；
  release：`a5c44928-1d59-41da-843d-fd1452743eea`；artifact：`786ab91a-bcaf-4fc7-86a0-8d1bce2b2eb1`。
- Android application：`4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`；
  release：`47b5bf17-3b28-4bf7-9d9b-d3dfe7327170`；artifact：`b65290a1-5aed-489f-a49c-38466b67e6ea`。
- 双端 release 均为 `published`，artifact 均为 `ready`、`manual_approved`。
  Windows build 120 与 Android build 121 均返回 `optional` 指向 0.2.66/122；build 122 均返回 `no_update`。
  公共接口的哈希、大小与本地 manifest 一致。
- 状态文件：`.appship/termlink-win64-stable-0.2.66-build122-signature-approved.json`、
  `.appship/termlink-android-stable-0.2.66-build122-signature-approved.json`。
- 证据：`release-122-production-build.log`、`release-122-setup-signature.json`、
  `release-122-apk-content.json`、`release-122-appship-*.log`、`release-122-*-public-after.json`。

构建的首次尝试因为把实际 cargo 二进制传给了要求 `+1.97.1` 的脚本而退出，尚未产生正式 manifest；
改为已有 rustup 代理 `D:/Dev/Env/Rust/cargo/bin/cargo.exe` 后成功，不改动源码或工具版本。
Windows 签名期间仅暂时导入同一私钥证书到 CurrentUser/My，使用 thumbprint 签名，结束后移除新增项；
没有把 PFX 密码传给 SignTool 命令参数。

## 回滚和限制

Android 可先关闭触摸鼠标开关恢复默认交互；0.2.65/121 APK 和 AppShip 历史保留，
若需代码回滚则以该源码发布更高 build，避免卸载或降级造成登录与密钥丢失。
Windows 保留 0.2.64.120 安装包；按运行手册原安装根恢复，升级/重启可能结束既有会话。
本次不更换本机 Windows 正在运行的客户端，也不修改用户正在使用的 Codex 会话。

Win32 本地输入只有格坐标，不能完整提供 1016 像素鼠标；该模式不会伪造坐标或终止会话。
真实物理触控板、多种手机 IME 和远端所有交互程序仍需用户环境验收，不能由隔离测试代替。
