import type { Metadata } from "next";
import { Suspense } from "react";

import { ScanForm } from "./scan-form";

export const metadata: Metadata = {
  title: "Kiểm tra tin",
  description: "Dán một tin phòng trọ hoặc việc làm thêm để xem các dấu hiệu rủi ro trước khi chuyển tiền.",
};

/** US-201: works for guests and while the platform has no listings yet. */
export default function ScanPage() {
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-6 px-4 py-8">
      <div className="flex flex-col gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">Kiểm tra một tin</h1>
        <p className="text-muted-foreground">
          Dán nội dung hoặc tải ảnh chụp tin bạn thấy trên mạng. Hệ thống chỉ ra các dấu hiệu rủi ro kèm câu trích
          trong tin, để bạn tự quyết định.
        </p>
      </div>
      <Suspense>
        <ScanForm />
      </Suspense>
    </main>
  );
}
