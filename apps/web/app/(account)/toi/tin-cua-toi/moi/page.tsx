import type { Metadata } from "next";

import { PermissionGate } from "@/components/domain/permission-gate";

import { CreateListing } from "./create-listing";

export const metadata: Metadata = { title: "Đăng tin mới" };

export default function NewListingPage() {
  return (
    <PermissionGate permission="listing.create">
      <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 px-4 py-8">
        <h1 className="text-2xl font-semibold tracking-tight">Đăng tin mới</h1>
        <p className="text-muted-foreground">Tin được lưu ở dạng nháp. Sau đó bạn thêm ảnh rồi gửi duyệt.</p>
        <CreateListing />
      </main>
    </PermissionGate>
  );
}
