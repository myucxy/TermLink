# TermLink Android 0.3.4 / 133 发布记录

## 本轮计划

2026-10-09 用户要求发布本轮改动。本轮平台为 Android APK，固定版本 **0.3.4 / build 133**。
Windows 和 Server 没有本轮必须部署的运行逻辑改动，不构建或部署这两个平台。
线上核验 Android、Windows 均为 stable 0.3.3/132，published/ready，nextBuildNumber=133；
候选未占用。证据保存在本机 `artifacts/validation/release-133-live-preflight.json`。

## 改动

- Codex 集成使用用户指定的 OpenAI 黑白结形标志，随明暗主题切换，保留官方资源来源及使用声明。
- AI 列表区分设备、Agent、会话三级；压缩标题和搜索区域。ZCode 远控入口简化为单行，保留打开及菜单操作。
- 刷新或短暂连接错误不再让已确认失效的 CLI 会话闪回设备卡片；保持设备身份、信任和密钥边界。
- 目录顶部返回设备页，中部按钮返回上一级目录；CLI 鼠标开关、帮助及左右侧选项布局更紧凑。
- 纳入此前授权的旧 Termux 材料清理和 GPL-3.0-only 许可整理。第三方许可保留；APK 携带项目 LICENSE/NOTICE。
- 五个 native 库及锁定 revision 不变：`e6e849172c6fca0de01ae0799eae97ad9483e687`，source_dirty=false。

## 验证

| 项目 | 结果 |
| --- | --- |
| Android app 单元测试 | 386 项通过，无失败、错误或跳过 |
| terminal-engine-native 单元测试 | 126 项通过的结果保持有效，本轮任务 UP-TO-DATE |
| 发布脚本测试 | parser、计划、版本、签名及发布门禁测试通过 |
| CLI 刷新回归 | 40 项相关测试通过；模拟器与 HOME-PC 新建/关闭 PowerShell 7 后刷新 5 次，无旧会话闪回 |
| AI 列表/图标 | 模拟器搜索、折叠、ZCode 打开/返回、明暗主题通过；最终资源为黑白 OpenAI 标志 |
| Git diff | diff --check 通过 |
| 源码敏感材料扫描 | 1,547 个文本文件的私钥及常见 token 模式扫描无命中，不等于全面安全审计 |
| LAN/NAT | 不适用，按用户指定只使用服务器转发 |

DEV 验证沿用正式基线 0.3.3/132。当前尚未发布，不能将 0.3.4/133 用作 DEV 基线。
测试证据在本机 `artifacts/validation/`：`release-android-full-tests-20261009.log`、
`release-133-build-script-tests.log`、`cli-refresh-verification-20261009.md`、
`ai-hierarchy-verification-20261009.md`、`openai-icon-verification-20261009.md`。
最后一份记录覆盖早期彩色 Codex 图标的记录；正式包使用黑白矢量图。

## 制品、源码交付与待完成门禁

本文件先随源码提交，制品绑定该提交并从干净、已推送的工作树构建。
构建后将最终 revision、APK 哈希、签名、对应源码包和复核结果写入本机
`artifacts/release/0.3.4-133/REVIEW.md`；该文件及任何凭据、制品、AppShip 状态不提交。

Android 沿用 signer SHA-256 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`，
现场证书有效至 2053-12-03。正式 APK 仍须独立核验唯一 signer、版本、哈希及人工签名审核。

按发布手册，GPLv3 APK 分发需同步提供完整对应源码。应用与 core 的匿名 GitHub 地址均未能访问；
不得把私有地址作为已完成的源码交付。本地准备应用最终提交与实际 core revision 的同级源码归档，
附 2026-10-09 许可授权说明；不修改 core 锁定 revision 冒充重编译。
公开归档包含原私有 core 源码，须取得维护者确认后再提供下载；不自动改变 GitHub 仓库可见性。

状态：测试完成；正式构建、对应源码交付、人工签名审核及 AppShip 发布结果待记录。
尚未产生本轮 AppShip release/artifact ID，不宣称已上传、发布或安装。

## 回滚

保留 Android 0.3.3/132 原始发布与制品，SHA-256
`17fba372eb74cd1d481f1011610178800e9110e1fec76185e51293c2dfd4fd6d`。
如新版本发现问题，以更高 build 发布已知良好源码；需要手动回退时先核验平台降级限制及备份，
不卸载或清空用户数据，不覆盖原发布哈希。未授权的 Windows、Server 部署和仓库公开不执行。
