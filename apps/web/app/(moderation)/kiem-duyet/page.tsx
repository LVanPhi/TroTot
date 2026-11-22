import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { Queue } from "./queue";

export const metadata: Metadata = { title: "Hàng đợi duyệt" };

export default function QueuePage() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="listing.moderate">
        <Queue />
      </PermissionGate>
    </Suspense>
  );
}
