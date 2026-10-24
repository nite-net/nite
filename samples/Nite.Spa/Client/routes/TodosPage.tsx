import { useEffect, useState, type FormEvent } from "react";
import { api, onServerUpdate, type Todo } from "../api";
import { plural } from "../format";
import { TodoItem } from "../components/TodoItem";

export function TodosPage() {
  const [todos, setTodos] = useState<Todo[] | null>(null);
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const load = () => api.todos().then(setTodos, (e: Error) => setError(e.message));
    load();
    return onServerUpdate(load);
  }, []);

  async function add(event: FormEvent) {
    event.preventDefault();
    if (!draft.trim()) return;
    const todo = await api.addTodo(draft);
    setTodos((current) => [...(current ?? []), todo]);
    setDraft("");
  }

  async function toggle(todo: Todo) {
    const updated = await api.setDone(todo.id, !todo.done);
    setTodos((current) => current?.map((t) => (t.id === updated.id ? updated : t)) ?? null);
  }

  if (error) return <p className="error">{error}</p>;
  if (!todos) return <p className="muted">Loading…</p>;

  const remaining = todos.filter((t) => !t.done).length;

  return (
    <section className="card">
      <header className="card-header">
        <h1>Todos</h1>
        <span className="muted remaining">{plural(remaining, "item")} left</span>
      </header>
      <ul className="todos">
        {todos.map((todo) => (
          <TodoItem key={todo.id} todo={todo} onToggle={toggle} />
        ))}
      </ul>
      <form onSubmit={add} className="add">
        <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="What needs doing?" aria-label="New todo" />
        <button type="submit">Add</button>
      </form>
    </section>
  );
}
