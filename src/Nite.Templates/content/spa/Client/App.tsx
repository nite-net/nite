import { useEffect, useState } from "react";

type Hello = { message: string; time: string };

export function App() {
  const [hello, setHello] = useState<Hello | null>(null);
  const [count, setCount] = useState(0);

  useEffect(() => {
    const load = () => fetch("/api/hello").then((r) => r.json()).then(setHello);
    load();
    // Under `nite watch`, C# hot reloads raise this event: re-fetch, keep state.
    addEventListener("nite:server-update", load);
    return () => removeEventListener("nite:server-update", load);
  }, []);

  return (
    <main>
      <h1>NITE_TITLE</h1>
      <p className="muted">
        Edit <code>Client/App.tsx</code> or <code>Program.cs</code> and save.
      </p>
      <p>{hello ? `${hello.message} at ${new Date(hello.time).toLocaleTimeString()}` : "Calling the API…"}</p>
      <button onClick={() => setCount((c) => c + 1)}>Clicked {count} times</button>
    </main>
  );
}
