import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { AuditAll } from "./audit";

export const metadata: Metadata = { title: "Nhật ký" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="audit.view_all">
        <AuditAll />
      </PermissionGate>
    </Suspense>
  );
}
