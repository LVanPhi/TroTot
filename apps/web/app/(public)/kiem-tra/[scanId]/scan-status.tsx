"use client";

import Link from "next/link";

import { secondaryButton } from "@/components/domain/form-controls";
import { ScanPending, ScanResult } from "@/components/domain/scan-result";
import { messageFor } from "@/lib/api/errors";
import { ScanLoadError, useScan } from "@/lib/scans/use-scan";

export function ScanStatus({ scanId }: { scanId: string }) {
  const { data, error } = useScan(scanId);
  if (error) {
    const notFound = error instanceof ScanLoadError && error.problem.status === 404;
    return (
      <div className="flex flex-col gap-4">
        <p role="alert">{notFound ? "Không tìm thấy lượt kiểm tra này, hoặc kết quả đã hết hạn sau 30 ngày." : messageFor("system.retry_later")}</p>
        <div>
          <Link href="/kiem-tra" className={secondaryButton}>
            Kiểm tra tin khác
          </Link>
        </div>
      </div>
    );
  }
  if (!data) return <ScanPending />;
  return <ScanResult scan={data} />;
}
