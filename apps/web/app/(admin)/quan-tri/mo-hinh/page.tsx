import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { ModelVersions } from "./model-versions";

export const metadata: Metadata = { title: "Mô hình" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="model.manage">
        <ModelVersions />
      </PermissionGate>
    </Suspense>
  );
}
