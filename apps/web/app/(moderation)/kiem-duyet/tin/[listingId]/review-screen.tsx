"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { toast } from "sonner";

import { linkClass, secondaryButton } from "@/components/domain/form-controls";
import { StatusBadge, StatusTimeline } from "@/components/domain/listing-status";
import { ReasonDialog } from "@/components/domain/moderation-dialogs";
import { OwnerPanel } from "@/components/domain/owner-panel";
import {
  CommunityList,
  EvidenceList,
  ImageMatchCard,
  PriceComparison,
  RiskBadge,
  ScoreGauge,
  Section,
  SignalBreakdown,
} from "@/components/domain/scan-result-parts";
import { VersionConflictDialog } from "@/components/domain/version-conflict-dialog";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { formatDateTime, formatPay, formatVnd, type PayUnit } from "@/lib/format";
import { REPORT_CATEGORY_LABELS, REPORT_STATUS_LABELS } from "@/lib/moderation/labels";
import { FAILED_MESSAGE, headline, num } from "@/lib/scans/view";

import { QUEUE_KEY } from "../../queue";

const screenKey = (id: string) => ["moderation", "listing", id] as const;

/** US-602/603/604: content on the left, AI result on the right; history, reports and poster below. */
export function ReviewScreen({ listingId }: { listingId: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [conflict, setConflict] = useState(false);
  const screen = useQuery({
    queryKey: screenKey(listingId),
    queryFn: async () => {
      const result = await api.GET("/api/v1/moderation/listings/{listingId}", { params: { path: { listingId } } });
      if (!result.data) throw new Error(String(result.response.status));
      return result.data;
    },
  });
  if (screen.isError) return <p role="alert">Không tìm thấy tin.</p>;
  if (!screen.data) return <p aria-busy="true">Đang tải…</p>;
  const { listing: l, screening, history, reports, poster } = screen.data;
  const version = Number(l.version);

  /** Returns an error code for the dialog, or null after a successful decision. */
  const decide = async (action: "approve" | "reject" | "hide" | "restore", reason: string | null): Promise<string | null> => {
    const params = { path: { listingId } };
    const body = { version, reason };
    const result =
      action === "approve"
        ? await api.POST("/api/v1/moderation/listings/{listingId}/approve", { params, body })
        : action === "reject"
          ? await api.POST("/api/v1/moderation/listings/{listingId}/reject", { params, body })
          : action === "hide"
            ? await api.POST("/api/v1/moderation/listings/{listingId}/hide", { params, body })
            : await api.POST("/api/v1/moderation/listings/{listingId}/restore", { params, body });
    if (!result.response.ok) {
      const code = toProblem((result as { error?: unknown }).error).code;
      if (code === "listing.version_conflict") {
        setConflict(true);
        return null;
      }
      return code;
    }
    toast.success("Đã lưu quyết định.");
    await queryClient.invalidateQueries({ queryKey: QUEUE_KEY });
    if (action === "approve" || action === "reject") router.push("/kiem-duyet");
    else await queryClient.invalidateQueries({ queryKey: screenKey(listingId) });
    return null;
  };

  const a = screening?.assessment;
  return (
    <main className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-col gap-1">
          <Link href="/kiem-duyet" className={linkClass}>
            ← Hàng đợi
          </Link>
          <h1 className="text-2xl font-semibold tracking-tight">{l.title}</h1>
          <StatusBadge status={l.status ?? "draft"} />
        </div>
        <div className="flex flex-wrap gap-2">
          {l.status === "pending_review" ? (
            <>
              <button type="button" className={secondaryButton} onClick={async () => { const c = await decide("approve", null); if (c) toast.error(c); }}>
                Duyệt (90 ngày)
              </button>
              <ReasonDialog trigger="Từ chối" title="Từ chối tin" label="Lý do (người đăng sẽ thấy)" confirm="Từ chối" send={(r) => decide("reject", r)} />
            </>
          ) : null}
          {l.status === "verified" ? (
            <ReasonDialog trigger="Tạm ẩn" title="Tạm ẩn tin" label="Lý do (người đăng sẽ thấy)" confirm="Ẩn tin" send={(r) => decide("hide", r)} />
          ) : null}
          {l.status === "hidden" ? (
            <button type="button" className={secondaryButton} onClick={async () => { const c = await decide("restore", null); if (c) toast.error(c); }}>
              Khôi phục
            </button>
          ) : null}
        </div>
      </div>

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
        <section className="flex flex-col gap-3" aria-label="Nội dung tin">
          {l.images.length > 0 ? (
            <div className="flex gap-2 overflow-x-auto">
              {l.images.map((img, i) => (
                // eslint-disable-next-line @next/next/no-img-element -- listing photos from Caddy
                <img key={String(img.imageId)} src={img.url} alt={`Ảnh số ${i + 1}`} width={Number(img.width)} height={Number(img.height)} className="h-40 w-auto rounded-lg object-cover" />
              ))}
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">Tin không có ảnh.</p>
          )}
          {l.room ? (
            <p className="font-semibold">
              {formatVnd(num(l.room.monthlyRentVnd))}/tháng · {String(l.room.areaM2).replace(".", ",")} m²{l.room.isSelfContained ? " · khép kín" : ""}
              {l.room.depositVnd != null ? ` · cọc ${formatVnd(num(l.room.depositVnd))}` : ""}
            </p>
          ) : null}
          {l.job ? (
            <p className="font-semibold">
              {l.job.employerName} · {formatPay(num(l.job.payVnd), l.job.payUnit as PayUnit)} · {num(l.job.hoursPerWeek)} giờ/tuần
            </p>
          ) : null}
          <p className="text-sm">
            {l.streetAddress}, {l.wardName}
          </p>
          <p className="whitespace-pre-line">{l.description}</p>
          {l.amenities.length > 0 ? <p className="text-sm">Tiện nghi: {l.amenities.map((x) => x.name).join(", ")}</p> : null}
        </section>

        <section className="flex flex-col gap-3" aria-label="Kết quả AI">
          {!screening ? <p>Tin chưa được sàng lọc.</p> : null}
          {screening && !a ? (
            <p role="alert">
              {screening.status === "failed" ? `${FAILED_MESSAGE} (${screening.errorCode ?? ""})` : "AI chưa có kết quả; xem tay toàn bộ nội dung."}
            </p>
          ) : null}
          {a ? (
            <>
              <div className="flex flex-col gap-2">
                <RiskBadge level={a.riskLevel} />
                <ScoreGauge score={num(a.score)} level={a.riskLevel} />
                <p className="font-semibold">{headline(a)}</p>
              </div>
              <Section title="Dấu hiệu">
                <EvidenceList flags={a.flags} />
              </Section>
              <Section title="Bốn tín hiệu">
                <SignalBreakdown assessment={a} />
              </Section>
              {a.imageMatches.length > 0 ? (
                <Section title="Ảnh trùng">
                  {a.imageMatches.map((m) => (
                    <ImageMatchCard key={num(m.sourceImageNo)} match={m} />
                  ))}
                </Section>
              ) : null}
              {a.price ? (
                <Section title="Giá">
                  <PriceComparison price={a.price} />
                </Section>
              ) : null}
              {a.community.length > 0 ? (
                <Section title="Cộng đồng">
                  <CommunityList items={a.community} />
                </Section>
              ) : null}
            </>
          ) : null}
        </section>
      </div>

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
        <section className="flex flex-col gap-3">
          <h2 className="text-lg font-semibold">Lịch sử</h2>
          <StatusTimeline items={history} />
          {reports.length > 0 ? (
            <>
              <h2 className="text-lg font-semibold">Báo cáo về tin</h2>
              <ul className="flex flex-col gap-2 text-sm">
                {reports.map((r) => (
                  <li key={String(r.id)} className="rounded-lg border border-border p-2">
                    {formatDateTime(r.createdAt)} · {REPORT_CATEGORY_LABELS[r.category] ?? r.category} · {REPORT_STATUS_LABELS[r.status] ?? r.status}
                    {r.description ? <p>{r.description}</p> : null}
                  </li>
                ))}
              </ul>
            </>
          ) : null}
        </section>
        <section className="flex flex-col gap-3">
          <h2 className="text-lg font-semibold">Người đăng</h2>
          <OwnerPanel
            invalidate={screenKey(listingId)}
            data={{
              userId: poster.userId,
              fullName: poster.fullName,
              phone: poster.phone,
              ownerType: poster.ownerProfile?.ownerType ?? null,
              businessName: poster.ownerProfile?.businessName,
              taxCode: poster.ownerProfile?.taxCode,
              verifiedUntil: poster.ownerProfile?.verifiedUntil,
              documents: poster.documents,
            }}
          />
          <p className="text-sm text-muted-foreground">Thành viên từ {formatDateTime(poster.memberSince)}</p>
        </section>
      </div>
      <VersionConflictDialog
        open={conflict}
        onReload={() => {
          setConflict(false);
          void screen.refetch();
        }}
      />
    </main>
  );
}
