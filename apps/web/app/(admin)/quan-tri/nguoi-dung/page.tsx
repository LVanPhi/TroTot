import type { Metadata } from "next";
import { Suspense } from "react";

import { PermissionGate } from "@/components/domain/permission-gate";

import { Users } from "./users";

export const metadata: Metadata = { title: "Người dùng" };

export default function Page() {
  return (
    <Suspense fallback={null}>
      <PermissionGate permission="user.manage">
        <Users />
      </PermissionGate>
    </Suspense>
  );
}
