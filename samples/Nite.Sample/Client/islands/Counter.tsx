import { useState } from "react";
import { Button } from "../components/Button";

type Props = {
  start: number;
  step?: number;
};

export default function Counter({ start, step = 1 }: Props) {
  const [count, setCount] = useState(start);

  return (
    <div className="counter">
      <Button variant="ghost" onClick={() => setCount((c) => c - step)} aria-label="Decrement">
        −
      </Button>
      <output aria-live="polite">{count}</output>
      <Button onClick={() => setCount((c) => c + step)} aria-label="Increment">
        +
      </Button>
    </div>
  );
}
