// Nite dev client: listens for file changes, applies hot updates (React Fast Refresh and CSS),
// reloads for everything else, and shows compile errors in an overlay.
const socketUrl = new URL("./ws", import.meta.url);
socketUrl.protocol = socketUrl.protocol === "https:" ? "wss:" : "ws:";

let overlay;

export function showErrorOverlay({ message, file, line, column, lineText }) {
  hideErrorOverlay();
  overlay = document.createElement("nite-error-overlay");
  const shadow = overlay.attachShadow({ mode: "open" });
  const location = file ? `${file}${line ? `:${line}:${column}` : ""}` : "";
  shadow.innerHTML = `
    <style>
      :host { position: fixed; inset: 0; z-index: 2147483647; background: rgb(0 0 0 / 0.66);
              display: flex; align-items: flex-start; justify-content: center; padding: 10vh 16px; }
      .card { max-width: 860px; width: 100%; background: #181818; color: #eee; border-top: 6px solid #ff5555;
              border-radius: 8px; padding: 20px 24px; font: 14px/1.5 ui-monospace, SFMono-Regular, Menlo, monospace;
              box-shadow: 0 20px 60px rgb(0 0 0 / 0.5); }
      h1 { margin: 0 0 4px; font-size: 16px; color: #ff7b7b; }
      .file { color: #9cdcfe; margin-bottom: 12px; }
      pre { white-space: pre-wrap; margin: 0; padding: 12px; background: #000; border-radius: 4px; overflow: auto; }
      .hint { margin-top: 12px; color: #999; font-size: 12px; }
    </style>
    <div class="card" role="alert">
      <h1></h1>
      <div class="file"></div>
      <pre></pre>
      <div class="hint">Fix the file and save to continue. Click outside or press Esc to dismiss.</div>
    </div>`;
  overlay.dataset.file = file ?? "";
  shadow.querySelector("h1").textContent = message;
  shadow.querySelector(".file").textContent = location;
  shadow.querySelector("pre").textContent = lineText ?? "";
  if (!lineText) shadow.querySelector("pre").remove();
  overlay.addEventListener("click", (e) => e.target === overlay && hideErrorOverlay());
  document.documentElement.appendChild(overlay);
}

function hideErrorOverlay() {
  overlay?.remove();
  overlay = undefined;
}

// Only an update to the broken file itself (or a reported fix) clears its overlay; another file
// saving cleanly at the same moment must not hide it.
function hideErrorOverlayFor(paths) {
  if (overlay && paths.includes(overlay.dataset.file)) hideErrorOverlay();
}

addEventListener("keydown", (e) => e.key === "Escape" && hideErrorOverlay());

function connect(isReconnect) {
  const socket = new WebSocket(socketUrl);

  socket.addEventListener("open", () => {
    // The server restarted (dotnet watch, or a crash); whatever we have is stale.
    if (isReconnect) location.reload();
  });

  socket.addEventListener("message", async (event) => {
    const message = JSON.parse(event.data);
    switch (message.type) {
      case "css":
        await updateStylesheet(message);
        break;
      case "update":
        await hotUpdate(message.updates);
        hideErrorOverlayFor([...message.updates.map((u) => u.path), ...(message.cleared ?? [])]);
        break;
      case "reload":
        await reloadWhenReachable();
        break;
      case "error":
        showErrorOverlay(message);
        break;
      case "server-update":
        // C# hot reload was applied in the running app. Pages listen for this to re-fetch data.
        console.debug(`[nite] server code updated${message.types.length ? `: ${message.types.join(", ")}` : ""}`);
        dispatchEvent(new CustomEvent("nite:server-update", { detail: { types: message.types } }));
        break;
    }
  });

  socket.addEventListener("close", () => setTimeout(() => connect(true), 1000));
}

// Reloading while the server is down (dotnet watch restarting it, say) would strand the tab on the
// browser's "can't connect" page, with no client left to recover. Wait until it answers first.
async function reloadWhenReachable() {
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetch(new URL("./client.js", import.meta.url), { method: "HEAD", cache: "no-store" });
      if (response.ok) break;
    } catch {
      // not up yet
    }
    await new Promise((resolve) => setTimeout(resolve, Math.min(250 * (attempt + 1), 1000)));
  }
  location.reload();
}

async function updateStylesheet({ path, url }) {
  // CSS imported from a module upserts its own <style> tag, so re-importing it swaps styles in place.
  if (document.querySelector(`style[data-nite-css="${CSS.escape(path)}"]`)) {
    const moduleUrl = new URL(url, import.meta.url);
    moduleUrl.searchParams.set("t", Date.now());
    await import(moduleUrl.href);
  }

  // CSS linked from an HTML entry: load the new sheet, then drop the old one (no flash).
  const target = new URL(`src/${path}`, import.meta.url).pathname;
  for (const link of document.querySelectorAll('link[rel="stylesheet"]')) {
    const href = new URL(link.href);
    if (href.pathname !== target) continue;
    href.searchParams.set("t", Date.now());
    const next = link.cloneNode();
    next.href = href.href;
    next.addEventListener("load", () => link.remove(), { once: true });
    link.after(next);
  }
  console.debug(`[nite] hot updated ${path}`);
}

// Re-runs the modules that accept the update (React components), then lets React Fast Refresh
// swap the new implementations in, keeping component state. If anything goes wrong, reload.
async function hotUpdate(updates) {
  if (updates.length === 0) return;
  const refresh = window.__NITE_REFRESH__;
  if (!refresh) return reloadWhenReachable();

  let failed = false;
  const onError = () => (failed = true);
  addEventListener("error", onError);
  try {
    for (const { url, timestamp } of updates) {
      const moduleUrl = new URL(url, import.meta.url);
      moduleUrl.searchParams.set("t", timestamp);
      await import(moduleUrl.href);
    }
    await refresh.perform();
    // React reports render errors from the refresh asynchronously; give it a moment.
    await new Promise((resolve) => setTimeout(resolve, 100));
  } catch (error) {
    console.error("[nite] hot update failed, reloading", error);
    failed = true;
  } finally {
    removeEventListener("error", onError);
  }

  if (failed) return reloadWhenReachable();
  console.debug(`[nite] hot updated ${updates.map((u) => u.path).join(", ")}`);
}

connect(false);

// dotnet watch injects its own script, which reloads the page after every C# hot reload. That is
// right for Razor pages but throws away a single-page app's state for an API-only change.
if (document.querySelector('script[src*="aspnetcore-browser-refresh"]') && !document.querySelector("[data-island]")) {
  console.info(
    "[nite] dotnet watch will reload this page after each C# edit. To keep client state and get a " +
      "`nite:server-update` event instead, run: DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1 dotnet watch",
  );
}
