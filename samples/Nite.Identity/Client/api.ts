import { config } from "nite:config";

/** Sends the page's form token, which the server requires on anything that changes data. */
export function csrfHeaders(): HeadersInit {
  return { "X-CSRF-Token": config.csrfToken };
}

/**
 * Calls /api on this origin. The browser sends the HttpOnly session cookie by itself; the page
 * never holds a credential. When the session has ended, it goes to the login page and back.
 */
export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      ...csrfHeaders(),
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...init.headers,
    },
  });
  if (response.status === 401) {
    location.assign(`/login?ReturnUrl=${encodeURIComponent(location.pathname + location.search)}`);
    return new Promise<T>(() => {});
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail ?? problem?.title ?? `${response.status} ${response.statusText}`);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export async function signOut() {
  await fetch("/account/logout", { method: "POST", headers: csrfHeaders() });
  location.assign("/login");
}
