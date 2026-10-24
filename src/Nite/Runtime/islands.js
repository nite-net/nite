// Nite island loader. Finds elements rendered by the <island> tag helper, loads each island's
// module when its client strategy says so, and mounts it with the configured renderer.
import { mount } from "./renderer.js";

const mounted = new WeakMap();

const strategies = {
  load: (_el, run) => run(),
  idle: (_el, run) =>
    "requestIdleCallback" in window ? requestIdleCallback(run, { timeout: 2000 }) : setTimeout(run, 200),
  visible: (el, run) => {
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        observer.disconnect();
        run();
      }
    }, { rootMargin: "200px" });
    observer.observe(el);
  },
  media: (el, run) => {
    const query = window.matchMedia(el.dataset.media || "all");
    if (query.matches) return run();
    query.addEventListener("change", function onChange(e) {
      if (e.matches) {
        query.removeEventListener("change", onChange);
        run();
      }
    });
  },
};

async function hydrate(el) {
  const props = el.dataset.props ? JSON.parse(el.dataset.props) : {};
  const module = await import(el.dataset.island);
  const component = module[el.dataset.export || "default"];
  if (component === undefined) {
    throw new Error(`${el.dataset.island} has no export named "${el.dataset.export || "default"}"`);
  }
  const unmount = await mount(el, component, props);
  mounted.set(el, typeof unmount === "function" ? unmount : null);
}

function start(el) {
  if (mounted.has(el)) return;
  mounted.set(el, null);
  const strategy = strategies[el.dataset.client] ?? strategies.load;
  strategy(el, () =>
    hydrate(el).catch((error) => console.error(`[nite] island ${el.dataset.name ?? el.dataset.island} failed:`, error)),
  );
}

export function scan(root = document) {
  if (root instanceof Element && root.matches("[data-island]")) start(root);
  root.querySelectorAll?.("[data-island]").forEach(start);
}

// Pages that swap HTML in later (htmx, Turbo, Blazor enhanced navigation) get islands for free.
new MutationObserver((records) => {
  for (const record of records) {
    record.addedNodes.forEach((node) => node.nodeType === 1 && scan(node));
    record.removedNodes.forEach((node) => {
      if (node.nodeType !== 1) return;
      const removed = node.matches("[data-island]") ? [node] : [...node.querySelectorAll("[data-island]")];
      for (const el of removed) {
        if (el.isConnected) continue;
        mounted.get(el)?.();
        mounted.delete(el);
      }
    });
  }
}).observe(document.documentElement, { childList: true, subtree: true });

scan();
