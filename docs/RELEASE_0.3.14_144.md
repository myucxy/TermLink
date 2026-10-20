# TermLink 0.3.14 / 144

Android 补齐 OpenCode、Grok 的产品图标，并将已支持 Agent 的名称和图标集中维护，避免新建会话、会话列表或历史分组显示通用图标。

## 调整

- 新增 OpenCode、Grok 随 APK 打包的矢量图标，浅色/深色使用黑白版本，不依赖网络加载；来源、固定 revision、SHA-256 和许可记录随 APK 提供。
- Codex、Claude Code、ZCode 的图标保持原资源；当前 5 个 Agent 均有独立的产品图标。
- 修复 `Grok Build`、`grok-build`、`Open Code`、`open-code` 等名称别名。新建选择、会话列表、历史分组与设备内 AI 入口统一使用公共图标组件；未知未来 Agent 保留通用兜底，不误用已有品牌。
- 新增图标覆盖检查并接入 GitHub `Repository checks`：对照 Windows 主机发现清单，拒绝遗漏名称/图标映射、缺失资源、重复 ID 或多个 Agent 共用同一图标资源。
- `AGENTS.md` 和贡献指南规定：接入新 Agent 时必须同步名称、别名、专用图标、来源/许可，以及浅色/深色和选中状态验收。

## 范围与验证

本轮发布 Android APK、Windows x64 Setup 及匹配的 GPLv3 完整对应源码。Windows 仅同步版本/build，运行代码未改；现有 GitHub 验签管道按双客户端成套发布。Server 与 Docker `latest` 保持 0.3.12 / 142，不部署 NAS。

- Android 491 项应用单测及 lint 通过。
- 手机和平板模拟器各 2 项新建会话界面测试通过，覆盖 5 个 Agent 的浅色/深色、图标显示和选中切换；仅选择 Agent 不会创建对话。
- 名称别名、显示名称回到原 ID、图标资源唯一性及未知 Agent 兜底测试通过。
- 图标覆盖脚本的遗漏 Agent、遗漏资源、重复映射与资源复用异常用例通过；新增 SVG 到 VectorDrawable 的原路径、24×24 视口和 even-odd 填充已核对，固定来源的 MIT 许可与随包文件一致。
- 正式制品还需验证版本、源码 revision、SHA-256、APK 与 Windows 签名连续性、Windows 时间戳、core 锁和对应源码下载；最终状态以发布结果为准。

保持 Relay Only。本轮不重测 MI 6、真实 AI 生成或完整 Android Relay 链路，UI 受控用例不代表真实模型调用通过。日常 Windows 安装未覆盖；Windows 沿用原自签名身份，不代表公开 CA 信任或 SmartScreen 信誉。core 来源继续为 `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。个别 PC 身份校验 401 不属于本版修复。


## 发布结果（2026-10-20）

已发布 [GitHub v0.3.14](https://github.com/myucxy/TermLink/releases/tag/v0.3.14) 和 AppShip stable。APK、Windows Setup 与匹配完整源码的构建 revision/tag 固定为 `a21edc2ef4f145a546a8852fd91ec39549f0ac4d`；文档补记不改变制品及标签。

| 制品 | 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/0696ce01-bb0a-4b4b-9853-4837118ce3a0/download) | `6375d3cf321873f23a2c1a9431a5990de109ddbac17402bec433e61fce26a698` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/89d25f4a-a8ae-4c1a-8505-d676af793cb0/download) | `f1b72eba7f754e5ac033ad2ee39bf51048c0d36292126524a53d31284279ea91` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/857db4f8-15c6-4c10-814a-04b7c6221e11/download) | `d740f9556642fe4e7c1cd1d013d975b9aea1129ca3f079f6b6db40b2fa98a23f` |

- GitHub 七个附件的大小与哈希一致，[独立 Windows 验签管道](https://github.com/myucxy/TermLink/actions/runs/39393189998)及固定源码的仓库 CI 通过。新增 Agent 图标覆盖检查已在远端 CI 实际执行。
- AppShip 三个 release 为 `published`、制品为 `ready`；匿名完整下载大小与 SHA-256 一致。客户端 build 143 返回 `optional`，144 返回 `no_update`。
- APK 单一 signer、v2 签名、正式包名及版本正确；Windows Authenticode、RFC 3161 时间戳及 17 个自有载荷签名保持原身份。core 哈希和来源未改变。
- 正式 APK 包内已确认 OpenCode、Grok 图标及来源许可记录，并验证图标映射与别名随代码打包。手机和平板模拟器均原地从 143 升级至 0.3.14 / 144，保留 appId、首次安装时间和应用数据，冷启动与产品界面检查通过。
- Android 491 项应用单测及 lint 通过；手机/平板各 2 项新建会话浅色/深色用例通过，验证全部 5 个 Agent 的图标与选中切换，未实际创建模型会话。

Windows 仅同步版本，日常安装未改变。本轮未重新执行 Windows 真机安装、MI 6、真实 AI 生成或完整 Android Relay 回归。Server 镜像和 Docker `latest` 保持 0.3.12 / 142，NAS 未部署。个别 PC 身份校验 401 不属于本版修复。
