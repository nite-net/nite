// Same origin as the ASP.NET Core app: no CORS, no dev proxy, no base URL to configure.

export type Todo = { id: number; title: string; done: boolean; created: string };
export type Stats = { serverTime: string; uptime: string; runtime: string; total: number; done: number };

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
  });
  if (!response.ok) throw new Error(`${init?.method ?? "GET"} ${url} failed: ${response.status}`);
  return response.json() as Promise<T>;
}

export const api = {
  todos: () => request<Todo[]>("/api/todos"),
  todo: (id: number) => request<Todo>(`/api/todos/${id}`),
  addTodo: (title: string) => request<Todo>("/api/todos", { method: "POST", body: JSON.stringify({ title }) }),
  setDone: (id: number, done: boolean) =>
    request<Todo>(`/api/todos/${id}`, { method: "PATCH", body: JSON.stringify({ done }) }),
  stats: () => request<Stats>("/api/stats"),
};

/**
 * Runs `callback` whenever C# hot reload changes the server (in development under dotnet watch),
 * so a page can re-fetch without reloading. Returns the unsubscribe function, so it fits useEffect.
 */
export function onServerUpdate(callback: () => void) {
  addEventListener("nite:server-update", callback);
  return () => removeEventListener("nite:server-update", callback);
}
