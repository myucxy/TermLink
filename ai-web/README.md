# AI 对话 Web 界面

React/Vite 实现的 TermLink AI 对话界面，目前随 Android APK 内嵌。Windows Agent 管理协议和任务；Android 提供受控 WebView 桥、原生附件与通知等能力；Web 界面负责消息、工具、审批和输入区展示。

ZCode `desktop_remote` 加载其官方远控网页，不使用这份界面。当前不是由 Windows 动态下载、托管所有 Web 资源，相关后续工作见[当前架构](../docs/architecture/CURRENT.md)。

## 构建

以下命令在 `ai-web/` 执行，先配置满足锁文件要求的 Node.js/npm。当前 Vite 要求 Node.js `^20.19.0 || >=22.12.0`；维护环境的工具选择见[构建说明](../docs/development/BUILDING.md)。

```powershell
npm ci
npm run build
npm run verify
```

Vite 将结果写入 `android/app/src/main/assets/ai-web/`，`scripts/assets.mjs` 生成源码/资源哈希清单及运行依赖的许可声明。资源是构建输出，不需要手工复制或提交。Android Gradle 构建会拒绝缺失或过期的清单。

修改 `src/`、`scripts/`、package.json、锁文件或 Vite 配置后应重新执行 build，再运行 verify。

## 浏览器测试

`playwright.config.ts` 默认探测 Windows 的 Edge 路径。其他安装位置必须显式传入现有 Chromium/Edge 可执行文件，不能假定新电脑一定存在默认路径：

```powershell
$env:PLAYWRIGHT_CHROMIUM_EXECUTABLE = Read-Host '现有 Chromium/Edge 可执行文件的绝对路径'
npm test
```

测试使用生成后的资源，通过本机 `127.0.0.1:4173` 的夹具服务运行；端口被占用时应先处理冲突。这验证浏览器界面，不等于 Android WebView、Windows Agent 与 Relay 的真实联测。

## 目录与边界

- `src/`：渲染、Markdown/代码、消息列表、输入区及桥契约。
- `scripts/assets.mjs`：资源完整性与第三方许可打包。
- `tests/`：浏览器夹具与显式模拟器检查工具。
- [THIRD_PARTY.md](THIRD_PARTY.md)：依赖来源与授权说明。

协议夹具使用示例内容。令牌和用户文件不能注入 Web 日志或静态资源；浏览器测试不需要真实模型账号。
