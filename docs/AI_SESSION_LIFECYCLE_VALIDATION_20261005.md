# Android AI 会话关闭、结束与电脑历史回归

日期：2026-10-05。应用基线 `d5a8505bfd7c37fb33382d27a4e7a0511f58fe60` 加本轮未提交修复。
仅开发验证，未提交、推送或正式发布；未改 Windows/Server 生产代码及 terminal-core。

## 修复与行为

- 批量关闭使用当前列表实际显示的会话，包括从电脑目录返回、手机尚未打开的会话。整行与复选框统一处理选择，支持全选、取消全选和选中数量；空选择不能提交。
- 关闭手机视图持久隐藏该条目，清除本机草稿、待发队列和提醒；电脑任务继续运行。刷新不会重新显示，可从电脑历史明确重新打开。
- 结束操作按需获取控制权，以最新控制版本提交。只有收到匹配主机、会话与结束状态的回执才移出当前列表；回执丢失继续查询原操作，不重复创建结束请求。
- 已结束、已中断及挂起记录只在电脑历史中显示。查看历史不重新加入当前列表，成功恢复后才加入，并沿用现有历史授权协议。
- 历史条目提供结束/删除入口及真实标题、Agent 确认信息。删除成功后同步更新本机目录，即使本机已经关闭该会话也能生效；失败或回执不匹配不删除记录。
- 电脑历史保留 TermLink 记录，以及主机能力声明支持的 Agent 原生历史入口。目前原生读取实现为 Codex、Claude；不代表任意 Agent 都能枚举原生历史。删除 TermLink 记录不删除工作目录、文件或 Agent 原生历史。

## 自动化验证

Android Gradle 8.13，JDK 21，沿用正式 Android 基线 `0.3.2 / 130`。

```powershell
.\gradlew.bat :app:testDebugUnitTest `
  --tests 'dev.termlink.product.Ai*' `
  --tests 'dev.termlink.product.ProductResourceParityTest' :app:assembleDebug `
  '-PtermlinkVersionName=0.3.2' '-PtermlinkVersionCode=130' `
  '-PtermlinkDebugApplicationIdSuffix=.aivalidation' `
  '-PtermlinkServerUrl=https://termlink.kget.cc:8910' --no-daemon --console=plain
```

结果：52 项测试，0 失败、0 错误、0 跳过；DEV 构建成功。
包含 6 项新增生命周期回归，以及单独的结束后保留电脑历史回归。
覆盖电脑目录会话关闭/刷新/重建 Workspace、历史阅读隔离、获取控制再结束、丢失回执重试、关闭后删除、拒绝及错配回执。
最终 `git diff --check` 通过。日志：`artifacts/ai-lifecycle-final-build.log`。

## 模拟器与 Windows 适配器验证

设备 `emulator-5556`。使用独立 DEV 包及既有 `AiAcceptanceActivity`，连接仅监听
`127.0.0.1:18793` 的 Windows `AiAgentValidation --bridge --fake-agent`。
使用真实 `AiSessionManager`、进程适配器及 Codex 协议测试进程，账号与会话均为隔离测试数据。
没有操作用户真实会话、登录凭据或正在运行的正式 Windows 客户端。

| 操作 | 实测结果 |
| --- | --- |
| 从设备的 Codex 分组进入列表，再打开批量关闭 | 电脑创建的三条测试会话全部可选 |
| 点行标题、点复选框、全选/取消全选 | 状态各切换一次，数量正确，空选择禁用确认 |
| 批量关闭 A/B | 当前列表隐藏；Windows 目录仍有三条 idle 会话 |
| 从电脑历史重新打开 A | 成功进入会话，A 重新出现在当前列表 |
| 历史来源菜单 | TermLink 记录和主机声明支持的原生历史入口仍可见；本轮未读取用户真实原生历史 |
| 从历史结束指定测试会话 | Windows 返回 exited，历史显示可恢复 |
| 打开已结束记录后返回列表 | 当前列表仍不显示该记录 |
| 恢复已结束记录 | 恢复成功并重新加入当前列表，显示沿用历史授权 |
| 从会话云朵菜单再次结束 | 回执成功后自动返回列表，条目消失 |
| 从历史删除指定已结束记录 | 确认框标题/Agent 正确；Windows 目录仅剩 A/B，工作目录仍存在 |

截图/XML：`artifacts/demo-20261005/private/lifecycle-*`。
测试汇总、删除后目录和工作目录保留证据：`artifacts/ai-lifecycle-20261005/`。

## 安装与验证边界

- 模拟器已通过 `install -r` 升级 DEV `dev.termlink.harness.aivalidation` 至 `0.3.2 / 130`，保留既有数据与正式包。
- 最终 APK 为正常 HTTPS 配置，`TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST=false`，已启动正常 `ProductActivity`。
- APK：`android/app/build/outputs/apk/debug/app-debug.apk`。
- SHA-256：`C9713675A70191C9D79903E95730109AC99D01343CE04C9A709A00429BA44E13`。
- 模拟器实际强制停止/重启测试命令被自动审批拦截（只返回策略限制）；持久化重建由自动化测试覆盖，不声称此项模拟器测试通过。
- 正式域名在模拟器中的 DNS 问题仍未解决；隔离适配器测试不等于真实 E2EE Relay 联测通过。LAN/NAT 按用户要求不在范围内。
- 自动审批曾拦截包含关闭隔离桥接、移除端口映射的组合命令。用户随后明确批准清理；已收到隔离实例关闭回执 HTTP 204，并核验本机端口不再监听、对应模拟器映射已移除。正式 Windows/Server 未变更。

## 2026-10-06 发布前补充验证

网络恢复后，在同一模拟器的正常 ProductActivity 中，通过 HTTPS 服务器中转连接正在运行的
HOME-PC Windows 0.3.2.131。使用独立空白 Codex 会话 `Release132Test`，未发送生成请求。

- 新建、重命名成功；批量关闭对话框三条记录均可选，仅选择测试会话后计数为 1。
- 关闭后当前列表恢复为两条原有会话，测试会话在电脑历史仍为“就绪”。
- 从历史结束测试会话，确认框显示真实标题及 Codex；收到 Windows 回执后显示“可恢复”。
- 查看已结束记录再返回，当前列表仍只有原来的两条会话。
- 从历史删除该记录成功，搜索无匹配；Windows 工作目录仍存在，其他会话保持不变。
- 结束测试后回到中文深色设备页。截图/XML 为 `artifacts/demo-20261005/private/release132-*`。

本轮完整 Android 测试为 app 383 项、native terminal 126 项，共 509 项，均通过且无跳过。
日志 `artifacts/validation/release-132-android-tests.log`，计数 `release-132-test-counts.json`。
上述是实际 Relay 生命周期验证，补充而非改写 10 月 5 日 DNS 不可用时的验证边界；
恢复流程由前述隔离桥接和自动化测试覆盖，本轮真实 Relay 未再次恢复。LAN/NAT 不适用。
