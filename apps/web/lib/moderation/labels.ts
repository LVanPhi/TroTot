/** Neutral labels for moderation lists (never "lừa đảo" attached to a listing or a person). */
import { REPORT_CATEGORIES } from "@/lib/scans/view";

export const REPORT_CATEGORY_LABELS: Record<string, string> = Object.fromEntries(REPORT_CATEGORIES.map((c) => [c.value, c.label]));

export const REPORT_STATUS_LABELS: Record<string, string> = { open: "Đang mở", confirmed: "Đã xác nhận", dismissed: "Đã bác bỏ" };

export const APPEAL_STATUS_LABELS: Record<string, string> = { open: "Đang chờ", accepted: "Chấp nhận", rejected: "Bác" };

export const AUDIT_KIND_LABELS: Record<string, string> = {
  listing_verified: "Duyệt tin",
  listing_rejected: "Từ chối tin",
  listing_hidden: "Ẩn tin",
  listing_removed: "Gỡ tin",
  listing_pending_review: "Đưa vào hàng đợi",
  listing_draft: "Đưa về nháp",
  document_opened: "Mở giấy tờ",
  document_accepted: "Chấp nhận giấy tờ",
  document_rejected: "Từ chối giấy tờ",
  report_confirmed: "Xác nhận báo cáo",
  report_dismissed: "Bác bỏ báo cáo",
  appeal_accepted: "Chấp nhận kháng nghị",
  appeal_rejected: "Bác kháng nghị",
  review_hidden: "Ẩn đánh giá",
};
