import type { Metadata } from "next";

import { PermissionGate } from "@/components/domain/permission-gate";

import { OwnerProfile } from "./owner-profile";

export const metadata: Metadata = { title: "Hồ sơ chủ" };

export default function OwnerProfilePage() {
  return (
    <PermissionGate permission="listing.create">
      <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 px-4 py-8">
        <h1 className="text-2xl font-semibold tracking-tight">Hồ sơ chủ</h1>
        <p className="text-muted-foreground">
          Sinh viên dùng email trường đã xác thực không cần hồ sơ này. Chủ trọ và nhà tuyển dụng cần hồ sơ được xác minh để
          gửi tin đi duyệt.
        </p>
        <OwnerProfile />
      </main>
    </PermissionGate>
  );
}
