# 本地 DEV 安装包

从已经构建的 `TermLink DEV.app` 生成未签名的本地 PKG：

```bash
bash pc-client/package/macos/build-dev-pkg.sh \
  "/absolute/path/TermLink DEV.app" \
  "/absolute/new-output/TermLink-DEV-osx-arm64.pkg"
```

两个参数必须是绝对路径，输出的父目录需已存在，输出文件必须不存在。脚本仅在 macOS 上运行，验证 DEV bundle 身份、版本、原始图标、现有代码签名和自包含 Agent 依赖，完整复制 bundle。包的独立 receipt identifier 为 `dev.termlink.desktop.dev`，目标为 `/Applications/TermLink DEV.app`，禁止重定位到别处已安装的副本。脚本读取并保留 bundle 中的版本/build，不分配或递增版本；本轮候选沿用 `0.3.16 / 146`。

此包帮助首次手动本地安装。PKG 未签名、未公证，应用原有本地代码签名不构成安装器的发布信任，不能将此包用于未来受信任的自动更新链路。脚本不包含安装钩子，不安装或启动应用，不改变现有应用和服务，也不执行网络、正式签名、公证或上传。

可用 `pkgutil --check-signature <pkg>` 检查未签名状态，该命令对本包返回非零是预期结果。用 `pkgutil --expand-full <pkg> <new-temporary-directory>` 可检查 `PackageInfo` 和 `Payload/Applications/TermLink DEV.app`；展开不会安装应用。

## 固定 DEV 代码签名

`build-dev-app.sh` 默认保留 ad-hoc 本地签名。需要连续构建使用同一开发证书时，显式传入
本机 `security find-identity -v -p codesigning` 中的证书 SHA-1 指纹：

```bash
TERMLINK_DEV_SIGNING_IDENTITY="$development_identity_sha1" \
TERMLINK_SOURCE_REVISION="$source_revision" \
bash pc-client/package/macos/build-dev-app.sh \
  "$dev_version" "$dev_build" "$dotnet_path" "$protoc_path" "$new_output_directory"
```

指纹只选择已有私钥，不生成或导出证书。辅助脚本 `sign-dev-app.sh` 只接受
`dev.termlink.desktop.dev` / `Development` bundle；逐个签署 `MacOS` 中的原生、托管和配套文件，再以固定
`dev.termlink.agent.dev` 签署 Agent，最后签署 UI bundle 并严格验证完整签名。
同一开发证书和签名标识应贯穿后续 DEV 构建；不要在每次构建时重新生成证书。

ad-hoc 旧包转为证书签名是一次身份迁移，已有钥匙串条目仍可能要求用户在系统对话框授权。
脚本不修改已有条目的 ACL，也不删除登录信息。新签名连续性可用两个不同载荷的签名要求比对，
并在独立测试条目中验证更新前写入、更新后读取。此验证不能替代原登录的实际迁移验收。

Apple Development 代码签名不把 DEV PKG 变成正式分发包；安装器仍未签名、未公证，自动更新保持关闭。
正式分发仍使用 `build-notarized-pkg.sh` 的 Developer ID Application / Installer 和公证流程。

.NET 的托管 DLL、运行配置和配套文件目前位于 `Contents/MacOS`，除 Mach-O 外也按嵌套代码签署。
这类签名使用文件扩展属性，因此复制和 ZIP 必须保留它们；不能使用会删除扩展属性的归档参数。
DEV 构建会在 ZIP 解压后及输出 bundle 复制后重新执行 `codesign --verify --deep --strict`，
PKG 构建也在 `pkgutil --expand-full` 后验证实际载荷，任一失败都不交付该包。
