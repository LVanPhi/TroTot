"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

import { can, useMe } from "@/lib/auth/use-auth";

const ITEMS = [
  { href: "/kiem-duyet", label: "Hàng đợi", permission: "listing.moderate" },
  { href: "/kiem-duyet/bao-cao", label: "Báo cáo", permission: "report.resolve" },
  { href: "/kiem-duyet/khang-nghi", label: "Kháng nghị", permission: "appeal.resolve" },
  { href: "/kiem-duyet/ho-so", label: "Hồ sơ chủ", permission: "owner.verify" },
  { href: "/kiem-duyet/nhat-ky", label: "Nhật ký của tôi", permission: "audit.view_own" },
];

/** Moderation sections the caller has a permission for (by permission, never by role name). */
export function ModerationNav() {
  const { data: me } = useMe();
  const path = usePathname();
  const items = ITEMS.filter((i) => can(me, i.permission));
  if (items.length === 0) return null;
  return (
    <nav aria-label="Kiểm duyệt" className="flex flex-wrap gap-1 border-b border-border pb-2">
      {items.map((i) => {
        const active = i.href === "/kiem-duyet" ? path === i.href || path.startsWith("/kiem-duyet/tin") : path.startsWith(i.href);
        return (
          <Link
            key={i.href}
            href={i.href}
            aria-current={active ? "page" : undefined}
            className={`inline-flex min-h-11 items-center rounded-lg px-3 font-medium ${active ? "bg-muted" : "hover:bg-muted"}`}
          >
            {i.label}
          </Link>
        );
      })}
    </nav>
  );
}
