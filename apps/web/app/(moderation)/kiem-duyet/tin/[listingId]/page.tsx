import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { ReviewScreen } from "./review-screen";

export const metadata: Metadata = { title: "Duyệt tin" };

async function ForParams({ params }: { params: PageProps<"/kiem-duyet/tin/[listingId]">["params"] }) {
  const { listingId } = await params;
  return <ReviewScreen listingId={listingId} />;
}

export default function ReviewPage({ params }: PageProps<"/kiem-duyet/tin/[listingId]">) {
  return (
    <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
      <PermissionGate permission="listing.moderate">
        <ForParams params={params} />
      </PermissionGate>
    </Suspense>
  );
}
