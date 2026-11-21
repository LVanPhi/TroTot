/**
 * Display formats (docs/06-frontend.md, Định dạng). Values are stored as UTC and integer VND;
 * everything shown to users is in Vietnamese and in Asia/Ho_Chi_Minh time.
 */
import { formatDistanceStrict } from "date-fns";
import { vi } from "date-fns/locale";

export const TIME_ZONE = "Asia/Ho_Chi_Minh";

const vndFormatter = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND" });
const decimalFormatter = new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 1 });
const dateTimeParts = new Intl.DateTimeFormat("en-GB", {
  timeZone: TIME_ZONE,
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

/** Normalise Intl's narrow/non-breaking spaces so output is predictable and testable. */
const plainSpaces = (s: string) => s.replace(/[  ]/g, " ");

/** 2500000 → "2.500.000 ₫" */
export function formatVnd(amount: number): string {
  return plainSpaces(vndFormatter.format(amount));
}

/** Short form for cards: 2500000 → "2,5 triệu", 850000 → "850 nghìn", 1200000000 → "1,2 tỷ". */
export function formatVndShort(amount: number): string {
  const abs = Math.abs(amount);
  if (abs >= 1_000_000_000) return `${decimalFormatter.format(amount / 1_000_000_000)} tỷ`;
  if (abs >= 1_000_000) return `${decimalFormatter.format(amount / 1_000_000)} triệu`;
  if (abs >= 1_000) return `${decimalFormatter.format(amount / 1_000)} nghìn`;
  return formatVnd(amount);
}

/** "2026-11-20T03:00:00Z" → "20/11/2026 10:00" (Vietnam time, UTC+7). */
export function formatDateTime(value: string | Date): string {
  const parts = Object.fromEntries(
    dateTimeParts.formatToParts(new Date(value)).map((p) => [p.type, p.value]),
  );
  return `${parts.day}/${parts.month}/${parts.year} ${parts.hour}:${parts.minute}`;
}

/** "3 phút trước" relative to `now` (injectable for tests). */
export function formatRelative(value: string | Date, now: Date = new Date()): string {
  return formatDistanceStrict(new Date(value), now, { addSuffix: true, locale: vi });
}

/** 350 → "350 m", 1234 → "1,2 km". Input in metres. */
export function formatDistance(metres: number): string {
  if (metres < 1000) return `${Math.round(metres)} m`;
  return `${decimalFormatter.format(metres / 1000)} km`;
}

/** 20 → "20 m²", 18.5 → "18,5 m²". */
export function formatArea(squareMetres: number): string {
  return `${decimalFormatter.format(squareMetres)} m²`;
}

export type PayUnit = "hour" | "shift" | "day" | "month";

const payUnitLabels: Record<PayUnit, string> = {
  hour: "giờ",
  shift: "buổi",
  day: "ngày",
  month: "tháng",
};

/** 30000, "hour" → "30.000 ₫/giờ". */
export function formatPay(amount: number, unit: PayUnit): string {
  return `${formatVnd(amount)}/${payUnitLabels[unit]}`;
}
