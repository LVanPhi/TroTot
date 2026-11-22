import type { FieldValues, Path, UseFormSetError } from "react-hook-form";

import { toProblem, type Problem } from "./problem";

/** Vietnamese text per API error code (docs/04-api.md, Lỗi). The client shows text by code, not the API title. */
const messages: Record<string, string> = {
  "validation.failed": "Dữ liệu chưa hợp lệ",
  "auth.unauthenticated": "Vui lòng đăng nhập",
  "auth.forbidden": "Bạn không có quyền thực hiện thao tác này",
  "auth.invalid_credentials": "Email hoặc mật khẩu không đúng",
  "auth.locked_temporarily": "Đăng nhập sai nhiều lần. Thử lại sau 15 phút",
  "auth.account_locked": "Tài khoản đã bị khóa",
  "auth.email_not_verified": "Vui lòng xác thực email trước khi đăng nhập",
  "auth.email_taken": "Email đã được đăng ký",
  "auth.student_email_domain": "Sinh viên cần dùng email do trường cấp",
  "auth.weak_password": "Mật khẩu cần ít nhất 10 ký tự và không quá phổ biến",
  "auth.code_invalid": "Mã không đúng hoặc đã hết hạn",
  "auth.code_attempts_exceeded": "Nhập sai quá số lần. Hãy xin mã mới",
  "auth.phone_taken": "Số điện thoại đã được dùng cho tài khoản khác",
  "auth.refresh_invalid": "Phiên đăng nhập đã hết",
  "auth.refresh_reused": "Phiên bị thu hồi vì lý do an toàn. Vui lòng đăng nhập lại",
  "phone.sms_unavailable": "Hệ thống chưa gửi được SMS; liên hệ quản trị viên để được xác thực số",
  "scan.input_required": "Dán nội dung hoặc tải ít nhất một ảnh",
  "scan.quota_exceeded": "Bạn đã dùng hết lượt kiểm tra hôm nay",
  "scan.not_found": "Không tìm thấy lượt kiểm tra",
  "image.unsupported_type": "Chỉ nhận ảnh JPEG, PNG, WebP",
  "image.too_large": "Mỗi ảnh tối đa 5 MB",
  "image.too_many": "Tối đa 5 ảnh",
  "report.already_open": "Bạn đã báo cáo mục này",
  "image.duplicate": "Ảnh này đã có trong tin",
  "listing.not_found": "Không tìm thấy tin",
  "listing.not_owner": "Bạn không phải người đăng tin này",
  "listing.version_conflict": "Tin vừa được cập nhật ở nơi khác",
  "listing.invalid_transition": "Không thể thực hiện ở trạng thái hiện tại",
  "listing.not_editable": "Chuyển tin về nháp trước khi sửa",
  "listing.poster_not_verified": "Hoàn tất xác minh để gửi duyệt",
  "listing.images_required": "Tin phòng cần ít nhất một ảnh",
  "listing.renew_too_early": "Chỉ gia hạn khi tin còn 7 ngày hoặc đã hết hạn",
  "appeal.pending": "Tin đang chờ xử lý kháng nghị",
  "appeal.already_open": "Đã có kháng nghị đang chờ xử lý",
  "appeal.already_used": "Mỗi quyết định chỉ kháng nghị một lần",
  "appeal.window_closed": "Đã quá 7 ngày, không còn kháng nghị được",
  "appeal.target_invalid": "Không có quyết định nào để kháng nghị",
  "appeal.decision_superseded": "Quyết định này không còn hiệu lực",
  "owner.profile_required": "Tạo hồ sơ chủ trước",
  "document.consent_required": "Cần đồng ý xử lý dữ liệu cá nhân",
  "document.duplicate": "Tệp này đã được tải lên",
  "document.unsupported_type": "Chỉ nhận ảnh JPEG, PNG, WebP hoặc PDF",
  "document.too_large": "Tệp tối đa 10 MB",
  "review.already_exists": "Bạn đã đánh giá tin này",
  "review.not_student": "Chỉ sinh viên được đánh giá",
  "review.own_listing": "Không thể đánh giá tin của chính mình",
  rate_limited: "Thao tác quá nhanh, vui lòng thử lại sau",
  "system.retry_later": "Hệ thống đang bận, thử lại sau giây lát",
};

export function messageFor(code: string): string {
  return messages[code] ?? "Có lỗi xảy ra, vui lòng thử lại";
}

/**
 * Shows an API error on a react-hook-form form: validation.failed errors go to their fields (camelCase keys match
 * field names); anything else becomes the form-level "root" error. Returns the problem for further handling.
 */
export function applyProblemToForm<T extends FieldValues>(
  body: unknown,
  setError: UseFormSetError<T>,
  fields: readonly Path<T>[],
): Problem {
  const problem = toProblem(body);
  if (problem.code === "validation.failed" && problem.errors) {
    let placed = false;
    for (const [field, texts] of Object.entries(problem.errors)) {
      if ((fields as readonly string[]).includes(field)) {
        setError(field as Path<T>, { type: "server", message: texts[0] });
        placed = true;
      }
    }
    if (placed) return problem;
  }
  setError("root.server" as Path<T>, { type: "server", message: messageFor(problem.code) });
  return problem;
}
