import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { CriteriaAdmin } from "./criteria";

export const metadata: Metadata = { title: "Tiêu chí" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="criteria.manage">
        <CriteriaAdmin />
      </PermissionGate>
    </Suspense>
  );
}
