import { useCallback, useEffect, useState } from "react";
import { config } from "nite:config";
import { trackEvent } from "./telemetry";

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
      <button
        onClick={() => {
          trackEvent("quote-requested");
          load();
        }}
      >
        Another one
      </button>
      <h2>From the server, when this page was served</h2>
      <p className="muted">
        Values from <code>NiteOptions.ClientConfig</code> in <code>Program.cs</code>, read here with{" "}
        <code>import {"{ config }"} from "nite:config"</code>. They come from the running server, so they can
        differ per environment and per request without rebuilding the client.
      </p>
      <dl>
        <dt>Environment</dt>
        <dd>{config.server.environment}</dd>
        <dt>Machine</dt>
        <dd>{config.server.machine}</dd>
        <dt>Rendered at</dt>
        <dd>{new Date(config.server.renderedAt).toLocaleString()}</dd>
        <dt>Trace</dt>
        <dd>
          <code>{config.server.traceId ?? "none"}</code>
        </dd>
        <dt>Application Insights</dt>
        <dd>{config.appInsights ? "on" : "off (no connection string in the AppHost)"}</dd>
      </dl>
    </main>
  );
}
