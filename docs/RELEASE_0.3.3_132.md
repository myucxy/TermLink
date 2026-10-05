# TermLink 0.3.3 / 132 发布记录

## 本轮计划

用户明确要求提交、推送已有改动，并将涉及的客户端重新发布到 AppShip。
本轮范围为 Android APK 和 Windows x64 Setup，统一版本 **0.3.3 / build 132**。
Server 无运行代码改动，不构建或推送 Docker；不部署服务、不替换运行中的客户端。

2026-10-05 17:20 UTC 现场核验：Android stable 0.3.2/130、Windows stable 0.3.2/131
均为 published/ready；nextBuildNumber 分别为 131、132。候选版本/build 未被占用，远端无同名版本 tag。
基线证据 `artifacts/validation/next-release-live.json`。本轮所有构建与恢复固定使用 0.3.3/132。

## 改动范围

- Android 修复批量关闭选择、手机隐藏会话、获取控制后结束及匹配回执处理。
- 已结束记录只进入电脑历史；查看历史不会重新加入当前列表，恢复成功才重新加入。
- 支持关闭后的会话记录删除，并保留 Windows 工作目录、文件和 Agent 原生历史。
- 两端包含此前完成但尚未正式发布的 binary-only terminal-core 打包边界：
  应用只消费已锁定 DLL/SO，不需要核心源码 checkout。Windows 更新启动修复继续保留。
- README 增加中文实录演示、旁白、字幕和本地合成配乐，原始素材及私人测试数据不入库。

核心锁定 revision 为 `e6e849172c6fca0de01ae0799eae97ad9483e687`，source_dirty=false。
应用构建来源为包含本计划的最终已提交且推送的 commit；精确 revision 和制品信息在构建后补录。

## 发布前验证

| 项目 | 结果 |
| --- | --- |
| Android app + native 单元测试 | 383 + 126 = 509，通过，无失败或跳过 |
| 原生库清单/完整性 | 5 个 DLL/SO 通过；提交后再次执行 RequireClean |
| NativeArtifacts 脚本测试 | 7 项通过 |
| release/Test-BuildRelease.ps1 | 通过，含签名、版本及发布包装门禁 |
| Windows NativeTerminalCoreValidation | 通过 |
| Windows WindowsSetupValidation | 通过 |
| Windows 安装生命周期 | 隔离安装、原地升级、跨目录拒绝、保留启动项、卸载通过 |
| 模拟器与 HOME-PC 真实 Relay | 新建、重命名、批量关闭、结束、历史查看、删除及目录保留通过 |
| LAN/NAT | 不适用，用户指定 Relay Only |

详细边界见 [AI 生命周期验证](AI_SESSION_LIFECYCLE_VALIDATION_20261005.md) 与
[二进制核心验证](development/BINARY_CORE_VALIDATION.md)。本轮 DEV 验证沿用已发布基线：
Android 0.3.2/130、Windows 0.3.2.131，不提前推进 DEV 基线。
安装生命周期首次调用因测试产物文件名不符合安装器识别约定而失败，改用
`TermLink-Setup-0.3.2.131-lifecycle.exe` 后通过；没有因此修改产品代码。
证据保存于 `artifacts/validation/release-132-*`，不提交制品或本机状态。

## 签名、发布与回滚边界

沿用既有已批准发布者，实际 Production 文件仍须独立核验版本、哈希和签名连续性：

- Android signer SHA-256：`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。
- Windows signer SHA-1：`91AE60BC06D866628B8EB2C62F32BDE9DAB56466`，验证 RFC 3161 时间戳。
  沿用自签发布者，只代表已有更新信任连续性，不代表公开 CA 信任。
- 用户本轮发布授权与机器制品校验分开记录，不把机器校验冒充用户逐项核对新哈希。
- 经 `scripts/Publish-AppShipProduction.ps1` 顺序发布 Android、Windows；中断保留 `.appship/` 幂等状态。
- 保留 Android 0.3.2/130、Windows 0.3.2/131 原始制品和发布记录。若需回退，用受控安装或更高 build
  发布已知良好源码，不覆盖已发布哈希、不删除用户数据。

当前此提交内的记录为构建前计划。Production 构建、线上 published/ready、旧版 optional / 新版 no_update
及最终 release/artifact ID 待执行后追加；不能将计划视为已发布。
