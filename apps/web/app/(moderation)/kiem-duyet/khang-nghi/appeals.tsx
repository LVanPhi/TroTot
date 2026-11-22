"use client";

import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { toast } from "sonner";

import { linkClass } from "@/components/domain/form-controls";
import { StatusBadge } from "@/components/domain/listing-status";
import { ReasonDialog } from "@/components/domain/moderation-dialogs";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { formatDateTime } from "@/lib/format";
import { APPEAL_STATUS_LABELS } from "@/lib/moderation/labels";

const KEY = ["moderation", "appeals"] as const;

/** US-607: accept → listing back to verified; reject → hidden becomes removed, rejected stays. */
export function Appeals() {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<"open" | "accepted" | "rejected">("open");
  const list = useQuery({
    queryKey: [...KEY, status],
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.GET("/api/v1/moderation/appeals", { params: { query: { status, pageSize: 50 } } })).data ?? null,
  });

  const decide = async (id: number, action: "accept" | "reject", note: string) => {
    const params = { path: { id } };
    const { error } =
      action === "accept"
        ? await api.POST("/api/v1/moderation/appeals/{id}/accept", { params, body: { note } })
        : await api.POST("/api/v1/moderation/appeals/{id}/reject", { params, body: { note } });
    if (error) return toProblem(error).code;
    toast.success("Đã xử lý kháng nghị.");
    await queryClient.invalidateQueries({ queryKey: KEY });
    return null;
  };

  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Kháng nghị</h1>
      <div role="group" aria-label="Trạng thái" className="flex gap-2">
        {(["open", "accepted", "rejected"] as const).map((s) => (
          <button
            key={s}
            type="button"
            aria-pressed={status === s}
            onClick={() => setStatus(s)}
            className={`min-h-11 rounded-full border px-3 text-sm ${status === s ? "border-primary bg-primary text-primary-foreground" : "border-border"}`}
          >
            {APPEAL_STATUS_LABELS[s]}
          </button>
        ))}
      </div>
      {list.data && list.data.items.length === 0 ? <p>Không có kháng nghị.</p> : null}
      <ul className="flex flex-col gap-3">
        {(list.data?.items ?? []).map((a) => (
          <li key={String(a.id)} className="flex flex-col gap-2 rounded-xl border border-border p-4">
            <Link href={`/kiem-duyet/tin/${a.listingId}`} className={linkClass}>
              {a.listingTitle}
            </Link>
            <p className="flex flex-wrap items-center gap-2 text-sm">
              Quyết định bị kháng nghị: <StatusBadge status={a.decisionStatus} />
              {a.decisionReason ? `· ${a.decisionReason}` : ""}
            </p>
            <p>Lý do kháng nghị: {a.reason}</p>
            <p className="text-sm text-muted-foreground">Gửi lúc {formatDateTime(a.createdAt)}</p>
            {a.decisionNote ? <p className="text-sm">Ghi chú xử lý: {a.decisionNote}</p> : null}
            {a.status === "open" ? (
              <div className="flex flex-wrap gap-2">
                <ReasonDialog
                  trigger="Chấp nhận"
                  title="Chấp nhận kháng nghị"
                  description="Tin sẽ trở lại trạng thái Đã xác minh."
                  label="Ghi chú (bắt buộc)"
                  confirm="Chấp nhận"
                  send={(n) => decide(Number(a.id), "accept", n)}
                />
                <ReasonDialog
                  trigger="Bác"
                  title="Bác kháng nghị"
                  description="Tin đang tạm ẩn sẽ bị gỡ; tin bị từ chối giữ nguyên."
                  label="Ghi chú (bắt buộc)"
                  confirm="Bác kháng nghị"
                  send={(n) => decide(Number(a.id), "reject", n)}
                />
              </div>
            ) : null}
          </li>
        ))}
      </ul>
    </main>
  );
}
