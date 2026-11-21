import { describe, expect, it } from "vitest";

import {
  formatArea,
  formatDateTime,
  formatDistance,
  formatPay,
  formatRelative,
  formatVnd,
  formatVndShort,
} from "@/lib/format";

describe("formatVnd", () => {
  it.each([
    [2_500_000, "2.500.000 ₫"],
    [0, "0 ₫"],
    [100_000_000, "100.000.000 ₫"],
  ])("%d → %s", (amount, expected) => {
    expect(formatVnd(amount)).toBe(expected);
  });
});

describe("formatVndShort", () => {
  it.each([
    [2_500_000, "2,5 triệu"],
    [3_000_000, "3 triệu"],
    [850_000, "850 nghìn"],
    [1_200_000_000, "1,2 tỷ"],
    [500, "500 ₫"],
  ])("%d → %s", (amount, expected) => {
    expect(formatVndShort(amount)).toBe(expected);
  });
});

describe("formatDateTime", () => {
  it("converts UTC to Asia/Ho_Chi_Minh (UTC+7)", () => {
    expect(formatDateTime("2026-11-20T03:00:00Z")).toBe("20/11/2026 10:00");
  });

  it("rolls over to the next day in Vietnam", () => {
    expect(formatDateTime("2026-12-31T17:30:00Z")).toBe("01/01/2027 00:30");
  });
});

describe("formatRelative", () => {
  const now = new Date("2026-11-20T10:00:00Z");

  it.each([
    ["2026-11-20T09:57:00Z", "3 phút trước"],
    ["2026-11-19T10:00:00Z", "1 ngày trước"],
  ])("%s → %s", (value, expected) => {
    expect(formatRelative(value, now)).toBe(expected);
  });
});

describe("formatDistance", () => {
  it.each([
    [350, "350 m"],
    [999.4, "999 m"],
    [1000, "1 km"],
    [1234, "1,2 km"],
  ])("%d m → %s", (metres, expected) => {
    expect(formatDistance(metres)).toBe(expected);
  });
});

describe("formatArea and formatPay", () => {
  it("formats area with a decimal comma", () => {
    expect(formatArea(20)).toBe("20 m²");
    expect(formatArea(18.5)).toBe("18,5 m²");
  });

  it.each([
    ["hour", "30.000 ₫/giờ"],
    ["shift", "30.000 ₫/buổi"],
    ["day", "30.000 ₫/ngày"],
    ["month", "30.000 ₫/tháng"],
  ] as const)("pay unit %s → %s", (unit, expected) => {
    expect(formatPay(30_000, unit)).toBe(expected);
  });
});
