(() => {
  "use strict";

  let theme = null;
  try {
    const stored = window.localStorage.getItem("termlink-theme");
    if (stored === "light" || stored === "dark") theme = stored;
  } catch (_) {
    // The system preference remains available when storage is restricted.
  }
  if (!theme) {
    theme = window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  document.documentElement.dataset.theme = theme;
  document.documentElement.classList.toggle("pf-v5-theme-dark", theme === "dark");
})();
