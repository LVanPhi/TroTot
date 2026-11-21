import type { Metadata } from "next";

import { PermissionGate } from "@/components/domain/permission-gate";

import { AccountPage } from "./account-page";

export const metadata: Metadata = { title: "Tài khoản" };

/** Signed-in only, no specific permission (docs/04-api.md: /me and /auth/phone/* are about the caller). */
export default function MePage() {
  return (
    <PermissionGate>
      <AccountPage />
    </PermissionGate>
  );
}
