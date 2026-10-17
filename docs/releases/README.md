# 发布记录索引

正式执行步骤以[发布运行手册](../RELEASE_RUNBOOK.md)和[构建及签名规则](../RELEASE.md)为准。此页只是既有记录的导航，文件名、Git tag 或本地产物不证明对应平台已成功上线。

同一产品版本可能有多次 build 或不同平台范围。阅读记录内的源码 revision、平台、成功/失败状态、签名、制品哈希及验证时间；查询当前最新版本仍要核对实际 AppShip/Docker Hub 状态，不能只依据本索引。

## 当前分发规则

- Android/Windows 客户端使用 AppShip。
- Docker 由本机从固定的已提交源码构建，直接推送 Docker Hub；核验版本镜像后再同步 latest。
- GitHub 仅运行源码和测试检查；旧 Docker 发布工作流已从当前树移除。客户端与 Docker 发布仍按本机运行手册执行。
- DEV 沿用对应平台最后一次成功正式发布的版本/build，不能把本索引最上面的文件直接当 DEV 基线。
- CLI 与 AI 使用及验收范围为 Relay Only。历史 LAN/NAT 结果仅按其记录时间阅读。

## 产品版本与 build 记录

- [0.3.5 / build 135：Android、Windows、Docker 已发布](../RELEASE_0.3.5_135.md)
- [0.3.4 / build 133：候选与验证记录](../RELEASE_0.3.4_133.md)

- [0.3.3 / build 132](../RELEASE_0.3.3_132.md)
- [0.3.2 / build 131](../RELEASE_0.3.2_131.md)
- [0.3.2 / build 130](../RELEASE_0.3.2_130.md)
- [0.3.1 / build 129](../RELEASE_0.3.1_129.md)
- [0.3.0 / build 128](../RELEASE_0.3.0_128.md)
- [0.2.71 / build 127](../RELEASE_0.2.71_127.md)
- [0.2.70 / build 126](../RELEASE_0.2.70_126.md)
- [0.2.69 / build 125](../RELEASE_0.2.69_125.md)
- [0.2.68 / build 124](../RELEASE_0.2.68_124.md)
- [0.2.67 / build 123](../RELEASE_0.2.67_123.md)
- [0.2.66 / build 122](../RELEASE_0.2.66_122.md)
- [0.2.65 / build 121](../RELEASE_0.2.65_121.md)
- [0.2.64 / build 120](../RELEASE_0.2.64_120.md)
- [0.2.63 / build 119](../RELEASE_0.2.63_119.md)
- [0.2.62 / build 118](../RELEASE_0.2.62_118.md)
- [0.2.61 / build 117](../RELEASE_0.2.61_117.md)
- [0.2.60 / build 116](../RELEASE_0.2.60_116.md)
- [0.2.59 / build 115](../RELEASE_0.2.59_115.md)
- [0.2.58 / build 114](../RELEASE_0.2.58_114.md)
- [0.2.57 / build 113](../RELEASE_0.2.57_113.md)
- [0.2.56 / build 112](../RELEASE_0.2.56_112.md)
- [0.2.55 / build 111](../RELEASE_0.2.55_111.md)
- [0.2.54 / build 110](../RELEASE_0.2.54_110.md)
- [0.2.53 / build 109](../RELEASE_0.2.53_109.md)
- [0.2.52 / build 108](../RELEASE_0.2.52_108.md)
- [0.2.51 / build 107](../RELEASE_0.2.51_107.md)
- [0.2.50 / build 106](../RELEASE_0.2.50_106.md)
- [0.2.49 / build 105](../RELEASE_0.2.49_105.md)
- [0.2.48 / build 104](../RELEASE_0.2.48_104.md)
- [0.2.47 / build 103](../RELEASE_0.2.47_103.md)
- [0.2.46 / build 102](../RELEASE_0.2.46_102.md)
- [0.2.33 / build 91](../RELEASE_0.2.33_91.md)
- [0.2.32 / build 90](../RELEASE_0.2.32_90.md)
- [0.2.31 / build 89](../RELEASE_0.2.31_89.md)
- [0.2.29 / build 87](../RELEASE_0.2.29_87.md)
- [0.2.26 / build 83](../RELEASE_0.2.26_83.md)

## 早期说明

- [RELEASE_NOTES_0.1.28.md](../RELEASE_NOTES_0.1.28.md)

旧记录中的机器路径、曾用命令和签名结论保留当时语义；后续进展应新增记录，不把旧失败改写成通过。签名私钥、账号凭据和生产配置不能加入发布证据文件。
