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
应用构建来源为 `5823ba290794eadfdd8b5c7e49e742bcb7ab2bf9`，构建时 HEAD 与 origin/main
一致且工作树干净，5 个原生库的 `RequireClean` 校验通过。后续发布记录提交不改变制品来源。

## 发布前验证

| 项目 | 结果 |
| --- | --- |
| Android app + native 单元测试 | 383 + 126 = 509，通过，无失败或跳过 |
| 原生库清单/完整性 | 5 个 DLL/SO 通过；提交后 RequireClean 再次通过 |
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

## 正式制品和线上结果

2026-10-06 01:42:11（北京时间，UTC 2026-10-05 17:42:11）完成独立线上核验。
双端均为 **stable / published / ready**，签名审核状态 manual_approved。

| 项目 | Android | Windows x64 |
| --- | --- | --- |
| 文件名 | `TermLink-Android-0.3.3-132.apk` | `TermLink-Setup-0.3.3.132-x64.exe` |
| 字节数 | 38856661 | 182639112 |
| SHA-256 | `17fba372eb74cd1d481f1011610178800e9110e1fec76185e51293c2dfd4fd6d` | `76a88bbed1a9c820a64ab36a10e953092ccb0c9589c6e381fd5585c2085595c7` |
| application ID | `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2` | `0ae760b3-294e-48e8-a793-95d162f685e2` |
| release ID | `21e424f5-485c-4dda-8c30-3fe998ce8be1` | `74401f97-fed5-4527-981a-36dd08b0c1a1` |
| artifact ID | `4d4b00b4-ad36-41ca-9a18-0748f54997f6` | `84b4ef63-79c9-4df8-b3a4-8d5150ad379c` |
| 发布时间（UTC） | 2026-10-05 17:41:30 | 2026-10-05 17:42:01 |
| 上一 build 更新查询 | 130 → optional / 132 | 131 → optional / 132 |
| 当前 build 更新查询 | 132 → no_update | 132 → no_update |

Android applicationId 为 `dev.termlink.harness`，versionName=0.3.3、versionCode=132，
唯一 signer 与既有指纹一致，v2 签名通过。Windows FileVersion=0.3.3.132，
ProductVersion=`0.3.3.132+5823ba290794eadfdd8b5c7e49e742bcb7ab2bf9`；
Authenticode 为 Valid，17 个签名载荷及 RFC 3161 时间戳通过。
管理 API 与公开更新接口中的制品 ID、版本、大小、哈希和 signer 均与本地独立复核相符。
Android 130、Windows 131 的原发布状态与哈希保留不变。

本地制品在 `artifacts/release/0.3.3-132/`。构建、独立校验、授权记录及线上核验：

- `artifacts/validation/release-132-build.log`
- `artifacts/validation/release-132-verification.json`
- `artifacts/release/0.3.3-132/REVIEW.md`
- `artifacts/validation/release-132-review-state.json`
- `artifacts/validation/release-132-appship-final.json`
- `.appship/termlink-android-stable-0.3.3-build132-signature-approved.json`
- `.appship/termlink-win64-stable-0.3.3-build132-signature-approved.json`

发布使用 appship-publish 客户端和仓库 Production 包装门禁，分发入口为 [AppShip](https://appship.kget.cc:8910)。
双端后续 DEV 基线更新为 **0.3.3 / 132**。本轮未安装此正式包到模拟器或本机 Windows，
未升级 Server；“已发布”不等于“已安装”。Docker 保持既有 0.3.0/latest，不涉及本轮客户端 build。
