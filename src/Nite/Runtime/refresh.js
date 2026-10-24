// React Fast Refresh runtime glue. Loaded before React itself (it must hook into React DevTools'
// global hook before react-dom initializes), then imported by every compiled component module.
import RefreshRuntime from "react-refresh/runtime";

RefreshRuntime.injectIntoGlobalHook(window);

export function register(type, id) {
  RefreshRuntime.register(type, id);
}

export function sign(type, hookSignature) {
  RefreshRuntime.setSignature(type, hookSignature, false);
}

let pending;

// Several modules can update at once; refresh React once after they have all re-run.
export function perform() {
  return new Promise((resolve) => {
    clearTimeout(pending);
    pending = setTimeout(() => {
      RefreshRuntime.performReactRefresh();
      resolve();
    }, 16);
  });
}

window.__NITE_REFRESH__ = { perform };
