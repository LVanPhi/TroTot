/**
 * Pure text logic for the scan result page (docs/06-frontend.md, Hiển thị kết quả kiểm tra).
 * Wording is neutral: signals of risk in the content, never a verdict on a listing or a person.
 */
import type { Schemas } from "@/lib/api/client";
import { formatVnd } from "@/lib/format";

export type Assessment = Schemas["GetScanAssessment"];
export type Scan = Schemas["GetScanResponse"];
export type RiskLevel = "low" | "caution" | "high";
export type SignalKey = "text" | "image" | "price" | "community";

export const DISCLAIMER =
  "Kết quả do máy chấm dựa trên dấu hiệu trong nội dung, không phải kết luận về người đăng. " +
  "Luôn xem phòng hoặc gặp nhà tuyển dụng trực tiếp trước khi chuyển tiền.";

export const FAILED_MESSAGE = "Chưa phân tích được tin này. Vui lòng thử lại sau ít phút.";

export const RISK_LABELS: Record<RiskLevel, string> = {
  low: "Thấp",
  caution: "Cần thận trọng",
  high: "Cao",
};

export function toRiskLevel(value: string): RiskLevel {
  return value === "high" || value === "caution" ? value : "low";
}

/** The API serializes numbers as number | string (OpenAPI 3.1); the UI always works with numbers. */
export const num = (value: number | string): number => (typeof value === "number" ? value : Number(value));
export const numOrNull = (value: number | string | null): number | null => (value === null ? null : num(value));

export function isFinal(status: string): boolean {
  return status === "completed" || status === "failed";
}

const REASON_PHRASES: Record<Exclude<SignalKey, "text">, string> = {
  image: "ảnh dùng lại",
  price: "giá thấp bất thường",
  community: "báo cáo cộng đồng",
};

/** Headline under the badge (mục 2), including the "Cao/Cần thận trọng without text signals" case. */
export function headline(assessment: Assessment): string {
  const flags = assessment.flags.length;
  if (flags > 0) return `Tin này có ${flags} dấu hiệu rủi ro`;
  const level = toRiskLevel(assessment.riskLevel);
  if (level === "low") return "Chưa thấy dấu hiệu rủi ro rõ ràng";

  const reasons = (Object.keys(REASON_PHRASES) as (keyof typeof REASON_PHRASES)[])
    .filter((key) => (numOrNull(assessment.signals[key]) ?? 0) >= 0.5)
    .map((key) => REASON_PHRASES[key]);
  const because = reasons.length > 0 ? ` do ${joinVi(reasons)}` : "";
  return `Tin này có điểm rủi ro ${RISK_LABELS[level].toLowerCase()}${because} — xem chi tiết bên dưới`;
}

function joinVi(items: string[]): string {
  if (items.length <= 1) return items.join("");
  return `${items.slice(0, -1).join(", ")} và ${items.at(-1)}`;
}

export const SIGNAL_LABELS: Record<SignalKey, string> = {
  text: "Nội dung chữ",
  image: "Hình ảnh",
  price: "Giá thuê",
  community: "Báo cáo cộng đồng",
};

/** Why a signal is null ("Không áp dụng" + reason, mục 5). */
export const SIGNAL_NULL_REASONS: Record<SignalKey, string> = {
  text: "không có nội dung chữ để phân tích",
  image: "không có ảnh",
  price: "không có giá hoặc chưa đủ 10 phòng cùng khu vực để so",
  community: "không có số điện thoại, số tài khoản hay đường link để đối chiếu",
};

export interface SignalRow {
  key: SignalKey;
  label: string;
  percent: number | null;
  note: string | null;
}

export function signalRows(assessment: Assessment): SignalRow[] {
  return (Object.keys(SIGNAL_LABELS) as SignalKey[]).map((key) => {
    const value = numOrNull(assessment.signals[key]);
    return {
      key,
      label: SIGNAL_LABELS[key],
      percent: value === null ? null : Math.round(value * 100),
      note: value === null ? `Không áp dụng: ${SIGNAL_NULL_REASONS[key]}` : null,
    };
  });
}

export type ImageMatch = Schemas["GetScanImageMatch"];

/** "Ảnh số 1 xuất hiện ở 3 tin khác, 2 tin đã bị gỡ" (mục 6). */
export function imageMatchSentence(match: ImageMatch): string {
  const others = num(match.otherListingCount);
  const removed = num(match.removedListingCount);
  let sentence = `Ảnh số ${num(match.sourceImageNo)} xuất hiện ở ${others} tin khác`;
  if (removed > 0) sentence += `, ${removed} tin đã bị gỡ`;
  if (match.differentLocation) sentence += ", ở các khu vực khác nhau";
  return sentence;
}

export type Price = Schemas["GetScanPrice"];

/** "Giá nêu 1.000.000 ₫ thấp hơn 62% so với trung vị 2.600.000 ₫ của 14 phòng cùng khu vực" (mục 7). */
export function priceSentence(price: Price): string {
  const rent = num(price.rentVnd);
  const median = num(price.medianVnd);
  const subject = price.rentSource === "extracted" ? "Giá đọc được trong nội dung" : "Giá nêu";
  const tail = `trung vị ${formatVnd(median)} của ${num(price.sampleSize)} phòng cùng khu vực`;
  if (median <= 0 || rent === median) return `${subject} ${formatVnd(rent)} ngang ${tail}`;
  const percent = Math.round((Math.abs(median - rent) / median) * 100);
  const direction = rent < median ? "thấp hơn" : "cao hơn";
  return `${subject} ${formatVnd(rent)} ${direction} ${percent}% so với ${tail}`;
}

export type CommunityItem = Schemas["GetScanCommunityItem"];

const IDENTIFIER_KINDS: Record<string, string> = {
  phone: "Số điện thoại",
  bank_account: "Số tài khoản",
  url: "Đường link",
};

/** "Số điện thoại •••321 đã có 3 báo cáo được cán bộ xác nhận" (mục 8). */
export function communitySentence(item: CommunityItem): string {
  const kind = IDENTIFIER_KINDS[item.kind] ?? "Thông tin liên hệ";
  return `${kind} ${item.hint} đã có ${num(item.confirmedReports)} báo cáo được cán bộ xác nhận`;
}

/** Report categories with neutral labels (docs/06-frontend.md, US-406). */
export const REPORT_CATEGORIES = [
  { value: "scam", label: "Có dấu hiệu gian lận, đòi tiền" },
  { value: "misleading", label: "Thông tin sai sự thật" },
  { value: "inappropriate", label: "Nội dung không phù hợp" },
  { value: "other", label: "Lý do khác" },
] as const;

export type ReportCategory = (typeof REPORT_CATEGORIES)[number]["value"];
