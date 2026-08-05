(() => {
  "use strict";

  const STORAGE_KEY = "termlink-theme";
  const TRANSFER_PARAMETER = "termlink_theme";
  const LIGHT = "light";
  const DARK = "dark";
  const root = document.documentElement;
  let accountReturnURL = null;
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
    updateAccountReturnTargets();
  }

  function safeAccountReturnURL(candidate) {
    if (!candidate) return null;
    try {
      const target = new URL(candidate, window.location.href);
      const localHTTP = target.protocol === "http:" &&
        (target.hostname === "localhost" || target.hostname === "127.0.0.1" || target.hostname === "::1");
      const normalizedPath = target.pathname.replace(/\/+$/, "") || "/";
      if ((target.protocol !== "https:" && !localHTTP) || target.username || target.password || normalizedPath !== "/account") {
        return null;
      }
      target.hash = "";
      target.searchParams.delete(TRANSFER_PARAMETER);
      return target.href;
    } catch (_) {
      return null;
    }
  }

  function environmentAccountReturnURL() {
    try {
      const environment = JSON.parse(document.getElementById("environment")?.textContent || "{}");
      return safeAccountReturnURL(environment.referrerUrl) ||
        safeAccountReturnURL(environment.accountReturnUrl) ||
        safeAccountReturnURL(environment.logoUrl);
    } catch (_) {
      return null;
    }
  }

  function themedAccountReturnURL(nativeReferrer) {
    if (!accountReturnURL) {
      accountReturnURL = safeAccountReturnURL(nativeReferrer?.getAttribute("href")) ||
        environmentAccountReturnURL() || safeAccountReturnURL("/account");
    }
    const target = new URL(accountReturnURL);
    target.searchParams.set(TRANSFER_PARAMETER, root.dataset.theme === DARK ? DARK : LIGHT);
    return target.href;
  }

  function updateAccountReturnTargets(nativeReferrer) {
    const target = themedAccountReturnURL(nativeReferrer);
    document.querySelectorAll("[data-termlink-account-link], [data-termlink-sidebar-return], [data-termlink-account-brand]").forEach((link) => {
      link.href = target;
    });
  }

  function translatedLabels() {
    const chinese = (document.documentElement.lang || "").toLowerCase().startsWith("zh");
    return chinese
      ? {
          accountAction: "返回个人中心",
          accountLabel: "个人中心",
          accountHint: "TermLink 账号总览",
          identityDescription: "账号资料、密码、多因素认证与活跃登录",
          identityNavLabel: "身份管理",
          identityTitle: "身份与安全",
          darkAction: "切换到深色主题",
          darkLabel: "深色主题",
          lightAction: "切换到明亮主题",
          lightLabel: "明亮主题",
        }
      : {
          accountAction: "Back to account center",
          accountLabel: "Account center",
          accountHint: "TermLink account overview",
          identityDescription: "Profile, password, multifactor authentication, and active sessions",
          identityNavLabel: "Identity management",
          identityTitle: "Identity & security",
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
    brand.setAttribute("data-termlink-account-brand", "");
    brand.setAttribute("aria-label", "TermLink Account Center");
    brand.href = themedAccountReturnURL();
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

    host.querySelectorAll(".pf-v5-c-avatar").forEach((avatar) => {
      const item = avatar.closest(".pf-v5-c-toolbar__item");
      if (item) item.setAttribute("data-termlink-native-avatar", "");
    });

    let referrer = actions.querySelector("[data-termlink-account-link]");
    if (!referrer) {
      referrer = document.createElement("a");
      referrer.setAttribute("data-termlink-account-link", "");
      actions.append(referrer);
    }
    // Keycloak validates referrer_uri against the registered web client. Keep
    // that absolute portal target so split-origin production deployments do
    // not strand the user on the identity host.
    referrer.href = themedAccountReturnURL(nativeReferrer);
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

  function ensureAccountSidebar() {
    const sidebarBody = document.querySelector(".pf-v5-c-page__sidebar-body");
    if (!sidebarBody) return;
    const labels = translatedLabels();

    let context = sidebarBody.querySelector("[data-termlink-account-context]");
    if (!context) {
      context = document.createElement("div");
      context.className = "tl-account-nav-context";
      context.setAttribute("data-termlink-account-context", "");
      const eyebrow = document.createElement("span");
      eyebrow.className = "tl-account-nav-eyebrow";
      eyebrow.textContent = "TERMLINK ACCOUNT";
      const title = document.createElement("strong");
      title.className = "tl-account-nav-title";
      const description = document.createElement("small");
      description.className = "tl-account-nav-description";
      context.append(eyebrow, title, description);
      sidebarBody.prepend(context);
    }
    const title = context.querySelector(".tl-account-nav-title");
    const description = context.querySelector(".tl-account-nav-description");
    if (title && title.textContent !== labels.identityTitle) title.textContent = labels.identityTitle;
    if (description && description.textContent !== labels.identityDescription) {
      description.textContent = labels.identityDescription;
    }

    let returnLink = sidebarBody.querySelector("[data-termlink-sidebar-return]");
    if (!returnLink) {
      returnLink = document.createElement("a");
      returnLink.className = "tl-account-sidebar-return";
      returnLink.setAttribute("data-termlink-sidebar-return", "");
      const icon = document.createElement("span");
      icon.className = "tl-account-sidebar-return-icon";
      icon.setAttribute("aria-hidden", "true");
      icon.innerHTML = '<svg viewBox="0 0 24 24" focusable="false"><path d="M19 12H5m6-6-6 6 6 6"/></svg>';
      const copy = document.createElement("span");
      copy.className = "tl-account-sidebar-return-copy";
      const label = document.createElement("strong");
      label.className = "tl-account-sidebar-return-label";
      const hint = document.createElement("small");
      hint.className = "tl-account-sidebar-return-hint";
      copy.append(label, hint);
      returnLink.append(icon, copy);
      sidebarBody.append(returnLink);
    }
    returnLink.href = themedAccountReturnURL();
    returnLink.setAttribute("aria-label", labels.accountAction);
    const returnLabel = returnLink.querySelector(".tl-account-sidebar-return-label");
    if (returnLabel && returnLabel.textContent !== labels.accountLabel) {
      returnLabel.textContent = labels.accountLabel;
    }
    const returnHint = returnLink.querySelector(".tl-account-sidebar-return-hint");
    if (returnHint && returnHint.textContent !== labels.accountHint) {
      returnHint.textContent = labels.accountHint;
    }

    let sectionLabel = sidebarBody.querySelector("[data-termlink-identity-nav-label]");
    if (!sectionLabel) {
      sectionLabel = document.createElement("p");
      sectionLabel.className = "tl-account-nav-section";
      sectionLabel.setAttribute("data-termlink-identity-nav-label", "");
    }
    if (sectionLabel.textContent !== labels.identityNavLabel) sectionLabel.textContent = labels.identityNavLabel;

    const navigation = sidebarBody.querySelector(".pf-v5-c-nav");
    if (
      navigation &&
      (returnLink.nextElementSibling !== sectionLabel || sectionLabel.nextElementSibling !== navigation)
    ) {
      sidebarBody.insertBefore(returnLink, navigation);
      sidebarBody.insertBefore(sectionLabel, navigation);
    }
  }

  function ensureAccountWorkspace() {
    document.body.classList.add("tl-account-console");
    const main = document.querySelector(".pf-v5-c-page__main");
    if (main) {
      const sections = Array.from(main.children).filter((element) =>
        element.classList.contains("pf-v5-c-page__main-section"),
      );
      sections.forEach((section, index) => {
        section.classList.toggle("tl-account-page-heading", index === 0);
        section.classList.toggle("tl-account-page-workspace", index > 0);
      });
    }

    document.querySelectorAll(".pf-v5-c-jump-links").forEach((jumpLinks) => {
      const host = jumpLinks.closest(".pf-v5-l-grid__item");
      const grid = host?.parentElement;
      const singleSection = jumpLinks.querySelectorAll("li").length <= 1;
      host?.classList.toggle("tl-account-single-jump", singleSection);
      grid?.classList.toggle("tl-account-single-section", singleSection);
    });

    const sidebar = document.querySelector(".pf-v5-c-page__sidebar");
    if (sidebar) {
      if (desktopSidebar.matches) sidebar.setAttribute("aria-hidden", "false");
      else if (sidebar.classList.contains("pf-m-collapsed")) sidebar.setAttribute("aria-hidden", "true");
      else sidebar.removeAttribute("aria-hidden");
    }

  }

  const desktopSidebar = window.matchMedia("(min-width: 1220px)");
  const handleSystemChange = () => {
    if (!readStoredTheme()) applyTheme();
  };
  if (systemPreference.addEventListener) systemPreference.addEventListener("change", handleSystemChange);
  else systemPreference.addListener?.(handleSystemChange);
  const handleSidebarBreakpoint = () => ensureAccountWorkspace();
  if (desktopSidebar.addEventListener) desktopSidebar.addEventListener("change", handleSidebarBreakpoint);
  else desktopSidebar.addListener?.(handleSidebarBreakpoint);
  window.addEventListener("storage", (event) => {
    if (event.key === STORAGE_KEY || event.key === null) applyTheme();
  });

  const ensureAccountChrome = () => {
    ensureAccountBrand();
    ensureAccountActions();
    ensureAccountSidebar();
    ensureAccountWorkspace();
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
