(() => {
  "use strict";

  const STORAGE_KEY = "termlink-theme";
  const TRANSFER_PARAMETER = "termlink_theme";
  const LIGHT = "light";
  const DARK = "dark";
  const SYSTEM = "system";
  const root = document.documentElement;
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
    const isChinese = /^zh(?:-|$)/i.test(root.lang || "");
    const next = nextPreference(preference);
    if (isChinese) {
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
        link.href = target.href;
      } catch (_) {
        // Keep the server-rendered validated client target.
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
