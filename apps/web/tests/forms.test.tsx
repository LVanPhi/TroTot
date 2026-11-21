// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const router = { push: vi.fn(), replace: vi.fn() };
let searchParams = new URLSearchParams();
vi.mock("next/navigation", () => ({
  useRouter: () => router,
  useSearchParams: () => searchParams,
  usePathname: () => "/toi",
}));

const api = { GET: vi.fn(), POST: vi.fn(), PATCH: vi.fn(), DELETE: vi.fn() };
vi.mock("@/lib/api/client", () => ({ api, csrfHeaders: {}, refreshSession: vi.fn() }));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), info: vi.fn() } }));

const { RegisterForm } = await import("@/app/(auth)/dang-ky/register-form");
const { LoginForm } = await import("@/app/(auth)/dang-nhap/login-form");
const { PhoneSection } = await import("@/app/(account)/toi/phone-section");
const { PostingChecklist } = await import("@/app/(account)/toi/posting-checklist");
const { session } = await import("@/lib/auth/session");

function wrap(ui: ReactNode) {
  return render(<QueryClientProvider client={new QueryClient()}>{ui}</QueryClientProvider>);
}

const me = {
  email: "a@sv.test.edu.vn",
  fullName: "Nguyễn Văn A",
  phone: null,
  emailVerified: true,
  phoneVerified: false,
  roles: ["student"],
  permissions: ["scan.create"],
  posting: { allowed: false, missing: ["phone_verification"] },
};

beforeEach(() => {
  searchParams = new URLSearchParams();
  session.reset();
});
afterEach(() => cleanup());

describe("RegisterForm", () => {
  async function fillAndSubmit() {
    const user = userEvent.setup();
    await user.type(screen.getByLabelText("Họ và tên"), "Nguyễn Văn A");
    await user.type(screen.getByLabelText("Email"), "a@gmail.test");
    await user.type(screen.getByLabelText("Mật khẩu"), "Correct-Horse-42");
    await user.click(screen.getByRole("button", { name: "Đăng ký" }));
  }

  it("shows client-side errors under each field before calling the API", async () => {
    wrap(<RegisterForm />);

    await userEvent.setup().click(screen.getByRole("button", { name: "Đăng ký" }));

    expect(await screen.findByText("Nhập họ tên")).toBeTruthy();
    expect(screen.getByText("Mật khẩu cần ít nhất 10 ký tự")).toBeTruthy();
    expect(api.POST).not.toHaveBeenCalled();
  });

  it("puts API validation errors on the matching field", async () => {
    api.POST.mockResolvedValue({ error: { code: "validation.failed", errors: { email: ["Email không hợp lệ (máy chủ)"] } } });
    wrap(<RegisterForm />);

    await fillAndSubmit();

    const email = screen.getByLabelText("Email");
    expect(await screen.findByText("Email không hợp lệ (máy chủ)")).toBeTruthy();
    expect(email.getAttribute("aria-invalid")).toBe("true");
  });

  it("shows business errors by code, in Vietnamese", async () => {
    api.POST.mockResolvedValue({ error: { code: "auth.student_email_domain", status: 422 } });
    wrap(<RegisterForm />);

    await fillAndSubmit();

    expect(await screen.findByText("Sinh viên cần dùng email do trường cấp")).toBeTruthy();
  });

  it("goes to email verification with the email after success", async () => {
    api.POST.mockResolvedValue({ data: undefined, error: undefined });
    wrap(<RegisterForm />);

    await fillAndSubmit();

    await vi.waitFor(() => expect(router.push).toHaveBeenCalledWith("/xac-thuc-email?email=a%40gmail.test"));
  });
});

describe("LoginForm", () => {
  it("signs in, keeps the token in memory and follows a safe ?next", async () => {
    searchParams = new URLSearchParams({ next: "/toi/tin-cua-toi" });
    api.POST.mockResolvedValue({ data: { accessToken: "jwt", expiresIn: 900, user: me } });
    wrap(<LoginForm />);
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("Email"), me.email);
    await user.type(screen.getByLabelText("Mật khẩu"), "Correct-Horse-42");
    await user.click(screen.getByRole("button", { name: "Đăng nhập" }));

    await vi.waitFor(() => expect(router.replace).toHaveBeenCalledWith("/toi/tin-cua-toi"));
    expect(session.get()).toEqual({ status: "signed_in", accessToken: "jwt" });
  });

  it("offers the verification page when the email is not verified", async () => {
    api.POST.mockResolvedValue({ error: { code: "auth.email_not_verified", status: 403 } });
    wrap(<LoginForm />);
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("Email"), me.email);
    await user.type(screen.getByLabelText("Mật khẩu"), "x");
    await user.click(screen.getByRole("button", { name: "Đăng nhập" }));

    const link = await screen.findByRole("link", { name: "Nhập mã xác thực email" });
    expect(link.getAttribute("href")).toBe(`/xac-thuc-email?email=${encodeURIComponent(me.email)}`);
  });
});

describe("/toi sections", () => {
  it("PostingChecklist lists what is missing, with text", () => {
    wrap(<PostingChecklist posting={{ allowed: false, missing: ["phone_verification", "owner_verification"] }} />);

    expect(screen.getByText("Xác thực số điện thoại")).toBeTruthy();
    expect(screen.getByText("Hồ sơ chủ trọ / nhà tuyển dụng được cán bộ xác minh")).toBeTruthy();
  });

  it("PostingChecklist says when the user may post", () => {
    wrap(<PostingChecklist posting={{ allowed: true, missing: [] }} />);

    expect(screen.getByText("Bạn đã đủ điều kiện đăng tin.")).toBeTruthy();
  });

  it("PhoneSection sends the normalised number, then verifies the code", async () => {
    api.POST.mockResolvedValue({ error: undefined });
    wrap(<PhoneSection me={me} />);
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("Số điện thoại di động"), "0912 345 678");
    await user.click(screen.getByRole("button", { name: "Gửi mã xác thực" }));
    await user.type(await screen.findByLabelText("Mã xác thực"), "123456");
    await user.click(screen.getByRole("button", { name: "Xác thực số" }));

    await vi.waitFor(() =>
      expect(api.POST).toHaveBeenCalledWith("/api/v1/auth/phone/verify", { body: { phone: "+84912345678", code: "123456" } }),
    );
    expect(api.POST).toHaveBeenCalledWith("/api/v1/auth/phone/otp", { body: { phone: "+84912345678" } });
  });

  it("PhoneSection explains manual verification when SMS is unavailable", async () => {
    api.POST.mockResolvedValue({ error: { code: "phone.sms_unavailable", status: 503 } });
    wrap(<PhoneSection me={me} />);
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("Số điện thoại di động"), "0912345678");
    await user.click(screen.getByRole("button", { name: "Gửi mã xác thực" }));

    expect(await screen.findByText(/liên hệ quản trị viên để được xác thực số/)).toBeTruthy();
    expect(screen.queryByLabelText("Mã xác thực")).toBeNull();
  });
});
