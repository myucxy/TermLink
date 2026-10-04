# AI Agent 联合测试记录（2026-10-03，开发验证）

本次按用户要求使用安卓模拟器、本机 Windows、termlink2 和本机 Docker 联测。不是正式发布：源码仍为 `faca6618685cbc7924f8b176115d2601d0eb6dd5` 上的未提交修改，未上传 AppShip 或 Docker Hub，未创建新 tag。

## 环境与最终状态

| 项目 | 本次状态 |
| --- | --- |
| 地址 / 测试账号 | `https://termlink2.kget.cc:8910` / `admin`；凭据不记入本文 |
| Android | `emulator-5556`，独立 Debug 包 `dev.termlink.harness.aivalidation`，普通 `ProductActivity`，正式包未覆盖 |
| Windows | HOME-PC，运行 `artifacts/validation/ai-joint-windows/` 中暂存的真实 Desktop + Agent；正式安装目录 `D:\Dev\Tool\TermLink` 未覆盖 |
| 链路 | 正常账号、设备身份、E2EE 与 Relay；无 acceptance bridge、无 adb reverse；不代表 AI LAN/NAT 已实现 |
| 服务端 | 本地开发镜像 `termlink-server:dev-ai-20261003`，版本 `dev-ai-20261003`，revision 标记 `faca661…-dirty-ai` |
| 镜像 ID | `sha256:9ed3776e59e8eb1362daa9c2cbc4a379bd50d848f5bae5c00cded21069bd8092` |
| 容器 / 数据 | `termlink-local-server-1` 与原 Postgres 均 healthy；保留原环境、端口、网络、数据库卷；migration 仍为 50 |
| 健康检查 | 本地及公网 `/readyz` 返回 200 |

Docker Desktop 曾因磁盘不足后卡住。得到用户明确许可后重启 Desktop，并仅终止 `docker-desktop` WSL 实例恢复引擎；未清理卷。开发镜像用现场验证过的旧本地基础镜像及当前 Go 二进制构建，没有完成正式多平台镜像发布门禁。

Windows 已切换到上述测试账号与地址，仍运行暂存开发客户端，远程访问已恢复开启。测试开始前无活动用户终端；Claude 测试会话已通过安卓确认结束。保留测试账号和开发部署便于后续 ZCode 联测，不代表正式客户端已升级。

## 本轮修复

1. Claude 默认 `mimo-v2.5-pro` 在当前配置的 API 返回 400/1211（模型不存在）。使用已有有效模型 `glm-5.3-flash[1m]` 验证成功，未修改用户全局 Claude 配置或复制其凭据。
2. Claude `set_model` 的成功控制回执允许不含内部 `response` 字段。原适配器对 Undefined 调用 `Clone()` 导致读取循环退出；现在接受空成功回执，并新增协议回归检查。
3. 新 Desktop 的已有控制命令对旧 Agent 保持 V12，AI 检测使用 V13；签名与序列化使用同一协议版本。已真实连接旧已安装 Agent，并加入双版本命名管道检查。
4. Android AI 请求超时是 `TimeoutCancellationException`，原先被误当成页面取消并终止轮询。现在超时隐藏旧正文、标记连接待确认且继续轮询；变更请求保留原 operation ID 供查询回执。真正页面取消仍传播，不伪造请求成功。
5. Windows AI 设置页副标题已改为 AI Agent；headless 与实际窗口均检查通过。

## 验证结果

| 场景 | 结果 / 证据（位于 `artifacts/validation/`） |
| --- | --- |
| Claude CLI 与生产适配器 | 显式模型取得真实回复；`ai-claude-model-retest.log`、`ai-claude-control-regression.log` |
| Windows 自动检测 / 重新检测 | Codex、Claude、ZCode 协议可用，Grok 未安装；`ai-joint-windows-ui.json`、`ai-joint-windows-final-ui.json` |
| 安卓新建 Claude、发送与回复 | 经真实 E2EE Relay 得到 `TERMLINK_CLAUDE_E2EE_OK`；`ai-joint-claude.png`、`ai-joint-claude-reply.xml` |
| 关闭手机视图再从设备页打开 | 电脑会话继续存在，重开恢复同一正文；`ai-joint-reopened.xml` |
| 控制权释放 / 重新获取 | 只读与控制中随 Windows 回执变化；`ai-joint-released.xml`、`ai-joint-reacquired.xml` |
| 前后台与 AI / CLI 切换 | 返回恢复对应视图；`ai-joint-foreground.xml`、`ai-joint-cli.png` |
| 关闭远程访问 / 恢复 | 断开 43.8 秒隐藏正文，恢复 43.8 秒重新取回原回复；`ai-joint-connection.log`、`ai-joint-disconnected.xml`、`ai-joint-reconnected.xml` |
| 结束 Claude | 安卓确认后收到已结束状态；`ai-joint-claude-ended.xml` |
| CLI 输入回归 | 真实 ConPTY 会话输入一次 `123`，仅输出 `TERMLINK_CLI_ACK:123`，键盘、Dock 可操作；`ai-joint-cli.png` |
| Android 单元测试 | app 331 + core-network 337，失败 / 错误 / 跳过均为 0；`ai-joint-final-android.log` |
| Windows 回归 | Agent / Desktop 编译零错误；适配器、IPC V13 与旧版本、Avalonia headless 通过；`ai-joint-win-build.log`、`ai-joint-final-ipc.log`、`ai-joint-final-desktop.log` |
| Go | realtime / realtimewire 测试通过；`ai-go-test.log` |
| CLI 源码保留 | `ProductActivity.kt` 从 `private fun SessionsPage(` 到文件末尾与设计基线一致（忽略换行）；`git diff --check` 通过 |

断线状态不是即时探测：目前 AI 请求统一有 40 秒超时，恢复可能等待上一个失联请求结束。发送超时只显示结果待确认，不自动生成另一个操作编号。CLI 临时测试宿主的等待脚本达到 250 秒清理期限后终止了自己创建的进程树，未生成预期的退出输出文件；输入结果以截图为准，不将该清理超时记为“自然退出通过”。已确认该测试宿主 PID 不再存在。

尚未完成：ZCode 真实模型回复、正式签名制品及升级回滚发布门禁。协议 fixture 的审批/问题矩阵不等同于每家真实模型都触发过这些交互。范围更新（2026-10-04）：用户指定仅使用 Relay，不再安排真实多设备 LAN / 跨 NAT 测试；此前未覆盖的直连场景记为当前不适用，不计为通过或待办。

## ZCode 测试准备

已检测到本机 ZCode 0.16.9，协议初始化、创建与订阅可运行。当前独立 CLI 没有可用默认模型，真实发送返回 `CONFIGURATION_ERROR`。

1. 在同一 Windows 用户下给 ZCode 配置**独立 CLI 可用的 API Provider 与模型**，并在 ZCode 自身确认简单消息可得到回答。API Key 留在电脑的 ZCode 配置中。
2. 提供已配置的 **Provider ID、模型 ID、测试工作目录**即可；不要在聊天中发送 Key 或 token。
3. 在 Android 新建 AI 对话中选择 HOME-PC / ZCode，填写目录与这些 ID，再验证消息、审批、中断与结束。

当前适配器复用安装包 provider 定义与用户 `~/.zcode/v2/provider_config.json`；无需把供应商代码或密钥复制到 TermLink。仅在 ZCode 桌面登录成功，不一定使独立协议模型可用：要求 `interaction/requestProviderRuntimeHeaders` 的桌面账号模型尚未接入，明确返回需要桌面授权，不能通过重打包 TermLink core 绕过。

Claude 后续测试可在创建表单直接填 `glm-5.3-flash[1m]`；留空仍会使用电脑原默认配置，原模型无效问题不会被 TermLink 自动改写。

## 本地备份与回滚

`artifacts/backups/ai-joint-20261003/` 保存数据库 dump 与原容器配置的 Windows DPAPI CurrentUser 加密备份；完成加解密哈希校验。`windows-account/` 保存切换前四个账号配置文件的同类备份。需要本机原 Windows 用户才能解密；这些是开发验证备份，不替代发布运行手册要求的 age 备份及异机恢复演练。

原服务容器保留为 `termlink-local-server-ai-rollback-20261002T230348Z`，旧镜像 ID 为 `sha256:6f16ac1c9bfa5d6f6822aa7902dafeb0c057b50768b2816ec0e1cd505fc46127`。服务回滚顺序：停止当前开发容器并另名保留，将旧容器改回 `termlink-local-server-1` 后启动，复核数据库连接及 `/readyz`；本次未执行数据库迁移，不需恢复数据库才能回滚程序。

Windows 回退需先退出暂存 Desktop 与 Agent，再在原用户下解密并恢复账号备份，启动 `D:\Dev\Tool\TermLink` 的正式客户端。Android 正式包未改动，可保留或卸载独立测试包。备份、镜像、日志与截图不提交。
