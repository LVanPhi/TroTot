import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { StatsPage } from "./stats";

export const metadata: Metadata = { title: "Thống kê" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="stats.view">
        <StatsPage />
      </PermissionGate>
    </Suspense>
  );
}
