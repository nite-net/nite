import { useCallback, useEffect, useState } from "react";

type Quote = { text: string; author: string; server: string };

export function App() {
  const [quote, setQuote] = useState<Quote | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    setError(null);
    fetch("/api/quote")
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`${r.status} ${r.statusText}`))))
      .then(setQuote, (e: Error) => setError(e.message));
  }, []);

  useEffect(load, [load]);

  return (
    <main>
      <h1>Nite under Aspire</h1>
      <p className="muted">
        This page is React, compiled by Nite inside the <code>web</code> project. The quote comes from the{" "}
        <code>api</code> project, through <code>web</code>, found by Aspire's service discovery. Open the
        dashboard's Traces page to see the request, the call to <code>api</code> and Nite's compile spans
        together.
      </p>
      {error ? (
        <p className="error">Could not get a quote: {error}</p>
      ) : quote ? (
        <blockquote>
          <p>“{quote.text}”</p>
          <footer>
            {quote.author} <span className="muted">(served by {quote.server})</span>
          </footer>
        </blockquote>
      ) : (
        <p className="muted">Asking the API…</p>
      )}
      <button onClick={load}>Another one</button>
    </main>
  );
}
