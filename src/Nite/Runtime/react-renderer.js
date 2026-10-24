// Client-rendered React islands. Fallback markup inside the island is replaced on first render.
import { createElement } from "react";
import { createRoot } from "react-dom/client";

export function mount(el, Component, props) {
  const root = createRoot(el);
  root.render(createElement(Component, props));
  return () => root.unmount();
}
