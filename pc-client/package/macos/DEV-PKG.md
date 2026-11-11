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
