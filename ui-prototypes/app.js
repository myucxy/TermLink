"use strict";

const STORAGE_KEY = "termlink.product-ui.v2";

const notes = {
  "android-account": {
    title: "账号与登录",
    copy: "先验证服务器身份和注册策略，再通过安全浏览器完成 PKCE 登录或注册。",
    items: [
      "App 不直接保存账号密码。",
      "注册、邮箱验证和找回密码由服务端身份能力提供。",
      "登录成功后仍需完成设备注册或批准。",
    ],
  },
  "android-devices": {
    title: "设备首页",
    copy: "用一层列表直接呈现 PC、延迟、权限、会员配额和活动会话，不在顶部堆叠 KPI 卡片。",
    items: [
      "状态同时使用圆点、文字和时间。",
      "免费版明确显示 1/1 PC 与 2/3 实时会话。",
      "搜索入口也是统一命令面板入口。",
    ],
  },
  "android-terminal": {
    title: "实时终端",
    copy: "终端画布占据绝大部分空间，控制租约、E2EE 和连接状态始终可见。",
    items: [
      "只读和控制中有明确文字状态。",
      "Extra Keys 不遮挡终端内容。",
      "输入失败不会自动重复执行命令。",
    ],
  },
  "android-replay": {
    title: "历史回放",
    copy: "回放页与实时页保持同一终端心智模型，但明确标注 Replay 并移除输入能力。",
    items: [
      "时间轴标记快照与连接事件。",
      "搜索只处理手机端已解密内容。",
      "拖动时间轴从最近快照恢复。",
    ],
  },
  "android-security": {
    title: "可信设备",
    copy: "设备权限、密钥指纹和最后活动时间集中呈现，撤销动作进入二次确认。",
    items: [
      "当前设备单独分组。",
      "权限以管理、控制、只读三档显示。",
      "同账号登录不等于自动获得访问权。",
    ],
  },
  "android-membership": {
    title: "会员与配额",
    copy: "先展示实际配额用量，再说明月付会员能解决什么限制；具体价格由购买渠道本地化显示。",
    items: [
      "Free：1 台 PC、3 个同时实时会话。",
      "购买和恢复购买均有稳定入口。",
      "E2EE 和基础安全能力不作为付费墙。",
    ],
  },
  "android-settings": {
    title: "设置与安全",
    copy: "账户、服务器、语言、主题、可信设备和数据删除集中在设置页。",
    items: [
      "语言支持跟随系统、简体中文和 English。",
      "退出当前设备不删除账号。",
      "删除账号与云端数据需要重新认证和二次确认。",
    ],
  },
  "windows-tray": {
    title: "托盘弹层",
    copy: "托盘只承载即时状态、新建会话、会员配额和紧急停止，复杂配置进入主窗口。",
    items: [
      "远端查看和控制状态始终可见。",
      "停止远程访问是独立高优先级动作。",
      "本地会话不依赖 Relay 正常运行。",
    ],
  },
  "windows-sessions": {
    title: "会话管理",
    copy: "主窗口为桌面密度优化，Session 列表直接显示 Shell、远程状态与最近活动。",
    items: [
      "运行中、已退出、已归档使用同一列表。",
      "状态与动作按行渐进披露。",
      "底部显示 Agent 与缓冲健康。",
    ],
  },
  "windows-access": {
    title: "远程访问",
    copy: "本地策略、当前查看者和控制确认规则放在同一页面，强调本地用户始终优先。",
    items: [
      "策略只有允许控制、仅查看、仅本机。",
      "当前远端连接可立即断开。",
      "本地键盘收回控制默认开启。",
    ],
  },
  "windows-settings": {
    title: "Agent 设置",
    copy: "常规、Profile、历史、网络和高级选项分层，默认页面不暴露协议级参数。",
    items: [
      "记录保留时间显示加密说明。",
      "更新和诊断位于可预期位置。",
      "设置不影响本地 Shell 权限。",
    ],
  },
  "windows-membership": {
    title: "账户与会员",
    copy: "Windows 端显示账号统一权益和配额，但购买入口跳转到合规渠道完成。",
    items: [
      "付费只扩展远程 PC 与实时会话额度。",
      "到期不终止本地 Shell。",
      "超过配额时允许用户选择保留资源。",
    ],
  },
  "server-overview": {
    title: "运行概览",
    copy: "运维首页只回答连接是否健康、路由是否延迟、容器是否正常，不展示虚假的增长指标。",
    items: [
      "所有流量数值明确标注为密文。",
      "正文不可见是持续显示的系统属性。",
      "事件只包含不可逆标识和结果。",
    ],
  },
  "server-connections": {
    title: "连接路由",
    copy: "连接表聚焦角色、路由、延迟、ACK Lag 和密文吞吐，用于排查实时链路。",
    items: [
      "不能展开查看 Payload。",
      "慢消费者和高延迟使用语义状态。",
      "连接 ID 与设备 ID 均做截断。",
    ],
  },
  "server-deployment": {
    title: "Docker 部署",
    copy: "容器、版本、备份和安全基线集中在部署页，支持运维人员快速判断是否可升级。",
    items: [
      "镜像版本与 Digest 同时展示。",
      "恢复演练和备份状态同级。",
      "Secret 值永不在控制台回显。",
    ],
  },
  "server-audit": {
    title: "安全审计",
    copy: "审计时间线按动作和结果组织，不记录终端正文、命令、Token 或完整网络标识。",
    items: [
      "筛选围绕安全域，不围绕内容。",
      "事件包含 Trace ID 便于关联。",
      "删除动作记录密文对象数量和证明 ID。",
    ],
  },
  "server-billing": {
    title: "订阅与权益",
    copy: "计费控制台版本化管理套餐、渠道映射、购买验证和 Entitlement，客户端只读取已发布目录。",
    items: [
      "Free 1 台 PC、3 个会话只是首发默认配置。",
      "名称、周期、额度、排序和 Product ID 均可配置。",
      "套餐发布有版本，存量订阅不会被静默降权。",
    ],
  },
  "server-identity": {
    title: "账号门户",
    copy: "统一承载注册、登录、找回密码、首次强制改密和 Android/Windows 授权回调。",
    items: [
      "默认使用内置 Keycloak，也允许切换企业 OIDC。",
      "首次管理员临时密码仅由初始化容器输出一次。",
      "全部回调使用公开 HTTPS 域名和 PKCE。",
    ],
  },
  "server-accounts": {
    title: "账号与角色",
    copy: "注册策略、账号状态、角色绑定、登录撤销和身份服务健康集中管理。",
    items: [
      "超级管理员角色变更必须重新认证。",
      "禁用账号不会删除安全审计。",
      "没有 SMTP 时默认使用邀请注册。",
    ],
  },
  "server-redemptions": {
    title: "兑换码管理",
    copy: "按批次生成、一次性导出、停用和审计会员兑换码，服务端不保存可恢复明文。",
    items: [
      "兑换使用事务、幂等和速率限制。",
      "完整码仅在创建后显示和导出一次。",
      "兑换权益来源明确标记为 REDEMPTION_CODE。",
    ],
  },
};

const english = Object.freeze({
  "明亮主题": "Light theme",
  "深色主题": "Dark theme",
  "切换到明亮主题": "Switch to light theme",
  "切换到深色主题": "Switch to dark theme",
  "Android 客户端": "Android client",
  "移动端控制中心": "Mobile control center",
  "终端内容优先，状态透明，远程输入始终经过显式控制。": "Terminal content comes first, status stays visible, and remote input always requires explicit control.",
  "账号与登录": "Account & sign-in",
  "设备首页": "Devices home",
  "实时终端": "Live terminal",
  "历史回放": "History replay",
  "可信设备": "Trusted devices",
  "会员与配额": "Membership & quotas",
  "设置与安全": "Settings & security",
  "基准": "Baseline",
  "导航": "Navigation",
  "3 个一级入口": "3 primary destinations",
  "终端": "Terminal",
  "沉浸式子页面": "Immersive child page",
  "下午好，Alex": "Good afternoon, Alex",
  "你的终端": "Your terminals",
  "搜索 PC、会话或操作": "Search PCs, sessions, or actions",
  "实时会话 2/3": "Live sessions 2/3",
  "设备": "Devices",
  "排序": "Sort",
  "添加更多 PC": "Add another PC",
  "免费版已使用 1/1 台 PC": "Free plan uses 1/1 PC",
  "升级月付会员后继续添加": "Upgrade to add more PCs",
  "活动": "Activity",
  "设置": "Settings",
  "只读": "Read only",
  "正在确定要还原的项目…": "Determining projects to restore...",
  "↓ 最新输出": "↓ Latest output",
  "申请 30 秒控制权": "Request control for 30 seconds",
  "历史回放 · 26 分钟": "History replay · 26 minutes",
  "· 已退出，code 0": "· Exited, code 0",
  "复制": "Copy",
  "安全": "Security",
  "4 台可信设备": "4 trusted devices",
  "所有设备密钥状态正常": "All device keys are healthy",
  "当前设备": "Current device",
  "其他设备": "Other devices",
  "筛选": "Filter",
  "批准新设备": "Approve new device",
  "账户": "Account",
  "当前方案": "Current plan",
  "适合在一台 PC 上轻量使用": "For light use on one PC",
  "免费": "Free",
  "本月权益": "Current entitlement",
  "实时计算": "Calculated live",
  "已连接 PC": "Connected PCs",
  "已满": "Full",
  "同时显示的实时会话": "Concurrent live sessions",
  "剩余 1": "1 remaining",
  "推荐": "Recommended",
  "月付会员": "Monthly membership",
  "自动续费，可随时取消": "Auto-renews, cancel anytime",
  "连接多台 Windows PC": "Connect multiple Windows PCs",
  "同时显示更多实时终端会话": "View more live terminal sessions",
  "会员配额由账户统一同步": "Account-wide quota sync",
  "保留全部安全与 E2EE 能力": "All security and E2EE features remain included",
  "查看月付价格并订阅": "View monthly price and subscribe",
  "实际价格、税费和货币由购买渠道在确认前显示。": "The billing provider shows the final price, taxes, and currency before confirmation.",
  "恢复购买": "Restore purchases",
  "会员兑换码": "Membership redemption code",
  "兑换由服务器验证，可在确认前预览方案与有效期。": "The server validates the code and previews the plan and term before confirmation.",
  "输入兑换码": "Enter code",
  "布局意图": "Layout intent",
  "账号、服务器、语言、主题、可信设备和数据删除集中在设置页。": "Account, server, language, theme, trusted devices, and data deletion live in Settings.",
  "语言支持跟随系统、简体中文和 English。": "Language supports system default, Simplified Chinese, and English.",
  "退出当前设备不删除账号。": "Signing out this device does not delete the account.",
  "删除账号与云端数据需要重新认证和二次确认。": "Deleting the account and cloud data requires reauthentication and confirmation.",
  "连接正常 · 支持开放注册": "Connected · Open registration supported",
  "更改": "Change",
  "账号操作": "Account action",
  "登录": "Sign in",
  "注册账号": "Create account",
  "邮箱或用户名": "Email or username",
  "密码": "Password",
  "登录并绑定此设备": "Sign in and link this device",
  "忘记密码？": "Forgot password?",
  "用户名": "Username",
  "邮箱": "Email",
  "我已阅读服务条款和隐私政策": "I have read the Terms and Privacy Policy",
  "创建账号": "Create account",
  "安全浏览器与 PKCE": "Secure browser and PKCE",
  "密码由服务器身份页面处理，App 只接收授权结果。": "The server identity page handles passwords; the app only receives the authorization result.",
  "管理": "Manage",
  "语言": "Language",
  "跟随系统或手动切换": "Follow system or choose manually",
  "跟随系统": "System default",
  "简体中文": "Simplified Chinese",
  "主题": "Theme",
  "服务器": "Server",
  "登录与安全": "Sign-in & security",
  "密码、MFA、活跃登录": "Password, MFA, active sessions",
  "退出当前设备": "Sign out this device",
  "删除账号与云端数据": "Delete account and cloud data",
  "本地控制平面": "Local control plane",
  "托盘负责即时状态与紧急操作，完整窗口负责会话、访问和设置。": "The tray handles immediate status and emergency actions; the full window manages sessions, access, and settings.",
  "托盘弹层": "Tray panel",
  "会话管理": "Session management",
  "远程访问": "Remote access",
  "Agent 设置": "Agent settings",
  "账户与会员": "Account & membership",
  "托盘": "Tray",
  "主窗口": "Main window",
  "优先级": "Priority",
  "本地用户最高": "Local user always wins",
  "远程策略": "Remote policy",
  "活动会话": "Active sessions",
  "无远程查看者": "No remote viewers",
  "＋ 新建受管终端": "+ New managed terminal",
  "升级 ›": "Upgrade ›",
  "停止所有远程访问": "Stop all remote access",
  "打开 TermLink": "Open TermLink",
  "网页账户中心": "Web account center",
  "系统浏览器授权": "System browser authorization",
  "重新登录": "Sign in again",
  "兑换会员码": "Redeem code",
  "管理账户": "Manage account",
  "查看月付方案": "View monthly plan",
  "已有会员？刷新权益": "Already subscribed? Refresh entitlement",
  "无正文运维台": "Content-blind operations console",
  "管理部署、连接和安全事件，只显示路由元数据与性能，不显示终端内容。": "Manage deployments, connections, and security events using metadata and performance only; terminal content is never shown.",
  "运行概览": "Runtime overview",
  "连接路由": "Connection routing",
  "Docker 部署": "Docker deployment",
  "安全审计": "Security audit",
  "订阅与权益": "Subscriptions & entitlements",
  "账号门户": "Account portal",
  "账号与角色": "Accounts & roles",
  "兑换码管理": "Redemption codes",
  "公开数据": "Visible data",
  "仅元数据": "Metadata only",
  "正文": "Content",
  "始终不可见": "Never visible",
  "环境": "Environment",
  "概览": "Overview",
  "连接": "Connections",
  "部署": "Deployment",
  "设备与授权": "Devices & grants",
  "管理员": "Administrator",
  "超级管理员": "Super administrator",
  "所有服务正常": "All services healthy",
  "账号门户": "Account portal",
  "登录、注册与账户安全": "Sign-in, registration & account security",
  "身份服务": "Identity service",
  "注册策略": "Registration policy",
  "开放注册 · 邮箱验证": "Open registration · Email verification",
  "回调": "Callback",
  "Docker 首次管理员": "Initial Docker administrator",
  "首次启动只在 identity-bootstrap 日志中输出一次性随机密码。": "The initial random password is printed once in the identity-bootstrap logs.",
  "查看强制改密流程": "View forced password change",
  "网页账号操作": "Web account action",
  "注册": "Register",
  "登录 TermLink": "Sign in to TermLink",
  "创建账号并验证邮箱": "Create account and verify email",
  "我同意服务条款与隐私政策": "I agree to the Terms and Privacy Policy",
  "账号与角色": "Accounts & roles",
  "新建账号": "New account",
  "Keycloak 正常": "Keycloak healthy",
  "Issuer 与回调已验证": "Issuer and callbacks verified",
  "开放注册": "Open registration",
  "要求邮箱验证": "Email verification required",
  "6 个角色绑定": "6 role bindings",
  "1 个超级管理员": "1 super administrator",
  "搜索用户名或邮箱": "Search username or email",
  "全部角色": "All roles",
  "全部状态": "All statuses",
  "角色": "Role",
  "状态": "Status",
  "最近登录": "Last sign-in",
  "正常": "Healthy",
  "待验证": "Pending verification",
  "超级管理员首次登录必须修改临时密码。角色变更、禁用账号和撤销登录都要求重新认证并写入安全审计。": "The bootstrap administrator must change the temporary password at first sign-in. Role changes, account suspension, and session revocation require reauthentication and an audit record.",
  "会员兑换码": "Membership redemption codes",
  "导出一次性 CSV": "Export one-time CSV",
  "新建批次": "New batch",
  "可兑换": "Available",
  "启用批次": "active batches",
  "已兑换": "Redeemed",
  "本月": "this month",
  "异常尝试": "Suspicious attempts",
  "速率限制已生效": "Rate limiting active",
  "搜索批次、前缀或渠道": "Search batch, prefix, or channel",
  "批次": "Batch",
  "权益": "Entitlement",
  "使用情况": "Usage",
  "有效期": "Valid until",
  "启用": "Active",
  "已用完": "Exhausted",
  "完整兑换码只在创建后导出一次。服务端仅保存 HMAC/Hash；日志、列表和审计只显示批次与截断前缀。": "Full redemption codes are exported once after creation. The server stores only HMAC/hash values; logs, lists, and audits show only the batch and truncated prefix.",
  "快速导航": "Quick navigation",
  "确认操作": "Confirm action",
  "取消": "Cancel",
  "确认": "Confirm",
  "详情": "Details",
});

const supplementalEnglish = Object.freeze({
  "。服务器不会通过其他域名索取密码。": ". The server never asks for your password on another domain.",
  "创建你的 TermLink 账号": "Create your TermLink account",
  "验证邮箱后，可在 Android、Windows 和网页端使用同一账号。": "Verify your email to use the same account on Android, Windows, and the web.",
  "Android 安全授权": "Android secure authorization",
  "Windows 系统浏览器授权": "Windows system browser authorization",
  "在安全浏览器中继续": "Continue in the secure browser",
  "正在等待浏览器授权": "Waiting for browser authorization",
  "请求权限": "Requested scopes",
  "设备绑定": "Device binding",
  "密码只提交到统一账号门户。客户端只接收短期授权码，并验证 state、nonce 和 PKCE。": "Your password is submitted only to the account portal. The client receives a short-lived authorization code and verifies state, nonce, and PKCE.",
  "模拟授权返回": "Simulate authorization callback",
  "授权完成": "Authorization complete",
  "账号已登录，等待设备批准": "Signed in, device approval pending",
  "Windows Agent 已重新登录": "Windows Agent signed in again",
  "账号验证成功 · 设备状态 PENDING": "Account verified · Device status PENDING",
  "同账号登录不会自动获得 PC 访问权。请在现有 MANAGER 设备上批准此设备。": "Signing in to the same account does not grant PC access. Approve this device from an existing MANAGER device.",
  "账号验证成功 · Token 已交回当前 Agent": "Account verified · Token returned to this Agent",
  "系统浏览器不向 Agent 暴露密码；现有可信设备密钥保持不变。": "The system browser never exposes the password to the Agent; existing trusted-device keys stay unchanged.",
  "完成": "Done",
  "登录与设备注册已完成": "Sign-in and device registration complete",
  "Windows 登录已更新": "Windows sign-in updated",
  "等待可信设备批准。": "Waiting for approval from a trusted device.",
  "账号权益和设备授权已刷新。": "Account entitlements and device authorization have been refreshed.",
  "首次登录安全要求": "First sign-in security requirement",
  "修改超级管理员临时密码": "Change the super administrator temporary password",
  "identity-bootstrap 只在 Docker 首次启动日志输出一次 24 位随机密码。该密码建议 24 小时过期，且首次登录不能跳过改密。": "identity-bootstrap prints a 24-character random password only once in the initial Docker startup logs. It should expire within 24 hours, and the first sign-in cannot skip the password change.",
  "当前临时密码": "Current temporary password",
  "新密码": "New password",
  "至少 14 个字符": "At least 14 characters",
  "再次输入新密码": "Re-enter new password",
  "必须与新密码一致": "Must match the new password",
  "改密成功后撤销全部临时登录会话，并要求配置 MFA 与恢复方式。": "After the password change, revoke every temporary session and require MFA and a recovery method.",
  "修改密码并继续": "Change password and continue",
  "管理员安全设置": "Administrator security setup",
  "临时密码已失效": "Temporary password invalidated",
  "密码已更新 · 临时会话已撤销": "Password updated · Temporary sessions revoked",
  "立即配置 TOTP 多因素认证": "Configure TOTP multi-factor authentication now",
  "生成一次性恢复码": "Generate one-time recovery codes",
  "超级管理员不允许关闭重新认证、审计记录或高风险操作保护。": "The super administrator cannot disable reauthentication, audit logging, or high-risk action protection.",
  "进入 Server Console": "Open Server Console",
  "超级管理员初始化完成": "Super administrator setup complete",
  "首次临时密码不会再次显示。": "The initial temporary password will not be shown again.",
  "创建 TermLink 账号": "Create a TermLink account",
  "确认注册并验证邮箱": "Confirm registration and verify email",
  "邮箱验证": "Email verification",
  "必需": "Required",
  "密码只提交到服务端身份页面，Android App 不直接保存或处理密码。": "The password is submitted only to the server identity page. The Android app never stores or handles it directly.",
  "验证邮箱": "Verify email",
  "验证链接已发送": "Verification link sent",
  "alex@example.com · 等待验证": "alex@example.com · Verification pending",
  "链接 30 分钟内有效。验证完成后会返回 Android，并继续设备注册流程。": "The link is valid for 30 minutes. After verification, return to Android and continue device registration.",
  "重新发送": "Resend",
  "模拟已验证": "Simulate verification",
  "账号恢复": "Account recovery",
  "重置密码": "Reset password",
  "账号邮箱": "Account email",
  "无论账号是否存在，页面都返回相同结果，避免泄露注册状态。重置链接 30 分钟内有效且只能使用一次。": "The page returns the same result whether the account exists or not, preventing account discovery. The reset link is valid for 30 minutes and can be used once.",
  "发送重置链接": "Send reset link",
  "如账号存在，重置邮件已发送": "If the account exists, a reset email has been sent",
  "请检查邮箱并在 30 分钟内完成操作。": "Check your inbox and finish within 30 minutes.",
  "验证邮件已重新发送": "Verification email resent",
  "请稍后再试或检查垃圾邮件目录。": "Try again shortly or check your spam folder.",
  "服务器连接": "Server connection",
  "选择 TermLink 服务器": "Choose a TermLink server",
  "公开服务器地址": "Public server address",
  "有效": "Valid",
  "已发现": "Discovered",
  "客户端只接受 HTTPS。192.168.9.28:8080 是 Lucky 上游测试地址，不作为客户端公开地址。": "The client accepts HTTPS only. 192.168.9.28:8080 is the Lucky upstream test address, not a public client endpoint.",
  "验证并保存": "Verify and save",
  "服务器连接已验证": "Server connection verified",
  "公开 HTTPS、OIDC 与回调配置均可用。": "Public HTTPS, OIDC, and callback configuration are available.",
  "账号已创建": "Account created",
  "name@example.com · 等待验证": "name@example.com · Verification pending",
  "验证链接 30 分钟内有效。验证后可返回 Android、Windows 或网页账户中心继续授权。": "The verification link is valid for 30 minutes. After verification, return to Android, Windows, or the web account center to continue authorization.",
  "重新发送验证邮件": "Resend verification email",
  "验证邮件已发送": "Verification email sent",
  "30 分钟内有效。": "Valid for 30 minutes.",
  "Docker 首次初始化": "Initial Docker setup",
  "超级管理员启动流程": "Super administrator bootstrap flow",
  "identity-bootstrap 创建 admin 和 24 位随机临时密码。": "identity-bootstrap creates admin and a 24-character random temporary password.",
  "密码只在首次启动日志输出一次，普通重启不再输出。": "The password is printed once in the initial startup logs and never printed again on normal restarts.",
  "临时密码建议 24 小时过期，首次登录强制改密。": "The temporary password should expire within 24 hours, and the first sign-in requires a password change.",
  "改密后撤销临时会话，并引导配置 MFA 与恢复码。": "After the password change, revoke temporary sessions and guide MFA and recovery-code setup.",
  "日志不得输出 Token、OIDC Secret、数据库密码或改密后的密码。": "Logs must never contain tokens, OIDC secrets, database passwords, or the changed password.",
  "模拟首次登录": "Simulate first sign-in",
  "↑↓ 选择": "Up/Down Select",
  "＋ 新建会话": "+ New session",
  "✓ 非 root 用户": "✓ Non-root user",
  "✓ 固定 Digest": "✓ Pinned digest",
  "✓ 镜像签名": "✓ Image signature",
  "✓ 只读根文件系统": "✓ Read-only root filesystem",
  "0 个慢消费者": "0 slow consumers",
  "1 / 1 台": "1 / 1",
  "1 个查看者": "1 viewer",
  "1 天": "1 day",
  "1/1 台 PC · 2/3 个实时会话": "1/1 PCs · 2/3 live sessions",
  "12 秒前": "12 seconds ago",
  "14 个连接": "14 connections",
  "18 分钟前": "18 minutes ago",
  "2 / 3 个": "2 / 3",
  "2 分钟前": "2 minutes ago",
  "2 个已启用 · Catalog v7 · 客户端读取已发布版本": "2 active · Catalog v7 · clients read the published version",
  "2 个有查看者": "2 with viewers",
  "2-32 个字符": "2-32 characters",
  "3 个启用批次": "3 active batches",
  "30 天": "30 days",
  "4 分钟前": "4 minutes ago",
  "4 台设备 · 密钥状态正常": "4 devices · keys healthy",
  "7 天": "7 days",
  "8 / 8 通过": "8 / 8 passed",
  "9 个 Session Route": "9 session routes",
  "版本与诊断": "Version & diagnostics",
  "帮助": "Help",
  "保留时间": "Retention",
  "备份": "Back up",
  "备份与恢复": "Backup & restore",
  "本地会话不依赖 Relay 正常运行。": "Local sessions keep working independently of the relay.",
  "本地记录": "Local records",
  "本地输入收回控制": "Local input reclaims control",
  "本地用户优先": "Local user first",
  "本机": "This PC",
  "本机策略": "Local policy",
  "本月 42 个": "42 this month",
  "不包含命令、终端正文和密文": "Excludes commands, terminal content, and ciphertext",
  "不记录": "Do not record",
  "不记录命令、终端正文、访问令牌、完整 IP、公钥或密文 Payload。": "Never records commands, terminal content, access tokens, full IPs, public keys, or ciphertext payloads.",
  "不显示完整支付信息": "Full payment details are never shown",
  "部署、连接、安全审计形成三个清晰工作区。": "Deployment, connections, and security audit are distinct workspaces.",
  "部署健康": "Deployment health",
  "策略": "Policy",
  "查看 compose.yml": "View compose.yml",
  "查看部署": "View deployment",
  "查看通知": "View notifications",
  "查看运行状态": "View runtime status",
  "常规": "General",
  "成功": "Success",
  "打开设置": "Open settings",
  "当前版本": "Current version",
  "当前连接": "Current connections",
  "导出元数据": "Export metadata",
  "导出诊断包": "Export diagnostics",
  "登录 Windows 后启动 TermLink": "Start TermLink after Windows sign-in",
  "登录即表示你访问的是": "By signing in, you confirm you are visiting",
  "登录与设备": "Sign-in & devices",
  "订阅 acct_18…af 的更多操作": "More actions for subscription acct_18…af",
  "订阅 acct_42…0d 的更多操作": "More actions for subscription acct_42…0d",
  "订阅 acct_7c…21 的更多操作": "More actions for subscription acct_7c…21",
  "断开": "Disconnect",
  "队列延迟 0.8s": "Queue delay 0.8s",
  "兑换码状态": "Redemption status",
  "多 PC 远程访问": "Multi-PC remote access",
  "发布 Catalog v8": "Publish Catalog v8",
  "方案": "Plan",
  "服务端安全审计": "Server security audit",
  "服务端订阅与权益": "Server subscriptions & entitlements",
  "服务端兑换码管理": "Server redemption code management",
  "服务端管理台页面": "Server Console pages",
  "服务端连接路由": "Server connection routing",
  "服务端运行概览": "Server runtime overview",
  "服务端账号与角色": "Server accounts & roles",
  "服务健康": "Service health",
  "刚刚": "Just now",
  "更多": "More",
  "更多并发实时会话": "More concurrent live sessions",
  "购买渠道": "Billing provider",
  "购买验证与 Webhook 正常": "Purchase verification and webhook healthy",
  "关闭弹窗": "Close dialog",
  "关闭命令面板": "Close command palette",
  "关闭详情": "Close details",
  "管理套餐": "Manage plans",
  "还有 1 个实时显示名额": "1 live display slot remaining",
  "缓冲 18.2 MB / 512 MB": "Buffer 18.2 MB / 512 MB",
  "会话": "Sessions",
  "会员到期不会终止 Windows 本地 Shell。超过免费配额的 PC 和实时订阅将暂停远程连接，用户可选择保留哪一台 PC 和三个会话。": "Membership expiry never terminates local Windows shells. PCs and live subscriptions over the free quota pause remote access, and the user chooses which PC and three sessions to keep.",
  "会员与兑换码": "Membership & redemption codes",
  "活动时间": "Last activity",
  "计费周期": "Billing period",
  "计费状态": "Billing status",
  "加密历史对象": "Encrypted history objects",
  "加密事件块已保存": "Encrypted event block saved",
  "价格在购买页显示": "Price shown at checkout",
  "检测到本地键盘输入后立即撤销租约": "Revoke the lease immediately when local keyboard input is detected",
  "检查更新": "Check for updates",
  "简体中文 / EN": "Simplified Chinese / EN",
  "简体中文 / English": "Simplified Chinese / English",
  "角色筛选": "Role filter",
  "今天": "Today",
  "今天 03:00 · 842 MB": "Today 03:00 · 842 MB",
  "仅本地": "Local only",
  "仅本机": "This PC only",
  "仅允许查看": "View only",
  "旧手机": "Old phone",
  "决定已批准设备何时可以取得输入租约。": "Controls when approved devices may obtain an input lease.",
  "客户端不能自行创建套餐或修改额度。订阅到期不会结束 Windows 本地 Shell，只暂停超过降级目标套餐额度的远程连接。": "Clients cannot create plans or change quotas. Subscription expiry never ends local Windows shells; it only pauses remote connections over the fallback plan quota.",
  "控制确认": "Control confirmation",
  "控制台只显示连接 ID、角色、路由数量、延迟和密文吞吐，无法查看终端标题、命令或输出。": "The console shows only connection IDs, roles, route counts, latency, and ciphertext throughput. Terminal titles, commands, and output are unavailable.",
  "跨 Android/Windows 同步权益": "Entitlements sync across Android and Windows",
  "来源": "Source",
  "连接 conn_09bc 的更多操作": "More actions for connection conn_09bc",
  "连接 conn_a3f8 的更多操作": "More actions for connection conn_a3f8",
  "连接 conn_c291 的更多操作": "More actions for connection conn_c291",
  "连接与路由": "Connections & routing",
  "路由": "Routes",
  "路由延迟 P95": "Route latency P95",
  "每台远程设备第一次控制本机时询问": "Ask when each remote device controls this PC for the first time",
  "免费权益适用于整个账号，不影响本地终端运行。": "Free entitlements apply to the whole account and never affect local terminal operation.",
  "明亮": "Light",
  "默认权益 · 已启用": "Default entitlement · Active",
  "配置检查": "Configuration checks",
  "启动与设备": "Startup & device",
  "清理": "Clear",
  "区域 / 延迟": "Region / latency",
  "区域 ap-east": "Region ap-east",
  "全部活动": "All activity",
  "全部事件": "All events",
  "删除": "Delete",
  "上限 2 GB": "Limit 2 GB",
  "设备名称": "Device name",
  "深色": "Dark",
  "审计日志只记录安全元数据": "Audit logs contain security metadata only",
  "升级后解锁更多 PC 和更多同时显示的终端会话。现有会话、密钥和安全策略保持不变。": "Upgrade for more PCs and concurrently displayed terminal sessions. Existing sessions, keys, and security policies stay unchanged.",
  "生命周期策略已启用": "Lifecycle policy enabled",
  "实时流量": "Live traffic",
  "实时密文流量折线图": "Live ciphertext traffic line chart",
  "首次控制需要本机确认。本地键盘输入会立即收回控制权。": "First control requires confirmation on this PC. Local keyboard input immediately reclaims control.",
  "首次控制需要确认": "First control requires confirmation",
  "，TermLink 不会通过其他域名索取密码。": ", and TermLink never asks for your password on another domain.",
  "安全连接你的设备": "Securely connect your devices",
  "保持登录": "Keep me signed in",
  "查看初始化流程": "View initialization flow",
  "登录后继续设备批准，账号验证不会自动授予任何 PC 的访问权。": "After sign-in, continue device approval. Account verification never grants access to a PC automatically.",
  "欢迎回来": "Welcome back",
  "继续访问你的设备、会员权益与 Server Console。": "Continue to your devices, membership, and Server Console.",
  "你的每台终端": "every terminal you own",
  "确认浏览器地址为": "Confirm the browser address is",
  "使用统一账号密码": "Use your account password",
  "首次启动 Docker？": "First Docker startup?",
  "受管会话": "Managed sessions",
  "授权": "Grants",
  "授权协议": "Authorization protocol",
  "输入设备、会话或操作": "Enter a device, session, or action",
  "输入租约": "Input leases",
  "输入租约已释放": "Input lease released",
  "刷新": "Refresh",
  "搜索": "Search",
  "搜索会话": "Search sessions",
  "搜索连接 ID": "Search connection ID",
  "搜索命令": "Search commands",
  "搜索入口也是统一命令面板入口。": "Search also opens the unified command palette.",
  "搜索账户或订单引用": "Search account or order reference",
  "随时回到": "Return anytime to",
  "所有 PC · 管理": "All PCs · Manage",
  "所有流量数值明确标注为密文。": "Every traffic metric is explicitly labeled as ciphertext.",
  "所有敏感操作仍需确认": "Sensitive actions still require confirmation",
  "套餐目录": "Plan catalog",
  "套餐与权益由服务端决定，价格由渠道展示": "Plans and entitlements come from the server; providers show prices",
  "提交后在系统安全浏览器完成授权；App 只接收授权结果，不保存密码。": "After submission, authorization continues in the secure system browser. The app receives only the result and never stores the password.",
  "添加第二台 PC 需要会员": "A membership is required for a second PC",
  "停止远程访问是独立高优先级动作。": "Stopping remote access is a dedicated high-priority action.",
  "同步购买凭据": "Sync purchase credentials",
  "同时实时显示会话": "Concurrent live displays",
  "同时远程显示会话": "Concurrent remote sessions",
  "吞吐": "Throughput",
  "托盘只承载即时状态、新建会话和紧急停止，复杂配置进入主窗口。": "The tray contains immediate status, new sessions, and emergency stop. Advanced configuration lives in the main window.",
  "现在": "Now",
  "协议 v1 · ConPTY 可用 · Profile 已注册": "Protocol v1 · ConPTY available · Profiles registered",
  "新建套餐": "New plan",
  "续费日期": "Renewal date",
  "选择客户端": "Choose client",
  "一个账号，安全连接": "One account, securely connecting",
  "一级导航始终只有设备、活动、设置。": "Primary navigation stays limited to Devices, Activity, and Settings.",
  "已归档": "Archived",
  "已过期": "Expired",
  "已禁用": "Disabled",
  "已连接到 relay.example.com": "Connected to relay.example.com",
  "已使用 182 MB": "182 MB used",
  "已停用": "Inactive",
  "已退出": "Exited",
  "已限制": "Limited",
  "已验证的 TermLink Server": "Verified TermLink Server",
  "以当前用户身份启动，不提升 Shell 权限": "Starts as the current user without elevating shell privileges",
  "用户": "User",
  "用一层列表直接呈现 PC、延迟、权限和活动会话，不在顶部堆叠 KPI 卡片。": "A single list shows PCs, latency, permissions, and active sessions without stacking KPI cards above it.",
  "远程访问策略": "Remote access policy",
  "远程客户端看到的名称": "Name shown to remote clients",
  "远程状态": "Remote status",
  "远端查看和控制状态始终可见。": "Remote viewing and control states stay visible.",
  "远端正在访问本机时始终显示。": "Always visible while a remote device is accessing this PC.",
  "月付": "Monthly",
  "允许查看和控制": "Allow viewing and control",
  "允许已批准设备查看和控制": "Allow approved devices to view and control",
  "允许已批准设备控制": "Allow approved devices to control",
  "运维首页只回答连接是否健康、路由是否延迟、容器是否正常，不展示虚假的业务增长指标。": "The operations overview answers whether connections, routing latency, and containers are healthy without invented growth metrics.",
  "运行恢复演练": "Run restore drill",
  "运行中 2": "Running 2",
  "在多台 PC 间无缝切换": "Move seamlessly between PCs",
  "在线": "Online",
  "在线连接": "Online connections",
  "暂停回放": "Pause replay",
  "账号": "Account",
  "诊断": "Diagnostics",
  "正文保持端到端加密": "Content remains end-to-end encrypted",
  "正文不可见是持续显示的系统属性。": "Content invisibility remains a persistent system property.",
  "正文使用 DPAPI 包装的数据密钥加密": "Content is encrypted with a DPAPI-wrapped data key",
  "正在查看": "Viewing",
  "正在查看 build-release · 28 ms": "Viewing build-release · 28 ms",
  "只保存必要的订单引用与验证状态": "Stores only necessary order references and verification state",
  "指纹 8A:72:1F:…:C9": "Fingerprint 8A:72:1F:…:C9",
  "至少 12 个字符": "At least 12 characters",
  "终端快捷键": "Terminal shortcuts",
  "终端输出": "Terminal output",
  "主导航": "Primary navigation",
  "状态筛选": "Status filter",
  "状态同时使用圆点、文字和时间。": "Status uses an indicator, text, and time together.",
  "最近 30 分钟 · 仅密文大小": "Last 30 minutes · ciphertext size only",
  "最近订阅": "Recent subscriptions",
  "最近事件": "Recent events",
  "最近一次验证恢复 6 天前": "Last verified restore 6 days ago",
  "最新": "Latest",
  "昨天": "Yesterday",
  "admin 临时密码仅在 identity-bootstrap 日志显示一次。": "The admin temporary password appears once in the identity-bootstrap logs.",
  "Agent 1.0.0 · 已是最新版本": "Agent 1.0.0 · up to date",
  "Agent 与 2 个 Session Host 正常": "Agent and 2 Session Hosts are healthy",
  "Android · 2 天前": "Android · 2 days ago",
  "Android · 30 天前": "Android · 30 days ago",
  "Android · 当前设备": "Android · current device",
  "Android 布局说明": "Android layout notes",
  "Android 会员与配额": "Android membership & quotas",
  "Android 可信设备": "Android trusted devices",
  "Android 历史回放": "Android history replay",
  "Android 设备首页": "Android devices home",
  "Android 设置与安全": "Android settings & security",
  "Android 实时终端": "Android live terminal",
  "Android 页面": "Android pages",
  "Android 与 Windows 都通过系统浏览器完成授权。密码始终留在账号门户，设备访问仍需单独批准。": "Android and Windows authorize through the system browser. Passwords stay in the account portal, while device access still requires separate approval.",
  "Android 账号与登录": "Android account & sign-in",
  "Compose 服务": "Compose services",
  "Enter 打开": "Enter to open",
  "Free · 1/1 台 PC": "Free · 1/1 PC",
  "Google Play · Web Billing Provider · 最近同步 18 秒前": "Google Play · Web Billing Provider · synced 18 seconds ago",
  "HOME-PC · 只读": "HOME-PC · read only",
  "https://termlink.kget.cc:8910/account · 系统浏览器授权": "https://termlink.kget.cc:8910/account · system browser authorization",
  "Manager device_3a…f1 批准 Android device_8c…29": "Manager device_3a…f1 approved Android device_8c…29",
  "OFFICE-PC · 可控制": "OFFICE-PC · control allowed",
  "OIDC Callback 连续失败触发短时限制": "Repeated OIDC callback failures triggered a temporary limit",
  "PC 上限": "PC limit",
  "PC 重新连接": "PC reconnected",
  "Pixel 10 正在查看": "Pixel 10 is viewing",
  "PowerShell 7 · 18 分钟": "PowerShell 7 · 18 minutes",
  "Pro · 30 天": "Pro · 30 days",
  "Pro · 7 天": "Pro · 7 days",
  "Pro · 90 天": "Pro · 90 days",
  "product: pro_monthly · 已启用": "product: pro_monthly · active",
  "RTDN 已连接": "RTDN connected",
  "Secret 与安全基线": "Secrets & security baseline",
  "Server Console 安全审计导航": "Server Console security audit navigation",
  "Server Console 布局说明": "Server Console layout notes",
  "Server Console 部署导航": "Server Console deployment navigation",
  "Server Console 订阅权益导航": "Server Console subscription navigation",
  "Server Console 兑换码导航": "Server Console redemption navigation",
  "Server Console 概览导航": "Server Console overview navigation",
  "Server Console 连接导航": "Server Console connection navigation",
  "Server Console 账号导航": "Server Console account navigation",
  "Session se_16…4d 的 18 个加密对象已删除": "18 encrypted objects for Session se_16…4d were deleted",
  "Session se_72…c1 向 device_8c…29 授予输入租约": "Session se_72…c1 granted an input lease to device_8c…29",
  "TermLink 账号门户": "TermLink account portal",
  "termlink.kget.cc:8910 · TLS 正常": "termlink.kget.cc:8910 · TLS healthy",
  "TLS 正常": "TLS healthy",
  "Webhook 签名已验证": "Webhook signature verified",
  "Windows · 刚刚在线": "Windows · online now",
  "Windows 会话导航": "Windows session navigation",
  "Windows 会话管理": "Windows session management",
  "Windows 托盘弹层": "Windows tray panel",
  "Windows 远程访问": "Windows remote access",
  "Windows 远程访问导航": "Windows remote access navigation",
  "Windows 账户与会员": "Windows account & membership",
  "Windows 账户与会员导航": "Windows account & membership navigation",
  "Windows Agent 布局说明": "Windows Agent layout notes",
  "Windows Agent 设置": "Windows Agent settings",
  "Windows Agent 设置导航": "Windows Agent settings navigation",
  "Windows Agent 页面": "Windows Agent pages",
  "WSL Ubuntu · 1 小时": "WSL Ubuntu · 1 hour",
  "按批次生成、一次性导出、停用和审计会员兑换码，服务端不保存可恢复明文。": "Generate, export once, disable, and audit membership codes by batch. The server stores no recoverable plaintext.",
  "本地策略、当前查看者和控制确认规则放在同一页面，强调本地用户始终优先。": "Local policy, current viewers, and control confirmation live together, keeping the local user in control.",
  "本地键盘收回控制默认开启。": "Local keyboard input reclaims control by default.",
  "不能展开查看 Payload。": "Payload cannot be expanded or viewed.",
  "策略只有允许控制、仅查看、仅本机。": "Policy options are control allowed, view only, and local only.",
  "常规、Profile、历史、网络和高级选项分层，默认页面不暴露协议级参数。": "General, Profiles, History, Network, and Advanced settings are layered without exposing protocol parameters by default.",
  "超过配额时允许用户选择保留资源。": "Users choose which resources to keep when over quota.",
  "超级管理员角色变更必须重新认证。": "Super administrator role changes require reauthentication.",
  "当前设备单独分组。": "The current device has its own group.",
  "当前远端连接可立即断开。": "The current remote connection can be disconnected immediately.",
  "到期不终止本地 Shell。": "Expiry never terminates local shells.",
  "登录成功后仍需完成设备注册或批准。": "Successful sign-in is followed by device registration or approval.",
  "底部显示 Agent 与缓冲健康。": "Agent and buffer health stay visible at the bottom.",
  "兑换权益来源明确标记为 REDEMPTION_CODE。": "Redeemed entitlements are explicitly sourced as REDEMPTION_CODE.",
  "兑换使用事务、幂等和速率限制。": "Redemption is transactional, idempotent, and rate limited.",
  "付费只扩展远程 PC 与实时会话额度。": "Payment only expands remote PC and live session quotas.",
  "更新和诊断位于可预期位置。": "Updates and diagnostics are placed predictably.",
  "购买和恢复购买均有稳定入口。": "Purchase and restore-purchase actions remain easy to find.",
  "恢复演练和备份状态同级。": "Restore drills and backup status have equal prominence.",
  "回放页与实时页保持同一终端心智模型，但明确标注 Replay 并移除输入能力。": "Replay keeps the live terminal mental model while clearly labeling Replay and removing input.",
  "计费控制台版本化管理套餐、渠道映射、购买验证和 Entitlement，客户端只读取已发布目录。": "The billing console versions plans, provider mappings, purchase verification, and entitlements. Clients read only published catalogs.",
  "记录保留时间显示加密说明。": "Retention controls explain encryption.",
  "禁用账号不会删除安全审计。": "Disabling an account never deletes its security audit.",
  "镜像版本与 Digest 同时展示。": "Image version and digest are shown together.",
  "连接 ID 与设备 ID 均做截断。": "Connection and device IDs are truncated.",
  "连接表聚焦角色、路由、延迟、ACK Lag 和密文吞吐，用于排查实时链路。": "The connection table focuses on roles, routes, latency, ACK lag, and ciphertext throughput for realtime troubleshooting.",
  "慢消费者和高延迟使用语义状态。": "Slow consumers and high latency use semantic statuses.",
  "没有 SMTP 时默认使用邀请注册。": "Invitation-only registration is the default when SMTP is unavailable.",
  "免费版明确显示 1/1 PC 与 2/3 实时会话。": "The free plan clearly shows 1/1 PCs and 2/3 live sessions.",
  "名称、周期、额度、排序和 Product ID 均可配置。": "Names, periods, quotas, order, and product IDs are configurable.",
  "默认使用内置 Keycloak，也允许切换企业 OIDC。": "Built-in Keycloak is the default, with external enterprise OIDC supported.",
  "全部回调使用公开 HTTPS 域名和 PKCE。": "Every callback uses the public HTTPS origin and PKCE.",
  "权限以管理、控制、只读三档显示。": "Permissions are displayed as manage, control, or read only.",
  "容器、版本、备份和安全基线集中在部署页，支持运维人员快速判断是否可升级。": "Containers, versions, backups, and security baseline share the deployment page for quick upgrade decisions.",
  "筛选围绕安全域，不围绕内容。": "Filters follow security domains, not content.",
  "删除动作记录密文对象数量和证明 ID。": "Deletion records ciphertext object counts and proof IDs.",
  "设备权限、密钥指纹和最后活动时间集中呈现，撤销动作进入二次确认。": "Device permissions, key fingerprints, and last activity are grouped together, with revocation requiring confirmation.",
  "设置不影响本地 Shell 权限。": "Settings never change local shell privileges.",
  "审计时间线按动作和结果组织，不记录终端正文、命令、Token 或完整网络标识。": "The audit timeline is organized by action and result and never records terminal content, commands, tokens, or full network identifiers.",
  "时间轴标记快照与连接事件。": "The timeline marks snapshots and connection events.",
  "事件包含 Trace ID 便于关联。": "Events include trace IDs for correlation.",
  "事件只包含不可逆标识和结果。": "Events contain only irreversible identifiers and outcomes.",
  "首次管理员临时密码仅由初始化容器输出一次。": "Only the bootstrap container prints the initial administrator temporary password, once.",
  "输入失败不会自动重复执行命令。": "Failed input never causes commands to be retried automatically.",
  "搜索只处理手机端已解密内容。": "Search processes only content decrypted on the phone.",
  "套餐发布有版本，存量订阅不会被静默降权。": "Plan publication is versioned, and existing subscriptions are never silently downgraded.",
  "同账号登录不等于自动获得访问权。": "Signing in to the same account never grants access automatically.",
  "统一承载注册、登录、找回密码、首次强制改密和 Android/Windows 授权回调。": "The portal unifies registration, sign-in, password recovery, forced first password change, and Android/Windows callbacks.",
  "托盘只承载即时状态、新建会话、会员配额和紧急停止，复杂配置进入主窗口。": "The tray contains immediate status, new sessions, membership quota, and emergency stop. Advanced configuration lives in the main window.",
  "拖动时间轴从最近快照恢复。": "Dragging the timeline restores from the nearest snapshot.",
  "完整码仅在创建后显示和导出一次。": "Full codes are displayed and exported once after creation.",
  "先验证服务器身份和注册策略，再通过安全浏览器完成 PKCE 登录或注册。": "Verify the server identity and registration policy before completing PKCE sign-in or registration in the secure browser.",
  "先展示实际配额用量，再说明月付会员能解决什么限制；具体价格由购买渠道本地化显示。": "Show actual quota usage first, then explain what membership expands. The billing provider localizes the price.",
  "用一层列表直接呈现 PC、延迟、权限、会员配额和活动会话，不在顶部堆叠 KPI 卡片。": "A single list shows PCs, latency, permissions, membership quota, and active sessions without stacked KPI cards.",
  "运维首页只回答连接是否健康、路由是否延迟、容器是否正常，不展示虚假的增长指标。": "The operations overview answers connection, routing latency, and container health without invented growth metrics.",
  "运行中、已退出、已归档使用同一列表。": "Running, exited, and archived sessions share one list.",
  "账户、服务器、语言、主题、可信设备和数据删除集中在设置页。": "Account, server, language, theme, trusted devices, and data deletion are grouped in Settings.",
  "只读和控制中有明确文字状态。": "Read-only and controlling states use explicit text.",
  "终端画布占据绝大部分空间，控制租约、E2EE 和连接状态始终可见。": "The terminal canvas dominates while control lease, E2EE, and connection state stay visible.",
  "主窗口为桌面密度优化，Session 列表直接显示 Shell、远程状态与最近活动。": "The main window uses desktop density, and the session list directly shows shell, remote state, and recent activity.",
  "注册、邮箱验证和找回密码由服务端身份能力提供。": "Registration, email verification, and password recovery are provided by the server identity service.",
  "注册策略、账号状态、角色绑定、登录撤销和身份服务健康集中管理。": "Registration policy, account status, role bindings, session revocation, and identity health are managed together.",
  "状态与动作按行渐进披露。": "Status and actions are progressively disclosed per row.",
  "App 不直接保存账号密码。": "The app never stores account passwords directly.",
  "E2EE 和基础安全能力不作为付费墙。": "E2EE and baseline security are never paywalled.",
  "Extra Keys 不遮挡终端内容。": "Extra Keys never cover terminal content.",
  "Free 1 台 PC、3 个会话只是首发默认配置。": "Free with 1 PC and 3 sessions is only the launch default.",
  "Free：1 台 PC、3 个同时实时会话。": "Free: 1 PC and 3 concurrent live sessions.",
  "Secret 值永不在控制台回显。": "Secret values are never echoed in the console.",
  "Windows 端显示账号统一权益和配额，但购买入口跳转到合规渠道完成。": "Windows displays account-wide entitlements and quotas while purchases continue through the configured compliant provider.",
});

const defaults = {
  theme: null,
  language: "system",
  product: "android",
  screens: {
    android: "android-account",
    windows: "windows-tray",
    server: "server-overview",
  },
  control: { granted: false, expiresAt: 0 },
  replay: { speed: 4, playing: false, progress: 68 },
  windows: {
    policyIndex: 0,
    remoteEnabled: true,
    viewerConnected: true,
    sessionFilter: "运行中",
    settingsTab: "常规",
    deviceName: "OFFICE-PC",
    autoStart: true,
    retention: "7 天",
  },
};

function mergeState(saved) {
  return {
    ...defaults,
    ...(saved || {}),
    screens: { ...defaults.screens, ...(saved?.screens || {}) },
    control: { ...defaults.control, ...(saved?.control || {}) },
    replay: { ...defaults.replay, ...(saved?.replay || {}) },
    windows: { ...defaults.windows, ...(saved?.windows || {}) },
  };
}

function loadState() {
  try {
    return mergeState(JSON.parse(localStorage.getItem(STORAGE_KEY) || "null"));
  } catch {
    return mergeState(null);
  }
}

const state = loadState();
const validScreens = Object.fromEntries(
  Object.keys(state.screens).map((product) => [
    product,
    new Set(
      [...document.querySelectorAll(`[data-product="${product}"] [data-screen]`)].map(
        (screen) => screen.dataset.screen,
      ),
    ),
  ]),
);

const toastRegion = document.querySelector("#toast-region");
const commandDialog = document.querySelector("#command-dialog");
const commandInput = document.querySelector("#command-input");
const commandResults = document.querySelector("#command-results");
const appDialog = document.querySelector("#app-dialog");
const dialogEyebrow = document.querySelector("#dialog-eyebrow");
const dialogTitle = document.querySelector("#dialog-title");
const dialogContent = document.querySelector("#dialog-content");
const dialogConfirm = document.querySelector("#dialog-confirm");
const detailDrawer = document.querySelector("#detail-drawer");
const drawerBackdrop = document.querySelector("#drawer-backdrop");
const drawerClose = document.querySelector("#drawer-close");
const drawerEyebrow = document.querySelector("#drawer-eyebrow");
const drawerTitle = document.querySelector("#drawer-title");
const drawerContent = document.querySelector("#drawer-content");
const staticTranslationRecords = [];

let commandMatches = [];
let commandIndex = 0;
let activeDialogAction = null;
let drawerReturnFocus = null;
let controlTimer = null;

function activeLanguage() {
  if (state.language === "system") {
    return navigator.language.toLowerCase().startsWith("zh") ? "zh-CN" : "en-US";
  }
  return state.language;
}

function localize(value) {
  if (activeLanguage() !== "en-US") return value;
  return english[value] || supplementalEnglish[value] || value;
}

function translatedText(original) {
  const trimmed = original.trim();
  if (!trimmed) return original;
  const translated = localize(trimmed);
  if (translated === trimmed) return original;
  return `${original.match(/^\s*/)?.[0] || ""}${translated}${original.match(/\s*$/)?.[0] || ""}`;
}

function captureStaticTranslations() {
  const excluded = ".theme-toggle, .language-toggle, [data-note-title], [data-note-copy], [data-note-list], #toast-region, #app-dialog, #detail-drawer";
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let node;
  while ((node = walker.nextNode())) {
    if (!node.textContent.trim() || node.parentElement?.closest(excluded)) continue;
    staticTranslationRecords.push({ type: "text", node, zh: node.textContent });
  }
  document.querySelectorAll("[aria-label], [placeholder], [title]").forEach((element) => {
    if (element.closest(excluded)) return;
    ["aria-label", "placeholder", "title"].forEach((attribute) => {
      if (element.hasAttribute(attribute)) {
        staticTranslationRecords.push({ type: "attribute", element, attribute, zh: element.getAttribute(attribute) });
      }
    });
  });
}

function translateTree(root) {
  if (activeLanguage() !== "en-US" || !root) return;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  let node;
  while ((node = walker.nextNode())) node.textContent = translatedText(node.textContent);
  root.querySelectorAll?.("[aria-label], [placeholder], [title]").forEach((element) => {
    ["aria-label", "placeholder", "title"].forEach((attribute) => {
      if (element.hasAttribute(attribute)) element.setAttribute(attribute, localize(element.getAttribute(attribute)));
    });
  });
}

function renderLanguage() {
  const language = activeLanguage();
  document.documentElement.lang = language === "en-US" ? "en" : "zh-CN";
  document.documentElement.dataset.locale = language;
  staticTranslationRecords.forEach((record) => {
    if (record.type === "text") record.node.textContent = language === "en-US" ? translatedText(record.zh) : record.zh;
    else record.element.setAttribute(record.attribute, language === "en-US" ? localize(record.zh) : record.zh);
  });
  const button = document.querySelector(".language-toggle");
  button.textContent = language === "en-US" ? "中文" : "EN";
  button.setAttribute("aria-label", language === "en-US" ? "切换到简体中文" : "Switch to English");
  button.setAttribute("aria-pressed", String(language === "en-US"));
  appDialog.querySelector(".overlay-close")?.setAttribute("aria-label", language === "en-US" ? "Close dialog" : "关闭弹窗");
  drawerClose.setAttribute("aria-label", language === "en-US" ? "Close details" : "关闭详情");
  document.querySelectorAll("[data-locale-select]").forEach((select) => { select.value = state.language; });
  const activeWebAuthTab = document.querySelector('[data-screen="server-identity"] [data-web-auth-tab].is-active');
  if (activeWebAuthTab) {
    switchAuthPanel(
      activeWebAuthTab.closest('[data-screen="server-identity"]'),
      activeWebAuthTab.dataset.webAuthTab,
      "[data-web-auth-tab]",
      "[data-web-auth-panel]",
      "webAuthTab",
    );
  }
  document.querySelectorAll("[data-product].is-active").forEach((workspace) => updateNote(workspace, state.screens[workspace.dataset.product]));
  renderTheme();
}

function toggleLanguage() {
  state.language = activeLanguage() === "en-US" ? "zh-CN" : "en-US";
  renderLanguage();
  saveState();
  showToast(state.language === "en-US" ? "Language updated" : "语言已更新", state.language === "en-US" ? "English" : "简体中文");
}

function saveState() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function showToast(title, detail = "", tone = "info") {
  const toast = document.createElement("div");
  toast.className = "app-toast";
  toast.dataset.tone = tone;
  toast.innerHTML = `<span></span><span><strong>${escapeHtml(localize(title))}</strong>${
    detail ? `<small>${escapeHtml(localize(detail))}</small>` : ""
  }</span><button type="button" aria-label="${activeLanguage() === "en-US" ? "Close notification" : "关闭通知"}">×</button>`;
  toast.querySelector("button").addEventListener("click", () => toast.remove());
  toastRegion.prepend(toast);
  [...toastRegion.children].slice(3).forEach((item) => item.remove());
  window.setTimeout(() => toast.remove(), 4600);
}

function setRoute(product, screen) {
  const path = `#${product}/${screen.replace(`${product}-`, "")}`;
  if (window.location.hash !== path) history.replaceState(null, "", path);
}

function updateNote(workspace, screenId) {
  const note = notes[screenId];
  if (!note) return;
  const title = workspace.querySelector("[data-note-title]");
  const copy = workspace.querySelector("[data-note-copy]");
  const list = workspace.querySelector("[data-note-list]");
  if (title) title.textContent = localize(note.title);
  if (copy) copy.textContent = localize(note.copy);
  if (list) {
    list.replaceChildren(
      ...note.items.map((item) => {
        const li = document.createElement("li");
        li.textContent = localize(item);
        return li;
      }),
    );
  }
}

function activateProduct(product, options = {}) {
  if (!validScreens[product]) return;
  state.product = product;
  document.querySelectorAll("[data-product-tab]").forEach((button) => {
    const active = button.dataset.productTab === product;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  document.querySelectorAll("[data-product]").forEach((workspace) => {
    const active = workspace.dataset.product === product;
    workspace.classList.toggle("is-active", active);
    workspace.setAttribute("aria-hidden", String(!active));
  });
  activateScreen(
    document.querySelector(`[data-product="${product}"]`),
    state.screens[product],
    { route: options.route !== false },
  );
  saveState();
}

function activateScreen(workspace, screenId, options = {}) {
  if (!workspace || !validScreens[workspace.dataset.product]?.has(screenId)) return;
  const product = workspace.dataset.product;
  state.screens[product] = screenId;
  workspace.querySelectorAll("[data-screen]").forEach((screen) => {
    const active = screen.dataset.screen === screenId;
    screen.classList.toggle("is-active", active);
    screen.setAttribute("aria-hidden", String(!active));
  });
  workspace.querySelectorAll("[data-screen-target]").forEach((button) => {
    const active = button.dataset.screenTarget === screenId;
    button.classList.toggle("is-active", active);
    if (active) button.setAttribute("aria-current", "page");
    else button.removeAttribute("aria-current");
  });
  updateNote(workspace, screenId);
  updateEmbeddedNavigation(product, screenId);
  if (options.route !== false && state.product === product) setRoute(product, screenId);
  saveState();
}

function parseRoute() {
  const [product, shortScreen] = window.location.hash.slice(1).split("/");
  const screen = `${product}-${shortScreen}`;
  if (validScreens[product]?.has(screen)) {
    state.product = product;
    state.screens[product] = screen;
  }
}

function preferredTheme() {
  return window.matchMedia("(prefers-color-scheme: light)").matches
    ? "light"
    : "dark";
}

function renderTheme() {
  const theme = state.theme || preferredTheme();
  document.documentElement.dataset.theme = theme;
  const button = document.querySelector(".theme-toggle");
  const nextLight = theme === "dark";
  button.textContent = localize(nextLight ? "明亮主题" : "深色主题");
  button.setAttribute("aria-label", localize(nextLight ? "切换到明亮主题" : "切换到深色主题"));
  button.setAttribute("aria-pressed", String(theme === "light"));
}

function toggleTheme() {
  const current = document.documentElement.dataset.theme;
  state.theme = current === "light" ? "dark" : "light";
  renderTheme();
  saveState();
  showToast("主题已更新", state.theme === "light" ? "明亮主题" : "深色主题");
}

function showDialog({
  eyebrow = "TermLink",
  title,
  content,
  confirmText = "确认",
  cancelText = "取消",
  danger = false,
  hideCancel = false,
  onConfirm = null,
}) {
  dialogEyebrow.textContent = localize(eyebrow);
  dialogTitle.textContent = localize(title);
  dialogContent.innerHTML = content;
  translateTree(dialogContent);
  dialogConfirm.textContent = localize(confirmText);
  dialogConfirm.classList.toggle("is-danger", danger);
  const cancel = appDialog.querySelector(".secondary-dialog-action");
  cancel.textContent = localize(cancelText);
  cancel.hidden = hideCancel;
  activeDialogAction = onConfirm;
  appDialog.showModal();
}

function openDrawer({ eyebrow = "详情", title, content }, trigger = null) {
  drawerReturnFocus = trigger || document.activeElement;
  drawerEyebrow.textContent = localize(eyebrow);
  drawerTitle.textContent = localize(title);
  drawerContent.innerHTML = content;
  translateTree(drawerContent);
  detailDrawer.hidden = false;
  drawerBackdrop.hidden = false;
  document.body.classList.add("is-overlay-open");
  drawerClose.focus();
}

function closeDrawer() {
  detailDrawer.hidden = true;
  drawerBackdrop.hidden = true;
  document.body.classList.remove("is-overlay-open");
  if (drawerReturnFocus instanceof HTMLElement) drawerReturnFocus.focus();
}

function commandCatalog() {
  return [
    ["android-account", "账号与登录", "服务器、注册、登录与找回密码", "Android"],
    ["android-devices", "设备", "OFFICE-PC 与实时状态", "Android"],
    ["android-terminal", "build-release", "打开 OFFICE-PC 实时终端", "Android"],
    ["android-replay", "deploy-prod 回放", "历史记录 · exit code 0", "Android"],
    ["android-security", "可信设备", "权限、指纹与批准新设备", "Android"],
    ["android-membership", "会员与配额", "Free · 1/1 PC · 2/3 Live", "Android"],
    ["android-settings", "设置与安全", "语言、服务器、账号与可信设备", "Android"],
    ["windows-tray", "Windows 托盘", "Relay、会话与紧急停止", "Windows Agent"],
    ["windows-sessions", "受管会话", "PowerShell 7 与 WSL Session", "Windows Agent"],
    ["windows-access", "远程访问策略", "本地用户优先与输入确认", "Windows Agent"],
    ["windows-settings", "Agent 设置", "启动、记录、网络与诊断", "Windows Agent"],
    ["windows-membership", "Windows 账户与会员", "账号权益与配额", "Windows Agent"],
    ["server-overview", "Server 运行概览", "连接、延迟与容器健康", "Server Console"],
    ["server-connections", "连接路由", "ACK Lag 与密文吞吐", "Server Console"],
    ["server-deployment", "Docker 部署", "镜像、备份与安全基线", "Server Console"],
    ["server-audit", "安全审计", "不可逆标识与 Trace ID", "Server Console"],
    ["server-billing", "订阅与权益", "Catalog、渠道与 Entitlement", "Server Console"],
    ["server-identity", "账号门户", "注册、登录、找回密码与首次改密", "Server Console"],
    ["server-accounts", "账号与角色", "注册策略、角色与登录撤销", "Server Console"],
    ["server-redemptions", "兑换码管理", "批次、导出、停用与兑换审计", "Server Console"],
  ].map(([screen, label, description, group]) => ({
    id: screen,
    screen,
    product: screen.split("-")[0],
    label: localize(label),
    description: localize(description),
    group,
  }));
}

function renderCommands(query = "") {
  const locale = activeLanguage() === "en-US" ? "en-US" : "zh-CN";
  const term = query.trim().toLocaleLowerCase(locale);
  commandMatches = commandCatalog().filter((item) =>
    `${item.label} ${item.description} ${item.group}`.toLocaleLowerCase(locale).includes(term),
  );
  commandIndex = Math.min(commandIndex, Math.max(0, commandMatches.length - 1));
  if (!commandMatches.length) {
    commandResults.innerHTML = '<div class="command-empty">没有匹配项。尝试“终端”“设备”或“部署”。</div>';
    return;
  }
  let previousGroup = "";
  commandResults.innerHTML = commandMatches
    .map((item, index) => {
      const heading = item.group !== previousGroup
        ? `<div class="command-group-label">${escapeHtml(item.group)}</div>`
        : "";
      previousGroup = item.group;
      return `${heading}<button class="command-option${index === commandIndex ? " is-selected" : ""}" type="button" aria-current="${index === commandIndex}" data-command-id="${escapeHtml(item.id)}"><span>›_</span><span><strong>${escapeHtml(item.label)}</strong><small>${escapeHtml(item.description)}</small></span><em>打开</em></button>`;
    })
    .join("");
}

function openCommandPalette(query = "") {
  commandInput.value = query;
  commandIndex = 0;
  renderCommands(query);
  commandDialog.showModal();
  commandInput.focus();
}

function executeCommand(id) {
  const item = commandCatalog().find((entry) => entry.id === id);
  if (!item) return;
  commandDialog.close();
  activateProduct(item.product);
  activateScreen(document.querySelector(`[data-product="${item.product}"]`), item.screen);
}

function normalizedText(element) {
  return element.textContent.replace(/\s+/g, " ").trim();
}

function setActionByText(root, text, action, all = false) {
  const matches = [...root.querySelectorAll("button")].filter((button) =>
    normalizedText(button).includes(text),
  );
  (all ? matches : matches.slice(0, 1)).forEach((button) => {
    button.dataset.action = action;
  });
}

function wireMarkup() {
  document.querySelector(".theme-toggle").dataset.action = "theme";
  document.querySelector(".language-toggle").dataset.action = "language";
  document.querySelectorAll("[data-auth-tab]").forEach((button) => { button.dataset.action = "android-auth-tab"; });
  document.querySelectorAll("[data-web-auth-tab]").forEach((button) => { button.dataset.action = "web-auth-tab"; });
  const androidDevices = document.querySelector('[data-screen="android-devices"]');
  androidDevices.querySelector(".mobile-search").dataset.action = "command";
  androidDevices.querySelector(".avatar-button").dataset.action = "account";
  setActionByText(androidDevices, "排序", "device-sort");
  document.querySelectorAll('[data-product="android"] .mobile-bottom-nav button').forEach((button) => {
    const label = normalizedText(button);
    button.dataset.action = label === "设备" ? "mobile-devices" : label === "活动" ? "mobile-activity" : "mobile-settings";
  });

  const terminal = document.querySelector('[data-screen="android-terminal"]');
  terminal.querySelector('[aria-label="更多"]').dataset.action = "terminal-details";
  terminal.querySelector(".jump-latest").dataset.action = "jump-latest";
  terminal.querySelector("#request-control").dataset.action = "control";
  terminal.querySelectorAll(".extra-keys button").forEach((button) => {
    button.dataset.action = "extra-key";
  });

  const replay = document.querySelector('[data-screen="android-replay"]');
  replay.querySelector(".terminal-appbar > button:first-child").dataset.action = "replay-back";
  replay.querySelector('[aria-label="搜索"]').dataset.action = "replay-search";
  replay.querySelectorAll(".playback-controls button").forEach((button) => {
    const label = normalizedText(button);
    if (label.endsWith("×")) {
      button.dataset.action = "replay-speed";
      button.dataset.speed = label.replace("×", "");
    } else if (button.classList.contains("round-control")) button.dataset.action = "replay-toggle";
    else button.dataset.action = "replay-copy";
  });
  const timeline = replay.querySelector(".timeline-track");
  timeline.dataset.action = "replay-seek";
  timeline.tabIndex = 0;
  timeline.setAttribute("role", "slider");
  timeline.setAttribute("aria-label", "回放进度");
  timeline.setAttribute("aria-valuemin", "0");
  timeline.setAttribute("aria-valuemax", "100");

  const security = document.querySelector('[data-screen="android-security"]');
  security.querySelector(".terminal-appbar, .mobile-appbar").querySelector("button").dataset.action = "security-back";
  setActionByText(security, "筛选", "trust-filter");
  security.querySelectorAll(".trust-list .trust-row").forEach((button) => {
    button.dataset.action = "trust-details";
  });
  setActionByText(security, "批准新设备", "approve-device");

  const membership = document.querySelector('[data-screen="android-membership"]');
  membership.querySelector('[aria-label="帮助"]').dataset.action = "membership-help";
  membership.querySelector("#android-upgrade").dataset.action = "android-upgrade";
  membership.querySelector(".restore-purchase").dataset.action = "restore-purchase";

  const tray = document.querySelector('[data-screen="windows-tray"]');
  tray.querySelector('[aria-label="打开设置"]').dataset.action = "open-windows-settings";
  tray.querySelector("#tray-policy-toggle").dataset.action = "cycle-policy";
  tray.querySelectorAll(".tray-session").forEach((button) => {
    button.dataset.action = "open-windows-sessions";
  });
  setActionByText(tray, "新建受管终端", "new-session");
  setActionByText(tray, "停止所有远程访问", "stop-remote");
  setActionByText(tray, "打开 TermLink", "open-windows-sessions");

  document.querySelectorAll(".desktop-sidebar").forEach((nav) => {
    nav.querySelectorAll("button").forEach((button) => {
      const label = normalizedText(button);
      if (label === "会话") button.dataset.action = "open-windows-sessions";
      else if (label === "远程访问") button.dataset.action = "open-windows-access";
      else if (label === "设置") button.dataset.action = "open-windows-settings";
      else if (label === "活动") button.dataset.action = "windows-activity";
      else if (label === "诊断") button.dataset.action = "diagnostics";
      else if (label === "账户与会员") button.dataset.action = "open-windows-membership";
    });
  });

  const sessions = document.querySelector('[data-screen="windows-sessions"]');
  setActionByText(sessions, "新建会话", "new-session");
  sessions.querySelectorAll(".toolbar > button").forEach((button) => {
    button.dataset.action = "session-filter";
  });
  sessions.querySelectorAll(".session-table-row").forEach((button) => {
    button.dataset.action = "session-details";
  });
  setActionByText(sessions, "查看运行状态", "runtime-health");

  const access = document.querySelector('[data-screen="windows-access"]');
  setActionByText(access, "断开", "disconnect-viewer");
  setActionByText(access, "停止所有远程访问", "stop-remote");

  const settings = document.querySelector('[data-screen="windows-settings"]');
  setActionByText(settings, "检查更新", "check-update");
  settings.querySelectorAll(".settings-tabs button").forEach((button) => {
    button.dataset.action = "settings-tab";
  });
  setActionByText(settings, "清理", "clear-history");
  setActionByText(settings, "导出诊断包", "export-diagnostics");

  const windowsMembership = document.querySelector('[data-screen="windows-membership"]');
  setActionByText(windowsMembership, "管理账户", "manage-account");
  setActionByText(windowsMembership, "查看月付方案", "windows-upgrade");
  setActionByText(windowsMembership, "刷新权益", "refresh-entitlement");

  document.querySelectorAll(".admin-nav").forEach((nav) => {
    nav.querySelectorAll("button").forEach((button) => {
      const label = normalizedText(button);
      const map = {
        "概览": "server-overview",
        "运行概览": "server-overview",
        "连接": "server-connections",
        "连接路由": "server-connections",
        "部署": "server-deployment",
        "Docker 部署": "server-deployment",
        "安全审计": "server-audit",
        "订阅与权益": "server-billing",
        "账号与角色": "server-accounts",
        "兑换码": "server-redemptions",
      };
      if (map[label]) {
        button.dataset.action = "server-nav";
        button.dataset.serverScreen = map[label];
      } else if (label === "设备与授权") button.dataset.action = "server-devices";
      else if (label === "设置") button.dataset.action = "server-settings";
    });
  });

  document.querySelectorAll('.admin-topbar [aria-label="查看通知"]').forEach((button) => {
    button.dataset.action = "notifications";
  });
  const overview = document.querySelector('[data-screen="server-overview"]');
  setActionByText(overview, "查看部署", "open-server-deployment");
  setActionByText(overview, "全部活动", "open-server-audit");

  const connections = document.querySelector('[data-screen="server-connections"]');
  setActionByText(connections, "刷新", "refresh-connections");
  connections.querySelectorAll(".data-row button").forEach((button) => {
    button.dataset.action = "connection-details";
  });

  const deployment = document.querySelector('[data-screen="server-deployment"]');
  setActionByText(deployment, "备份", "create-backup");
  setActionByText(deployment, "检查更新", "server-update");
  setActionByText(deployment, "compose.yml", "show-compose");
  setActionByText(deployment, "恢复演练", "restore-drill");

  const audit = document.querySelector('[data-screen="server-audit"]');
  setActionByText(audit, "导出元数据", "export-audit");
  setActionByText(audit, "策略", "audit-policy");
  audit.querySelectorAll(".audit-filters > button").forEach((button) => {
    button.dataset.action = "audit-filter";
  });

  const billing = document.querySelector('[data-screen="server-billing"]');
  setActionByText(billing, "同步购买凭据", "sync-purchases");
  setActionByText(billing, "发布 Catalog", "publish-catalog");
  setActionByText(billing, "新建套餐", "new-plan");
  setActionByText(billing, "管理套餐", "manage-plans");
  billing.querySelectorAll(".data-row button").forEach((button) => {
    button.dataset.action = "subscription-details";
  });
}

function updateEmbeddedNavigation(product, screenId) {
  if (product === "android") {
    document.querySelectorAll(".mobile-bottom-nav button").forEach((button) => {
      const action = button.dataset.action;
      const active =
        (action === "mobile-devices" && screenId === "android-devices") ||
        (action === "mobile-activity" && screenId === "android-replay") ||
        (action === "mobile-settings" && ["android-security", "android-membership", "android-settings"].includes(screenId));
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-pressed", String(active));
    });
  }
  if (product === "windows") {
    const labels = {
      "windows-sessions": "会话",
      "windows-access": "远程访问",
      "windows-settings": "设置",
      "windows-membership": "账户与会员",
    };
    document.querySelectorAll(".desktop-sidebar button").forEach((button) => {
      button.classList.toggle("is-active", normalizedText(button) === labels[screenId]);
    });
  }
  if (product === "server") {
    document.querySelectorAll(".admin-nav button").forEach((button) => {
      button.classList.toggle("is-active", button.dataset.serverScreen === screenId);
    });
  }
}

function renderControlState() {
  const button = document.querySelector("#request-control");
  const status = document.querySelector("#android-control-state");
  const remaining = Math.max(0, Math.ceil((state.control.expiresAt - Date.now()) / 1000));
  if (state.control.granted && remaining <= 0) {
    state.control.granted = false;
    state.control.expiresAt = 0;
    saveState();
    showToast("控制租约已结束", "终端已恢复只读，本次输入不会自动重试。", "warning");
  }
  const active = state.control.granted;
  button.classList.toggle("is-active", active);
  button.querySelector("span").textContent = active ? `释放控制权 · ${remaining} 秒` : "申请 30 秒控制权";
  button.setAttribute("aria-pressed", String(active));
  status.textContent = active ? "控制中" : "只读";
  status.classList.toggle("is-controlling", active);
}

function requestControl(button) {
  if (state.control.granted) {
    state.control.granted = false;
    state.control.expiresAt = 0;
    window.clearInterval(controlTimer);
    renderControlState();
    saveState();
    showToast("已释放控制权", "远程终端立即恢复只读。", "success");
    return;
  }
  button.setAttribute("aria-busy", "true");
  button.querySelector("span").textContent = "等待 OFFICE-PC 确认…";
  window.setTimeout(() => {
    button.removeAttribute("aria-busy");
    state.control.granted = true;
    state.control.expiresAt = Date.now() + 30_000;
    renderControlState();
    saveState();
    showToast("已取得 30 秒控制权", "本地键盘输入会立即撤销租约。", "success");
    window.clearInterval(controlTimer);
    controlTimer = window.setInterval(renderControlState, 1000);
  }, 700);
}

function renderReplay() {
  const replay = document.querySelector('[data-screen="android-replay"]');
  replay.querySelectorAll("[data-speed]").forEach((button) => {
    const active = Number(button.dataset.speed) === state.replay.speed;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  replay.querySelector(".replay-banner b").textContent = `${state.replay.speed}×`;
  const toggle = replay.querySelector(".round-control");
  toggle.setAttribute("aria-label", state.replay.playing ? "暂停回放" : "继续回放");
  toggle.innerHTML = `<svg><use href="#${state.replay.playing ? "i-pause" : "i-play"}" /></svg>`;
  const track = replay.querySelector(".timeline-track");
  track.querySelector("i").style.width = `${state.replay.progress}%`;
  track.querySelector("b").style.left = `calc(${state.replay.progress}% - 6px)`;
  track.setAttribute("aria-valuenow", String(state.replay.progress));
}

function seekReplay(value) {
  state.replay.progress = Math.max(0, Math.min(100, Math.round(value)));
  renderReplay();
  saveState();
}

const policyLabels = ["允许已批准设备控制", "仅允许远程查看", "仅本机"];

function renderWindowsState() {
  const policyLabel = policyLabels[state.windows.policyIndex];
  document.querySelector("#tray-policy-label").textContent = policyLabel;
  const select = document.querySelector('[data-screen="windows-access"] select[aria-label="远程访问策略"]');
  select.selectedIndex = state.windows.policyIndex;
  const viewer = document.querySelector('[data-screen="windows-access"] .active-viewer');
  if (viewer) viewer.hidden = !state.windows.viewerConnected || !state.windows.remoteEnabled;
  const viewerCount = document.querySelector('[data-screen="windows-access"] .settings-section-head > span');
  if (viewerCount) viewerCount.textContent = state.windows.viewerConnected && state.windows.remoteEnabled ? "1 个查看者" : "无查看者";
  document.querySelectorAll('[data-action="stop-remote"]').forEach((button) => {
    button.textContent = state.windows.remoteEnabled ? "停止所有远程访问" : "恢复远程访问";
    button.classList.toggle("danger-action", state.windows.remoteEnabled && button.closest('[data-screen="windows-tray"]'));
  });
}

function stopRemoteAccess() {
  if (!state.windows.remoteEnabled) {
    state.windows.remoteEnabled = true;
    state.windows.policyIndex = 1;
    renderWindowsState();
    saveState();
    showToast("远程查看已恢复", "控制仍需本机确认。", "success");
    return;
  }
  showDialog({
    eyebrow: "本地用户优先",
    title: "停止所有远程访问？",
    content: '<p class="modal-copy">将立即断开当前查看者并撤销全部输入租约。<strong>本地 Shell 不会被终止</strong>，历史密文也不会删除。</p><div class="warning-callout">生产环境会要求本机用户再次确认，并写入不含终端正文的安全审计事件。</div>',
    confirmText: "停止远程访问",
    danger: true,
    onConfirm: () => {
      state.windows.remoteEnabled = false;
      state.windows.viewerConnected = false;
      state.windows.policyIndex = 2;
      renderWindowsState();
      saveState();
      showToast("所有远程访问已停止", "本地终端继续运行。", "success");
    },
  });
}

function openSessionCreator() {
  showDialog({
    eyebrow: "Windows Agent",
    title: "新建受管终端",
    content: '<div class="field-stack"><label>会话名称<input id="new-session-name" value="work-session" maxlength="48"></label><label>Terminal Profile<select id="new-session-profile"><option>PowerShell 7</option><option>Windows PowerShell</option><option>Command Prompt</option><option>WSL Ubuntu</option></select></label><label>工作目录<input id="new-session-path" value="C:\\src\\termlink"></label></div><div class="security-callout">新会话由当前 Windows 用户启动，不提升权限；只有受管 Session Host 会开放远程能力。</div>',
    confirmText: "创建本地会话",
    onConfirm: () => {
      const name = document.querySelector("#new-session-name")?.value.trim() || "work-session";
      showToast("会话创建请求已准备", `${name} · 等待 Session Host 生产接口`, "success");
      activateProduct("windows");
      activateScreen(document.querySelector('[data-product="windows"]'), "windows-sessions");
    },
  });
}

function renderSettingsTab(tabName) {
  const screen = document.querySelector('[data-screen="windows-settings"]');
  state.windows.settingsTab = tabName;
  screen.querySelectorAll(".settings-tabs button").forEach((button) => {
    const active = normalizedText(button) === tabName;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-selected", String(active));
  });
  screen.querySelectorAll(":scope .desktop-content > .settings-section").forEach((section) => {
    section.hidden = tabName !== "常规";
  });
  let panel = screen.querySelector(".settings-tab-panel");
  if (!panel) {
    panel = document.createElement("div");
    panel.className = "settings-tab-panel";
    screen.querySelector(".settings-tabs").after(panel);
  }
  panel.hidden = tabName === "常规";
  const templates = {
    "终端 Profile": '<div class="settings-section"><h3>默认 Profile</h3><label class="field-row"><span><strong>新会话默认项</strong><small>来自 Windows Terminal Profiles</small></span><select><option>PowerShell 7</option><option>Windows PowerShell</option><option>Command Prompt</option><option>WSL Ubuntu</option></select></label><div class="version-row"><span><strong>4 个 Profile 已同步</strong><small>仅保存 GUID、名称、图标和配色引用</small></span><em>正常</em><button type="button" data-action="sync-profiles">重新同步</button></div></div>',
    "历史": '<div class="settings-section"><h3>加密记录</h3><label class="field-row"><span><strong>默认保留时间</strong><small>正文使用 DPAPI 包装的数据密钥加密</small></span><select data-setting="retention"><option>1 天</option><option selected>7 天</option><option>30 天</option><option>不记录</option></select></label><label class="setting-row"><span><strong>会话结束后允许本地回放</strong><small>不上传明文终端正文</small></span><input type="checkbox" checked></label></div>',
    "网络": '<div class="settings-section"><h3>Relay 与重连</h3><label class="field-row"><span><strong>Relay 地址</strong><small>必须使用 TLS；证书失败时不降级</small></span><input value="wss://relay.example.com" inputmode="url"></label><label class="field-row"><span><strong>代理</strong><small>默认使用系统设置</small></span><select><option>系统代理</option><option>不使用代理</option><option>手动配置</option></select></label><label class="setting-row"><span><strong>网络恢复后自动重连</strong><small>重新授权后从 ACK 或 Snapshot 恢复</small></span><input type="checkbox" checked></label></div>',
    "高级": '<div class="settings-section"><h3>缓冲与诊断</h3><label class="field-row"><span><strong>本地加密 Buffer 上限</strong><small>达到上限时优先保留最新 Snapshot</small></span><select><option>512 MB</option><option>1 GB</option><option>2 GB</option></select></label><label class="setting-row"><span><strong>详细诊断日志</strong><small>从不写入命令、Token 或终端正文</small></span><input type="checkbox"></label><div class="warning-callout">协议版本、ACK 窗口和背压参数由兼容性协商决定，UI 不允许绕过服务端安全策略。</div></div>',
  };
  panel.innerHTML = templates[tabName] || "";
  saveState();
}

function filterRows(input, rowSelector) {
  const term = input.value.trim().toLocaleLowerCase("zh-CN");
  const screen = input.closest("[data-screen]");
  screen.querySelectorAll(rowSelector).forEach((row) => {
    row.classList.toggle("is-filtered-out", !normalizedText(row).toLocaleLowerCase("zh-CN").includes(term));
  });
}

function switchAuthPanel(screen, mode, buttonSelector, panelSelector, dataKey) {
  screen.querySelectorAll(buttonSelector).forEach((button) => {
    const active = button.dataset[dataKey] === mode;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-selected", String(active));
    button.setAttribute("tabindex", active ? "0" : "-1");
  });
  screen.querySelectorAll(panelSelector).forEach((panel) => {
    panel.hidden = panel.dataset[dataKey.replace("Tab", "Panel")] !== mode;
  });
  if (dataKey === "webAuthTab") {
    const heading = screen.querySelector(".identity-form-heading h3");
    const copy = screen.querySelector(".identity-form-heading > p:last-child");
    heading.textContent = localize(mode === "login" ? "欢迎回来" : "创建你的 TermLink 账号");
    copy.textContent = localize(mode === "login" ? "继续访问你的设备、会员权益与 Server Console。" : "验证邮箱后，可在 Android、Windows 和网页端使用同一账号。");
  }
}

function showAuthorizationHandoff(client) {
  const isAndroid = client === "android";
  showDialog({
    eyebrow: isAndroid ? "Android 安全授权" : "Windows 系统浏览器授权",
    title: isAndroid ? "在安全浏览器中继续" : "正在等待浏览器授权",
    content: `<div class="status-callout"><strong>https://termlink.kget.cc:8910</strong><br>${isAndroid ? "Authorization Code + PKCE · dev.termlink:/oauth2redirect" : "Authorization Code + PKCE · 返回当前 Windows Agent"}</div><dl class="detail-list"><div><dt>账号</dt><dd>alex@example.com</dd></div><div><dt>请求权限</dt><dd>openid profile offline_access</dd></div><div><dt>设备绑定</dt><dd>${isAndroid ? "Pixel 10 Pro" : "OFFICE-PC"}</dd></div></dl><div class="security-callout">密码只提交到统一账号门户。客户端只接收短期授权码，并验证 state、nonce 和 PKCE。</div>`,
    confirmText: "模拟授权返回",
    onConfirm: () => showDialog({
      eyebrow: "授权完成",
      title: isAndroid ? "账号已登录，等待设备批准" : "Windows Agent 已重新登录",
      content: isAndroid
        ? '<div class="status-callout">账号验证成功 · 设备状态 PENDING</div><p>同账号登录不会自动获得 PC 访问权。请在现有 MANAGER 设备上批准此设备。</p>'
        : '<div class="status-callout">账号验证成功 · Token 已交回当前 Agent</div><p>系统浏览器不向 Agent 暴露密码；现有可信设备密钥保持不变。</p>',
      confirmText: "完成",
      hideCancel: true,
      onConfirm: () => showToast(isAndroid ? "登录与设备注册已完成" : "Windows 登录已更新", isAndroid ? "等待可信设备批准。" : "账号权益和设备授权已刷新。", "success"),
    }),
  });
}

function showForcedPasswordChange() {
  showDialog({
    eyebrow: "首次登录安全要求",
    title: "修改超级管理员临时密码",
    content: '<div class="warning-callout">identity-bootstrap 只在 Docker 首次启动日志输出一次 24 位随机密码。该密码建议 24 小时过期，且首次登录不能跳过改密。</div><div class="field-stack"><label>当前临时密码<input type="password" autocomplete="current-password" value="temporary-password"></label><label>新密码<input type="password" autocomplete="new-password" placeholder="至少 14 个字符"></label><label>再次输入新密码<input type="password" autocomplete="new-password" placeholder="必须与新密码一致"></label></div><div class="security-callout">改密成功后撤销全部临时登录会话，并要求配置 MFA 与恢复方式。</div>',
    confirmText: "修改密码并继续",
    hideCancel: true,
    onConfirm: () => showDialog({
      eyebrow: "管理员安全设置",
      title: "临时密码已失效",
      content: '<div class="status-callout">密码已更新 · 临时会话已撤销</div><div class="option-stack"><label><input type="checkbox" checked>立即配置 TOTP 多因素认证</label><label><input type="checkbox" checked>生成一次性恢复码</label></div><div class="security-callout">超级管理员不允许关闭重新认证、审计记录或高风险操作保护。</div>',
      confirmText: "进入 Server Console",
      hideCancel: true,
      onConfirm: () => {
        activateProduct("server");
        activateScreen(document.querySelector('[data-product="server"]'), "server-overview");
        showToast("超级管理员初始化完成", "首次临时密码不会再次显示。", "success");
      },
    }),
  });
}

function openRedemptionFlow() {
  showDialog({
    eyebrow: "会员兑换码",
    title: "输入兑换码",
    content: '<div class="field-stack"><label>兑换码<input id="membership-code" inputmode="text" autocomplete="off" value="TL-A7K9-PRO-30D" maxlength="32"></label></div><div class="security-callout">兑换码将通过 TLS 发送到服务器验证。App 不记录完整码，失败尝试受账号、设备与网络速率限制。</div>',
    confirmText: "验证并预览",
    onConfirm: () => {
      const code = document.querySelector("#membership-code")?.value.trim() || "TL-A7K9-…";
      showDialog({
        eyebrow: "兑换预览",
        title: "确认领取 TermLink Pro？",
        content: `<div class="status-callout"><strong>TermLink Pro · 30 天</strong><br>立即生效，预计到期 2026-09-03</div><dl class="detail-list"><div><dt>兑换码</dt><dd>${escapeHtml(`${code.slice(0, 7)}…`)}</dd></div><div><dt>权益来源</dt><dd>REDEMPTION_CODE</dd></div><div><dt>远程 PC</dt><dd>5</dd></div><div><dt>实时 Session</dt><dd>20</dd></div></dl><div class="security-callout">确认后该兑换码立即绑定当前账号且不可转移；重复提交会返回同一兑换结果。</div>`,
        confirmText: "确认兑换",
        onConfirm: () => showToast("兑换成功", "TermLink Pro 已生效 30 天。", "success"),
      });
    },
  });
}

function handleAction(action, target, event) {
  const workspace = target.closest("[data-product]");
  switch (action) {
    case "theme":
      toggleTheme();
      break;
    case "language":
      toggleLanguage();
      break;
    case "android-auth-tab":
      switchAuthPanel(target.closest('[data-screen="android-account"]'), target.dataset.authTab, "[data-auth-tab]", "[data-auth-panel]", "authTab");
      break;
    case "web-auth-tab":
      switchAuthPanel(target.closest('[data-screen="server-identity"]'), target.dataset.webAuthTab, "[data-web-auth-tab]", "[data-web-auth-panel]", "webAuthTab");
      break;
    case "android-login":
      showAuthorizationHandoff("android");
      break;
    case "android-register":
      showDialog({ eyebrow: "创建 TermLink 账号", title: "确认注册并验证邮箱", content: '<dl class="detail-list"><div><dt>服务器</dt><dd>termlink.kget.cc:8910</dd></div><div><dt>注册策略</dt><dd>开放注册</dd></div><div><dt>邮箱验证</dt><dd>必需</dd></div></dl><div class="security-callout">密码只提交到服务端身份页面，Android App 不直接保存或处理密码。</div>', confirmText: "创建账号", onConfirm: () => showDialog({ eyebrow: "验证邮箱", title: "验证链接已发送", content: '<div class="status-callout">alex@example.com · 等待验证</div><p>链接 30 分钟内有效。验证完成后会返回 Android，并继续设备注册流程。</p><div class="drawer-actions"><button type="button" data-action="resend-verification">重新发送</button></div>', confirmText: "模拟已验证", hideCancel: true, onConfirm: () => showAuthorizationHandoff("android") }) });
      break;
    case "forgot-password":
      showDialog({ eyebrow: "账号恢复", title: "重置密码", content: '<div class="field-stack"><label>账号邮箱<input type="email" autocomplete="email" value="alex@example.com"></label></div><div class="security-callout">无论账号是否存在，页面都返回相同结果，避免泄露注册状态。重置链接 30 分钟内有效且只能使用一次。</div>', confirmText: "发送重置链接", onConfirm: () => showToast("如账号存在，重置邮件已发送", "请检查邮箱并在 30 分钟内完成操作。", "success") });
      break;
    case "resend-verification":
      showToast("验证邮件已重新发送", "请稍后再试或检查垃圾邮件目录。", "success");
      break;
    case "change-server":
      showDialog({ eyebrow: "服务器连接", title: "选择 TermLink 服务器", content: '<div class="field-stack"><label>公开服务器地址<input type="url" value="https://termlink.kget.cc:8910" inputmode="url"></label></div><dl class="detail-list"><div><dt>TLS</dt><dd>有效</dd></div><div><dt>OIDC</dt><dd>已发现</dd></div><div><dt>注册策略</dt><dd>开放注册</dd></div></dl><div class="security-callout">客户端只接受 HTTPS。192.168.9.28:8080 是 Lucky 上游测试地址，不作为客户端公开地址。</div>', confirmText: "验证并保存", onConfirm: () => showToast("服务器连接已验证", "公开 HTTPS、OIDC 与回调配置均可用。", "success") });
      break;
    case "open-redeem":
    case "windows-web-redeem":
      openRedemptionFlow();
      break;
    case "open-account-portal":
      openDrawer({ eyebrow: "账号中心", title: "Alex Chen", content: '<div class="status-callout">alex@example.com · Free</div><dl class="detail-list"><div><dt>邮箱</dt><dd>已验证</dd></div><div><dt>MFA</dt><dd>未配置</dd></div><div><dt>活跃登录</dt><dd>3</dd></div><div><dt>可信设备</dt><dd>4</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="change-password">修改密码</button><button type="button" data-drawer-action="setup-mfa">配置 MFA</button><button type="button" data-drawer-action="revoke-logins">撤销其他登录</button></div>' }, target);
      break;
    case "account-security":
      openDrawer({ eyebrow: "登录与安全", title: "账号安全", content: '<div class="status-callout">密码正常 · MFA 未配置</div><dl class="detail-list"><div><dt>当前登录</dt><dd>Pixel 10 Pro</dd></div><div><dt>其他登录</dt><dd>2</dd></div><div><dt>最近改密</dt><dd>46 天前</dd></div><div><dt>恢复邮箱</dt><dd>已验证</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="change-password">修改密码</button><button type="button" data-drawer-action="setup-mfa">配置 MFA</button><button class="danger-outline" type="button" data-drawer-action="revoke-logins">撤销其他登录</button></div>' }, target);
      break;
    case "android-signout":
      showDialog({ eyebrow: "退出当前设备", title: "退出 Pixel 10 Pro？", content: '<p>将删除本机登录 Token 并断开远程连接，但不会删除账号、可信设备记录或云端权益。</p>', confirmText: "退出设备", danger: true, onConfirm: () => { activateScreen(workspace, "android-account"); showToast("当前设备已退出", "本地密钥保留到设备撤销或重新登录。", "success"); } });
      break;
    case "delete-account":
      showDialog({ eyebrow: "不可撤销操作", title: "删除账号与云端数据？", content: '<div class="warning-callout">需要重新认证并等待 7 天冷静期。期间可撤销删除；到期后删除账号、设备授权、订阅关联和服务器密文对象，安全审计仅保留法规要求的不可逆元数据。</div><div class="field-stack"><label>输入 DELETE 确认<input autocomplete="off" placeholder="DELETE"></label></div>', confirmText: "开始 7 天删除流程", danger: true, onConfirm: () => showToast("账号删除请求已建立", "重新认证后进入 7 天冷静期。", "warning") });
      break;
    case "windows-web-auth":
      showAuthorizationHandoff("windows");
      break;
    case "web-login":
      showForcedPasswordChange();
      break;
    case "web-register":
      showDialog({ eyebrow: "验证邮箱", title: "账号已创建", content: '<div class="status-callout">name@example.com · 等待验证</div><p>验证链接 30 分钟内有效。验证后可返回 Android、Windows 或网页账户中心继续授权。</p>', confirmText: "重新发送验证邮件", hideCancel: true, onConfirm: () => showToast("验证邮件已发送", "30 分钟内有效。", "success") });
      break;
    case "bootstrap-login":
      showDialog({ eyebrow: "Docker 首次初始化", title: "超级管理员启动流程", content: '<ol><li>identity-bootstrap 创建 admin 和 24 位随机临时密码。</li><li>密码只在首次启动日志输出一次，普通重启不再输出。</li><li>临时密码建议 24 小时过期，首次登录强制改密。</li><li>改密后撤销临时会话，并引导配置 MFA 与恢复码。</li></ol><div class="security-callout">日志不得输出 Token、OIDC Secret、数据库密码或改密后的密码。</div>', confirmText: "模拟首次登录", onConfirm: showForcedPasswordChange });
      break;
    case "registration-policy":
      openDrawer({ eyebrow: "账号与角色", title: "注册策略", content: '<div class="field-stack"><label>注册方式<select><option>开放注册</option><option>仅邀请</option><option>关闭注册</option></select></label><label>邮箱验证<select><option>必需</option><option>可选</option></select></label><label>默认角色<select><option>USER</option></select></label></div><div class="option-stack"><label><input type="checkbox" checked>启用密码找回</label><label><input type="checkbox" checked>新账号接受服务条款</label><label><input type="checkbox">强制所有用户配置 MFA</label></div><div class="drawer-actions"><button type="button" data-drawer-action="save-registration-policy">保存策略</button></div><div class="security-callout">没有可用 SMTP 时，默认切换为仅邀请，避免用户无法完成验证或找回密码。</div>' }, target);
      break;
    case "create-account":
      openDrawer({ eyebrow: "账号与角色", title: "新建账号", content: '<div class="field-stack"><label>用户名<input value="new-user"></label><label>邮箱<input type="email" value="new-user@example.com"></label><label>角色<select><option>USER</option><option>AUDITOR</option><option>ADMIN</option></select></label><label>初始方式<select><option>发送邀请链接</option><option>创建一次性临时密码</option></select></label></div><div class="drawer-actions"><button type="button" data-drawer-action="create-account">创建账号</button></div><div class="security-callout">管理员不能设置可恢复的永久密码。邀请和临时密码都必须设置有效期，并在首次登录时改密。</div>' }, target);
      break;
    case "account-details": {
      const account = target.querySelector("b").textContent;
      const identity = target.querySelector("small").textContent;
      const role = target.children[1].textContent.trim();
      openDrawer({ eyebrow: "账号与角色", title: account, content: `<div class="status-callout">${escapeHtml(identity)}</div><dl class="detail-list"><div><dt>角色</dt><dd>${escapeHtml(role)}</dd></div><div><dt>邮箱</dt><dd>${account === "admin" ? "不适用" : "已验证"}</dd></div><div><dt>MFA</dt><dd>${account === "admin" ? "必需" : "可选"}</dd></div><div><dt>活跃登录</dt><dd>${account === "admin" ? "1" : "3"}</dd></div></dl><div class="field-stack"><label>角色绑定<select${account === "admin" ? " disabled" : ""}><option>${escapeHtml(role)}</option><option>USER</option><option>AUDITOR</option><option>ADMIN</option></select></label></div><div class="drawer-actions"><button type="button" data-drawer-action="revoke-logins">撤销全部登录</button><button type="button" data-drawer-action="send-password-reset">发送改密邮件</button><button class="danger-outline" type="button" data-drawer-action="disable-account"${account === "admin" ? " disabled" : ""}>禁用账号</button></div><div class="security-callout">${account === "admin" ? "唯一超级管理员不能被降级、禁用或删除。请先创建并验证另一个 SUPER_ADMIN。" : "角色变更、登录撤销与禁用都需要管理员重新认证并写入审计。"}</div>` }, target);
      break;
    }
    case "export-redemptions":
      showDialog({ eyebrow: "一次性导出", title: "当前没有待导出的完整兑换码", content: '<p>完整兑换码只在新批次创建成功后显示和下载一次。已有批次仅保存 HMAC/Hash，因此无法恢复或重新导出原始兑换码。</p><div class="security-callout">可随时导出批次统计和兑换审计，但其中只包含截断前缀与不可逆标识。</div>', confirmText: "新建批次", onConfirm: () => handleAction("new-redemption-batch", target, event) });
      break;
    case "new-redemption-batch":
      openDrawer({ eyebrow: "兑换码管理", title: "新建兑换码批次", content: '<div class="field-stack"><label>批次名称<input value="September Campaign" maxlength="64"></label><label>权益方案<select><option>TermLink Pro</option></select></label><label>权益时长<select><option>30 天</option><option>90 天</option><option>365 天</option></select></label><label>生成数量<input type="number" min="1" max="10000" value="100"></label><label>兑换截止日期<input type="date" value="2026-12-31"></label><label>渠道标识<input value="campaign-september" maxlength="40"></label></div><div class="drawer-actions"><button type="button" data-drawer-action="create-redemption-batch">生成并一次性导出</button></div><div class="warning-callout">创建后只显示一次完整码。关闭下载窗口后服务端无法恢复明文；重新生成会创建不同批次。</div>' }, target);
      break;
    case "redemption-details": {
      const batch = target.querySelector("b").textContent;
      const prefix = target.querySelector("small").textContent;
      openDrawer({ eyebrow: "兑换码批次", title: batch, content: `<div class="status-callout">${escapeHtml(prefix)}</div><dl class="detail-list"><div><dt>权益</dt><dd>${escapeHtml(target.children[1].textContent.trim())}</dd></div><div><dt>使用情况</dt><dd>${escapeHtml(target.children[2].textContent.trim())}</dd></div><div><dt>截止日期</dt><dd>${escapeHtml(target.querySelector("time").textContent)}</dd></div><div><dt>存储</dt><dd>HMAC / Hash only</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="export-redemption-audit">导出兑换审计</button><button class="danger-outline" type="button" data-drawer-action="disable-redemption-batch">停用未兑换码</button></div><div class="security-callout">完整兑换码不可恢复。停用只影响尚未兑换的码，已生成的 Entitlement 不会被静默撤销。</div>` }, target);
      break;
    }
    case "command":
      openCommandPalette();
      break;
    case "account":
      activateScreen(workspace, "android-membership");
      break;
    case "device-sort":
      target.classList.toggle("is-active");
      target.setAttribute("aria-pressed", String(target.classList.contains("is-active")));
      showToast("设备排序已更新", target.classList.contains("is-active") ? "按最近活动排序" : "按名称排序");
      break;
    case "mobile-devices":
      activateScreen(workspace, "android-devices");
      break;
    case "mobile-activity":
      activateScreen(workspace, "android-replay");
      break;
    case "mobile-settings":
      activateScreen(workspace, "android-settings");
      break;
    case "terminal-details":
      openDrawer({ eyebrow: "实时会话", title: "build-release", content: '<div class="status-callout">Live · E2EE · 82×28 · OFFICE-PC</div><dl class="detail-list"><div><dt>Shell</dt><dd>PowerShell 7</dd></div><div><dt>延迟</dt><dd>28 ms</dd></div><div><dt>控制策略</dt><dd>本地优先</dd></div><div><dt>加密</dt><dd>E2EE</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="replay">查看历史</button><button type="button" data-drawer-action="copy-session">复制 Session ID</button></div><div class="security-callout">终端正文只在端侧解密。服务端与 UI 评审壳均无法查看生产 Payload。</div>' }, target);
      break;
    case "jump-latest":
      target.closest(".terminal-canvas").scrollTop = target.closest(".terminal-canvas").scrollHeight;
      showToast("已跳到最新输出");
      break;
    case "control":
      requestControl(target);
      break;
    case "extra-key":
      if (!state.control.granted) showToast("当前为只读", "请先取得输入租约；按键不会排队或自动重试。", "warning");
      else showToast(`已发送 ${normalizedText(target)}`, "输入已绑定当前租约序号。", "success");
      break;
    case "replay-back":
    case "security-back":
      activateScreen(workspace, "android-devices");
      break;
    case "replay-search":
      showDialog({ eyebrow: "端侧搜索", title: "搜索已解密回放", content: '<div class="field-stack"><label>关键词<input id="replay-query" type="search" placeholder="例如 Deployment"></label></div><div class="security-callout">搜索只在本设备已解密的回放块中执行，不把关键词或正文发送到 Server Console。</div>', confirmText: "搜索", onConfirm: () => showToast("找到 2 处匹配", "已在 14:17:08 与 14:23:18 标记。", "success") });
      break;
    case "replay-speed":
      state.replay.speed = Number(target.dataset.speed);
      renderReplay();
      saveState();
      break;
    case "replay-toggle":
      state.replay.playing = !state.replay.playing;
      renderReplay();
      saveState();
      showToast(state.replay.playing ? "回放继续" : "回放已暂停", `${state.replay.speed}×`);
      break;
    case "replay-copy":
      navigator.clipboard?.writeText("Deployment completed in 04:18").then(
        () => showToast("已复制所选终端文本", "仅复制当前端侧已解密内容。", "success"),
        () => showToast("无法访问剪贴板", "请在浏览器权限中允许剪贴板写入。", "warning"),
      );
      break;
    case "replay-seek": {
      const rect = target.getBoundingClientRect();
      seekReplay(((event.clientX - rect.left) / rect.width) * 100);
      showToast("已从最近快照恢复", `回放进度 ${state.replay.progress}%`);
      break;
    }
    case "trust-filter":
      target.classList.toggle("is-active");
      document.querySelectorAll(".trust-list .trust-row").forEach((row) => {
        row.classList.toggle("is-filtered-out", target.classList.contains("is-active") && !normalizedText(row).includes("Android"));
      });
      target.textContent = target.classList.contains("is-active") ? "仅 Android" : "筛选";
      break;
    case "trust-details": {
      const name = target.querySelector("strong").textContent;
      const meta = target.querySelector("em").textContent;
      const permission = meta.split(" · ").at(-1);
      const permissionOptions = ["只读", "可控制", "管理"]
        .map((option) => `<option${option === permission ? " selected" : ""}>${option}</option>`)
        .join("");
      openDrawer({ eyebrow: "可信设备", title: name, content: `<div class="status-callout">${escapeHtml(meta)}</div><dl class="detail-list"><div><dt>设备密钥</dt><dd>已验证</dd></div><div><dt>最后活动</dt><dd>${name === "旧手机" ? "30 天前" : "2 天前"}</dd></div><div><dt>权限</dt><dd>${escapeHtml(permission)}</dd></div><div><dt>指纹</dt><dd>8A:72:…:C9</dd></div></dl><div class="field-stack"><label>访问权限<select data-trust-permission>${permissionOptions}</select></label></div><div class="drawer-actions"><button type="button" data-drawer-action="save-trust">保存权限</button><button class="danger-outline" type="button" data-drawer-action="revoke-trust" data-device="${escapeHtml(name)}">撤销设备</button></div>` }, target);
      break;
    }
    case "approve-device":
      showDialog({ eyebrow: "设备批准", title: "批准新设备", content: '<p>在新设备上输入一次性短码，或由当前 MANAGER 扫描配对二维码。</p><div class="pairing-code">TL-482 913</div><div class="security-callout">短码将在 5 分钟后失效。批准只建立设备信任，不自动授予任何 PC 的控制权。</div>', confirmText: "完成", hideCancel: true, onConfirm: () => showToast("配对窗口已关闭", "没有新增设备或变更权限。") });
      break;
    case "membership-help":
      openDrawer({ eyebrow: "会员帮助", title: "配额如何计算", content: '<dl class="detail-list"><div><dt>远程 PC</dt><dd>账号级去重</dd></div><div><dt>实时 Session</dt><dd>session_id 去重</dd></div><div><dt>历史回放</dt><dd>不计入</dd></div><div><dt>本地 Shell</dt><dd>不限制</dd></div></dl><div class="security-callout">E2EE、设备批准与基础安全能力永远不是付费墙。</div>' }, target);
      break;
    case "android-upgrade":
    case "windows-upgrade":
      showDialog({ eyebrow: "合规购买", title: "查看 TermLink Pro 月付方案", content: '<div class="status-callout"><strong>价格由购买渠道本地化展示</strong><br>进入最终确认前会显示实际价格、税费、周期与自动续费规则。</div><p>升级只扩展远程 PC 和同时显示的实时 Session 配额，不改变 E2EE、设备授权或本地用户优先策略。</p>', confirmText: "打开购买渠道", onConfirm: () => showToast("已准备购买渠道跳转", "此 UI 原型不会提交订单或付款数据。", "warning") });
      break;
    case "restore-purchase":
    case "refresh-entitlement":
      target.setAttribute("aria-busy", "true");
      target.disabled = true;
      window.setTimeout(() => {
        target.removeAttribute("aria-busy");
        target.disabled = false;
        showToast("权益已同步", "当前仍为 TermLink Free。", "success");
      }, 850);
      break;
    case "open-windows-settings":
      activateScreen(workspace, "windows-settings");
      break;
    case "open-windows-sessions":
      activateScreen(workspace, "windows-sessions");
      break;
    case "open-windows-access":
      activateScreen(workspace, "windows-access");
      break;
    case "open-windows-membership":
      activateScreen(workspace, "windows-membership");
      break;
    case "cycle-policy":
      state.windows.policyIndex = (state.windows.policyIndex + 1) % policyLabels.length;
      state.windows.remoteEnabled = state.windows.policyIndex !== 2;
      renderWindowsState();
      saveState();
      showToast("远程策略已更新", policyLabels[state.windows.policyIndex], state.windows.policyIndex === 2 ? "warning" : "success");
      break;
    case "new-session":
      openSessionCreator();
      break;
    case "stop-remote":
      stopRemoteAccess();
      break;
    case "windows-activity":
      openDrawer({ eyebrow: "本机活动", title: "最近安全事件", content: '<dl class="detail-list"><div><dt>刚刚</dt><dd>VIEW_ATTACHED</dd></div><div><dt>18 分钟前</dt><dd>SESSION_STARTED</dd></div><div><dt>1 小时前</dt><dd>DEVICE_APPROVED</dd></div></dl><div class="security-callout">活动记录不包含命令、终端正文、Token 或完整网络标识。</div>' }, target);
      break;
    case "diagnostics":
    case "runtime-health":
      openDrawer({ eyebrow: "本机诊断", title: "Agent 运行状态", content: '<div class="status-callout">所有本机组件正常</div><dl class="detail-list"><div><dt>Agent</dt><dd>Healthy</dd></div><div><dt>Session Host</dt><dd>2 / 2</dd></div><div><dt>加密 Buffer</dt><dd>18.2 MB</dd></div><div><dt>ConPTY</dt><dd>Available</dd></div><div><dt>协议</dt><dd>v1</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="export-diagnostics">导出脱敏诊断包</button></div>' }, target);
      break;
    case "session-filter":
      state.windows.sessionFilter = normalizedText(target).replace(/\s+\d+$/, "");
      target.parentElement.querySelectorAll(":scope > button").forEach((button) => button.classList.toggle("is-active", button === target));
      document.querySelectorAll('[data-screen="windows-sessions"] .session-table-row').forEach((row, index) => {
        const visible = state.windows.sessionFilter === "运行中" || (state.windows.sessionFilter === "已退出" && index === 1) || false;
        row.classList.toggle("is-filtered-out", !visible);
      });
      saveState();
      break;
    case "session-details": {
      const name = target.querySelector("b").textContent;
      openDrawer({ eyebrow: "受管会话", title: name, content: `<div class="status-callout">${escapeHtml(normalizedText(target.querySelector(".status-pill")))}</div><dl class="detail-list"><div><dt>Shell</dt><dd>${escapeHtml(normalizedText(target.children[1]))}</dd></div><div><dt>Session Host</dt><dd>Healthy</dd></div><div><dt>输入租约</dt><dd>无</dd></div><div><dt>加密 Buffer</dt><dd>8.4 MB</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="open-android-terminal">在 Android 查看</button><button class="danger-outline" type="button" data-drawer-action="terminate-session">终止会话</button></div>` }, target);
      break;
    }
    case "disconnect-viewer":
      showDialog({ eyebrow: "当前连接", title: "断开 Pixel 10 Pro？", content: '<p>远端查看和现有输入租约会立即结束，本地 Shell 保持运行。</p>', confirmText: "断开查看者", danger: true, onConfirm: () => { state.windows.viewerConnected = false; renderWindowsState(); saveState(); showToast("查看者已断开", "本地会话未受影响。", "success"); } });
      break;
    case "check-update":
    case "server-update":
      target.setAttribute("aria-busy", "true");
      window.setTimeout(() => { target.removeAttribute("aria-busy"); showToast("已是最新版本", "TermLink 1.0.0 · 签名验证通过", "success"); }, 700);
      break;
    case "settings-tab":
      renderSettingsTab(normalizedText(target));
      break;
    case "clear-history":
      showDialog({ eyebrow: "本地加密记录", title: "清理本机历史？", content: '<p>只删除 OFFICE-PC 上当前 Windows 用户的加密历史块，不终止会话，也不撤销设备。</p>', confirmText: "清理 182 MB", danger: true, onConfirm: () => showToast("本地历史已清理", "释放 182 MB；删除证明已记录。", "success") });
      break;
    case "export-diagnostics":
      showToast("脱敏诊断包已生成", "不包含命令、终端正文、Token 或密钥。", "success");
      break;
    case "manage-account":
      openDrawer({ eyebrow: "账户", title: "Alex Chen", content: '<dl class="detail-list"><div><dt>当前方案</dt><dd>Free</dd></div><div><dt>可信设备</dt><dd>4</dd></div><div><dt>远程 PC</dt><dd>1 / 1</dd></div><div><dt>实时会话</dt><dd>2 / 3</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="security">管理可信设备</button><button type="button" data-drawer-action="signout">退出当前 Agent</button></div>' }, target);
      break;
    case "server-nav":
      activateScreen(workspace, target.dataset.serverScreen);
      break;
    case "server-devices":
      openDrawer({ eyebrow: "设备与授权", title: "服务端信任元数据", content: '<div class="status-callout">4 台可信设备 · 1 个待批准请求</div><dl class="detail-list"><div><dt>MANAGER</dt><dd>Pixel 10 Pro</dd></div><div><dt>Windows Agent</dt><dd>1 active</dd></div><div><dt>Android</dt><dd>2 active</dd></div><div><dt>撤销列表</dt><dd>1 entry</dd></div></dl><div class="security-callout">服务端只保存设备公钥、授权范围与不可逆标识；同账号登录不会自动获得控制权。</div>' }, target);
      break;
    case "server-settings":
      openDrawer({ eyebrow: "Server Console", title: "运维设置", content: '<div class="field-stack"><label>环境<select><option>production</option><option>staging</option></select></label><label>审计保留时间<select><option>90 天</option><option>180 天</option><option>365 天</option></select></label><label>告警渠道<select><option>Webhook（已验证）</option><option>Email</option></select></label></div><div class="security-callout">Secret 由 Docker Secret 或外部密钥管理器注入，控制台不能读取或回显 Secret 值。</div>' }, target);
      break;
    case "notifications":
      openDrawer({ eyebrow: "Server Console", title: "运维通知", content: '<div class="status-callout">当前没有阻断性告警</div><dl class="detail-list"><div><dt>2 分钟前</dt><dd>Backup OK</dd></div><div><dt>18 分钟前</dt><dd>Slow ACK</dd></div><div><dt>1 小时前</dt><dd>Catalog v7</dd></div></dl>' }, target);
      break;
    case "open-server-deployment":
      activateScreen(workspace, "server-deployment");
      break;
    case "open-server-audit":
      activateScreen(workspace, "server-audit");
      break;
    case "refresh-connections":
      target.setAttribute("aria-busy", "true");
      target.textContent = "刷新中…";
      window.setTimeout(() => { target.removeAttribute("aria-busy"); target.textContent = "刷新"; showToast("连接状态已刷新", "3 个连接 · P95 42 ms", "success"); }, 650);
      break;
    case "connection-details": {
      const row = target.closest(".data-row");
      const id = row.querySelector("b").textContent;
      openDrawer({ eyebrow: "连接路由", title: id, content: '<div class="status-callout">Healthy · Payload 永不可见</div><dl class="detail-list"><div><dt>角色</dt><dd>Android</dd></div><div><dt>Session Route</dt><dd>2</dd></div><div><dt>延迟</dt><dd>28 ms</dd></div><div><dt>ACK Lag</dt><dd>4</dd></div><div><dt>密文吞吐</dt><dd>42 KB/s</dd></div></dl><div class="drawer-actions"><button type="button" data-drawer-action="copy-connection">复制截断 ID</button><button class="danger-outline" type="button" data-drawer-action="disconnect-route">断开连接</button></div><div class="security-callout">Server Console 只能查看路由元数据，不能展开 Ciphertext 或终端 Payload。</div>' }, target);
      break;
    }
    case "create-backup":
      showDialog({ eyebrow: "Docker 部署", title: "创建加密备份？", content: '<p>将创建 PostgreSQL 一致性快照并验证对象存储生命周期。Secret 与终端正文不会进入操作日志。</p>', confirmText: "开始备份", onConfirm: () => showToast("备份任务已排队", "任务 backup_…7f · 等待生产运维 API", "success") });
      break;
    case "show-compose":
      showDialog({ eyebrow: "只读配置", title: "Compose 安全摘要", content: '<pre class="code-panel">services:\n  relay:\n    image: termlink/server@sha256:…\n    read_only: true\n    user: 65532:65532\n    cap_drop: [ALL]\n    secrets: [REDACTED]</pre><div class="security-callout">控制台永不回显 Secret 值；生产部署必须固定镜像 Digest。</div>', confirmText: "关闭", hideCancel: true });
      break;
    case "restore-drill":
      showDialog({ eyebrow: "恢复演练", title: "启动隔离恢复演练？", content: '<p>在隔离网络中恢复最近备份并验证数据库、对象索引和签名，不切换生产流量。</p>', confirmText: "启动演练", onConfirm: () => showToast("恢复演练已启动", "隔离任务 restore_…91", "success") });
      break;
    case "audit-filter": {
      const label = normalizedText(target);
      target.parentElement.querySelectorAll(":scope > button").forEach((button) => button.classList.toggle("is-active", button === target));
      document.querySelectorAll('[data-screen="server-audit"] .audit-event').forEach((row) => {
        const visible = label === "全部事件" || normalizedText(row).includes(label.replace("与", "")) || (label === "输入租约" && /lease|租约/i.test(normalizedText(row)));
        row.classList.toggle("is-filtered-out", !visible);
      });
      break;
    }
    case "export-audit":
      showToast("审计元数据导出已准备", "已排除终端正文、命令、Token、完整 IP 与 Ciphertext。", "success");
      break;
    case "audit-policy":
      openDrawer({ eyebrow: "安全审计", title: "数据最小化策略", content: '<dl class="detail-list"><div><dt>终端正文</dt><dd>禁止</dd></div><div><dt>命令</dt><dd>禁止</dd></div><div><dt>Token / 密钥</dt><dd>禁止</dd></div><div><dt>完整 IP</dt><dd>禁止</dd></div><div><dt>Trace ID</dt><dd>允许</dd></div><div><dt>不可逆 Hash</dt><dd>允许</dd></div></dl>' }, target);
      break;
    case "sync-purchases":
      target.setAttribute("aria-busy", "true");
      window.setTimeout(() => { target.removeAttribute("aria-busy"); showToast("购买凭据同步完成", "只接受渠道签名或服务端 API 验证结果。", "success"); }, 780);
      break;
    case "publish-catalog":
      showDialog({ eyebrow: "Catalog v8 草稿", title: "发布套餐目录？", content: '<p>将把当前草稿发布为新的只读 Catalog 版本。存量订阅继续保留原版本，除非存在显式迁移规则。</p><div class="warning-callout">客户端上报的 isPro=true 永远不会直接生成 Entitlement。</div>', confirmText: "发布 v8", onConfirm: () => showToast("Catalog 发布请求已准备", "等待生产计费 API 与双人复核。", "warning") });
      break;
    case "new-plan":
      openDrawer({ eyebrow: "Catalog v8 草稿", title: "新建套餐", content: '<div class="field-stack"><label>套餐名称<input value="Team Monthly" maxlength="64"></label><label>计费周期<select><option>月付</option><option>年付</option><option>免费</option></select></label><label>远程 PC 上限<input type="number" min="1" value="10"></label><label>实时 Session 上限<input type="number" min="1" value="40"></label><label>降级目标<select><option>Free v3</option></select></label></div><div class="drawer-actions"><button type="button" data-drawer-action="save-plan">保存草稿</button></div><div class="security-callout">保存草稿不会影响客户端；必须发布新的 Catalog 版本后才可见。</div>' }, target);
      break;
    case "manage-plans":
      openDrawer({ eyebrow: "Catalog 草稿", title: "TermLink Pro Monthly", content: '<div class="field-stack"><label>显示名称<input value="TermLink Pro"></label><label>远程 PC 上限<input type="number" min="1" value="5"></label><label>实时 Session 上限<input type="number" min="1" value="20"></label><label>降级目标<select><option>Free v3</option></select></label></div><div class="drawer-actions"><button type="button" data-drawer-action="save-plan">保存草稿</button></div><div class="security-callout">最终价格与 Product ID 映射由购买渠道配置，不能写死在客户端。</div>' }, target);
      break;
    case "subscription-details": {
      const row = target.closest(".data-row");
      const id = row.querySelector("b").textContent;
      openDrawer({ eyebrow: "订阅与权益", title: id, content: '<dl class="detail-list"><div><dt>Catalog</dt><dd>v7</dd></div><div><dt>验证来源</dt><dd>Provider API</dd></div><div><dt>状态</dt><dd>Active</dd></div><div><dt>降级目标</dt><dd>Free v3</dd></div></dl><div class="security-callout">Entitlement 由服务端验证结果生成，客户端声明不能直接生效。</div>' }, target);
      break;
    }
    default:
      showToast("界面能力已定义", "此操作等待对应生产 API 接入，不会伪造成功状态。", "warning");
  }
}

function handleDrawerAction(action, target) {
  if (action === "replay") {
    closeDrawer();
    activateProduct("android");
    activateScreen(document.querySelector('[data-product="android"]'), "android-replay");
  } else if (["copy-session", "copy-connection"].includes(action)) {
    navigator.clipboard?.writeText(action === "copy-session" ? "sess_build_…91" : "conn_a3f8");
    showToast("截断标识已复制", "完整敏感标识不会显示在 UI。", "success");
  } else if (action === "save-trust") {
    closeDrawer();
    showToast("设备权限草稿已保存", "生产环境需由 MANAGER 签名确认。", "success");
  } else if (action === "revoke-trust") {
    const name = target.dataset.device;
    closeDrawer();
    showDialog({ eyebrow: "高风险操作", title: `撤销 ${name}？`, content: '<p>该设备的现有连接将立即断开，设备密钥会进入撤销列表。其他可信设备不受影响。</p>', confirmText: "撤销设备", danger: true, onConfirm: () => showToast("设备撤销请求已准备", "等待生产设备授权 API。", "warning") });
  } else if (action === "open-android-terminal") {
    closeDrawer();
    activateProduct("android");
    activateScreen(document.querySelector('[data-product="android"]'), "android-terminal");
  } else if (action === "terminate-session") {
    closeDrawer();
    showDialog({ eyebrow: "本机进程", title: "终止受管会话？", content: '<p>将向 Session Host 发送正常终止请求；超时后才允许强制终止。远程断开本身不会触发此操作。</p>', confirmText: "终止会话", danger: true, onConfirm: () => showToast("终止请求已准备", "等待 Session Host 生命周期 API。", "warning") });
  } else if (action === "export-diagnostics") {
    showToast("脱敏诊断包已生成", "终端正文与密钥已排除。", "success");
  } else if (action === "security") {
    closeDrawer();
    activateProduct("android");
    activateScreen(document.querySelector('[data-product="android"]'), "android-security");
  } else if (action === "save-plan") {
    closeDrawer();
    showToast("套餐草稿已保存", "尚未发布，不影响现有 Entitlement。", "success");
  } else if (action === "change-password") {
    closeDrawer();
    showDialog({ eyebrow: "账号安全", title: "修改密码", content: '<div class="field-stack"><label>当前密码<input type="password" autocomplete="current-password"></label><label>新密码<input type="password" autocomplete="new-password" placeholder="至少 14 个字符"></label><label>再次输入新密码<input type="password" autocomplete="new-password"></label></div><div class="security-callout">改密后撤销其他设备的刷新 Token，当前设备需要重新认证高风险操作。</div>', confirmText: "修改密码", onConfirm: () => showToast("密码已更新", "其他登录已撤销。", "success") });
  } else if (action === "setup-mfa") {
    closeDrawer();
    showDialog({ eyebrow: "多因素认证", title: "配置验证器", content: '<div class="pairing-code">JBSW Y3DP EHPK 3PXP</div><div class="field-stack"><label>验证器生成的 6 位代码<input inputmode="numeric" autocomplete="one-time-code" value="482913" maxlength="6"></label></div><div class="security-callout">二维码种子只显示一次。完成验证后生成一组一次性恢复码，服务端仅保存其 Hash。</div>', confirmText: "验证并启用", onConfirm: () => showToast("MFA 已启用", "恢复码只会显示和下载一次。", "success") });
  } else if (action === "revoke-logins") {
    closeDrawer();
    showDialog({ eyebrow: "账号安全", title: "撤销其他登录？", content: '<p>将撤销其他浏览器和客户端的刷新 Token。可信设备公钥不会被删除，但这些设备必须重新登录。</p>', confirmText: "撤销其他登录", danger: true, onConfirm: () => showToast("其他登录已撤销", "当前登录保持有效。", "success") });
  } else if (action === "save-registration-policy") {
    closeDrawer();
    showToast("注册策略已保存", "新请求立即使用更新后的策略。", "success");
  } else if (action === "create-account") {
    closeDrawer();
    showDialog({ eyebrow: "账号已创建", title: "邀请链接已生成", content: '<div class="status-callout">new-user@example.com · USER</div><p>邀请链接 24 小时内有效且只能使用一次。用户接受邀请后必须验证邮箱并设置自己的密码。</p>', confirmText: "完成", hideCancel: true, onConfirm: () => showToast("账号邀请已创建", "管理员无法查看用户设置的密码。", "success") });
  } else if (action === "send-password-reset") {
    closeDrawer();
    showToast("改密邮件已发送", "链接 30 分钟内有效且只能使用一次。", "success");
  } else if (action === "disable-account") {
    closeDrawer();
    showDialog({ eyebrow: "高风险操作", title: "禁用此账号？", content: '<p>将撤销全部登录并阻止新 Token 签发。设备授权和安全审计不会被删除，可由管理员重新启用。</p>', confirmText: "禁用账号", danger: true, onConfirm: () => showToast("账号禁用请求已准备", "等待重新认证和生产身份 API。", "warning") });
  } else if (action === "create-redemption-batch") {
    closeDrawer();
    showDialog({ eyebrow: "一次性兑换码导出", title: "100 个兑换码已生成", content: '<div class="status-callout">September Campaign · TermLink Pro · 30 天</div><pre class="code-panel">TL-X8QP-6M4K-PRO30\nTL-T2DN-9W7R-PRO30\nTL-C5HA-3J8V-PRO30\n… 共 100 个，仅本次可见</pre><div class="warning-callout">下载完成并关闭此窗口后，服务端只保留 HMAC/Hash，无法恢复完整码。请将 CSV 存放到受控位置。</div>', confirmText: "下载 CSV 并关闭", hideCancel: true, onConfirm: () => showToast("一次性 CSV 已准备", "完整码已从控制台内存清除。", "success") });
  } else if (action === "export-redemption-audit") {
    showToast("兑换审计已导出", "仅包含批次、截断前缀、结果和不可逆账号标识。", "success");
  } else if (action === "disable-redemption-batch") {
    closeDrawer();
    showDialog({ eyebrow: "兑换码管理", title: "停用此批次的未兑换码？", content: '<p>尚未兑换的码将立即失效；已兑换产生的 Entitlement 保持到原到期日，除非另有经过审计的撤销操作。</p>', confirmText: "停用未兑换码", danger: true, onConfirm: () => showToast("批次停用请求已准备", "等待重新认证和生产兑换 API。", "warning") });
  } else if (["disconnect-route", "signout"].includes(action)) {
    closeDrawer();
    showDialog({ eyebrow: "需要确认", title: action === "disconnect-route" ? "断开此路由连接？" : "退出当前 Agent？", content: '<p>操作会影响当前连接，但不会删除设备密钥、历史密文或本地 Shell。</p>', confirmText: "确认操作", danger: true, onConfirm: () => showToast("操作请求已准备", "等待生产授权 API。", "warning") });
  }
}

dialogConfirm.addEventListener("click", (event) => {
  event.preventDefault();
  const action = activeDialogAction;
  activeDialogAction = null;
  appDialog.close("confirm");
  action?.();
});

drawerClose.addEventListener("click", closeDrawer);
drawerBackdrop.addEventListener("click", closeDrawer);

commandInput.addEventListener("input", () => {
  commandIndex = 0;
  renderCommands(commandInput.value);
});

commandInput.addEventListener("keydown", (event) => {
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    const direction = event.key === "ArrowDown" ? 1 : -1;
    commandIndex = (commandIndex + direction + commandMatches.length) % commandMatches.length;
    renderCommands(commandInput.value);
  } else if (event.key === "Enter" && commandMatches[commandIndex]) {
    event.preventDefault();
    executeCommand(commandMatches[commandIndex].id);
  }
});

document.addEventListener("click", (event) => {
  const commandOption = event.target.closest("[data-command-id]");
  if (commandOption) {
    executeCommand(commandOption.dataset.commandId);
    return;
  }
  const drawerAction = event.target.closest("[data-drawer-action]");
  if (drawerAction) {
    handleDrawerAction(drawerAction.dataset.drawerAction, drawerAction);
    return;
  }
  const productTab = event.target.closest("[data-product-tab]");
  if (productTab) {
    activateProduct(productTab.dataset.productTab);
    return;
  }
  const screenTarget = event.target.closest("[data-screen-target]");
  if (screenTarget) {
    activateScreen(screenTarget.closest("[data-product]"), screenTarget.dataset.screenTarget);
    return;
  }
  const jumpTarget = event.target.closest("[data-jump-screen]");
  if (jumpTarget) {
    activateScreen(jumpTarget.closest("[data-product]"), jumpTarget.dataset.jumpScreen);
    return;
  }
  const actionable = event.target.closest("[data-action]");
  if (actionable) {
    if (actionable.matches('button[type="submit"]') && actionable.form?.matches(".mobile-auth-form, .web-auth-form")) return;
    handleAction(actionable.dataset.action, actionable, event);
    return;
  }
  const button = event.target.closest("button");
  if (button && !button.closest("dialog") && !button.disabled) {
    handleAction("unwired", button, event);
  }
});

document.addEventListener("submit", (event) => {
  const form = event.target;
  if (!form.matches(".mobile-auth-form, .web-auth-form")) return;
  event.preventDefault();
  if (!form.reportValidity()) return;
  const submitter = event.submitter || form.querySelector('button[type="submit"][data-action]');
  if (submitter) handleAction(submitter.dataset.action, submitter, event);
});

document.addEventListener("input", (event) => {
  const input = event.target;
  if (input.matches('[data-screen="windows-sessions"] input[type="search"]')) filterRows(input, ".session-table-row");
  else if (input.matches('[data-screen="server-connections"] input[type="search"]')) filterRows(input, ".data-row");
  else if (input.matches('[data-screen="server-audit"] input[type="search"]')) filterRows(input, ".audit-event");
  else if (input.matches('[data-screen="server-accounts"] input[type="search"]')) filterRows(input, ".account-table-row");
  else if (input.matches('[data-screen="server-redemptions"] input[type="search"]')) filterRows(input, ".redemption-table-row");
});

document.addEventListener("change", (event) => {
  const input = event.target;
  if (input.matches("[data-locale-select]")) {
    state.language = input.value;
    renderLanguage();
    saveState();
    showToast(state.language === "en-US" ? "Language updated" : "语言已更新", state.language === "system" ? "跟随系统" : state.language === "en-US" ? "English" : "简体中文", "success");
  } else if (input.matches('[data-screen="server-accounts"] .account-toolbar select, [data-screen="server-redemptions"] .account-toolbar select')) {
    const screen = input.closest("[data-screen]");
    const rowSelector = screen.dataset.screen === "server-accounts" ? ".account-table-row" : ".redemption-table-row";
    const search = screen.querySelector('input[type="search"]')?.value.trim().toLocaleLowerCase(activeLanguage()) || "";
    const filters = [...screen.querySelectorAll(".account-toolbar select")];
    screen.querySelectorAll(rowSelector).forEach((row) => {
      const rowText = normalizedText(row).toLocaleLowerCase(activeLanguage());
      const matchesSearch = !search || rowText.includes(search);
      const matchesFilters = filters.every((select) => select.selectedIndex === 0 || rowText.includes(normalizedText(select.selectedOptions[0]).toLocaleLowerCase(activeLanguage())));
      row.classList.toggle("is-filtered-out", !(matchesSearch && matchesFilters));
    });
  } else if (input.matches('[data-screen="windows-access"] select[aria-label="远程访问策略"]')) {
    state.windows.policyIndex = input.selectedIndex;
    state.windows.remoteEnabled = input.selectedIndex !== 2;
    renderWindowsState();
    saveState();
    showToast("远程策略已保存", policyLabels[state.windows.policyIndex], "success");
  } else if (input.matches('[data-screen="windows-settings"] input[type="text"]')) {
    state.windows.deviceName = input.value.trim() || "OFFICE-PC";
    saveState();
    showToast("设备名称已保存", state.windows.deviceName, "success");
  } else if (input.matches('[data-screen="windows-settings"] input[type="checkbox"]')) {
    state.windows.autoStart = input.checked;
    saveState();
    showToast("启动设置已保存", input.checked ? "登录后自动启动" : "仅手动启动", "success");
  } else if (input.matches('[data-screen="windows-settings"] select')) {
    state.windows.retention = input.value;
    saveState();
    showToast("记录策略已保存", input.value, "success");
  } else if (input.matches('[data-screen="windows-access"] input[type="checkbox"]')) {
    showToast("控制确认策略已保存", input.checked ? "已启用" : "已停用", "success");
  }
});

document.addEventListener("keydown", (event) => {
  const authTab = event.target.closest?.(".auth-mode-switch > [role=tab]");
  if (authTab && ["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
    const tabs = [...authTab.parentElement.querySelectorAll(":scope > [role=tab]")];
    const currentIndex = tabs.indexOf(authTab);
    const nextIndex = event.key === "Home"
      ? 0
      : event.key === "End"
        ? tabs.length - 1
        : (currentIndex + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
    event.preventDefault();
    tabs[nextIndex].focus();
    tabs[nextIndex].click();
  } else if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === "k") {
    event.preventDefault();
    if (!commandDialog.open) openCommandPalette();
  } else if (event.key === "Escape" && !detailDrawer.hidden) {
    closeDrawer();
  } else if (event.target.matches('[data-action="replay-seek"]') && ["ArrowLeft", "ArrowRight"].includes(event.key)) {
    event.preventDefault();
    seekReplay(state.replay.progress + (event.key === "ArrowRight" ? 2 : -2));
  }
});

window.addEventListener("hashchange", () => {
  parseRoute();
  activateProduct(state.product, { route: false });
});

wireMarkup();
parseRoute();
renderReplay();
renderControlState();
renderWindowsState();
renderSettingsTab(state.windows.settingsTab);
captureStaticTranslations();
renderLanguage();
activateProduct(state.product);

if (state.control.granted) {
  controlTimer = window.setInterval(renderControlState, 1000);
}
