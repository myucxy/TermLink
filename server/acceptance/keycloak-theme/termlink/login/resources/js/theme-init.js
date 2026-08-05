(() => {
  "use strict";

  const storageKey = "termlink-theme";
  const transferParameter = "termlink_theme";
  let theme = null;
  try {
    const current = new URL(window.location.href);
    const transferred = current.searchParams.get(transferParameter);
    if (transferred === "light" || transferred === "dark") {
      theme = transferred;
      try {
        window.localStorage.setItem(storageKey, transferred);
      } catch (_) {
        // The transferred preference still applies to this page.
      }
      current.searchParams.delete(transferParameter);
      window.history?.replaceState?.(window.history.state, "", current.href);
    }
  } catch (_) {
    // A malformed URL cannot override the stored or system preference.
  }
  try {
    const stored = window.localStorage.getItem(storageKey);
    if (!theme && (stored === "light" || stored === "dark")) theme = stored;
  } catch (_) {
    // The system preference remains available when storage is restricted.
  }
  if (!theme) {
    theme = window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  document.documentElement.dataset.theme = theme;
})();
