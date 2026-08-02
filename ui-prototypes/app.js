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
    copy: "Windows 端显示账号统一权益和配额，但购买入口可跳转到合规渠道完成。",
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

function activateProduct(product) {
  document.querySelectorAll("[data-product-tab]").forEach((button) => {
    button.classList.toggle("is-active", button.dataset.productTab === product);
  });
  document.querySelectorAll("[data-product]").forEach((workspace) => {
    workspace.classList.toggle(
      "is-active",
      workspace.dataset.product === product,
    );
  });
}

function activateScreen(workspace, screenId) {
  workspace.querySelectorAll("[data-screen]").forEach((screen) => {
    screen.classList.toggle("is-active", screen.dataset.screen === screenId);
  });
  workspace.querySelectorAll("[data-screen-target]").forEach((button) => {
    button.classList.toggle(
      "is-active",
      button.dataset.screenTarget === screenId,
    );
  });
  const note = notes[screenId];
  if (!note) return;
  const title = workspace.querySelector("[data-note-title]");
  const copy = workspace.querySelector("[data-note-copy]");
  const list = workspace.querySelector("[data-note-list]");
  if (title) title.textContent = note.title;
  if (copy) copy.textContent = note.copy;
  if (list)
    list.innerHTML = note.items.map((item) => `<li>${item}</li>`).join("");
}

document.querySelectorAll("[data-product-tab]").forEach((button) => {
  button.addEventListener("click", () =>
    activateProduct(button.dataset.productTab),
  );
});

document.querySelectorAll("[data-screen-target]").forEach((button) => {
  button.addEventListener("click", () => {
    const workspace = button.closest("[data-product]");
    activateScreen(workspace, button.dataset.screenTarget);
  });
});

document.querySelectorAll("[data-jump-screen]").forEach((button) => {
  button.addEventListener("click", () => {
    const workspace = button.closest("[data-product]");
    activateScreen(workspace, button.dataset.jumpScreen);
  });
});

document.querySelector(".theme-toggle").addEventListener("click", () => {
  const root = document.documentElement;
  root.dataset.theme = root.dataset.theme === "light" ? "dark" : "light";
});

const controlButton = document.querySelector("#request-control");
const controlState = document.querySelector("#android-control-state");
controlButton.addEventListener("click", () => {
  const controlling = controlButton.classList.toggle("is-active");
  controlButton.querySelector("span").textContent = controlling
    ? "释放控制权 · 28 秒"
    : "申请 30 秒控制权";
  controlState.textContent = controlling ? "控制中" : "只读";
  controlState.classList.toggle("is-controlling", controlling);
});

const policyButton = document.querySelector("#tray-policy-toggle");
const policyLabel = document.querySelector("#tray-policy-label");
const policies = ["允许已批准设备控制", "仅允许远程查看", "仅本机"];
let policyIndex = 0;
policyButton.addEventListener("click", () => {
  policyIndex = (policyIndex + 1) % policies.length;
  policyLabel.textContent = policies[policyIndex];
});

const upgradeButton = document.querySelector("#android-upgrade");
upgradeButton.addEventListener("click", () => {
  upgradeButton.querySelector("span").textContent = "购买确认由应用商店展示";
  window.setTimeout(() => {
    upgradeButton.querySelector("span").textContent = "查看月付价格并订阅";
  }, 1800);
});
