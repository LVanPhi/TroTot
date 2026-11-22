import { BadgeCheck, Briefcase, Home } from "lucide-react";
import Link from "next/link";

import type { Schemas } from "@/lib/api/client";
import { formatDistance, formatPay, formatVndShort, type PayUnit } from "@/lib/format";

export type ListingItem = Schemas["SearchEndpointsItem"];

const n = (v: number | string) => Number(v);

/** One search result (docs/06-frontend.md, Thành phần dùng chung). The image is lazy below the first screen. */
export function ListingCard({ item, eager = false }: { item: ListingItem; eager?: boolean }) {
  return (
    <Link href={`/tin/${item.listingId}`} className="flex gap-3 rounded-xl border border-border p-3 hover:bg-muted">
      {item.coverImageUrl ? (
        // eslint-disable-next-line @next/next/no-img-element -- listing photos come from Caddy, already resized WebP
        <img
          src={item.coverImageUrl}
          alt=""
          width={112}
          height={84}
          loading={eager ? "eager" : "lazy"}
          className="h-21 w-28 shrink-0 rounded-lg object-cover"
        />
      ) : (
        <div aria-hidden className="flex h-21 w-28 shrink-0 items-center justify-center rounded-lg bg-muted">
          {item.listingType === "job" ? <Briefcase className="size-6" /> : <Home className="size-6" />}
        </div>
      )}
      <div className="flex min-w-0 flex-col gap-1">
        <span className="line-clamp-2 font-medium">{item.title}</span>
        {item.room ? (
          <span className="font-semibold">
            {formatVndShort(n(item.room.monthlyRentVnd))}/tháng · {String(item.room.areaM2).replace(".", ",")} m²
            {item.room.isSelfContained ? " · khép kín" : ""}
          </span>
        ) : item.job ? (
          <span className="font-semibold">{formatPay(n(item.job.payVnd), item.job.payUnit as PayUnit)}</span>
        ) : null}
        <span className="text-sm text-muted-foreground">
          {item.wardName} · {formatDistance(n(item.distanceM))}
        </span>
        <span className="inline-flex items-center gap-1 text-sm text-emerald-800">
          <BadgeCheck aria-hidden className="size-4" />
          Đã xác minh
        </span>
      </div>
    </Link>
  );
}
