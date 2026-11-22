import type { Metadata } from "next";
import Link from "next/link";
import { Suspense } from "react";

import { linkClass } from "@/components/domain/form-controls";
import { PermissionGate } from "@/components/domain/permission-gate";

import { ListingManage } from "./listing-manage";

export const metadata: Metadata = { title: "Quản lý tin" };

async function ForParams({ params }: { params: PageProps<"/toi/tin-cua-toi/[id]">["params"] }) {
  const { id } = await params;
  return <ListingManage id={id} />;
}

export default function ManageListingPage({ params }: PageProps<"/toi/tin-cua-toi/[id]">) {
  return (
    <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
    <PermissionGate permission="listing.create">
      <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 px-4 py-8">
        <Link href="/toi/tin-cua-toi" className={linkClass}>
          ← Tin của tôi
        </Link>
        <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
          <ForParams params={params} />
        </Suspense>
      </main>
    </PermissionGate>
    </Suspense>
  );
}
