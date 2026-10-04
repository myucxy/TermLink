# 双端交互修复与发布跟踪

用户于 2026-10-04 授权：修复交互审查问题，测试、复核后发布。包含之前未发布的安卓目录 AI 入口、设备 Agent 分组与产品图标。

范围：Android、Windows、嵌入式 AI Web；仅 Relay。Server 如无代码变更不重发镜像。厂商推送项目缺失，本轮完善准确状态与接入说明，不宣称完成离线推送。

- [x] A01/A02 草稿与队列保护，包括旧 WebView 的原生回退界面
- [x] A03 服务器配置结果，失败保留输入和错误
- [x] A04 按电脑隔离待确认操作及持久化恢复，重试保持原 operationId
- [x] A05 返回与网页弹层，目录逐级返回
- [x] A06/A07 错误与危险操作对象
- [x] A08 账号拥有文件下载任务；进度、取消、重试、打开与分享
- [x] A09 更新下载取消/收起；取消会中止阻塞 HTTP 请求
- [x] A10 搜索与文件筛选隔离
- [x] A11 提醒权限与实际能力；离线推送仍未配置
- [x] A12 离线设备紧凑摘要，不显示离线会话正文
- [x] A13 常用动作、列表状态、设备按钮与 Web 双语、44px Web 触摸目标
- [x] W01/W03 未知状态与刷新结果
- [x] W02 显示真实注册名称，不提供无效的编辑入口
- [x] W04 确认层默认取消、Tab、Escape 与焦点恢复
- [x] W05 网页授权取消/重开与晚到结果防护
- [x] W06/W07 真实 CLI 标题、搜索、已加载 AI 元数据和目录、Agent 官方帮助入口
- [x] W08/W09 下载/验证/待安装阶段，安装前任务影响、错误通知保留
- [x] 本轮新增：AI 文件及预览打开所在目录、定位高亮、返回原会话
- [x] Android app 375 项单测、Relay 目录/AI 契约 4 项测试、Web 9 项回归
- [x] Windows Agent 全解决方案、桌面 Windows 目标构建，Control/Update/Adapter/Headless 验证
- [x] Windows 安装器自动化（隔离临时安装根、既有注册信息恢复），真实更新下载及缓存篡改拒绝
- [x] 模拟器与 HOME-PC：真实生成文件目录跳转、预览、返回、下载切页、分享与文件哈希一致
- [x] 复核、线上版本查询、固定源码及发布计划（见 0.3.1/129 计划）
- [ ] Production 构建、签名连续性验证、精确制品审核
- [ ] Android 再 Windows AppShip 发布和更新接口验证

本轮固定 0.3.1/129，见 [发布计划](RELEASE_0.3.1_129.md)。Docker 已发布 0.3.0，客户端旧候选 0.3.0/128 不覆盖。

## 2026-10-04 现场复核与剩余门禁

后续进展：用户开启代理后，Docker Hub 全部标签只读查询恢复，最高为 0.3.0；AppShip 与远端 Git 已重新核对。网络占号门禁解除，下一步 Production 构建和精确制品审核。以下保留首次阻塞记录。

AppShip 两端 stable 当前均为 `0.2.71/127`（published、artifact ready，nextBuildNumber=128）；
远端 `v0.3*` Git tag 查询为空。DEV 一直保持 `0.2.71/127`，独立 Android 包名 `.aivalidation`。
旧 `0.3.0/128` 候选不覆盖。Docker Hub REST、Registry 和既有本机代理的只读查询发生连接超时/EOF，
本次无法现场确认完整版本占用，**正式版选号、Production 构建和 AppShip 上传暂停在此门禁**。
不复用上一轮 digest 代替本次在线核验，不把本次测试当作已发布。Server 本轮无修改，无需重编译镜像。

继续发布需要：恢复 Docker Hub 只读核验、固定版本/build及源码 revision，构建双端精确 Production 制品、
独立验签和哈希核验、完成人工签名审核后按 Android → Windows 顺序上传。没有新制品人工批准记录。

证据位于 ignored `artifacts/validation/interaction-*`、`ai-folder.png`、`ai-download-complete.png`。
Windows 安装器测试使用独立临时测试证书，不是正式发布 signer；既有签名连续性在真实已发布安装包
下载测试中验证，正式候选仍需重新独立核验。没有替换当前 HOME-PC 正在使用的 Windows 安装。

边界：Windows AI 列表仅列运行时已加载记录，未伪称完整扫描所有原生历史；CLI 尚无权威 cwd 元数据。
常用 Android/Web 文案已本地化，部分高级诊断、Agent 提供的描述及历史原生辅助页面仍有中文文案。
目录定位高亮已加载的目标条目，超出目录分页时保留既有加载更多/搜索。ZCode 外部网页按原策略处理。
FCM/小米项目尚缺配置；本轮不声明系统终止 App 后可收到推送。LAN/NAT 不适用（用户指定 Relay Only）。
