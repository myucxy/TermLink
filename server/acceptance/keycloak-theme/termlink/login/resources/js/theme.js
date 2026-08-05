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
      const icon = button.querySelector(".theme-toggle-icon");
      if (label) label.textContent = labels.label;
      if (icon) icon.textContent = theme === DARK ? "☼" : "☾";
      button.setAttribute("aria-label", labels.aria);
      button.setAttribute("title", labels.aria);
      button.setAttribute("aria-pressed", String(theme === LIGHT));
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
