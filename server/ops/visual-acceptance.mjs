import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const options = Object.fromEntries(process.argv.slice(2).map((item) => {
  const separator = item.indexOf("=");
  if (!item.startsWith("--") || separator < 3) throw new Error(`invalid option ${item}`);
  return [item.slice(2, separator), item.slice(separator + 1)];
}));
for (const required of ["edge", "origin", "routes", "output"]) {
  if (!options[required]) throw new Error(`--${required} is required`);
}

const origin = new URL(options.origin).origin;
const routes = options.routes.split(",").map((route) => route.trim()).filter(Boolean);
const viewports = (options.viewports || "1440x1000,390x844").split(",").map((raw) => {
  const match = /^(\d{3,4})x(\d{3,4})$/.exec(raw.trim());
  if (!match) throw new Error(`invalid viewport ${raw}`);
  return { width: Number(match[1]), height: Number(match[2]) };
});
const password = options["password-file"] ? readFileSync(options["password-file"], "utf8").trim() : "";
if (options.username && password.length < 16) throw new Error("password file is unavailable");
const forbiddenValues = (options["forbidden-files"] || "").split(",").map((path) => path.trim()).filter(Boolean).map((path) => {
  const value = readFileSync(path, "utf8").trim();
  if (value.length < 8) throw new Error("forbidden-value file is unavailable");
  return value;
});
for (const path of (options["forbidden-json-files"] || "").split(",").map((item) => item.trim()).filter(Boolean)) {
  const visit = (value, sensitive = false) => {
    if (typeof value === "string" && sensitive && value.length >= 8) forbiddenValues.push(value);
    else if (Array.isArray(value)) value.forEach((item) => visit(item, sensitive));
    else if (value && typeof value === "object") Object.entries(value).forEach(([key, item]) => {
      visit(item, sensitive || /(private|secret|exchange|token|password|credential|key_material)/i.test(key));
    });
  };
  visit(JSON.parse(readFileSync(path, "utf8")));
}

mkdirSync(options.output, { recursive: true });
const profile = mkdtempSync(join(tmpdir(), "termlink-edge-"));
const port = 19222 + Math.floor(Math.random() * 1000);
const resolverArguments = options["host-resolution"] === "system"
  ? []
  : [`--host-resolver-rules=MAP ${new URL(origin).hostname} 127.0.0.1`];
const edge = spawn(options.edge, [
  "--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  "--ignore-certificate-errors", "--disable-gpu", "--disable-extensions", "--no-first-run",
  "--no-default-browser-check", "--no-proxy-server",
  ...resolverArguments, "about:blank",
], { stdio: "ignore", windowsHide: true });

const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
async function retry(action, description, timeout = 30000) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    try { return await action(); } catch (error) { last = error; await delay(150); }
  }
  throw new Error(`${description}: ${last?.message || "timed out"}`);
}

let sequence = 0;
const pending = new Map();
function command(socket, method, params = {}) {
  const id = ++sequence;
  socket.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

async function evaluate(socket, expression) {
  const result = await command(socket, "Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text || "browser evaluation failed");
  return result.result?.value;
}

async function waitFor(socket, expression, description, timeout = 30000) {
  return retry(async () => {
    const value = await evaluate(socket, expression);
    if (!value) throw new Error("condition is false");
    return value;
  }, description, timeout);
}

function safeName(route) {
  const value = route.replace(/^\//, "").replace(/[^a-zA-Z0-9]+/g, "-").replace(/^-|-$/g, "");
  return value || "home";
}

try {
  const targets = await retry(async () => {
    const response = await fetch(`http://127.0.0.1:${port}/json/list`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const items = await response.json();
    const page = items.find((item) => item.type === "page" && item.url === "about:blank") ||
      items.find((item) => item.type === "page" && item.url.startsWith("http"));
    if (!page?.webSocketDebuggerUrl) throw new Error("page target unavailable");
    return page;
  }, "Edge DevTools endpoint unavailable");
  const socket = new WebSocket(targets.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  socket.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    if (!message.id || !pending.has(message.id)) return;
    const request = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) request.reject(new Error(message.error.message)); else request.resolve(message.result);
  });
  await command(socket, "Page.enable");
  await command(socket, "Runtime.enable");

  if (options.username) {
    await command(socket, "Page.navigate", { url: `${origin}/login?return_to=${encodeURIComponent(routes[0])}` });
    await waitFor(socket, "document.readyState === 'complete' && !!document.querySelector('#username')", "OIDC login form unavailable");
    await evaluate(socket, `(() => {
      const username = document.querySelector('#username');
      const password = document.querySelector('#password');
      const submit = document.querySelector('#kc-login');
      if (!username || !password || !submit) return false;
      username.value = ${JSON.stringify(options.username)};
      password.value = ${JSON.stringify(password)};
      username.dispatchEvent(new Event('input', { bubbles: true }));
      password.dispatchEvent(new Event('input', { bubbles: true }));
      submit.click();
      return true;
    })()`);
    const loginOutcome = await waitFor(socket, `(() => {
      if (location.origin === ${JSON.stringify(origin)} && !location.pathname.startsWith('/realms/') && !location.pathname.startsWith('/auth/')) return 'returned';
      if (document.querySelector('#password-new') && document.querySelector('#password-confirm')) return 'password-update';
      return '';
    })()`, "OIDC login did not advance", 45000);
    if (loginOutcome === "password-update") {
      await evaluate(socket, `(() => {
        const next = document.querySelector('#password-new');
        const confirm = document.querySelector('#password-confirm');
        const submit = document.querySelector('#kc-passwd-update-form input[type="submit"], #kc-passwd-update-form button[type="submit"]');
        if (!next || !confirm || !submit) return false;
        next.value = ${JSON.stringify(password)};
        confirm.value = ${JSON.stringify(password)};
        next.dispatchEvent(new Event('input', { bubbles: true }));
        confirm.dispatchEvent(new Event('input', { bubbles: true }));
        submit.click();
        return true;
      })()`);
    }
    await waitFor(socket, `location.origin === ${JSON.stringify(origin)} && !location.pathname.startsWith('/realms/') && !location.pathname.startsWith('/auth/')`, "OIDC login did not return", 45000);
  }

  const report = [];
  for (const viewport of viewports) {
    await command(socket, "Emulation.setDeviceMetricsOverride", {
      width: viewport.width, height: viewport.height, deviceScaleFactor: 1, mobile: viewport.width < 600,
    });
    for (const route of routes) {
      const url = new URL(route, origin).href;
      await command(socket, "Page.navigate", { url });
      await waitFor(socket, "document.readyState === 'complete'", `page did not load: ${route}`);
      await delay(350);
      const inspection = await evaluate(socket, `(() => ({
        url: location.href,
        title: document.title,
        heading: document.querySelector('h1')?.textContent?.trim() || '',
        status: document.body?.innerText?.includes('Internal Server Error') ? 'server-error' : 'ok',
        viewportWidth: document.documentElement.getBoundingClientRect().width,
        windowInnerWidth: innerWidth,
        documentWidth: document.documentElement.scrollWidth,
        overflow: document.documentElement.scrollWidth > document.documentElement.getBoundingClientRect().width + 1,
        clippedFixed: [...document.querySelectorAll('*')].filter((node) => {
          const style = getComputedStyle(node);
          if (style.position !== 'fixed' && style.position !== 'sticky') return false;
          const box = node.getBoundingClientRect();
          return box.right > innerWidth + 1 || box.left < -1;
        }).length,
        controls: [...document.querySelectorAll('input, select, textarea')].map((node) => ({
          tag: node.tagName.toLowerCase(), id: node.id, name: node.getAttribute('name') || '',
          type: node.getAttribute('type') || '', className: node.className || '',
          parentClass: node.parentElement?.className || '', grandparentClass: node.parentElement?.parentElement?.className || '',
        })),
        layout: ['html', 'body', '.admin-shell', '.admin-main', '.admin-panel', '.admin-table-wrap', '.admin-table'].map((selector) => {
          const node = selector === 'html' ? document.documentElement : selector === 'body' ? document.body : document.querySelector(selector);
          if (!node) return { selector };
          const box = node.getBoundingClientRect();
          return { selector, left: Math.round(box.left), right: Math.round(box.right), width: Math.round(box.width), scrollWidth: node.scrollWidth };
        }),
        offenders: [...document.querySelectorAll('*')].map((node) => {
          const box = node.getBoundingClientRect();
          return { tag: node.tagName.toLowerCase(), className: node.className || '', left: Math.round(box.left), right: Math.round(box.right), width: Math.round(box.width) };
        }).filter((item) => item.left < -1 || item.right > document.documentElement.getBoundingClientRect().right + 1).sort((a, b) => b.right - a.right).slice(0, 20),
        forbiddenValuePresent: ${JSON.stringify(forbiddenValues)}.some((value) => document.documentElement.outerHTML.includes(value)),
      }))()`);
      if (new URL(inspection.url).origin !== origin || inspection.status !== "ok") {
        throw new Error(`unexpected page result for ${route}: ${inspection.url} ${inspection.status}`);
      }
      if (inspection.forbiddenValuePresent) throw new Error(`forbidden value rendered by ${route}`);
      const expectsIdentityDialog = new URL(route, origin).searchParams.get("section") === "settings";
      const identityDialog = await evaluate(socket, `(() => {
        const root = document.querySelector('[data-identity-dialog-root]');
        const frame = root?.querySelector('[data-identity-dialog-frame]');
        const actions = {};
        for (const button of document.querySelectorAll('[data-identity-dialog]')) {
          const action = button.getAttribute('data-identity-dialog');
          button.click();
          actions[action] = {
            open: root instanceof HTMLDialogElement && root.open,
            source: frame instanceof HTMLIFrameElement ? frame.getAttribute('src') : '',
          };
          if (root instanceof HTMLDialogElement && root.open) root.close();
        }
        return { exists: root instanceof HTMLDialogElement, actions };
      })()`);
      if (expectsIdentityDialog) {
        if (!identityDialog.exists) throw new Error(`identity dialog unavailable for ${route}`);
        for (const action of ["password", "mfa", "sessions"]) {
          const state = identityDialog.actions[action];
          if (!state?.open || state.source !== `/account/identity/${action}`) {
            throw new Error(`identity dialog action ${action} is not in-page and allowlisted`);
          }
        }
      }
      const screenshot = await command(socket, "Page.captureScreenshot", {
        format: "png", captureBeyondViewport: true, fromSurface: true,
      });
      const file = `${safeName(route)}-${viewport.width}x${viewport.height}.png`;
      writeFileSync(join(options.output, file), Buffer.from(screenshot.data, "base64"));
      report.push({ route, viewport: `${viewport.width}x${viewport.height}`, ...inspection, identityDialog, file });
    }
  }
  writeFileSync(join(options.output, "report.json"), JSON.stringify(report, null, 2));
  if (options.quiet === "true") console.log(`visual acceptance passed (${report.length} page/viewport combinations)`);
  else console.log(JSON.stringify(report));
  socket.close();
} finally {
  edge.kill();
  await delay(250);
  rmSync(profile, { recursive: true, force: true });
}
