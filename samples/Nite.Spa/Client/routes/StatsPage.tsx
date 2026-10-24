import { useEffect, useState } from "react";
import { api, onServerUpdate, type Stats } from "../api";

export function StatsPage() {
  const [stats, setStats] = useState<Stats | null>(null);

  const load = () => api.stats().then(setStats);
  useEffect(() => {
    load();
    return onServerUpdate(load);
  }, []);

  if (!stats) return <p className="muted">Loading…</p>;

  return (
    <section className="card">
      <header className="card-header">
        <h1>Server</h1>
        <button onClick={load}>Refresh</button>
      </header>
      <dl>
        <dt>Runtime</dt>
        <dd>{stats.runtime}</dd>
        <dt>Server time</dt>
        <dd>{new Date(stats.serverTime).toLocaleTimeString()}</dd>
        <dt>Uptime</dt>
        <dd>{stats.uptime}</dd>
        <dt>Todos</dt>
        <dd>
          {stats.done} of {stats.total} done
        </dd>
      </dl>
    </section>
  );
}
