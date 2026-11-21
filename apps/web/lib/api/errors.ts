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
