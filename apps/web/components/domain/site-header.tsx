"use client";

import { CircleUserRound, LogIn } from "lucide-react";
import Link from "next/link";

import { useSession } from "@/lib/auth/use-auth";

const navLink =
  "inline-flex min-h-11 items-center gap-2 rounded-lg px-3 font-medium hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring";

export function SiteHeader() {
  const { status } = useSession();
  return (
    <header className="border-b border-border">
      <nav aria-label="Chính" className="mx-auto flex w-full max-w-5xl items-center justify-between gap-2 px-4 py-1">
        <div className="flex items-center gap-1">
          <Link href="/" className="min-h-11 content-center pr-2 font-semibold">
            Trọ &amp; Việc Sạch
          </Link>
          <Link href="/tin" className={navLink}>
            Tìm tin
          </Link>
          <Link href="/kiem-tra" className={navLink}>
            Kiểm tra tin
          </Link>
        </div>
        {status === "signed_in" ? (
          <Link href="/toi" className={navLink}>
            <CircleUserRound aria-hidden className="size-5" />
            Tài khoản
          </Link>
        ) : status === "guest" ? (
          <Link href="/dang-nhap" className={navLink}>
            <LogIn aria-hidden className="size-5" />
            Đăng nhập
          </Link>
        ) : (
          <span className="min-h-11" aria-hidden />
        )}
      </nav>
    </header>
  );
}
