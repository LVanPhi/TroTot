"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { toast } from "sonner";

import { secondaryButton } from "@/components/domain/form-controls";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { api, type Schemas } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { formatDateTime } from "@/lib/format";

type ModelVersion = Schemas["AdminCatalogModelVersion"];
const KEY = ["admin", "model-versions"] as const;

export const COMPONENT_LABELS: Record<string, string> = {
  ocr: "OCR",
  image_embedding: "Embedding ảnh",
  text_embedding: "Embedding chữ",
  aggregator: "Bộ gộp điểm",
  llm: "LLM",
  text_classifier: "Bộ phân loại chữ",
};

/** Text of the confirmation (US-803): activating an aggregator changes the risk thresholds for every new scan. */
export function activationSummary(target: ModelVersion, current: ModelVersion | undefined): string {
  if (target.component !== "aggregator" || !target.thresholds) return `Bật ${target.name} ${target.version}; phiên bản đang chạy sẽ tắt.`;
  const t = target.thresholds;
  const now = current?.thresholds ? ` (hiện tại: Cần thận trọng từ ${current.thresholds.caution}, Cao từ ${current.thresholds.high})` : "";
  return `Ngưỡng mới: Cần thận trọng từ ${t.caution}, Cao từ ${t.high}${now}. Áp dụng cho mọi lượt chấm sau khi bật.`;
}

export function ModelVersions() {
  const queryClient = useQueryClient();
  const list = useQuery({ queryKey: KEY, queryFn: async () => (await api.GET("/api/v1/admin/model-versions")).data ?? [] });
  const [confirm, setConfirm] = useState<ModelVersion | null>(null);

  const groups = new Map<string, ModelVersion[]>();
  for (const m of list.data ?? []) groups.set(m.component, [...(groups.get(m.component) ?? []), m]);

  const activate = async (m: ModelVersion) => {
    const { error } = await api.POST("/api/v1/admin/model-versions/{id}/activate", { params: { path: { id: Number(m.id) } } });
    setConfirm(null);
    if (error) return toast.error(messageFor(toProblem(error).code));
    toast.success(`Đã kích hoạt ${m.name} ${m.version}.`);
    await queryClient.invalidateQueries({ queryKey: KEY });
  };

  const current = confirm ? list.data?.find((m) => m.component === confirm.component && m.isActive) : undefined;
  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Phiên bản mô hình</h1>
      {[...groups.entries()].map(([component, versions]) => (
        <section key={component} className="flex flex-col gap-2">
          <h2 className="text-lg font-semibold">{COMPONENT_LABELS[component] ?? component}</h2>
          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {versions.map((m) => (
              <li key={m.id} className="flex flex-wrap items-center justify-between gap-2 p-3">
                <span>
                  <span className="font-medium">
                    {m.name} · {m.version}
                  </span>
                  {m.thresholds ? <span className="text-sm text-muted-foreground"> · ngưỡng {m.thresholds.caution}/{m.thresholds.high}</span> : null}
                  {m.activatedAt ? <span className="block text-xs text-muted-foreground">Kích hoạt lần cuối {formatDateTime(m.activatedAt)}</span> : null}
                </span>
                {m.isActive ? (
                  <span className="rounded-full bg-emerald-50 px-3 py-1 text-sm font-medium text-emerald-800">Đang chạy</span>
                ) : (
                  <button type="button" className={secondaryButton} onClick={() => setConfirm(m)}>
                    Kích hoạt
                  </button>
                )}
              </li>
            ))}
          </ul>
        </section>
      ))}
      <Dialog open={confirm !== null} onOpenChange={(o) => !o && setConfirm(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Kích hoạt {confirm ? `${confirm.name} ${confirm.version}` : ""}?</DialogTitle>
            <DialogDescription>{confirm ? activationSummary(confirm, current) : ""}</DialogDescription>
          </DialogHeader>
          <button type="button" className={secondaryButton} onClick={() => confirm && void activate(confirm)}>
            Kích hoạt
          </button>
        </DialogContent>
      </Dialog>
    </main>
  );
}
