# TermLink 0.3.2 / 130 发布记录

## 范围和状态

用户要求先完成测试并发布 Android、Windows，然后适配其他 Agent；后续适配不要求登录，
本机已安装时可测试。本轮不修改 Server，不构建或推送 Docker。只验收服务器转发，LAN/NAT 不适用。

现场 AppShip 两端 stable 为 0.3.1/129，发布状态为 published/ready，nextBuildNumber=130；
Docker Hub 最高版本为 0.3.0，远端 v0.3* tag 为空。本轮固定 **0.3.2/130**，重试不重新加号。
源码 revision 以本文件首次提交及 Production artifact-manifest 中的完整 revision 为准。

当前状态：功能及影响范围测试已完成，等待从已提交且推送的干净 revision 构建 Production 制品。
构建、签名审核、AppShip 上传与客户端部署分别记录，不能把候选构建当成已发布。

## 发布内容

- Windows 新增 Grok ACP：安装路径探测、原生模型与独立推理强度、新建权限、流式正文/思考、
  工具、单次允许/拒绝、停止及 TermLink 历史恢复。动态配置以原生 ACK 为准。
- 修复 Grok scalar config 参数、新建时过早校验模型能力和进程退出覆盖原始错误；
  限流/网络重试显示明确状态，用户仍可停止，正文恢复后清除提示。
- 修复 Claude 单块最终消息与流式块关联错误造成的双份正文。已存在的重复历史不自动改写。
- Android 紧凑 AI 标题/工具栏、按内容宽度右对齐的用户气泡、新建 Agent 彩色选项；
  模型/推理强度/授权选择器靠左并按内容收缩。CLI 布局不变。

## 验证与证据

- Windows AI 全量验证通过，包含 Codex/Claude/ZCode、Grok ACP、配置、授权、隔离、附件、
  归档、流式去重、工具合并和错误保留。Agent 编译 0 warning / 0 error。
- Android 配置/模型/策略/workspace 19 项及展示/Web 桥 11 项测试通过；AI Web 12 项
  Playwright 通过，包含窄屏/大字号、消息气泡、工具入口和重试时停止。
- Windows 桌面 headless 通过；隔离安装、原地升级、签名载荷、路径迁移拒绝和卸载验证通过。
- 本机 Grok 1.0.46：真实创建、模型/强度切换、精确单条正文、DPAPI 归档、进程重启与
  原上下文续聊、停止通过。隔离文件的 reject_once/allow_once 结果按磁盘实际内容验证。
- emulator-5556 使用 0.3.1/129 最新 DEV，实际新建气泡/模型/强度、默认目录已验收；
  通过真实 Relay 到临时新版 HOME-PC Agent 创建 Grok，收到正文、思考和工具，生成文件。
  新 Claude 会话在同一 Relay 链路仅显示一条正文。loopback UI 验证单独记录。
- 上游 Grok 曾间歇性 HTTP 429，取消后有限复测成功；保留失败记录，不宣称账号不会再次限流。

日志/截图均在 ignored 的 artifacts/validation/：ai-grok-*、ai-claude-relay-*。
本轮 DEV 沿用 0.3.1/129；待每端正式发布并验证后才更新 DEV 基线。

## 签名、回滚与边界

使用既有 Android signer SHA-256 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`、
Windows signer SHA-1 `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`，构建时重新核对有效期与私钥、
独立验证制品哈希/版本/签名连续性及 RFC 3161 时间戳。Windows 为既有自签发布者，非公开 CA 信任。
AppShip 精确制品签名审核见 artifacts/release/0.3.2-130/REVIEW.md（构建完成后生成）。

回滚使用保留的 0.3.1/129 制品及既有签名；Android 不卸载清数据，版本降级限制需要走兼容的
更高 build 修复发布或受控测试流程。Windows 保留用户数据及安装器回滚证据；Server 不变。
联测临时 Windows Agent 结束后恢复 D:/Dev/Tool/TermLink 运行时，原安装文件没有替换。

Grok 外部原生历史导入、会话中途切换权限、原生识图、问答/usage 扩展未宣称支持；
后台真机与全部弱网组合未全覆盖。Gemini/OpenCode/Qwen/Kimi/Copilot/Cursor/Pi 等适配在本轮
双端正式发布之后进行，不在当前制品中伪报可用。
