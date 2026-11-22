import { CircleCheck, CircleDot, Clock, EyeOff, FileText, Hourglass, Trash2, XCircle } from "lucide-react";

import { formatDateTime } from "@/lib/format";
import { STATUS_LABELS, type ListingStatus } from "@/lib/listings/model";

const STYLES: Record<ListingStatus, { Icon: typeof Clock; className: string }> = {
  draft: { Icon: FileText, className: "bg-muted text-foreground" },
  screening: { Icon: Hourglass, className: "bg-sky-50 text-sky-900" },
  pending_review: { Icon: Clock, className: "bg-amber-50 text-amber-900" },
  verified: { Icon: CircleCheck, className: "bg-emerald-50 text-emerald-800" },
  rejected: { Icon: XCircle, className: "bg-red-50 text-red-800" },
  hidden: { Icon: EyeOff, className: "bg-orange-50 text-orange-900" },
  removed: { Icon: Trash2, className: "bg-muted text-muted-foreground" },
  expired: { Icon: CircleDot, className: "bg-muted text-muted-foreground" },
};

/** Listing status with text + icon, never colour alone (docs/06-frontend.md, Thành phần dùng chung). */
export function StatusBadge({ status }: { status: string }) {
  const s = (status in STYLES ? status : "draft") as ListingStatus;
  const { Icon, className } = STYLES[s];
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-sm font-medium ${className}`}>
      <Icon aria-hidden className="size-4" />
      {STATUS_LABELS[s]}
    </span>
  );
}

export interface TimelineItem {
  fromStatus: string | null;
  toStatus: string;
  reason: string | null;
  actor: string;
  createdAt: string;
}

/** US-308: transitions with reasons; the poster sees "Cán bộ xác minh", never a moderator's name. */
export function StatusTimeline({ items }: { items: TimelineItem[] }) {
  return (
    <ol className="flex flex-col gap-3 border-l-2 border-border pl-4">
      {items.map((t, i) => (
        <li key={`${t.createdAt}-${i}`} className="flex flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge status={t.toStatus} />
            <span className="text-sm text-muted-foreground">
              {formatDateTime(t.createdAt)} · {t.actor}
            </span>
          </div>
          {t.reason ? <p className="text-sm">Lý do: {t.reason}</p> : null}
        </li>
      ))}
    </ol>
  );
}
