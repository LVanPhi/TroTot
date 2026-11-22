import type { Metadata } from "next";
import { connection } from "next/server";
import { Suspense } from "react";

import { serverApi } from "@/lib/api/server";

export const metadata: Metadata = {
  title: "Cách chấm điểm",
  description: "Hệ thống Kiểm tra tin chấm điểm rủi ro dựa trên bốn tín hiệu và một bộ tiêu chí công khai.",
};

const SIGNALS = [
  ["Nội dung chữ", "Đối chiếu nội dung (kể cả chữ đọc được trong ảnh chụp) với các tiêu chí bên dưới; mỗi dấu hiệu luôn kèm câu trích nguyên văn."],
  ["Hình ảnh", "So ảnh với ảnh của các tin trên nền tảng: ảnh dùng lại từ tin đã bị gỡ hoặc của người đăng khác là dấu hiệu đáng chú ý."],
  ["Giá thuê", "So giá với trung vị của ít nhất 10 phòng cùng khu vực; giá thấp bất thường làm tăng điểm. Chưa đủ mẫu thì không áp dụng."],
  ["Báo cáo cộng đồng", "Số điện thoại, số tài khoản, đường link từng bị báo cáo và được cán bộ xác nhận. Hệ thống chỉ lưu dạng mã hóa một chiều."],
] as const;

async function Criteria() {
  await connection(); // request-time data, never prerendered
  const { data } = await serverApi.GET("/api/v1/meta/criteria");
  if (!data || data.length === 0) return <p>Chưa tải được danh sách tiêu chí.</p>;
  return (
    <ul className="flex flex-col gap-3">
      {data.map((c) => (
        <li key={c.code} className="rounded-lg border border-border p-3">
          <p className="font-medium">{c.name}</p>
          <p className="text-sm">{c.guidance}</p>
          {c.legalBasis ? <p className="mt-1 text-sm text-muted-foreground">Căn cứ: {c.legalBasis}</p> : null}
        </li>
      ))}
    </ul>
  );
}

/** Transparency page (docs/06-frontend.md, /cach-cham-diem). */
export default function ScoringPage() {
  return (
    <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col gap-6 px-4 py-8">
      <h1 className="text-2xl font-semibold tracking-tight">Cách chấm điểm</h1>
      <p>
        Điểm 0–100 cho biết mức độ một tin chứa dấu hiệu rủi ro, chia ba mức: <strong>Thấp</strong> (dưới 40),{" "}
        <strong>Cần thận trọng</strong> (40–69) và <strong>Cao</strong> (từ 70). Đây là kết quả do máy chấm dựa trên dấu hiệu
        trong nội dung, không phải kết luận về người đăng.
      </p>
      <section className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">Bốn tín hiệu</h2>
        <dl className="flex flex-col gap-3">
          {SIGNALS.map(([name, text]) => (
            <div key={name}>
              <dt className="font-medium">{name}</dt>
              <dd className="text-sm">{text}</dd>
            </div>
          ))}
        </dl>
      </section>
      <section className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">Tiêu chí đang dùng</h2>
        <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
          <Criteria />
        </Suspense>
      </section>
    </main>
  );
}
