import clsx from "clsx";
import type { ButtonHTMLAttributes } from "react";
import "./Button.css";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "ghost";
};

export function Button({ variant = "primary", className, ...props }: Props) {
  return <button className={clsx("btn", `btn-${variant}`, className)} {...props} />;
}
