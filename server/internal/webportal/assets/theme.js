(() => {
  "use strict";

  const STORAGE_KEY = "termlink-theme";
  const TRANSFER_PARAMETER = "termlink_theme";
  const LIGHT = "light";
  const DARK = "dark";
  const SYSTEM = "system";
  const root = document.documentElement;
  const isChineseUI = (navigator.languages || [navigator.language || "en"])
    .some((value) => String(value).toLowerCase().startsWith("zh"));
  const systemPreference = window.matchMedia
    ? window.matchMedia("(prefers-color-scheme: dark)")
    : { matches: false };

  function isPreference(value) {
    return value === SYSTEM || value === LIGHT || value === DARK;
  }

  function readStoredTheme() {
    try {
      const value = window.localStorage.getItem(STORAGE_KEY);
      if (isPreference(value)) return value;
      if (value !== null) window.localStorage.removeItem(STORAGE_KEY);
    } catch (_) {
      // Storage can be unavailable in private browsing or with strict policies.
    }
    return null;
  }

  function preferredTheme() {
    return systemPreference.matches ? DARK : LIGHT;
  }

  function resolvedTheme(preference) {
    return preference === SYSTEM ? preferredTheme() : preference;
  }

  function nextPreference(preference) {
    if (preference === SYSTEM) return LIGHT;
    if (preference === LIGHT) return DARK;
    return SYSTEM;
  }

  function labelsFor(preference) {
    const next = nextPreference(preference);
    if (isChineseUI) {
      return {
        label: preference === SYSTEM ? "跟随系统" : (preference === LIGHT ? "明亮" : "深色"),
        aria: `当前${preference === SYSTEM ? "跟随系统" : (preference === LIGHT ? "明亮主题" : "深色主题")}，切换到${next === SYSTEM ? "跟随系统" : (next === LIGHT ? "明亮主题" : "深色主题")}`,
      };
    }
    return {
      label: preference === SYSTEM ? "System" : (preference === LIGHT ? "Light" : "Dark"),
      aria: `Current: ${preference}. Switch to ${next}`,
    };
  }

  function render(preference) {
    const theme = resolvedTheme(preference);
    root.dataset.theme = theme;
    root.dataset.themePreference = preference;
    const labels = labelsFor(preference);
    document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
      const label = button.querySelector(".theme-toggle-label");
      if (label) label.textContent = labels.label;
      button.setAttribute("aria-label", labels.aria);
      button.setAttribute("title", labels.aria);
      button.removeAttribute("aria-pressed");
      button.dataset.theme = theme;
      button.dataset.themePreference = preference;
    });
    document.querySelectorAll("a[data-theme-transfer]").forEach((link) => {
      const base = link.dataset.themeTransferBase || link.getAttribute("href");
      if (!base) return;
      link.dataset.themeTransferBase = base;
      try {
        const target = new URL(base, window.location.href);
        target.searchParams.set(TRANSFER_PARAMETER, preference);
        link.href = target.origin === window.location.origin
          ? `${target.pathname}${target.search}${target.hash}`
          : target.href;
      } catch (_) {
        // Keep the server-rendered safe target when URL parsing fails.
      }
    });
  }

  function persist(theme) {
    try {
      window.localStorage.setItem(STORAGE_KEY, theme);
    } catch (_) {
      // Theme still applies for this page when persistent storage is unavailable.
    }
  }

  function toggleTheme() {
    const current = isPreference(root.dataset.themePreference) ? root.dataset.themePreference : SYSTEM;
    const next = nextPreference(current);
    persist(next);
    render(next);
  }

  function applyStoredOrSystemTheme() {
    render(readStoredTheme() || SYSTEM);
  }

  document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
    button.addEventListener("click", toggleTheme);
  });

  document.querySelectorAll("form[data-confirm-action]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      const account = form.dataset.account || (isChineseUI ? "该账号" : "this account");
      const action = form.dataset.confirmAction;
      let message;
      if (action === "role") {
        const roleSelect = form.querySelector("select[name=role]");
        const role = isChineseUI
          ? (roleSelect?.selectedOptions?.[0]?.textContent?.trim() || "新角色")
          : (roleSelect?.value || "the selected role");
        message = isChineseUI
          ? `确认将 ${account} 的角色改为 ${role}？该变更会立即生效并写入安全审计。`
          : `Change ${account} to ${role}? This takes effect immediately and is recorded in the security audit.`;
      } else if (action === "lock") {
        message = isChineseUI
          ? `确认停用 ${account}？其门户访问和新会话将被拒绝。`
          : `Disable ${account}? Portal access and new sessions will be denied.`;
      } else if (action === "unlock") {
        message = isChineseUI
          ? `确认启用 ${account}？这会恢复其门户访问权限。`
          : `Enable ${account}? This restores portal access.`;
      }
      if (message && !window.confirm(message)) event.preventDefault();
    });
  });

  const accountMenus = [...document.querySelectorAll("details.admin-account-menu, details.account-menu")];
  accountMenus.forEach((menu) => {
    menu.addEventListener("toggle", () => {
      if (!menu.open) return;
      accountMenus.forEach((other) => { if (other !== menu) other.removeAttribute("open"); });
    });
  });
  document.addEventListener("click", (event) => {
    accountMenus.forEach((menu) => {
      if (menu.open && !menu.contains(event.target)) menu.removeAttribute("open");
    });
  });
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    accountMenus.forEach((menu) => menu.removeAttribute("open"));
  });

  const identityDialog = document.querySelector("[data-identity-dialog-root]");
  if (identityDialog instanceof HTMLDialogElement) {
    const frame = identityDialog.querySelector("[data-identity-dialog-frame]");
    const pending = identityDialog.querySelector("[data-identity-dialog-pending]");
    const description = identityDialog.querySelector("[data-identity-dialog-description]");
    const fallback = identityDialog.querySelector("[data-identity-dialog-fallback]");
    const baseURL = identityDialog.dataset.identityUrl || "/account/identity";
    const actionCopy = isChineseUI ? {
      password: "修改密码由身份服务验证当前凭据后完成。",
      mfa: "绑定或管理 Google 身份验证器，Secret 仅由身份服务处理。",
      sessions: "查看并撤销其他浏览器登录会话。",
    } : {
      password: "The identity service verifies the current credentials before changing the password.",
      mfa: "Set up or manage an authenticator. The identity service handles the secret.",
      sessions: "Review and revoke other browser sign-in sessions.",
    };
    let loadedURL = "";
    document.querySelectorAll("[data-identity-dialog]").forEach((button) => {
      button.addEventListener("click", () => {
        const action = Object.hasOwn(actionCopy, button.dataset.identityDialog) ? button.dataset.identityDialog : "password";
        const actionURL = `${baseURL.replace(/\/$/, "")}/${action}`;
        if (description) description.textContent = actionCopy[action];
        if (loadedURL !== actionURL && frame instanceof HTMLIFrameElement) {
          if (pending) pending.hidden = false;
          frame.src = actionURL;
          loadedURL = actionURL;
        }
        if (fallback instanceof HTMLAnchorElement) fallback.href = actionURL;
        identityDialog.showModal();
      });
    });
    if (frame instanceof HTMLIFrameElement) {
      frame.addEventListener("load", () => { if (pending) pending.hidden = true; });
    }
    identityDialog.querySelectorAll("[data-identity-dialog-close]").forEach((button) => {
      button.addEventListener("click", () => identityDialog.close());
    });
    identityDialog.addEventListener("click", (event) => {
      if (event.target === identityDialog) identityDialog.close();
    });
  }

  const stepUpResult = document.body?.dataset.sensitiveStepUpStatus;
  if (stepUpResult && window.parent !== window) {
    window.parent.postMessage({
      type: "termlink:sensitive-step-up",
      status: stepUpResult,
      method: document.body.dataset.sensitiveStepUpMethod || "",
    }, window.location.origin);
  }

  // Operation reasons are generated from the authenticated route on the
  // server. Keep the field in the submitted form for compatibility with the
  // existing audited mutation contract, without asking users to type it.
  document.querySelectorAll('[name="reason"]').forEach((field) => {
    if (field instanceof HTMLInputElement && field.type === "hidden") return;
    const hidden = document.createElement("input");
    hidden.type = "hidden";
    hidden.name = "reason";
    hidden.value = "";
    const label = field.closest("label");
    if (label) label.replaceWith(hidden);
    else field.replaceWith(hidden);
  });

  const sensitiveForms = [...document.querySelectorAll("form")].filter((form) => {
    return form.method.toLowerCase() === "post"
      && form.querySelector('[name="idempotency_key"]')
      && form.querySelector('[name="reason"]');
  });
  if (sensitiveForms.length > 0 && typeof HTMLDialogElement !== "undefined") {
    const stepUpCopy = isChineseUI ? {
      eyebrow: "安全验证",
      title: "确认高风险操作",
      close: "关闭安全验证",
      opening: "正在打开身份验证…",
      wait: "请稍候",
      frameTitle: "TermLink 高风险操作安全验证",
      detail: "验证通过后 20 分钟内不再重复输入；已绑定 MFA 时验证动态码，未绑定时验证登录密码。",
      notCompleted: "验证未完成，原操作尚未执行。",
    } : {
      eyebrow: "SECURE VERIFICATION",
      title: "Confirm sensitive operation",
      close: "Close secure verification",
      opening: "Opening identity verification…",
      wait: "Please wait",
      frameTitle: "TermLink sensitive-operation verification",
      detail: "Verification remains valid for 20 minutes. TermLink requests an MFA code when configured, otherwise the account password.",
      notCompleted: "Verification was not completed. The original operation was not performed.",
    };
    const dialog = document.createElement("dialog");
    dialog.className = "sensitive-step-up-dialog";
    dialog.setAttribute("aria-labelledby", "sensitive-step-up-title");
    dialog.innerHTML = `
      <div class="sensitive-step-up-shell">
        <header class="sensitive-step-up-header">
          <div><p class="eyebrow">${stepUpCopy.eyebrow}</p><h2 id="sensitive-step-up-title">${stepUpCopy.title}</h2></div>
          <button class="sensitive-step-up-close" type="button" aria-label="${stepUpCopy.close}">×</button>
        </header>
        <div class="sensitive-step-up-frame-wrap">
          <div class="sensitive-step-up-pending"><strong>${stepUpCopy.opening}</strong><span>${stepUpCopy.wait}</span></div>
          <iframe class="sensitive-step-up-frame" title="${stepUpCopy.frameTitle}" referrerpolicy="no-referrer"></iframe>
        </div>
        <footer class="sensitive-step-up-footer">
          <p>${stepUpCopy.detail}</p>
          <span class="sensitive-step-up-status" role="status" aria-live="polite"></span>
        </footer>
      </div>`;
    document.body.append(dialog);
    const frame = dialog.querySelector(".sensitive-step-up-frame");
    const pending = dialog.querySelector(".sensitive-step-up-pending");
    const status = dialog.querySelector(".sensitive-step-up-status");
    const closeButton = dialog.querySelector(".sensitive-step-up-close");
    let pendingForm = null;
    const checkingForms = new WeakSet();

    const closeStepUp = () => {
      dialog.close();
      pendingForm = null;
      if (frame instanceof HTMLIFrameElement) frame.src = "about:blank";
      if (status) status.textContent = "";
    };
    closeButton?.addEventListener("click", closeStepUp);
    dialog.addEventListener("click", (event) => {
      if (event.target === dialog) closeStepUp();
    });
    dialog.addEventListener("cancel", () => {
      pendingForm = null;
      if (frame instanceof HTMLIFrameElement) frame.src = "about:blank";
    });
    frame?.addEventListener("load", () => {
      if (pending) pending.hidden = true;
    });

    const openStepUp = (form) => {
      if (!form.isConnected) return;
      pendingForm = form;
      if (pending) pending.hidden = false;
      if (status) status.textContent = "";
      const preference = isPreference(root.dataset.themePreference) ? root.dataset.themePreference : SYSTEM;
      if (frame instanceof HTMLIFrameElement) {
        frame.src = `/auth/step-up?${TRANSFER_PARAMETER}=${encodeURIComponent(preference)}`;
      }
      dialog.showModal();
    };

    sensitiveForms.forEach((form) => {
      form.addEventListener("submit", async (event) => {
        event.preventDefault();
        if (pendingForm || checkingForms.has(form)) return;
        checkingForms.add(form);
        const controller = typeof AbortController === "undefined" ? null : new AbortController();
        const timeout = window.setTimeout(() => controller?.abort(), 4000);
        try {
          const response = await fetch("/auth/step-up/status", {
            method: "GET",
            credentials: "same-origin",
            cache: "no-store",
            headers: {"Accept": "application/json"},
            signal: controller?.signal,
          });
          const result = await response.json().catch(() => ({}));
          if (response.ok && result?.verified === true) {
            HTMLFormElement.prototype.submit.call(form);
            return;
          }
        } catch (_) {
          // Compatibility and transient failures fall back to the secure prompt.
        } finally {
          window.clearTimeout(timeout);
          checkingForms.delete(form);
        }
        openStepUp(form);
      });
    });

    window.addEventListener("message", (event) => {
      if (event.origin !== window.location.origin || !(frame instanceof HTMLIFrameElement)
          || event.source !== frame.contentWindow || event.data?.type !== "termlink:sensitive-step-up") return;
      if (event.data.status !== "verified") {
        if (status) status.textContent = stepUpCopy.notCompleted;
        return;
      }
      const form = pendingForm;
      closeStepUp();
      if (form?.isConnected) HTMLFormElement.prototype.submit.call(form);
    });
  }

  const handleSystemChange = () => {
    if ((readStoredTheme() || SYSTEM) === SYSTEM) render(SYSTEM);
  };
  if (systemPreference.addEventListener) systemPreference.addEventListener("change", handleSystemChange);
  else systemPreference.addListener?.(handleSystemChange);
  window.addEventListener("storage", (event) => {
    if (event.key === STORAGE_KEY || event.key === null) applyStoredOrSystemTheme();
  });

  applyStoredOrSystemTheme();
})();
