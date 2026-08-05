(() => {
  "use strict";

  const STORAGE_KEY = "termlink-theme";
  const LIGHT = "light";
  const DARK = "dark";
  const root = document.documentElement;
  const systemPreference = window.matchMedia
    ? window.matchMedia("(prefers-color-scheme: dark)")
    : { matches: false };

  function isTheme(value) {
    return value === LIGHT || value === DARK;
  }

  function readStoredTheme() {
    try {
      const value = window.localStorage.getItem(STORAGE_KEY);
      if (isTheme(value)) return value;
      if (value !== null) window.localStorage.removeItem(STORAGE_KEY);
    } catch (_) {
      // Storage can be unavailable in private browsing or with strict policies.
    }
    return null;
  }

  function preferredTheme() {
    return systemPreference.matches ? DARK : LIGHT;
  }

  function labelsFor(theme) {
    const isChinese = /^zh(?:-|$)/i.test(root.lang || "");
    const nextTheme = theme === DARK ? LIGHT : DARK;
    if (isChinese) {
      return {
        label: nextTheme === LIGHT ? "明亮主题" : "深色主题",
        aria: `切换到${nextTheme === LIGHT ? "明亮" : "深色"}主题`,
      };
    }
    return {
      label: nextTheme === LIGHT ? "Light theme" : "Dark theme",
      aria: `Switch to ${nextTheme} theme`,
    };
  }

  function render(theme) {
    root.dataset.theme = theme;
    const labels = labelsFor(theme);
    document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
      const label = button.querySelector(".theme-toggle-label");
      if (label) label.textContent = labels.label;
      button.setAttribute("aria-label", labels.aria);
      button.setAttribute("title", labels.aria);
      button.removeAttribute("aria-pressed");
      button.dataset.theme = theme;
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
    const current = root.dataset.theme === DARK ? DARK : LIGHT;
    const next = current === DARK ? LIGHT : DARK;
    persist(next);
    render(next);
  }

  function applyStoredOrSystemTheme() {
    render(readStoredTheme() || preferredTheme());
  }

  document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
    button.addEventListener("click", toggleTheme);
  });

  document.querySelectorAll("form[data-confirm-action]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      const account = form.dataset.account || "该账号";
      const action = form.dataset.confirmAction;
      let message;
      if (action === "role") {
        const role = form.querySelector("select[name=role] option:checked")?.textContent?.trim() || "新角色";
        message = `确认将 ${account} 的角色改为 ${role}？该变更会立即生效并写入安全审计。`;
      } else if (action === "lock") {
        message = `确认停用 ${account}？其门户访问和新会话将被拒绝。`;
      } else if (action === "unlock") {
        message = `确认启用 ${account}？这会恢复其门户访问权限。`;
      }
      if (message && !window.confirm(message)) event.preventDefault();
    });
  });

  const handleSystemChange = () => {
    if (!readStoredTheme()) render(preferredTheme());
  };
  if (systemPreference.addEventListener) systemPreference.addEventListener("change", handleSystemChange);
  else systemPreference.addListener?.(handleSystemChange);
  window.addEventListener("storage", (event) => {
    if (event.key === STORAGE_KEY || event.key === null) applyStoredOrSystemTheme();
  });

  applyStoredOrSystemTheme();
})();
