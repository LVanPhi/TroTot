"use client";

import { Info, ListChecks, Gauge, Images, Wallet, Users } from "lucide-react";
import Link from "next/link";

import { secondaryButton } from "@/components/domain/form-controls";
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
import { ReportScanButton } from "@/components/domain/report-scan-button";
import { DISCLAIMER, FAILED_MESSAGE, headline, num, type Scan } from "@/lib/scans/view";

/** Always shown with any AI result (docs/06-frontend.md, nguyên tắc 4). */
export function Disclaimer() {
  return (
    <p role="note" className="flex items-start gap-2 rounded-lg border border-border bg-muted/40 p-3 text-sm">
      <Info aria-hidden className="mt-0.5 size-4 shrink-0" />
      {DISCLAIMER}
    </p>
  );
}

export function ScanPending() {
  return (
    <div aria-live="polite" aria-busy="true" className="flex flex-col gap-4">
      <p className="font-medium">Đang phân tích nội dung và hình ảnh…</p>
      <p className="text-sm text-muted-foreground">Thường mất dưới 10 giây. Bạn có thể giữ trang này mở.</p>
      <div className="flex flex-col gap-3 motion-safe:animate-pulse" aria-hidden>
        <div className="h-8 w-48 rounded-full bg-muted" />
        <div className="h-4 w-full rounded bg-muted" />
        <div className="h-4 w-2/3 rounded bg-muted" />
        <div className="h-24 w-full rounded-xl bg-muted" />
      </div>
    </div>
  );
}

const checkAnother = (
  <Link href="/kiem-tra" className={secondaryButton}>
    Kiểm tra tin khác
  </Link>
);

/** The result page body in the order of docs/06-frontend.md (Hiển thị kết quả kiểm tra). */
export function ScanResult({ scan }: { scan: Scan }) {
  if (scan.status === "failed" || (scan.status === "completed" && !scan.assessment)) {
    return (
      <div className="flex flex-col gap-4">
        <p role="alert" className="text-lg font-medium">
          {FAILED_MESSAGE}
        </p>
        {scan.errorCode ? <p className="text-xs text-muted-foreground">Mã hỗ trợ: {scan.errorCode}</p> : null}
        <div>{checkAnother}</div>
      </div>
    );
  }
  if (!scan.assessment) return <ScanPending />;

  const a = scan.assessment;
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3">
        <RiskBadge level={a.riskLevel} />
        <ScoreGauge score={num(a.score)} level={a.riskLevel} />
        <p className="text-xl font-semibold">{headline(a)}</p>
      </div>
      <Disclaimer />
      <Section title="Dấu hiệu trong nội dung" icon={<ListChecks aria-hidden className="size-5" />}>
        <EvidenceList flags={a.flags} />
      </Section>
      <Section title="Bốn tín hiệu" icon={<Gauge aria-hidden className="size-5" />}>
        <SignalBreakdown assessment={a} />
      </Section>
      {a.imageMatches.length > 0 ? (
        <Section title="Ảnh dùng lại" icon={<Images aria-hidden className="size-5" />}>
          {a.imageMatches.map((m) => (
            <ImageMatchCard key={num(m.sourceImageNo)} match={m} />
          ))}
        </Section>
      ) : null}
      {a.price ? (
        <Section title="So sánh giá" icon={<Wallet aria-hidden className="size-5" />}>
          <PriceComparison price={a.price} />
        </Section>
      ) : null}
      {a.community.length > 0 ? (
        <Section title="Cộng đồng" icon={<Users aria-hidden className="size-5" />}>
          <CommunityList items={a.community} />
        </Section>
      ) : null}
      <div className="flex flex-col gap-3 sm:flex-row">
        {scan.canReport ? <ReportScanButton scanId={scan.scanId} /> : null}
        {checkAnother}
      </div>
    </div>
  );
}
