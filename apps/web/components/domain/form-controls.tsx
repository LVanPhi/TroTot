"use client";

import { AlertCircle } from "lucide-react";
import type { ComponentProps, ReactNode } from "react";

import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

/** Labelled input with its error under it. 44 px tall for touch (docs/06-frontend.md, Chất lượng). */
export function TextField({
  id,
  label,
  error,
  hint,
  ...input
}: { id: string; label: string; error?: string; hint?: ReactNode } & ComponentProps<"input">) {
  const describedBy = [error ? `${id}-error` : null, hint ? `${id}-hint` : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} aria-invalid={error ? true : undefined} aria-describedby={describedBy} className="h-11 text-base" {...input} />
      {hint && !error ? (
        <p id={`${id}-hint`} className="text-sm text-muted-foreground">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-error`} role="alert" className="text-sm text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** Form-level error (react-hook-form root.server), with an icon so it never relies on colour alone. */
export function FormError({ message }: { message?: string }) {
  if (!message) return null;
  return (
    <div role="alert" className="flex items-start gap-2 rounded-lg border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive">
      <AlertCircle aria-hidden className="mt-0.5 size-4 shrink-0" />
      <span>{message}</span>
    </div>
  );
}

export function SubmitButton({ pending, children, ...props }: { pending: boolean } & ComponentProps<"button">) {
  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex min-h-11 items-center justify-center rounded-lg bg-primary px-4 font-medium text-primary-foreground hover:bg-primary/85 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring disabled:opacity-60"
      {...props}
    >
      {pending ? "Đang xử lý…" : children}
    </button>
  );
}

export const secondaryButton =
  "inline-flex min-h-11 items-center justify-center rounded-lg border border-border px-4 font-medium hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring disabled:opacity-60";

export const linkClass = "font-medium text-primary underline-offset-4 hover:underline";

/** Centered card used by every (auth) page. */
export function AuthCard({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <main className="mx-auto flex w-full max-w-md flex-1 flex-col justify-center gap-6 px-4 py-10">
      <div className="flex flex-col gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {description ? <p className="text-muted-foreground">{description}</p> : null}
      </div>
      {children}
    </main>
  );
}
