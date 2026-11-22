import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { Appeals } from "./appeals";

export const metadata: Metadata = { title: "Kháng nghị" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="appeal.resolve">
        <Appeals />
      </PermissionGate>
    </Suspense>
  );
}
