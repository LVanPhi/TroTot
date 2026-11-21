"use client";

import { ShieldAlert } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, type ReactNode } from "react";

import { can, useMe, useSession } from "@/lib/auth/use-auth";

import { secondaryButton } from "./form-controls";

/**
 * Renders children only for signed-in users who have `permission` (or anyone signed in when omitted).
 * Guests go to /dang-nhap?next=<here>; signed-in users without the permission see a friendly 403, never a
 * redirect loop (docs/06-frontend.md, Bản đồ trang).
 */
export function PermissionGate({ permission, children }: { permission?: string; children: ReactNode }) {
  const { status } = useSession();
  const me = useMe();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (status === "guest") router.replace(`/dang-nhap?next=${encodeURIComponent(pathname)}`);
  }, [status, router, pathname]);

  if (status !== "signed_in" || me.isPending) {
    return (
      <p role="status" className="px-4 py-10 text-center text-muted-foreground">
        Đang tải…
      </p>
    );
  }
  if (permission && !can(me.data, permission)) return <Forbidden />;
  return <>{children}</>;
}

export function Forbidden() {
  return (
    <main className="mx-auto flex w-full max-w-md flex-1 flex-col items-center justify-center gap-4 px-4 py-12 text-center">
      <ShieldAlert aria-hidden className="size-10 text-muted-foreground" />
      <h1 className="text-2xl font-semibold">Bạn không có quyền xem trang này</h1>
      <p className="text-muted-foreground">Trang này dành cho tài khoản có quyền phù hợp.</p>
      <Link href="/" className={secondaryButton}>
        Về trang chủ
      </Link>
    </main>
  );
}
