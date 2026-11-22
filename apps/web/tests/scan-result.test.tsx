// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it } from "vitest";

import { ScanResult } from "@/components/domain/scan-result";
import { RiskBadge } from "@/components/domain/scan-result-parts";
import { DISCLAIMER, FAILED_MESSAGE, type Scan } from "@/lib/scans/view";

afterEach(cleanup);

function wrap(node: ReactNode) {
  return render(<QueryClientProvider client={new QueryClient()}>{node}</QueryClientProvider>);
}

const base: Scan = {
  scanId: "4f2c7a9e-1111-4c1a-9e2b-000000000001",
  status: "completed",
  createdAt: "2026-11-20T03:00:00Z",
  completedAt: "2026-11-20T03:00:06Z",
  errorCode: null,
  canReport: false,
  assessment: {
    score: 98,
    riskLevel: "high",
    signals: { text: 0.9, image: 1, price: 0.95, community: 0.78 },
    flags: [
      { code: "deposit_before_viewing", name: "Đòi đặt cọc trước khi xem phòng", evidence: "chuyển cọc trước 1 triệu", legalBasis: null },
    ],
    imageMatches: [{ sourceImageNo: 1, otherListingCount: 3, removedListingCount: 2, differentLocation: true, publicListing: null }],
    price: { rentVnd: 1_000_000, rentSource: "claimed", medianVnd: 2_600_000, sampleSize: 14 },
    community: [{ kind: "phone", hint: "•••321", confirmedReports: 3, lastConfirmedAt: null }],
  },
};

describe("RiskBadge", () => {
  it.each([
    ["low", "Mức rủi ro: Thấp"],
    ["caution", "Mức rủi ro: Cần thận trọng"],
    ["high", "Mức rủi ro: Cao"],
  ])("%s has text, not only colour", (level, text) => {
    const { container } = render(<RiskBadge level={level} />);
    expect(screen.getByText(text)).toBeTruthy();
    expect(container.querySelector("svg")).not.toBeNull();
  });
});

describe("ScanResult", () => {
  it("shows every section in order with the disclaimer and neutral wording", () => {
    wrap(<ScanResult scan={base} />);

    expect(screen.getByText("Tin này có 1 dấu hiệu rủi ro")).toBeTruthy();
    expect(screen.getByText("98")).toBeTruthy();
    expect(screen.getByRole("note").textContent).toBe(DISCLAIMER);
    expect(screen.getByText("“chuyển cọc trước 1 triệu”")).toBeTruthy();
    expect(screen.getByText(/Chỉ đặt cọc sau khi đã xem phòng/)).toBeTruthy();
    expect(screen.getByText(/Ảnh số 1 xuất hiện ở 3 tin khác, 2 tin đã bị gỡ/)).toBeTruthy();
    expect(screen.getByText(/thấp hơn 62% so với trung vị/)).toBeTruthy();
    expect(screen.getByText(/•••321 đã có 3 báo cáo/)).toBeTruthy();
    expect(document.body.textContent?.toLowerCase()).not.toContain("lừa đảo");
    expect(screen.queryByRole("button", { name: /Báo cáo/ })).toBeNull();
  });

  it("high level without text signals still shows a headline and the disclaimer", () => {
    const scan = { ...base, assessment: { ...base.assessment!, flags: [] } };
    wrap(<ScanResult scan={scan} />);

    expect(screen.getByText(/Tin này có điểm rủi ro cao do ảnh dùng lại, giá thấp bất thường và báo cáo cộng đồng/)).toBeTruthy();
    expect(screen.getByRole("note")).toBeTruthy();
  });

  it("null signals read 'Không áp dụng' with a reason", () => {
    const scan = {
      ...base,
      assessment: { ...base.assessment!, signals: { text: 0.2, image: null, price: null, community: null }, imageMatches: [], price: null, community: [] },
    };
    wrap(<ScanResult scan={scan} />);

    expect(screen.getByText("Không áp dụng: không có ảnh")).toBeTruthy();
  });

  it("shows the report button when the API allows it", () => {
    wrap(<ScanResult scan={{ ...base, canReport: true }} />);
    expect(screen.getByRole("button", { name: /Báo cáo/ })).toBeTruthy();
  });

  it("failed scan shows the retry message and the support code", () => {
    wrap(<ScanResult scan={{ ...base, status: "failed", assessment: null, errorCode: "worker.timeout" }} />);

    expect(screen.getByRole("alert").textContent).toBe(FAILED_MESSAGE);
    expect(screen.getByText("Mã hỗ trợ: worker.timeout")).toBeTruthy();
  });

  it("pending scan shows the progress text", () => {
    wrap(<ScanResult scan={{ ...base, status: "processing", assessment: null, completedAt: null }} />);
    expect(screen.getByText("Đang phân tích nội dung và hình ảnh…")).toBeTruthy();
  });
});
