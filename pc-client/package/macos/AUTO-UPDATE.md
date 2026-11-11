# macOS 安装与自动更新

2026-10-26：客户端已经实现 macOS PKG 更新流程。当前本机产物是 DEV 包，未发布正式 Mac 更新渠道；正式签名、公证、授权安装及重启仍需真实验收。

## 首次安装和 DEV 包

DEV `.app` 可直接运行，也可使用 [DEV PKG 脚本](DEV-PKG.md)生成安装包，手动安装至 `/Applications/TermLink DEV.app`。沿用原始图标及独立 DEV 数据、Keychain 和 IPC。手动覆盖前，应从托盘选择“完全退出 TermLink”，确保 Agent 和受管任务结束。

DEV PKG 没有 Developer ID Installer 签名和公证，不能进入受信的自动更新流程。DEV 构建不会因环境变量或更新服务器返回的身份而开启正式更新。

## 正式客户端更新流程

正式构建开启更新功能并将发布者 Apple Team ID 固定在程序集元数据中。客户端仅在 `/Applications/TermLink.app` 运行时接入，启动及此后每 30 分钟自动检查；设置与托盘也可手动检查。

1. 验证当前应用的 Developer ID Application 签名、公证及 bundle ID。
2. 从 AppShip 对应 Mac 架构的渠道查询；仅接受更高 build、匹配架构的 ready PKG、HTTPS URL、有效大小／SHA-256 及固定发布者。
3. 用户确认后下载，支持进度、下载取消及已验证缓存复用。包签名必须为同 Team 的 Developer ID Installer，Gatekeeper 必须认可公证。
4. 再次确认安装并提示活动任务会中断、系统将请求安装授权。确认后通过认证 IPC 完整停止 Agent 与会话。
5. 由 macOS 授权对话框确认权限。提权后将包复制到私有临时目录，再核对 SHA-256，才调用系统 `installer`；应用不读取或保存管理员密码。系统安装进行中不提供下载取消，也不允许从托盘退出。
6. 核验安装后的签名、公证、bundle ID、version 和 build，旧 UI 退出后重启应用。授权取消或安装失败则解除后台启动限制，可重试；被停止的任务不会自动恢复。

未实现自动回滚。不以打开旧包或安装器启动成功作为升级成功证据。安装失败后若系统已部分写入，需要重新安装可信包；不承诺原任务或旧二进制自动还原。

## AppShip 渠道契约

- 地址：`https://appship.kget.cc:8910`。
- 应用 slug：`termlink-macos-arm64`／`termlink-macos-x64`，当前正式构建使用 `stable`。
- 查询：`/api/v1/apps/<slug>/updates?platform=macos&arch=<arm64|x64>&channel=stable&build=<current>`。
- 无更新：`status` 为 `no_update`／`current`。尚无渠道返回 404 时显示未配置。
- 有更新：`status` 为 `optional`／`required`；`latest` 含 `versionName`、`buildNumber`、`releaseNotes`。
- `artifact` 须含 `platform=macos`、匹配的 `architecture`、`fileKind=pkg`、`status=ready`、`originalFilename`、`sizeBytes`、`sha256`、HTTPS `downloadUrl`。`expectedSizeBytes` 如提供必须匹配，`downloadEnabled` 如提供必须为 true。
- `signerFingerprint` 对 Mac 填 10 位 Apple Team ID，或完整 `Developer ID Installer: … (TEAMID)`。客户端以本地固定 Team 为准。Mac 字段语义须在 AppShip 发布流程中保留，不能套用 Windows 的 40 位证书指纹校验。

元数据最大 64 KiB、查询限时 30 秒、包最大 2 GiB。元数据禁止重定向；包下载最多 3 次且每跳必须 HTTPS。禁止降级或跨架构更新。

## 正式包构建

需同一 Team 的 **Developer ID Application** 与 **Developer ID Installer** 证书，以及 Keychain 中配置好的 notarytool profile。Apple Development 证书不足以用于此分发流程。脚本只支持与构建主机一致的 arm64 或 Intel 架构，版本／build 必须按发布运行手册从已发布状态计算。

```bash
TERMLINK_PROTOC="$protoc_path" bash pc-client/package/macos/build-notarized-pkg.sh \
  "$version" "$build" "$rid" "$dotnet_path" \
  "$application_identity" "$installer_identity" "$team_id" "$notary_profile" "$new_output_pkg"
```

此脚本会上传到 Apple 公证服务，正式执行属于发布操作。它启用更新编译元数据，签署所有 Mach-O 和含 JIT entitlement 的 .NET 入口，先公证并 staple `.app`，再构建、公证并 staple PKG。固定安装到 `/Applications/TermLink.app`，不重定位。输出必须是新绝对路径，不覆盖旧包。没有安装前后脚本，不自动安装、启动或上传 AppShip。

发布前仍须按 [发布运行手册](../../../docs/RELEASE_RUNBOOK.md)提供匹配源码，完成真实两版本升级、管理员授权取消、安装失败、活动任务停止、重启与登录保留验收；分别记录 arm64 和 Intel 的证据。离线 HTTP／命令夹具通过不能替代 Apple 签名、公证和真实系统安装验收。
