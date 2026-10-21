# TermLink 0.3.16 / 146

Android 远程升级 Windows Agent 时，点击确认后自动收起升级窗口；升级成功后自动清除设备卡片上的升级提示，失败、取消、过期或回滚结果保留查看和关闭入口。进行中的升级继续按设备独立显示，不影响其他设备。

## Android

- 点击确认升级后立即最小化弹窗，升级任务继续在后台执行，设备卡片只保留进行中状态。
- 服务端确认 `SUCCEEDED` 后清除该设备的卡片提示和本地任务记录，重新进入应用不会恢复成功提示。
- 请求失败和未成功终态保留“查看结果”和“×”；查看可见错误详情，关闭只隐藏提示，长按设备仍可重新检查。
- 两台 Windows 设备同时升级时，任务状态、最小化和成功清理按设备隔离。

## 验证范围

- Android 协调器单测 12 项通过，失败为 0，错误为 0。
- `RemoteAgentUpdateInstrumentedTest` 模拟器 5 项通过，覆盖自动最小化、成功清理、失败查看与关闭、重新进入和多设备隔离。
- Android debug validation APK 按 0.3.15 / 145 构建参数验证了 UI 行为；正式发布构建固定为 0.3.16 / 146。

本轮仅包含客户端行为更新，Server/Docker/NAS 不变，传输范围保持 Relay Only。Windows 安装包随 GitHub 统一发布重新签名构建，Windows 生产代码没有功能变化。

## 发布结果（2026-10-22）

已发布 [GitHub v0.3.16](https://github.com/myucxy/TermLink/releases/tag/v0.3.16) 和 AppShip stable。正式 APK、Windows Setup 与匹配完整源码绑定 `b01f57f8858214be7f95748b3a19928d5ee65bb0`，core revision 为 `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。

| 制品 | 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [AppShip 下载](https://appship.kget.cc:8910/api/v1/artifacts/52b8b4a0-66b8-44bf-ac6a-80702cac42e7/download) | `752b8971ab7d4ccf2078d748c36738521d7ddf068358f6ac46413afe097b7f78` |
| Windows x64 Setup | [AppShip 下载](https://appship.kget.cc:8910/api/v1/artifacts/bf561878-06c0-4f6f-aa80-582d24d94160/download) | `cd044c611612092e4ab2943ded2d1a05264be8c695d96458f87ed7284130d777` |
| 完整对应源码 | [AppShip 下载](https://appship.kget.cc:8910/api/v1/artifacts/a6c64e99-e0f4-4cd6-b845-c9f260fad4ff/download) | `13995fea38ba37ea56b35a384fffee03cf1e33b6417e7a1ac1f3239adf0b53bb` |

- GitHub 7 个附件的大小与哈希一致，[独立 Windows 验签管道](https://github.com/myucxy/TermLink/actions/runs/39643909404)及源码仓库 CI 通过。
- AppShip 三个 release 为 `published`、制品为 `ready`；145 返回 `optional`，146 返回 `no_update`，匿名完整下载校验通过。
- APK 单一 signer、v2 签名、版本 0.3.16 / 146 正确；Windows Authenticode、RFC 3161 时间戳及 17 个自有载荷签名通过，沿用既有指纹。
- Server/Docker latest 保持 0.3.12 / 142，NAS 未改动；本轮不包含 Windows 功能代码变化。
