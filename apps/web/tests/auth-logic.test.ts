import { describe, expect, it, vi } from "vitest";

import { safeNext } from "@/app/(auth)/dang-nhap/login-form";
import { applyProblemToForm, messageFor } from "@/lib/api/errors";
import { formatPhone, normalizePhone, registerSchema, resetSchema } from "@/lib/auth/schemas";
import { can } from "@/lib/auth/use-auth";
import type { Me } from "@/lib/api/client";

vi.mock("next/navigation", () => ({ useRouter: vi.fn(), useSearchParams: vi.fn() }));

describe("applyProblemToForm", () => {
  it("puts validation errors on matching fields", () => {
    const setError = vi.fn();

    applyProblemToForm({ code: "validation.failed", errors: { email: ["Email không hợp lệ"] } }, setError, ["email", "password"]);

    expect(setError).toHaveBeenCalledWith("email", { type: "server", message: "Email không hợp lệ" });
    expect(setError).toHaveBeenCalledTimes(1);
  });

  it("shows other codes as a form-level message in Vietnamese", () => {
    const setError = vi.fn();

    const problem = applyProblemToForm({ code: "auth.email_taken", status: 409 }, setError, ["email"]);

    expect(problem.code).toBe("auth.email_taken");
    expect(setError).toHaveBeenCalledWith("root.server", { type: "server", message: "Email đã được đăng ký" });
  });

  it("falls back to a generic message for unknown bodies", () => {
    const setError = vi.fn();
    applyProblemToForm("Bad Gateway", setError, ["email"]);
    expect(setError).toHaveBeenCalledWith("root.server", { type: "server", message: messageFor("system.error") });
  });
});

describe("phone helpers", () => {
  it.each([
    ["0912 345 678", "+84912345678"],
    ["0912.345.678", "+84912345678"],
    ["+84 912-345-678", "+84912345678"],
    ["0212345678", null],
    ["091234567", null],
  ])("normalizePhone(%s) = %s", (input, expected) => {
    expect(normalizePhone(input)).toBe(expected);
  });

  it("formats E.164 for display", () => {
    expect(formatPhone("+84912345678")).toBe("0912 345 678");
  });
});

describe("schemas", () => {
  it("register rejects short passwords and unknown account types", () => {
    const result = registerSchema.safeParse({ accountType: "admin", fullName: "A", email: "a@b.vn", password: "short" });
    expect(result.success).toBe(false);
    expect(result.error!.issues.map((i) => i.path[0])).toEqual(expect.arrayContaining(["accountType", "password"]));
  });

  it("reset requires both passwords to match", () => {
    const result = resetSchema.safeParse({ newPassword: "Correct-Horse-1", confirm: "Correct-Horse-2" });
    expect(result.success).toBe(false);
    expect(result.error!.issues[0].path).toEqual(["confirm"]);
  });
});

describe("safeNext", () => {
  it.each([
    ["/toi/tin-cua-toi", "/toi/tin-cua-toi"],
    [null, "/toi"],
    ["//evil.test", "/toi"],
    ["https://evil.test", "/toi"],
  ])("safeNext(%s) = %s", (next, expected) => {
    expect(safeNext(next)).toBe(expected);
  });
});

describe("can", () => {
  const me = { permissions: ["scan.create", "listing.create"] } as Me;

  it("checks permissions, not roles", () => {
    expect(can(me, "listing.create")).toBe(true);
    expect(can(me, "listing.moderate")).toBe(false);
    expect(can(undefined, "scan.create")).toBe(false);
  });
});
