"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { toast } from "sonner";

import { FormError, secondaryButton, SubmitButton } from "@/components/domain/form-controls";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api, type Schemas } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";

type Criterion = Schemas["AdminCatalogCriterion"];
const KEY = ["admin", "criteria"] as const;
const SCOPES = { both: "Cả phòng và việc", room: "Phòng trọ", job: "Việc làm" } as const;

/** US-802: add, edit wording, deactivate (no delete; the code never changes). */
export function CriteriaAdmin() {
  const list = useQuery({ queryKey: KEY, queryFn: async () => (await api.GET("/api/v1/admin/criteria")).data ?? [] });
  const [editing, setEditing] = useState<Criterion | "new" | null>(null);
  return (
    <main className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">Bộ tiêu chí</h1>
        <button type="button" className={secondaryButton} onClick={() => setEditing("new")}>
          Thêm tiêu chí
        </button>
      </div>
      <p className="text-sm text-muted-foreground">
        Tiêu chí mới được LLM dùng ngay (worker nạp lại mỗi 60 giây); bộ luật dự phòng chỉ có cho các tiêu chí ban đầu.
      </p>
      {editing ? <CriterionForm value={editing === "new" ? null : editing} onDone={() => setEditing(null)} /> : null}
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {(list.data ?? []).map((c) => (
          <li key={c.id} className="flex flex-wrap items-start justify-between gap-2 p-3">
            <div className="flex min-w-0 flex-col gap-1">
              <span className="font-medium">
                {c.name} <code className="text-xs text-muted-foreground">{c.code}</code>
              </span>
              <span className="text-sm">{c.guidance}</span>
              <span className="text-xs text-muted-foreground">
                {SCOPES[c.scope as keyof typeof SCOPES] ?? c.scope} · {c.isActive ? "Đang dùng" : "Đã ngừng"}
                {c.legalBasis ? ` · ${c.legalBasis}` : ""}
              </span>
            </div>
            <button type="button" className={secondaryButton} onClick={() => setEditing(c)}>
              Sửa
            </button>
          </li>
        ))}
      </ul>
    </main>
  );
}

function CriterionForm({ value, onDone }: { value: Criterion | null; onDone: () => void }) {
  const queryClient = useQueryClient();
  const [code, setCode] = useState(value?.code ?? "");
  const [name, setName] = useState(value?.name ?? "");
  const [guidance, setGuidance] = useState(value?.guidance ?? "");
  const [scope, setScope] = useState(value?.scope ?? "both");
  const [legal, setLegal] = useState(value?.legalBasis ?? "");
  const [active, setActive] = useState(value?.isActive ?? true);
  const [error, setError] = useState<string | undefined>();
  const [pending, setPending] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setPending(true);
    const body = { name, guidance, scope, legalBasis: legal.trim() || null };
    const result = value
      ? await api.PUT("/api/v1/admin/criteria/{id}", { params: { path: { id: Number(value.id) } }, body: { ...body, isActive: active } })
      : await api.POST("/api/v1/admin/criteria", { body: { ...body, code } });
    setPending(false);
    if (!result.response.ok) {
      const p = toProblem((result as { error?: unknown }).error);
      return setError(p.errors ? Object.values(p.errors).flat().join(" · ") : messageFor(p.code));
    }
    toast.success("Đã lưu tiêu chí.");
    await queryClient.invalidateQueries({ queryKey: KEY });
    onDone();
  };

  return (
    <form onSubmit={submit} className="flex flex-col gap-3 rounded-xl border border-border p-4" noValidate>
      <h2 className="text-lg font-semibold">{value ? "Sửa tiêu chí" : "Thêm tiêu chí"}</h2>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="c-code">Mã (chữ thường, gạch dưới; không đổi được sau khi tạo)</Label>
        <Input id="c-code" className="h-11" value={code} disabled={value !== null} onChange={(e) => setCode(e.target.value)} />
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="c-name">Tên</Label>
        <Input id="c-name" className="h-11" value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="c-guidance">Hướng dẫn (cho LLM và cán bộ)</Label>
        <textarea id="c-guidance" rows={3} className="rounded-lg border border-input p-2" value={guidance} onChange={(e) => setGuidance(e.target.value)} />
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="c-scope">Phạm vi</Label>
        <select id="c-scope" className="h-11 rounded-lg border border-input px-3" value={scope} onChange={(e) => setScope(e.target.value)}>
          {Object.entries(SCOPES).map(([v, l]) => (
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </select>
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="c-legal">Căn cứ pháp lý (không bắt buộc)</Label>
        <Input id="c-legal" className="h-11" value={legal} onChange={(e) => setLegal(e.target.value)} />
      </div>
      {value ? (
        <label className="flex min-h-11 items-center gap-2">
          <input type="checkbox" className="size-4" checked={active} onChange={(e) => setActive(e.target.checked)} />
          Đang dùng
        </label>
      ) : null}
      <FormError message={error} />
      <div className="flex gap-2">
        <SubmitButton pending={pending}>Lưu</SubmitButton>
        <button type="button" className={secondaryButton} onClick={onDone}>
          Hủy
        </button>
      </div>
    </form>
  );
}
