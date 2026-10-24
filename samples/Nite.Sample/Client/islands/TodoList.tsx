import { useId, useState, type FormEvent } from "react";
import clsx from "clsx";
import { Button } from "../components/Button";
import "./TodoList.css";

type Todo = { id: number; title: string; done: boolean };

type Props = {
  title: string;
  items: Todo[];
};

export default function TodoList({ title, items }: Props) {
  const [todos, setTodos] = useState(items);
  const [draft, setDraft] = useState("");
  const inputId = useId();

  const remaining = todos.filter((t) => !t.done).length;

  function add(event: FormEvent) {
    event.preventDefault();
    const text = draft.trim();
    if (!text) return;
    setTodos((current) => [...current, { id: Date.now(), title: text, done: false }]);
    setDraft("");
  }

  function toggle(id: number) {
    setTodos((current) => current.map((t) => (t.id === id ? { ...t, done: !t.done } : t)));
  }

  return (
    <section className="todos">
      <header>
        <h2>{title}</h2>
        <span className="todos-count">{remaining} left</span>
      </header>
      <ul>
        {todos.map((todo) => (
          <li key={todo.id} className={clsx({ done: todo.done })}>
            <label>
              <input type="checkbox" checked={todo.done} onChange={() => toggle(todo.id)} />
              {todo.title}
            </label>
          </li>
        ))}
      </ul>
      <form onSubmit={add}>
        <label htmlFor={inputId} className="visually-hidden">
          New item
        </label>
        <input id={inputId} value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="Add an item" />
        <Button type="submit">Add</Button>
      </form>
    </section>
  );
}
