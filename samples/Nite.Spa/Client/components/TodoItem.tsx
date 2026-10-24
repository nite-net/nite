import { Link } from "react-router";
import type { Todo } from "../api";
import { timeAgo } from "../format";

type Props = {
  todo: Todo;
  onToggle: (todo: Todo) => void;
};

export function TodoItem({ todo, onToggle }: Props) {
  return (
    <li className={todo.done ? "todo done" : "todo"}>
      <input type="checkbox" checked={todo.done} onChange={() => onToggle(todo)} aria-label={`Done: ${todo.title}`} />
      <Link to={`/todos/${todo.id}`}>{todo.title}</Link>
      <time dateTime={todo.created}>{timeAgo(todo.created)}</time>
    </li>
  );
}
