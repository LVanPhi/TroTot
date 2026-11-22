"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

import { can, useMe } from "@/lib/auth/use-auth";

const ITEMS = [
  { href: "/quan-tri/nguoi-dung", label: "Người dùng", permission: "user.manage" },
  { href: "/quan-tri/tieu-chi", label: "Tiêu chí", permission: "criteria.manage" },
  { href: "/quan-tri/mo-hinh", label: "Mô hình", permission: "model.manage" },
  { href: "/quan-tri/thong-ke", label: "Thống kê", permission: "stats.view" },
  { href: "/quan-tri/nhat-ky", label: "Nhật ký", permission: "audit.view_all" },
];

/** Sections the caller has a permission for (by permission, never by role name). */
export function AdminNav() {
  const { data: me } = useMe();
  const path = usePathname();
  const items = ITEMS.filter((i) => can(me, i.permission));
  if (items.length === 0) return null;
  return (
    <nav aria-label="Quản trị" className="flex flex-wrap gap-1 border-b border-border pb-2">
      {items.map((i) => (
        <Link
          key={i.href}
          href={i.href}
          aria-current={path.startsWith(i.href) ? "page" : undefined}
          className={`inline-flex min-h-11 items-center rounded-lg px-3 font-medium ${path.startsWith(i.href) ? "bg-muted" : "hover:bg-muted"}`}
        >
          {i.label}
        </Link>
      ))}
    </nav>
  );
}
