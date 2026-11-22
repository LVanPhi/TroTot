import type { Metadata } from "next";

import { PermissionGate } from "@/components/domain/permission-gate";

import { MyScans } from "./my-scans";

export const metadata: Metadata = { title: "Lịch sử kiểm tra" };

/** Signed-in only: the caller's own quick checks from the last 30 days. */
export default function MyScansPage() {
  return (
    <PermissionGate>
      <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 px-4 py-8">
        <h1 className="text-2xl font-semibold tracking-tight">Lịch sử kiểm tra</h1>
        <p className="text-muted-foreground">Các lượt kiểm tra trong 30 ngày gần nhất. Sau 30 ngày, lượt kiểm tra tự xóa.</p>
        <MyScans />
      </main>
    </PermissionGate>
  );
}
