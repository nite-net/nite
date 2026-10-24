import { NavLink, Route, Routes } from "react-router";
import { TodosPage } from "./routes/TodosPage";
import { TodoDetail } from "./routes/TodoDetail";
import { StatsPage } from "./routes/StatsPage";

export function App() {
  return (
    <div className="shell">
      <header className="topbar">
        <span className="brand">nite</span>
        <nav>
          <NavLink to="/" end>
            Todos
          </NavLink>
          <NavLink to="/stats">Server</NavLink>
        </nav>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<TodosPage />} />
          <Route path="/todos/:id" element={<TodoDetail />} />
          <Route path="/stats" element={<StatsPage />} />
          <Route path="*" element={<p className="muted">Nothing here.</p>} />
        </Routes>
      </main>
    </div>
  );
}
