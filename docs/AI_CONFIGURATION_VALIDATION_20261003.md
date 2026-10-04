# AI 输入栏与 Agent 配置（DEV，2026-10-03）

本轮仅开发、编译与测试。DEV 基线为现场确认的 AppShip 已发布版本 0.2.71 / 127，未提交、推送、上传或正式发布，服务端没有本轮改动。

## 交互与能力边界

- 输入栏左侧为附件、授权方式，右侧为模型 / 推理强度、发送。模型直接展开下拉菜单；使用 Windows 回传的每个模型 / Provider 的原生档位，不硬编码共同的 low/high 枚举。
- 选择模型后保留兼容的推理设置；Codex 不兼容时回到模型默认并提示。Claude 已有会话无法修改 effort，若新模型不兼容已固定的 effort 则提示新建会话，不偷偷重启。
- 创建 Claude 会话时可选 effort，既有会话显示只读档位。未知模型不会借用默认模型或其他 Agent 的能力。
- 授权名称、说明与确认要求来自对应 Agent。主动切换到较高授权仍须明确确认。按后续用户要求，恢复 / 分支沿用历史授权；当前 Agent 或管理策略不支持时明确报错，不静默降级。原生历史导入细节见 AI_HISTORY_VALIDATION_20261003.md。
- 配置请求继续使用控制权版本和设置版本校验；超时 / 缺失原生回执不显示配置成功。更新模型和授权分别提交。
- 原 CLI 页、终端 Dock、键盘、鼠标及传输策略不在本轮修改范围。

| Agent / 策略 | 模型与推理强度 | 授权方式 |
| --- | --- | --- |
| Codex native | 原生 model/list、每模型 supportedReasoningEfforts/defaultReasoningEffort；下一条消息生效 | 按需确认、只读沙箱、帮我批准、完全访问；会话按 configRequirements/read 过滤 |
| Claude native | initialize 模型与 supportedEffortLevels；effort 通过创建进程参数设置 | 默认审批、计划、自动编辑、不询问、跳过权限检查 |
| ZCode native | 不伪造全局模型列表；手动 Provider/模型；会话从 settings.thoughtLevel 读取；原生 setModel/setThoughtLevel | 默认执行(build)、自动编辑(edit)、自动审批(auto)、完全访问(yolo) |
| ZCode desktop_remote | 模型、授权、附件等由官方远控网页处理 | 不向网页附加 native 配置按钮 |
| Grok / 未适配 Agent | 不宣称支持未经接入和验证的协议 | 不继承其他 Agent 的菜单 |

Claude dontAsk 是拒绝需要询问且未获规则授权的操作，不等同于完全访问。Codex auto_review 保留沙箱，由原生审核器批准 / 拒绝，不等同于 never + danger-full-access。

ZCode 0.16.9 的 session/read 和设置回执中，版本在 runtime.stateRevision。每次配置前重新读取，避免生成轮次变化导致使用过期版本。实测 setMode(plan) 不改变 settings.mode.current（独立计划状态），故没有把 plan 放进权限菜单。

高级模式按已核对的本机版本启用：Codex 0.160.0、Claude Code 2.1.116、ZCode 0.16.9。未知 / 旧版本使用较窄能力，不承诺未来主版本协议自动兼容。

## 验证

- Windows AiAgentValidation：原模型发现、适配器、重复操作回执、控制租约、工作目录、附件、归档回归全部通过。ConfigurationValidation 纳入默认运行入口，覆盖模型 / Provider 差异、自定义 effort 值、失效 / 重复选项、设置版本冲突、提权确认、原生参数映射、管理策略过滤、恢复及版本降级、缺失回执拒绝。
- Android app 单元测试 355 项，失败 / 错误为 0；DEV APK 构建通过。Windows Desktop / Agent / SessionHost 暂存构建通过，文件版本均为 0.2.71.127。
- 真 Codex：获取 6 档模型能力；auto-review、full-access 的原生 turn 请求被接受并结束；低档位配置和独立文字标记回复通过。测试提示词明确不调用工具，未验证高风险工具实际执行。
- 真 Claude：获取 5 档原生 effort；默认、plan、acceptEdits、dontAsk、bypassPermissions 控制回执通过；新会话 --effort low 初始化通过；独立文字标记回复通过。不宣称会话内 effort 可变，也未验证本轮未接入的 Claude auto 模式。
- 真 ZCode：新建、读取 settings、build/edit/auto/yolo 模式回执通过。本机 CLI 未配置可用模型，真实 thoughtLevel 返回 unsupported；模型 / 推理级别切换只完成协议桩验证，尚无真实模型推理验收。桌面远控网页沿用原实现。
- emulator-5556：真实点击模型直接下拉；Codex high 创建、切换 Model B 后仅显示 ultra；完全访问确认框与回执更新；真实 Claude 创建时选择 low；320 dp 宽度、1.3 倍字体下附件 / 授权 / 模型 / 发送均保留；模型切换保留输入草稿。截图在 artifacts/validation/ai-composer-normal.png、ai-composer-narrow.png。
- 模拟器 UI 使用本机隔离 HTTP 验收桥，其中 Codex 为可控协议桩、Claude 为本机原生程序；这不替代正式 E2EE 公网回归。完成后撤销端口映射并恢复显示密度 / 字号，重新覆盖安装 HTTPS DEV，保留账号数据。

本轮协议来源：已安装 Codex app-server 生成的 schema（artifacts/validation/ai-composer-schema-review/v2）、Claude initialize/help 与原生控制回执、ZCode 安装包协议及本轮 session/read/setMode 实测。OpenAI 在线文档请求返回 403，未将在线文档算作已验证证据。仅参考协议行为，未复制第三方实现或添加依赖。

## 产物与部署状态

- Android：artifacts/validation/ai-configuration-android-dev-0.2.71-127.apk，已覆盖安装模拟器，服务地址 https://termlink2.kget.cc:8910。
- APK SHA-256：EE1E52D8446A0FD7D357BB7AE4E517979DA2D35C2CDA32666DAF47DEECD14751。
- DEV signer SHA-256：b0bcabe365a44108af2101fa3b34899dfeed5efef306639a004097060871d343，与原 DEV 相同；不是正式发布签名。
- Windows 构建源：artifacts/validation/ai-configuration-windows-dev-20261003/。用户随后明确要求替换本机客户端，已于 2026-10-03 23:02 原地覆盖正在运行的 artifacts/validation/ai-features-windows-dev-20261003/，保持其工作目录路径与登录数据；注册表指向的正式安装目录未修改。
- 源码为当前未提交工作区，包含此前开发内容，不是可正式发布的固定 revision；后续正式发布仍须按 RELEASE_RUNBOOK 重新固定版本、revision、签名和端到端门禁。

## 本机 Windows 替换验收（23:02）

- 停止旧控制 UI 后，通过已认证 IPC 的 RestartAgent 有序重启 Agent；替换前 CLI 活动会话数为 0。
- 原地覆盖 73 个构建载荷文件，全部 SHA-256 与暂存产物一致；账号文件和 AiWorkspaces 未改动。旧二进制保留于 artifacts/validation/ai-configuration-rollback-20261003-230206/，清单 payload-manifest.json。
- 新 UI PID 52628、Agent PID 31212，均在原 DEV 路径运行；UI 正常响应，版本 0.2.71.127。
- 现场 IPC：HOME-PC、AccountLinked=true、RemoteAccess=true、Relay=Connected、LastErrorCode 为空，服务地址 https://termlink2.kget.cc:8910。
- 重新检测 Codex 0.160.0 / Claude Code 2.1.116 均 available/native；ZCode 0.16.9 available/desktop_remote；Grok missing，不伪装为支持。
- 模拟器正式 ProductActivity 通过真实服务连接取得 HOME-PC 新建对话能力，展示原工作目录及“帮我批准”的确认说明。此次未创建新会话或自动批准提权；旧会话恢复与真实工具调用不计入本次部署验收。
- 操作记录 artifacts/validation/ai-configuration-win-update.json；本次为已授权的本机 DEV 替换，不是 AppShip 正式发布。
