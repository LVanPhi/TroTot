import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { MyAudit } from "./my-audit";

export const metadata: Metadata = { title: "Nhật ký của tôi" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="audit.view_own">
        <MyAudit />
      </PermissionGate>
    </Suspense>
  );
}
