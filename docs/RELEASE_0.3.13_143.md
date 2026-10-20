# TermLink 0.3.13 / 143

Android 设备卡片支持关闭升级结果与错误提示，并在 Agent 版本后显示绿色 `↗` 更新入口。版本、Windows 与远程路由保持同一行，有无箭头时行高和卡片间距一致。

## 调整

- 升级成功、失败、取消及回滚等终态提示右侧增加“×”，关闭后重新进入不再恢复该条结果；设备卡片保留。
- 暂时无法确认升级状态时可关闭提示，保留原任务 ID 和恢复查询能力，不发送取消请求。相同错误重进仍隐藏，获取到进度或新结果后再次显示。
- 每台 PC 独立处理，关闭提示不会影响其他设备。提示偏好按服务器和账号隔离。
- 支持远程升级的在线 Windows PC，查询到更高的稳定 build 后在 Agent 版本后显示绿色 `↗`；点击重新检查，确认后才发起升级。长按菜单入口保留。
- 更新查询失败时不新增常驻错误，缓存检查结果并支持刷新；无箭头不代表一定是最新版，离线、未获得升级资格或需手动安装引导版时不会显示。
- 精简为 `Windows · 远程路由`，关闭路由时仍明确显示“已停用”。版本与路由单行展示，窄屏超出时省略；图标不额外撑高行间距，设备名点击与长按操作保留。

## 范围与兼容性

本轮发布 Android APK、Windows x64 Setup 和匹配的 GPLv3 完整对应源码。Windows 仅同步版本/build，未修改运行逻辑；现有 GitHub 验签管道按双客户端成套发布。Server 保持 0.3.12 / 142，不构建或推送新镜像，不部署 NAS。

本版复用已有的升级资格、进度和任务接口。自动结束手动更新后的远程任务仍需要包含 0.3.12 修复的 Server 和 Windows Agent／升级器；仅更新 Android 不会补全旧升级器保护。个别 PC 设备身份校验 401 未在本版修复。

## 验证

- Android 489 项应用单测及 lint 通过。
- 手机／平板模拟器各 5 项升级仪器测试通过，覆盖关闭结果重进、未知状态隐藏与重试、更新确认、多设备独立进度以及设备名／箭头点击边界。
- 320dp 窄屏测试通过；直接比较有无更新图标时的文本行数和高度，保持单行且行高一致。绿色调整后两台模拟器的更新入口与恢复测试通过。
- 本地模拟器 DEV 包原地安装、冷启动及进程重启通过，保留账号与应用数据；实际配对 PC 离线时，卡片交互使用受控状态用例验证，未重新触发真实 Windows 升级。
- 正式制品需继续核验源码 revision、SHA-256、core 锁、签名连续性、Windows RFC 3161 时间戳和对应源码下载；最终发布状态以下方发布结果为准。

保持 Relay Only。本轮未重跑 MI 6、完整 Android Relay 回归或 Windows 真机安装流程；Windows 沿用原自签名身份，不代表公开 CA 信任或 SmartScreen 信誉。核心二进制继续锁定 `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。


## 发布结果（2026-10-20）

已发布 [GitHub v0.3.13](https://github.com/myucxy/TermLink/releases/tag/v0.3.13) 和 AppShip stable。APK、Windows Setup 与匹配完整源码的构建 revision/tag 固定为 `5812c3214667d34ce144191837fe714c0b24b65e`；文档补记不改变制品及标签。

| 制品 | 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/45a0d90c-5ed9-4601-b0cc-57af6f584745/download) | `74881fbac51e742aef15714a83a725eda5d8cd34caa4277adf9b1d5872edbb9c` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/19068d5a-70f5-4c04-ba00-6ef5d78aa84d/download) | `9c926b90163e1c3abd8ea8e38aa72c6cec07e2e10be9721df28ced0c0dd62088` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/fff1800e-1ebc-4f10-beea-10de871e194a/download) | `82bfd49b9a80877b77355d9aa4c51195ed8472f9590e39f9d9b9e653dabe946b` |

- GitHub 七个附件的大小与哈希一致，[独立 Windows 验签管道](https://github.com/myucxy/TermLink/actions/runs/39389300538)及固定源码的仓库 CI 通过。
- AppShip 三个 release 为 `published`、制品为 `ready`；匿名完整下载的大小与 SHA-256 一致。客户端 build 142 返回 `optional`，143 返回 `no_update`。
- APK 单一 signer、v2 签名、正式包名及版本正确；Windows Authenticode、RFC 3161 时间戳及 17 个自有载荷签名保持原身份。core 哈希和来源未改变。
- 正式 APK 已在手机及平板模拟器原地从旧版升级至 0.3.13 / 143，保留 appId、首次安装时间和应用数据，冷启动与产品界面检查通过。DEV 仪器用例与正式 APK 启动检查分开记录，不将受控用例宣称为实际 Windows 安装。
- Android 489 项应用单测及 lint 通过；手机／平板各 5 项升级 UI 用例、320dp 单行与等行高用例通过，绿色箭头调整后再次验证两端入口和恢复行为。

Windows 仅同步发布版本，日常 Windows 安装未改变；本轮未再次实际安装 Windows Setup 或复测 MI 6、完整 Android Relay 链路。Server 镜像与 Docker `latest` 保持 0.3.12 / 142，NAS 未部署。个别 PC 设备身份校验 401 不属于本版修复。
