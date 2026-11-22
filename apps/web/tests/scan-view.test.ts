import { describe, expect, it } from "vitest";

import { GENERAL_TIP, tipFor } from "@/lib/criteria-tips";
import { parseVndInput } from "@/lib/format";
import { buildScanForm, imageProblem, scanSchema, sharedText } from "@/lib/scans/form";
import { POLL_MS, pollInterval } from "@/lib/scans/use-scan";
import {
  communitySentence,
  headline,
  imageMatchSentence,
  priceSentence,
  REPORT_CATEGORIES,
  signalRows,
  type Assessment,
  type Scan,
} from "@/lib/scans/view";

function assessment(overrides: Partial<Assessment> = {}): Assessment {
  return {
    score: 20,
    riskLevel: "low",
    signals: { text: 0.1, image: null, price: null, community: null },
    flags: [],
    imageMatches: [],
    price: null,
    community: [],
    ...overrides,
  };
}

const flag = { code: "deposit_before_viewing", name: "Đòi cọc trước khi xem", evidence: "chuyển cọc trước", legalBasis: null };

describe("headline", () => {
  it("counts text signals", () => {
    expect(headline(assessment({ riskLevel: "high", flags: [flag, { ...flag, code: "pressure" }] }))).toBe(
      "Tin này có 2 dấu hiệu rủi ro",
    );
  });

  it("explains a high score without text signals by the other signals", () => {
    const a = assessment({ riskLevel: "high", score: 80, signals: { text: 0, image: 1, price: 0.9, community: null } });
    expect(headline(a)).toBe("Tin này có điểm rủi ro cao do ảnh dùng lại và giá thấp bất thường — xem chi tiết bên dưới");
  });

  it("caution from community reports only", () => {
    const a = assessment({ riskLevel: "caution", signals: { text: null, image: null, price: null, community: "0.78" } });
    expect(headline(a)).toBe("Tin này có điểm rủi ro cần thận trọng do báo cáo cộng đồng — xem chi tiết bên dưới");
  });

  it("low without signals", () => {
    expect(headline(assessment())).toBe("Chưa thấy dấu hiệu rủi ro rõ ràng");
  });
});

describe("signals and evidence wording", () => {
  it("null signal shows 'Không áp dụng' with a reason; numbers become percent", () => {
    const rows = signalRows(assessment({ signals: { text: 0.765, image: null, price: null, community: null } }));
    expect(rows[0]).toMatchObject({ key: "text", percent: 77, note: null });
    expect(rows[1]).toMatchObject({ key: "image", percent: null, note: "Không áp dụng: không có ảnh" });
  });

  it("image match sentence", () => {
    expect(
      imageMatchSentence({ sourceImageNo: 1, otherListingCount: 3, removedListingCount: 2, differentLocation: false, publicListing: null }),
    ).toBe("Ảnh số 1 xuất hiện ở 3 tin khác, 2 tin đã bị gỡ");
  });

  it("price sentence for claimed and extracted price", () => {
    expect(priceSentence({ rentVnd: 1_000_000, rentSource: "claimed", medianVnd: 2_600_000, sampleSize: 14 })).toBe(
      "Giá nêu 1.000.000 ₫ thấp hơn 62% so với trung vị 2.600.000 ₫ của 14 phòng cùng khu vực",
    );
    expect(priceSentence({ rentVnd: "1000000", rentSource: "extracted", medianVnd: 2_600_000, sampleSize: 14 })).toMatch(
      /^Giá đọc được trong nội dung 1\.000\.000 ₫ thấp hơn/,
    );
  });

  it("community sentence", () => {
    expect(communitySentence({ kind: "phone", hint: "•••321", confirmedReports: 3, lastConfirmedAt: null })).toBe(
      "Số điện thoại •••321 đã có 3 báo cáo được cán bộ xác nhận",
    );
  });

  it("tips: known criterion and general fallback for criteria added later", () => {
    expect(tipFor("deposit_before_viewing")).toBe("Chỉ đặt cọc sau khi đã xem phòng và có giấy biên nhận ghi rõ thông tin hai bên.");
    expect(tipFor("criterion_added_by_admin")).toBe(GENERAL_TIP);
  });

  it("report categories never use the word 'lừa đảo'", () => {
    expect(REPORT_CATEGORIES.map((c) => c.label).join(" ").toLowerCase()).not.toContain("lừa đảo");
  });
});

describe("polling", () => {
  const scan = (status: string) => ({ status }) as Scan;

  it("polls every 3 s only while the hub is down and the scan is not final", () => {
    expect(pollInterval(undefined, false)).toBe(POLL_MS);
    expect(pollInterval(scan("processing"), false)).toBe(POLL_MS);
    expect(pollInterval(scan("processing"), true)).toBe(false);
    expect(pollInterval(scan("completed"), false)).toBe(false);
    expect(pollInterval(scan("failed"), false)).toBe(false);
  });
});

describe("scan form", () => {
  it.each([
    ["2.500.000", 2_500_000],
    ["2500000", 2_500_000],
    ["2tr5", 2_500_000],
    ["2,5 triệu", 2_500_000],
    ["800k", 800_000],
    ["1tr", 1_000_000],
    ["", null],
    ["hai triệu", null],
  ])("parses money %s", (input, expected) => {
    expect(parseVndInput(input)).toBe(expected);
  });

  it("needs text or an image, and a rent in range", () => {
    expect(scanSchema.safeParse({ text: " ", rent: "", wardCode: "", imageCount: 0 }).success).toBe(false);
    expect(scanSchema.safeParse({ text: "", rent: "", wardCode: "", imageCount: 1 }).success).toBe(true);
    expect(scanSchema.safeParse({ text: "phòng", rent: "50k", wardCode: "", imageCount: 0 }).success).toBe(false);
  });

  it("checks image type and size before sending", () => {
    expect(imageProblem({ type: "image/gif", size: 10 })).toBe("Chỉ nhận ảnh JPEG, PNG, WebP");
    expect(imageProblem({ type: "image/png", size: 6 * 1024 * 1024 })).toBe("Mỗi ảnh tối đa 5 MB");
    expect(imageProblem({ type: "image/webp", size: 10 })).toBeNull();
  });

  it("builds the multipart body with API field names", () => {
    const image = new File([new Uint8Array([1])], "a.png", { type: "image/png" });
    const form = buildScanForm({ text: "Phòng đẹp", rent: "2tr5", wardCode: "00004", imageCount: 1 }, [image]);
    expect(form.get("text")).toBe("Phòng đẹp");
    expect(form.get("claimedRentVnd")).toBe("2500000");
    expect(form.get("claimedWardCode")).toBe("00004");
    expect(form.getAll("images")).toHaveLength(1);
  });

  it("shared title, text and url become plain text (url is not fetched)", () => {
    expect(sharedText({ title: "Phòng trọ", text: "Phòng trọ", url: "https://example.com/p/1" })).toBe(
      "Phòng trọ\nhttps://example.com/p/1",
    );
  });
});
