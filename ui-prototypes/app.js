"use strict";

const STORAGE_KEY = "termlink.product-ui.v2";

const notes = {
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
};

const defaults = {
  theme: null,
  product: "android",
  screens: {
    android: "android-devices",
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

let commandMatches = [];
let commandIndex = 0;
let activeDialogAction = null;
let drawerReturnFocus = null;
let controlTimer = null;

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
  toast.innerHTML = `<span></span><span><strong>${escapeHtml(title)}</strong>${
    detail ? `<small>${escapeHtml(detail)}</small>` : ""
  }</span><button type="button" aria-label="关闭通知">×</button>`;
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
  if (title) title.textContent = note.title;
  if (copy) copy.textContent = note.copy;
  if (list) {
    list.replaceChildren(
      ...note.items.map((item) => {
        const li = document.createElement("li");
        li.textContent = item;
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
  button.textContent = nextLight ? "明亮主题" : "深色主题";
  button.setAttribute("aria-label", nextLight ? "切换到明亮主题" : "切换到深色主题");
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
  dialogEyebrow.textContent = eyebrow;
  dialogTitle.textContent = title;
  dialogContent.innerHTML = content;
  dialogConfirm.textContent = confirmText;
  dialogConfirm.classList.toggle("is-danger", danger);
  const cancel = appDialog.querySelector(".secondary-dialog-action");
  cancel.textContent = cancelText;
  cancel.hidden = hideCancel;
  activeDialogAction = onConfirm;
  appDialog.showModal();
}

function openDrawer({ eyebrow = "详情", title, content }, trigger = null) {
  drawerReturnFocus = trigger || document.activeElement;
  drawerEyebrow.textContent = eyebrow;
  drawerTitle.textContent = title;
  drawerContent.innerHTML = content;
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
    ["android-devices", "设备", "OFFICE-PC 与实时状态", "Android"],
    ["android-terminal", "build-release", "打开 OFFICE-PC 实时终端", "Android"],
    ["android-replay", "deploy-prod 回放", "历史记录 · exit code 0", "Android"],
    ["android-security", "可信设备", "权限、指纹与批准新设备", "Android"],
    ["android-membership", "会员与配额", "Free · 1/1 PC · 2/3 Live", "Android"],
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
  ].map(([screen, label, description, group]) => ({
    id: screen,
    screen,
    product: screen.split("-")[0],
    label,
    description,
    group,
  }));
}

function renderCommands(query = "") {
  const term = query.trim().toLocaleLowerCase("zh-CN");
  commandMatches = commandCatalog().filter((item) =>
    `${item.label} ${item.description} ${item.group}`.toLocaleLowerCase("zh-CN").includes(term),
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
  const androidDevices = document.querySelector('[data-screen="android-devices"]');
  androidDevices.querySelector(".mobile-search").dataset.action = "command";
  androidDevices.querySelector(".avatar-button").dataset.action = "account";
  setActionByText(androidDevices, "排序", "device-sort");
  [...androidDevices.querySelectorAll(".mobile-bottom-nav button")].forEach((button) => {
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
        (action === "mobile-settings" && ["android-security", "android-membership"].includes(screenId));
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

function handleAction(action, target, event) {
  const workspace = target.closest("[data-product]");
  switch (action) {
    case "theme":
      toggleTheme();
      break;
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
      activateScreen(workspace, "android-security");
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
    handleAction(actionable.dataset.action, actionable, event);
    return;
  }
  const button = event.target.closest("button");
  if (button && !button.closest("dialog") && !button.disabled) {
    handleAction("unwired", button, event);
  }
});

document.addEventListener("input", (event) => {
  const input = event.target;
  if (input.matches('[data-screen="windows-sessions"] input[type="search"]')) filterRows(input, ".session-table-row");
  else if (input.matches('[data-screen="server-connections"] input[type="search"]')) filterRows(input, ".data-row");
  else if (input.matches('[data-screen="server-audit"] input[type="search"]')) filterRows(input, ".audit-event");
});

document.addEventListener("change", (event) => {
  const input = event.target;
  if (input.matches('[data-screen="windows-access"] select[aria-label="远程访问策略"]')) {
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
  if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === "k") {
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
renderTheme();
renderReplay();
renderControlState();
renderWindowsState();
renderSettingsTab(state.windows.settingsTab);
activateProduct(state.product);

if (state.control.granted) {
  controlTimer = window.setInterval(renderControlState, 1000);
}
