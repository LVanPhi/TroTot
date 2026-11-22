"use client";

import Link from "next/link";
import type { ReactNode } from "react";

import { linkClass } from "@/components/domain/form-controls";

import { can, useMe } from "@/lib/auth/use-auth";

import { DeleteAccountSection, ProfileSection, SignOutButton } from "./account-sections";
import { PhoneSection } from "./phone-section";
import { PostingChecklist } from "./posting-checklist";

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section aria-labelledby={title} className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <h2 id={title} className="text-lg font-semibold">
        {title}
      </h2>
      {children}
    </section>
  );
}

export function AccountPage() {
  const { data: me } = useMe();
  if (!me) return null;
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 px-4 py-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">Tài khoản của tôi</h1>
        <SignOutButton />
      </div>
      <nav aria-label="Trang của tôi" className="flex flex-wrap gap-4">
        <Link href="/toi/tin-cua-toi" className={linkClass}>
          Tin của tôi
        </Link>
        <Link href="/toi/ho-so-chu" className={linkClass}>
          Hồ sơ chủ
        </Link>
        <Link href="/toi/kiem-tra" className={linkClass}>
          Lịch sử kiểm tra tin
        </Link>
        <Link href="/toi/tin-da-luu" className={linkClass}>
          Tin đã lưu
        </Link>
        {["listing.moderate", "report.resolve", "appeal.resolve", "owner.verify"].some((p) => can(me, p)) ? (
          <Link href="/kiem-duyet" className={linkClass}>
            Kiểm duyệt
          </Link>
        ) : null}
      </nav>
      <Section title="Điều kiện đăng tin">
        <PostingChecklist posting={me.posting} />
      </Section>
      <Section title="Thông tin">
        <ProfileSection me={me} />
      </Section>
      <Section title="Số điện thoại">
        <PhoneSection me={me} />
      </Section>
      <Section title="Xóa tài khoản">
        <DeleteAccountSection />
      </Section>
    </main>
  );
}
