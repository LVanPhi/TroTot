import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { Reports } from "./reports";

export const metadata: Metadata = { title: "Báo cáo" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="report.resolve">
        <Reports />
      </PermissionGate>
    </Suspense>
  );
}
