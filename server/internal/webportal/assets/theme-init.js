(() => {
  "use strict";

  const storageKey = "termlink-theme";
  const transferParameter = "termlink_theme";
  const supportedPreferences = ["system", "light", "dark"];
  let preference = null;
  try {
    const current = new URL(window.location.href);
    const transferred = current.searchParams.get(transferParameter);
    if (supportedPreferences.includes(transferred)) {
      preference = transferred;
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
    if (!preference && supportedPreferences.includes(stored)) preference = stored;
  } catch (_) {
    // The system preference remains a deterministic pre-paint fallback.
  }
  if (!preference) preference = "system";
  const theme = preference === "system"
    ? (window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light")
    : preference;
  document.documentElement.dataset.themePreference = preference;
  document.documentElement.dataset.theme = theme;
})();
