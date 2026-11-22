import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { EditListing } from "./edit-listing";

export const metadata: Metadata = { title: "Sửa tin" };

async function ForParams({ params }: { params: PageProps<"/toi/tin-cua-toi/[id]/sua">["params"] }) {
  const { id } = await params;
  return <EditListing id={id} />;
}

export default function EditListingPage({ params }: PageProps<"/toi/tin-cua-toi/[id]/sua">) {
  return (
    <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
    <PermissionGate permission="listing.create">
      <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 px-4 py-8">
        <h1 className="text-2xl font-semibold tracking-tight">Sửa tin</h1>
        <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
          <ForParams params={params} />
        </Suspense>
      </main>
    </PermissionGate>
    </Suspense>
  );
}
