# Termux 退役代码清理与来源记录

2026-10-08，按维护者要求继续开源准备，清理已经退出 Android 构建图的 Termux 材料。本文只记录当前工作树和验证边界，不改变历史分发的授权，也不重写 Git 历史。

后续许可更新（2026-10-09）：应用与 core 自有代码统一采用 `GPL-3.0-only`，core 计划后续公开；下文“非商业许可待定”等内容保留清理当天的历史语境，以[当前授权说明](../../LICENSE-STATUS.md)为准。Termux 与 wcwidth 的原有许可和署名不变。

## 已迁出的内容

| 原路径 | 处理 |
| --- | --- |
| `android/terminal-engine-termux/` | 旧适配器、测试和本机构建缓存迁到仓库外私有备份 |
| `terminal-forks/` | 固定上游源码、资源、补丁及同步脚本一并迁出 |
| `terminal-reference/xterm-harness/scripts/compare-phase0.mjs` | 依赖旧 Gradle 导出的对照脚本随原材料归档 |

本次从应用工作树移除 67 个受 Git 跟踪文件。私有备份包含 1,094 个不同文件、57,240,480 字节，含旧构建缓存；重新校验全部 SHA-256 一致。初始清单将单独的对照脚本重复计入一次，1,095 是条目数而非不同文件数，复核按路径去重统计。

本机可恢复备份在工作区父目录 `.workspace-backups/termux-archive-20261008-154109/`，不属于应用或 core 仓库。机器校验清单保存在本机 `artifacts/validation/termux-archive-result.json`；备份不作为新的应用依赖。旧内容也可从清理前应用 revision `6d58975a462d68f1e504624f9397b49bc58e238d` 查阅。

xterm Harness 保留离线 Golden Stream 回放、严格验证及当前网格导出，移除自动读取 Termux 导出、比较面板和失效构建命令。旧的 phase 0 比较报告继续作为历史证据保存，不再是构建或发布门禁。

## 来源与许可

- 原上游：<https://github.com/termux/termux-app>，固定 revision `3df69d1da197dd9bd71a3bafd902dffd720576b4`。
- 原导入范围：`terminal-emulator` 的选定 Java 核心/测试，`terminal-view` 的 Java 与资源。不包含 `TerminalSession`、本地 JNI/PTY、Shell 启动、包管理和 Termux App 服务。
- 原声明逐字保存在 [TERMUX_UPSTREAM_LICENSE.txt](TERMUX_UPSTREAM_LICENSE.txt)。这是上游仓库声明及组件例外说明，不等于各文件的完整许可审计；其中相对路径指向原上游目录。
- 原补丁、各源码头部及原始署名保存在私有备份和既有 Git 历史。更早的 Android 替换结果见 [2026-09-24 实施记录](../ANDROID_TERMUX_REMOVAL.md)。

不能用删除目录、改包名或翻译代码消除既有版权与分发义务。历史包曾包含的代码仍应按其当时内容核对授权。

## 当前保留边界

Android 产品、Harness 和测试继续使用 TermLink 的 native terminal-core、JNI 与 View。行为参考注释和历史文档保留来源，不构成加载 Termux 模块的依赖。

Windows 仍有旧版 V1 快照兼容逻辑，不能把“Android 无 Termux 依赖”扩大成“全仓库无 Termux 来源内容”：

- [ProductionTerminalHistoryProducer.cs](../../win-agent/src/Agent/ProductionTerminalHistoryProducer.cs) 保留 `ValidateTermuxProfile`、旧协议错误码、游标转换和字宽兼容表。native 快照权威开启时走 `ProduceTerminalCoreCheckpoint`，不调用该 V1 检查；旧分支与测试仍在源码中。
- 该文件的 Unicode 15 字宽表来自固定上游的 `WcWidth.java`；其注明数据来源为 `jquast/wcwidth`、revision `1b9b6585b0080ea5cb88dc9815796505724793fe`（2022-12-16）。本轮已直接核对该 revision 的 Unicode 15.0.0 数据：343 个零宽区间、121 个宽字符区间的数值和顺序全部一致。来源、MIT 许可与 Markus Kuhn 声明补齐到 [WcWidth-NOTICE.txt](../../win-agent/src/Agent/WcWidth-NOTICE.txt)，Agent 构建与 publish 自动携带。没有更改字宽数据或将其改名后宣称原创；这项核验不代替其他兼容代码及历史包的授权审核。
- [PortableTerminalModelV1.cs](../../win-agent/src/Agent/PortableTerminalModelV1.cs) 和旧协议验证仍保留相应恢复行为。移除旧分支需要另行核对禁用 native 快照时的回退路径、旧会话恢复及历史数据，不能作为纯目录清理直接删除。
- 本轮未修改私有核心源码、五个 DLL/SO、核心锁文件、产品协议或签名。移出旧目录不代表完成私有核心及二进制的授权审计。

## 验证与开源边界

本轮验证结果在 [开源准备清单](../development/OPEN_SOURCE_READINESS.md) 的 2026-10-08 复核中汇总；本机日志在 `artifacts/validation/termux-cleanup-*`。

清理影响退役材料、独立参考工具及 Windows 许可文本随包复制；Android/Windows 业务逻辑未改动。项目级非商业许可、core 二进制公开再分发、旧 Git 历史中核心源码和第三方来源审核仍需处理；不因本次清理就宣称已具备完整开源发布条件。
