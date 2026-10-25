import { config } from "nite:config";

/**
 * Calls /api with the bearer token this page came with. When the token has expired, reloading the
 * page brings a fresh one (or, if the sign-in cookie has expired too, the login page).
 */
export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      Authorization: `Bearer ${config.token}`,
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...init.headers,
    },
  });
  if (response.status === 401) {
    location.reload();
    return new Promise<T>(() => {});
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail ?? problem?.title ?? `${response.status} ${response.statusText}`);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export async function signOut() {
  await fetch("/account/logout", { method: "POST" });
  location.assign("/login");
}
