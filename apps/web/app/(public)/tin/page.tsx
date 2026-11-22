import type { Metadata } from "next";
import { Suspense } from "react";

import { SearchPage } from "./search-page";

export const metadata: Metadata = {
  title: "Tìm tin",
  description: "Phòng trọ và việc làm thêm đã được xác minh, tìm bằng câu tự nhiên hoặc bộ lọc, xem trên bản đồ.",
};

export default function ListingsPage() {
  return (
    <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col gap-4 px-4 py-6">
      <h1 className="text-2xl font-semibold tracking-tight">Tìm phòng trọ, việc làm thêm</h1>
      <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
        <SearchPage />
      </Suspense>
    </main>
  );
}
