"use client";

import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { toast } from "sonner";

import { linkClass } from "@/components/domain/form-controls";
import { ReasonDialog } from "@/components/domain/moderation-dialogs";
import { EvidenceList, RiskBadge } from "@/components/domain/scan-result-parts";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { formatDateTime } from "@/lib/format";
import { REPORT_CATEGORY_LABELS, REPORT_STATUS_LABELS } from "@/lib/moderation/labels";
import { num } from "@/lib/scans/view";

const KEY = ["moderation", "reports"] as const;

/** US-606: open reports; the detail shows the reported listing or quick check (text, AI result, images). */
export function Reports() {
  const [status, setStatus] = useState<"open" | "confirmed" | "dismissed">("open");
  const [selected, setSelected] = useState<number | null>(null);
  const list = useQuery({
    queryKey: [...KEY, status],
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.GET("/api/v1/moderation/reports", { params: { query: { status, pageSize: 50 } } })).data ?? null,
  });

  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Báo cáo</h1>
      <div role="group" aria-label="Trạng thái" className="flex gap-2">
        {(["open", "confirmed", "dismissed"] as const).map((s) => (
          <button
            key={s}
            type="button"
            aria-pressed={status === s}
            onClick={() => setStatus(s)}
            className={`min-h-11 rounded-full border px-3 text-sm ${status === s ? "border-primary bg-primary text-primary-foreground" : "border-border"}`}
          >
            {REPORT_STATUS_LABELS[s]}
          </button>
        ))}
      </div>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_2fr]">
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {(list.data?.items ?? []).map((r) => (
            <li key={String(r.id)}>
              <button
                type="button"
                onClick={() => setSelected(Number(r.id))}
                aria-current={selected === Number(r.id) ? "true" : undefined}
                className={`flex w-full flex-col items-start gap-1 p-3 text-left hover:bg-muted ${selected === Number(r.id) ? "bg-muted" : ""}`}
              >
                <span className="font-medium">{REPORT_CATEGORY_LABELS[r.category] ?? r.category}</span>
                <span className="text-sm text-muted-foreground">
                  {r.targetType === "listing" ? `Tin: ${r.listingTitle}` : "Lượt kiểm tra tin"} · {formatDateTime(r.createdAt)}
                </span>
              </button>
            </li>
          ))}
          {list.data && list.data.items.length === 0 ? <li className="p-3 text-muted-foreground">Không có báo cáo.</li> : null}
        </ul>
        {selected ? <ReportDetail id={selected} onDone={() => setSelected(null)} /> : <p className="text-muted-foreground">Chọn một báo cáo để xem.</p>}
      </div>
    </main>
  );
}

function ReportDetail({ id, onDone }: { id: number; onDone: () => void }) {
  const queryClient = useQueryClient();
  const [hide, setHide] = useState(false);
  const detail = useQuery({
    queryKey: ["moderation", "report", id],
    queryFn: async () => (await api.GET("/api/v1/moderation/reports/{id}", { params: { path: { id } } })).data ?? null,
  });
  const d = detail.data;
  if (!d) return <p aria-busy="true">Đang tải…</p>;

  const finish = async () => {
    toast.success("Đã xử lý báo cáo.");
    await queryClient.invalidateQueries({ queryKey: KEY });
    onDone();
  };
  const listingVersion = d.listing ? Number(d.listing.listing.version) : null;
  const a = d.scan?.result?.assessment;
  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <h2 className="text-lg font-semibold">{REPORT_CATEGORY_LABELS[d.category] ?? d.category}</h2>
      {d.description ? <p>Người báo cáo ghi: {d.description}</p> : null}
      {d.resolutionNote ? <p className="text-sm">Ghi chú xử lý: {d.resolutionNote}</p> : null}

      {d.listing ? (
        <p>
          Tin:{" "}
          <Link href={`/kiem-duyet/tin/${d.listing.listing.listingId}`} className={linkClass}>
            {d.listing.listing.title}
          </Link>{" "}
          ({d.listing.listing.status})
        </p>
      ) : null}
      {d.scan ? (
        <div className="flex flex-col gap-2">
          <h3 className="font-medium">Nội dung lượt kiểm tra</h3>
          {d.scan.inputText ? <blockquote className="whitespace-pre-line rounded-lg bg-muted/50 p-3">{d.scan.inputText}</blockquote> : null}
          {d.scan.imageNumbers.length > 0 ? (
            <div className="flex flex-wrap gap-2">
              {d.scan.imageNumbers.map((n) => (
                <ScanImage key={n} reportId={id} imageNo={Number(n)} />
              ))}
            </div>
          ) : null}
          {a ? (
            <div className="flex flex-col gap-2">
              <span className="flex items-center gap-2">
                <RiskBadge level={a.riskLevel} size="sm" /> {num(a.score)}/100
              </span>
              <EvidenceList flags={a.flags} />
            </div>
          ) : null}
        </div>
      ) : null}

      {d.status === "open" ? (
        <div className="flex flex-wrap gap-2">
          <ReasonDialog
            trigger="Xác nhận"
            title="Xác nhận báo cáo"
            description="Số điện thoại, số tài khoản, đường link liên quan sẽ được cộng một báo cáo đã xác nhận."
            label="Ghi chú (bắt buộc)"
            confirm="Xác nhận"
            extra={
              d.listing && d.listing.listing.status === "verified" ? (
                <label className="flex items-center gap-2">
                  <input type="checkbox" className="size-4" checked={hide} onChange={(e) => setHide(e.target.checked)} />
                  Ẩn tin này luôn (lý do là ghi chú)
                </label>
              ) : null
            }
            send={async (note) => {
              const { error } = await api.POST("/api/v1/moderation/reports/{id}/confirm", {
                params: { path: { id } },
                body: { note, hideListing: hide, listingVersion: hide ? listingVersion : null },
              });
              if (error) return toProblem(error).code;
              await finish();
              return null;
            }}
          />
          <ReasonDialog
            trigger="Bác bỏ"
            title="Bác bỏ báo cáo"
            label="Ghi chú (bắt buộc)"
            confirm="Bác bỏ"
            send={async (note) => {
              const { error } = await api.POST("/api/v1/moderation/reports/{id}/dismiss", { params: { path: { id } }, body: { note } });
              if (error) return toProblem(error).code;
              await finish();
              return null;
            }}
          />
        </div>
      ) : null}
    </section>
  );
}

/** The image needs the bearer token, so it is fetched through the API client and shown from a blob URL. */
function ScanImage({ reportId, imageNo }: { reportId: number; imageNo: number }) {
  const image = useQuery({
    queryKey: ["moderation", "report", reportId, "image", imageNo],
    staleTime: Infinity,
    queryFn: async () => {
      const { data, response } = await api.GET("/api/v1/moderation/reports/{id}/scan-images/{imageNo}", {
        params: { path: { id: reportId, imageNo } },
        parseAs: "blob",
      });
      return response.ok && data ? URL.createObjectURL(data as Blob) : null;
    },
  });
  if (!image.data) return <div className="size-32 rounded-lg bg-muted" aria-busy={image.isPending} />;
  // eslint-disable-next-line @next/next/no-img-element -- blob URL
  return <img src={image.data} alt={`Ảnh số ${imageNo} của lượt kiểm tra`} width={128} height={128} className="size-32 rounded-lg object-cover" />;
}
