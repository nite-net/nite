import { useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { api, type Todo } from "../api";

export function TodoDetail() {
  const { id } = useParams();
  const [todo, setTodo] = useState<Todo | null>(null);
  const [missing, setMissing] = useState(false);

  useEffect(() => {
    api.todo(Number(id)).then(setTodo, () => setMissing(true));
  }, [id]);

  if (missing) return <p className="muted">No todo #{id}. <Link to="/">Back</Link></p>;
  if (!todo) return <p className="muted">Loading…</p>;

  return (
    <section className="card">
      <p><Link to="/">← All todos</Link></p>
      <h1>{todo.title}</h1>
      <dl>
        <dt>Status</dt>
        <dd>{todo.done ? "Done" : "Open"}</dd>
        <dt>Created</dt>
        <dd>{new Date(todo.created).toLocaleString()}</dd>
      </dl>
    </section>
  );
}
