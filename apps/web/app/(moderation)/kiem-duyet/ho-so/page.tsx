import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { OwnerProfiles } from "./owner-profiles";

export const metadata: Metadata = { title: "Hồ sơ chủ" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="owner.verify">
        <OwnerProfiles />
      </PermissionGate>
    </Suspense>
  );
}
