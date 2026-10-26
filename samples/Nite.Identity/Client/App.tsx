import { useEffect, useState, type FormEvent } from "react";
import { config } from "nite:config";
import { api, signOut } from "./api";

type Note = { id: number; text: string; created: string };
type UserSummary = { userName: string; email: string; notes: number };

// This page is only served to signed-in users, so the server always sent these.
const user = config.user!;
const isAdmin = user.roles.includes("Admin");

export function App() {
  return (
    <main>
      <header>
        <h1>Hello, {user.name}</h1>
        <button className="secondary" onClick={signOut}>
          Sign out
        </button>
      </header>
      <Notes />
      {isAdmin && <Users />}
      <Identity />
    </main>
  );
}

function Notes() {
  const [notes, setNotes] = useState<Note[] | null>(null);
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api<Note[]>("/notes").then(setNotes, (e: Error) => setError(e.message));
  }, []);

  async function add(event: FormEvent) {
    event.preventDefault();
    try {
      const note = await api<Note>("/notes", { method: "POST", body: JSON.stringify({ text }) });
      setNotes((current) => [note, ...(current ?? [])]);
      setText("");
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function remove(id: number) {
    await api(`/notes/${id}`, { method: "DELETE" });
    setNotes((current) => current?.filter((n) => n.id !== id) ?? null);
  }

  return (
    <section>
      <h2>Your notes</h2>
      <form className="row" onSubmit={add}>
        <input value={text} onChange={(e) => setText(e.target.value)} placeholder="Write a note" maxLength={500} />
        <button type="submit">Add</button>
      </form>
      {error && <p className="error">{error}</p>}
      {notes === null ? (
        <p className="muted">Loading…</p>
      ) : notes.length === 0 ? (
        <p className="muted">No notes yet. They are stored in SQLite, visible only to you.</p>
      ) : (
        <ul className="notes">
          {notes.map((note) => (
            <li key={note.id}>
              <span>{note.text}</span>
              <time>{new Date(note.created).toLocaleString()}</time>
              <button className="link" onClick={() => remove(note.id)} aria-label="Delete note">
                ✕
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function Users() {
  const [users, setUsers] = useState<UserSummary[] | null>(null);
  useEffect(() => {
    api<UserSummary[]>("/users").then(setUsers);
  }, []);
  return (
    <section>
      <h2>All users</h2>
      <p className="muted">
        Only the <code>Admin</code> role may call <code>/api/users</code>.
      </p>
      <table>
        <thead>
          <tr>
            <th>User</th>
            <th>Email</th>
            <th>Notes</th>
          </tr>
        </thead>
        <tbody>
          {users?.map((u) => (
            <tr key={u.userName}>
              <td>{u.userName}</td>
              <td>{u.email}</td>
              <td>{u.notes}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function Identity() {
  return (
    <section>
      <h2>What the server passed in</h2>
      <p className="muted">
        <code>NiteOptions.ClientConfig</code> in <code>Program.cs</code> wrote these into the page as it was served;
        this app reads them with <code>import {"{ config }"} from "nite:config"</code>. There is no credential among
        them: the session is an <code>HttpOnly</code> cookie that no script can read, and the API on this origin
        accepts it together with the page's form token.
      </p>
      <h3>Roles</h3>
      <p>{user.roles.length > 0 ? user.roles.join(", ") : "none"}</p>
      <h3>Claims</h3>
      <table>
        <tbody>
          {user.claims.map((claim, i) => (
            <tr key={i}>
              <td className="type">{claim.type}</td>
              <td>{claim.value}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
