(() => {
  "use strict";

  const STORAGE_KEY = "termlink-theme";
  const LIGHT = "light";
  const DARK = "dark";
  const root = document.documentElement;
  const systemPreference = window.matchMedia
    ? window.matchMedia("(prefers-color-scheme: dark)")
    : { matches: false };

  function readStoredTheme() {
    try {
      const value = window.localStorage.getItem(STORAGE_KEY);
      if (value === LIGHT || value === DARK) return value;
      if (value !== null) window.localStorage.removeItem(STORAGE_KEY);
    } catch (_) {
      // A system preference still provides a deterministic safe fallback.
    }
    return null;
  }

  function preferredTheme() {
    return systemPreference.matches ? DARK : LIGHT;
  }

  function applyTheme() {
    const theme = readStoredTheme() || preferredTheme();
    root.dataset.theme = theme;
    root.classList.toggle("pf-v5-theme-dark", theme === DARK);
    updateToggle(document.querySelector("[data-termlink-theme-toggle]"));
  }

  function translatedLabels() {
    const chinese = (document.documentElement.lang || "").toLowerCase().startsWith("zh");
    return chinese
      ? {
          accountAction: "返回个人中心",
          accountLabel: "个人中心",
          darkAction: "切换到深色主题",
          darkLabel: "深色主题",
          lightAction: "切换到明亮主题",
          lightLabel: "明亮主题",
        }
      : {
          accountAction: "Back to account center",
          accountLabel: "Account center",
          darkAction: "Switch to dark theme",
          darkLabel: "Dark theme",
          lightAction: "Switch to light theme",
          lightLabel: "Light theme",
        };
  }

  function updateToggle(button) {
    if (!button) return;
    const dark = root.dataset.theme === DARK;
    const labels = translatedLabels();
    button.setAttribute("aria-label", dark ? labels.lightAction : labels.darkAction);
    button.setAttribute("title", dark ? labels.lightAction : labels.darkAction);
    button.removeAttribute("aria-pressed");
    const label = button.querySelector(".tl-account-theme-toggle-label");
    const nextLabel = dark ? labels.lightLabel : labels.darkLabel;
    if (label && label.textContent !== nextLabel) label.textContent = nextLabel;
  }

  function persistTheme(theme) {
    try {
      window.localStorage.setItem(STORAGE_KEY, theme);
    } catch (_) {
      // The active page still changes even when storage is unavailable.
    }
  }

  function findAccountActionHost() {
    const header = document.querySelector('[data-testid="page-header"]') || document.querySelector(".pf-v5-c-masthead");
    if (!header) return null;
    return (
      header.querySelector(".pf-v5-c-toolbar__content-section") ||
      header.querySelector(".pf-v5-c-toolbar__content") ||
      header.querySelector(".pf-v5-c-masthead__content") ||
      header
    );
  }

  function ensureAccountBrand() {
    const brand = document.querySelector('[data-testid="page-header"] .pf-v5-c-masthead__brand');
    if (!brand) return;
    brand.classList.add("tl-account-brand");
    brand.setAttribute("aria-label", "TermLink Account Center");
    if (brand.querySelector(".tl-account-brand-copy")) return;

    const copy = document.createElement("span");
    copy.className = "tl-account-brand-copy";
    const name = document.createElement("strong");
    name.textContent = "TermLink";
    const context = document.createElement("small");
    context.textContent = "Account Center";
    copy.append(name, context);
    brand.append(copy);
  }

  function ensureAccountActions() {
    const labels = translatedLabels();
    const nativeReferrer = document.querySelector('[data-testid="referrer-link"]');
    const host = findAccountActionHost();
    if (!host) {
      if (nativeReferrer) {
        nativeReferrer.removeAttribute("data-termlink-native-referrer");
        nativeReferrer.removeAttribute("aria-hidden");
        nativeReferrer.removeAttribute("tabindex");
      }
      return;
    }

    let actions = document.querySelector("[data-termlink-account-actions]");
    if (!actions) {
      actions = document.createElement("div");
      actions.className = "tl-account-actions";
      actions.setAttribute("data-termlink-account-actions", "");
      host.append(actions);
    } else {
      if (actions.parentElement !== host || actions !== host.lastElementChild) host.append(actions);
    }

    if (nativeReferrer) {
      nativeReferrer.setAttribute("data-termlink-native-referrer", "");
      nativeReferrer.setAttribute("aria-hidden", "true");
      nativeReferrer.tabIndex = -1;
    }

    let referrer = actions.querySelector("[data-termlink-account-link]");
    if (!referrer) {
      referrer = document.createElement("a");
      referrer.href = "/account";
      referrer.setAttribute("data-termlink-account-link", "");
      actions.append(referrer);
    }
    referrer.classList.add("tl-account-return");
    referrer.setAttribute("aria-label", labels.accountAction);
    const currentIcon = referrer.querySelector(".tl-account-return-icon");
    const currentLabel = referrer.querySelector(".tl-account-return-label");
    if (
      referrer.dataset.termlinkLabel !== labels.accountLabel ||
      !currentIcon ||
      !currentIcon.querySelector("svg") ||
      !currentLabel ||
      currentLabel.textContent !== labels.accountLabel
    ) {
      referrer.replaceChildren();
      const returnIcon = document.createElement("span");
      returnIcon.className = "tl-account-return-icon";
      returnIcon.setAttribute("aria-hidden", "true");
      returnIcon.innerHTML = '<svg viewBox="0 0 24 24" focusable="false"><path d="M19 12H5m6-6-6 6 6 6"/></svg>';
      const returnLabel = document.createElement("span");
      returnLabel.className = "tl-account-return-label";
      returnLabel.textContent = labels.accountLabel;
      referrer.append(returnIcon, returnLabel);
      referrer.dataset.termlinkLabel = labels.accountLabel;
    }

    if (actions.querySelector("[data-termlink-theme-toggle]")) {
      updateToggle(actions.querySelector("[data-termlink-theme-toggle]"));
      return;
    }

    const button = document.createElement("button");
    button.type = "button";
    button.className = "tl-account-theme-toggle";
    button.setAttribute("data-termlink-theme-toggle", "");

    const icon = document.createElement("span");
    icon.className = "tl-account-theme-toggle-icon";
    icon.setAttribute("aria-hidden", "true");
    icon.innerHTML = '<svg viewBox="0 0 24 24" focusable="false"><path class="theme-icon-moon" d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/><g class="theme-icon-sun"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.42-1.42M17.66 6.34l1.41-1.41"/></g></svg>';
    const label = document.createElement("span");
    label.className = "tl-account-theme-toggle-label";
    button.append(icon, label);
    button.addEventListener("click", () => {
      persistTheme(root.dataset.theme === DARK ? LIGHT : DARK);
      applyTheme();
    });
    actions.insertBefore(button, referrer);
    updateToggle(button);
  }

  const handleSystemChange = () => {
    if (!readStoredTheme()) applyTheme();
  };
  if (systemPreference.addEventListener) systemPreference.addEventListener("change", handleSystemChange);
  else systemPreference.addListener?.(handleSystemChange);
  window.addEventListener("storage", (event) => {
    if (event.key === STORAGE_KEY || event.key === null) applyTheme();
  });

  const ensureAccountChrome = () => {
    ensureAccountBrand();
    ensureAccountActions();
  };
  const observer = new MutationObserver(ensureAccountChrome);
  const start = () => {
    ensureAccountChrome();
    observer.observe(document.body, { childList: true, subtree: true });
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();

  applyTheme();
})();
