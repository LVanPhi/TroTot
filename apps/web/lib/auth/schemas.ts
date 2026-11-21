import { z } from "zod";

/** Zod schemas mirroring the API validators (Features/Auth, Features/Me). */

const email = z.string().trim().min(1, "Nhập email").max(254).email("Email không hợp lệ");
const password = z.string().min(10, "Mật khẩu cần ít nhất 10 ký tự").max(256);
const code = z.string().trim().regex(/^[0-9]{6}$/, "Mã gồm 6 chữ số");

export const registerSchema = z.object({
  accountType: z.enum(["student", "owner"]),
  fullName: z.string().trim().min(1, "Nhập họ tên").max(100, "Họ tên tối đa 100 ký tự"),
  email,
  password,
});
export type RegisterValues = z.infer<typeof registerSchema>;

export const loginSchema = z.object({ email, password: z.string().min(1, "Nhập mật khẩu").max(256) });
export type LoginValues = z.infer<typeof loginSchema>;

export const verifyEmailSchema = z.object({ email, code });
export type VerifyEmailValues = z.infer<typeof verifyEmailSchema>;

export const forgotSchema = z.object({ email });
export type ForgotValues = z.infer<typeof forgotSchema>;

export const resetSchema = z
  .object({ newPassword: password, confirm: z.string() })
  .refine((v) => v.newPassword === v.confirm, { message: "Mật khẩu nhập lại không khớp", path: ["confirm"] });
export type ResetValues = z.infer<typeof resetSchema>;

/** "0912 345 678", "0912.345.678", "+84 912-345-678" → "+84912345678"; null when not a Vietnamese mobile. */
export function normalizePhone(input: string): string | null {
  let digits = input.trim().replace(/[\s.\-()]/g, "");
  if (digits.startsWith("+")) digits = digits.slice(1);
  else if (digits.startsWith("0")) digits = "84" + digits.slice(1);
  return /^84[35789][0-9]{8}$/.test(digits) ? `+${digits}` : null;
}

/** "+84912345678" → "0912 345 678" for display. */
export function formatPhone(e164: string): string {
  const local = "0" + e164.slice(3);
  return `${local.slice(0, 4)} ${local.slice(4, 7)} ${local.slice(7)}`;
}

export const phoneSchema = z.object({
  phone: z.string().refine((p) => normalizePhone(p) !== null, "Số điện thoại di động Việt Nam không hợp lệ"),
});
export type PhoneValues = z.infer<typeof phoneSchema>;

export const otpSchema = z.object({ code });
export type OtpValues = z.infer<typeof otpSchema>;

export const fullNameSchema = z.object({
  fullName: z.string().trim().min(1, "Nhập họ tên").max(100, "Họ tên tối đa 100 ký tự"),
});
export type FullNameValues = z.infer<typeof fullNameSchema>;

export const deleteAccountSchema = z.object({ password: z.string().min(1, "Nhập mật khẩu để xác nhận") });
export type DeleteAccountValues = z.infer<typeof deleteAccountSchema>;
