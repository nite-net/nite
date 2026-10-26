import { useState, type FormEvent } from "react";
import { config } from "nite:config";
import { csrfHeaders } from "../api";

/** Where to go after signing in: the page that sent us here, if it is one of ours. */
function returnUrl() {
  const target = new URLSearchParams(location.search).get("ReturnUrl");
  return target?.startsWith("/") && !target.startsWith("//") ? target : "/";
}

export function Login() {
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const response = await fetch("/account/login", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...csrfHeaders() },
      body: JSON.stringify({ userName, password }),
    });
    if (response.ok) {
      // The session cookie is set; the app's page now loads, with this user's claims.
      location.assign(returnUrl());
      return;
    }
    const problem = await response.json().catch(() => null);
    setError(
      response.status === 429
        ? "Too many attempts from this address. Wait a minute and try again."
        : (problem?.detail ?? `Sign-in failed (${response.status}).`),
    );
    setBusy(false);
  }

  return (
    <main className="narrow">
      <h1>Sign in</h1>
      {config.user && (
        <p className="notice">
          You are already signed in as <strong>{config.user.name}</strong>. <a href="/">Go to the app</a>.
        </p>
      )}
      <form onSubmit={submit}>
        <label>
          User name
          <input value={userName} onChange={(e) => setUserName(e.target.value)} autoComplete="username" autoFocus required />
        </label>
        <label>
          Password
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
        </label>
        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={busy}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
      <p className="muted">
        Demo users: <code>admin</code> / <code>admin</code> (an administrator) and <code>benson</code> / <code>hedges</code>.
      </p>
    </main>
  );
}
