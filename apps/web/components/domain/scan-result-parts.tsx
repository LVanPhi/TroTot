import { Images, OctagonAlert, ShieldCheck, TriangleAlert, Users, Wallet } from "lucide-react";
import Link from "next/link";
import type { ReactNode } from "react";

import { tipFor } from "@/lib/criteria-tips";
import {
  communitySentence,
  imageMatchSentence,
  priceSentence,
  RISK_LABELS,
  signalRows,
  toRiskLevel,
  type Assessment,
  type CommunityItem,
  type ImageMatch,
  type Price,
  type RiskLevel,
} from "@/lib/scans/view";

/** Level → icon + colours (docs/06-frontend.md table). Text + icon + colour, never colour alone. */
const RISK_STYLES: Record<RiskLevel, { Icon: typeof ShieldCheck; className: string }> = {
  low: { Icon: ShieldCheck, className: "bg-emerald-50 text-emerald-800 border-emerald-200" },
  caution: { Icon: TriangleAlert, className: "bg-amber-50 text-amber-900 border-amber-200" },
  high: { Icon: OctagonAlert, className: "bg-red-50 text-red-800 border-red-200" },
};

export function RiskBadge({ level, size = "md" }: { level: string; size?: "sm" | "md" }) {
  const risk = toRiskLevel(level);
  const { Icon, className } = RISK_STYLES[risk];
  const sizing = size === "sm" ? "gap-1 px-2 py-0.5 text-sm" : "gap-1.5 px-3 py-1 text-base";
  return (
    <span className={`inline-flex items-center rounded-full border font-semibold ${sizing} ${className}`}>
      <Icon aria-hidden className={size === "sm" ? "size-4" : "size-5"} />
      <span>Mức rủi ro: {RISK_LABELS[risk]}</span>
    </span>
  );
}

/** Score out of 100 as text plus a bar; the bar is decorative, the number carries the meaning. */
export function ScoreGauge({ score, level }: { score: number; level: string }) {
  const bar = { low: "bg-emerald-600", caution: "bg-amber-500", high: "bg-red-600" }[toRiskLevel(level)];
  return (
    <div className="flex flex-col gap-1.5">
      <p className="text-3xl font-bold tabular-nums">
        {score}
        <span className="text-lg font-medium text-muted-foreground">/100</span>
      </p>
      <div aria-hidden className="h-2 w-full overflow-hidden rounded-full bg-muted">
        <div className={`h-full ${bar}`} style={{ width: `${Math.min(100, Math.max(0, score))}%` }} />
      </div>
    </div>
  );
}

export function Section({ title, icon, children }: { title: string; icon?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <h2 className="flex items-center gap-2 text-lg font-semibold">
        {icon}
        {title}
      </h2>
      {children}
    </section>
  );
}

export function EvidenceList({ flags }: { flags: Assessment["flags"] }) {
  if (flags.length === 0) return <p className="text-muted-foreground">Không thấy dấu hiệu nào trong nội dung chữ.</p>;
  return (
    <ol className="flex flex-col gap-4">
      {flags.map((flag) => (
        <li key={flag.code} className="flex flex-col gap-2">
          <p className="font-semibold">{flag.name}</p>
          <blockquote className="border-l-4 border-amber-400 bg-muted/50 px-3 py-2 italic">“{flag.evidence}”</blockquote>
          {flag.legalBasis ? <p className="text-sm text-muted-foreground">Căn cứ: {flag.legalBasis}</p> : null}
          <p className="text-sm">
            <span className="font-medium">Lời khuyên: </span>
            {tipFor(flag.code)}
          </p>
        </li>
      ))}
    </ol>
  );
}

export function SignalBreakdown({ assessment }: { assessment: Assessment }) {
  return (
    <ul className="flex flex-col gap-3">
      {signalRows(assessment).map((row) => (
        <li key={row.key} className="flex flex-col gap-1">
          <div className="flex items-baseline justify-between gap-2">
            <span className="font-medium">{row.label}</span>
            <span className="tabular-nums text-sm">{row.percent === null ? "Không áp dụng" : `${row.percent}%`}</span>
          </div>
          {row.percent === null ? (
            <p className="text-sm text-muted-foreground">{row.note}</p>
          ) : (
            <div
              role="meter"
              aria-label={row.label}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={row.percent}
              className="h-2 w-full overflow-hidden rounded-full bg-muted"
            >
              <div className="h-full bg-foreground/70" style={{ width: `${row.percent}%` }} />
            </div>
          )}
        </li>
      ))}
    </ul>
  );
}

export function ImageMatchCard({ match }: { match: ImageMatch }) {
  return (
    <div className="flex flex-col gap-1 rounded-lg border border-border p-3">
      <p className="flex items-start gap-2">
        <Images aria-hidden className="mt-0.5 size-4 shrink-0" />
        {imageMatchSentence(match)}
      </p>
      {match.publicListing ? (
        <Link href={`/tin/${match.publicListing.listingId}`} className="text-sm font-medium text-primary underline-offset-4 hover:underline">
          Xem tin đang hiển thị: {match.publicListing.title}
        </Link>
      ) : null}
    </div>
  );
}

export function PriceComparison({ price }: { price: Price }) {
  return (
    <p className="flex items-start gap-2">
      <Wallet aria-hidden className="mt-0.5 size-4 shrink-0" />
      {priceSentence(price)}
    </p>
  );
}

export function CommunityList({ items }: { items: CommunityItem[] }) {
  return (
    <ul className="flex flex-col gap-2">
      {items.map((item) => (
        <li key={`${item.kind}-${item.hint}`} className="flex items-start gap-2">
          <Users aria-hidden className="mt-0.5 size-4 shrink-0" />
          {communitySentence(item)}
        </li>
      ))}
    </ul>
  );
}
