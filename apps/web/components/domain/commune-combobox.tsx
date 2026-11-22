"use client";

import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useEffect, useId, useState } from "react";

import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api, type Schemas } from "@/lib/api/client";

export type Commune = Schemas["MetaEndpointsCommune"];

export const communeLabel = (c: Commune) => `${c.name}, ${c.provinceName}`;

/**
 * Ward/commune picker (docs/06-frontend.md, Form): accent-insensitive search via /meta/communes?q=,
 * shows "Phường X, Thành phố Hà Nội". Keyboard: ↑/↓ to move, Enter to pick, Esc to close.
 */
export function CommuneCombobox({
  label,
  value,
  onChange,
  error,
}: {
  label: string;
  value: Commune | null;
  onChange: (commune: Commune | null) => void;
  error?: string;
}) {
  const id = useId();
  const [text, setText] = useState("");
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);

  useEffect(() => {
    const t = setTimeout(() => setQuery(text.trim()), 250);
    return () => clearTimeout(t);
  }, [text]);

  const { data: options = [] } = useQuery({
    queryKey: ["meta", "communes", query],
    enabled: open && query.length >= 2,
    staleTime: 60 * 60 * 1000,
    queryFn: async (): Promise<Commune[]> => {
      const { data } = await api.GET("/api/v1/meta/communes", { params: { query: { q: query } } });
      return data ?? [];
    },
  });

  const pick = (c: Commune) => {
    onChange(c);
    setText("");
    setOpen(false);
  };

  if (value) {
    return (
      <div className="flex flex-col gap-1.5">
        <Label>{label}</Label>
        <div className="flex min-h-11 items-center justify-between gap-2 rounded-lg border border-input px-3">
          <span>{communeLabel(value)}</span>
          <button type="button" onClick={() => onChange(null)} className="inline-flex size-11 items-center justify-center" aria-label="Bỏ chọn phường/xã">
            <X aria-hidden className="size-4" />
          </button>
        </div>
      </div>
    );
  }

  const listId = `${id}-list`;
  const showList = open && options.length > 0;
  return (
    <div className="relative flex flex-col gap-1.5">
      <Label htmlFor={`${id}-input`}>{label}</Label>
      <Input
        id={`${id}-input`}
        role="combobox"
        aria-expanded={showList}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={showList ? `${id}-opt-${active}` : undefined}
        aria-invalid={error ? true : undefined}
        autoComplete="off"
        placeholder="Gõ tên phường/xã, không cần dấu"
        className="h-11 text-base"
        value={text}
        onChange={(e) => {
          setText(e.target.value);
          setOpen(true);
          setActive(0);
        }}
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        onKeyDown={(e) => {
          if (!showList) return;
          if (e.key === "ArrowDown") setActive((a) => Math.min(a + 1, options.length - 1));
          else if (e.key === "ArrowUp") setActive((a) => Math.max(a - 1, 0));
          else if (e.key === "Enter") pick(options[active]);
          else if (e.key === "Escape") setOpen(false);
          else return;
          e.preventDefault();
        }}
      />
      {showList ? (
        <ul id={listId} role="listbox" className="absolute top-full z-10 mt-1 max-h-64 w-full overflow-auto rounded-lg border border-border bg-popover shadow-md">
          {options.map((c, i) => (
            <li
              key={c.code}
              id={`${id}-opt-${i}`}
              role="option"
              aria-selected={i === active}
              className={`min-h-11 cursor-pointer px-3 py-2.5 ${i === active ? "bg-muted" : ""}`}
              onMouseDown={(e) => {
                e.preventDefault();
                pick(c);
              }}
            >
              {communeLabel(c)}
            </li>
          ))}
        </ul>
      ) : null}
      {error ? (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}
