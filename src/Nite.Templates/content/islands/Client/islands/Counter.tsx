import { useState } from "react";

export default function Counter({ start }: { start: number }) {
  const [count, setCount] = useState(start);
  return <button onClick={() => setCount((c) => c + 1)}>Clicked {count} times</button>;
}
