// Nite dev client: listens for file changes, hot-swaps CSS, reloads for everything else, and
// shows compile errors in an overlay.
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
      <div class="hint">Fix the file and save; the page reloads. Click outside or press Esc to dismiss.</div>
    </div>`;
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
      case "css": {
        // CSS modules upsert their own <style> tag, so re-importing one swaps styles in place.
        // Pages that never imported the stylesheet are left alone.
        if (!document.querySelector(`style[data-nite-css="${CSS.escape(message.path)}"]`)) break;
        const url = new URL(message.url, import.meta.url);
        url.searchParams.set("t", Date.now());
        await import(url.href);
        hideErrorOverlay();
        console.debug(`[nite] hot updated ${message.path}`);
        break;
      }
      case "reload":
        location.reload();
        break;
      case "error":
        showErrorOverlay(message);
        break;
    }
  });

  socket.addEventListener("close", () => setTimeout(() => connect(true), 1000));
}

connect(false);
