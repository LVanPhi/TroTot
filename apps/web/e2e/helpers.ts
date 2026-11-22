import { expect, type Page } from "@playwright/test";

/** Demo accounts from db/seed/demo (password in db/seed/demo/README.md). */
export const PASSWORD = process.env.DEMO_PASSWORD ?? "Demo-TroViec-2026";
export const STUDENT_DOMAIN = process.env.E2E_STUDENT_DOMAIN ?? "sv.example.edu.vn";
export const ACCOUNTS = {
  student: `sinhvien@${STUDENT_DOMAIN}`,
  owner: "chutro@demo.troviec.test",
  moderator: "canbo@demo.troviec.test",
  moderator2: "canbo2@demo.troviec.test",
  admin: "quantri@demo.troviec.test",
} as const;

/** First campus of Campuses__Json in the E2E stack (used as the poster's "current position"). */
export const CAMPUS = { latitude: 21.0537, longitude: 105.7353 };

export async function login(page: Page, email: string) {
  await page.goto("/dang-nhap");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Mật khẩu").fill(PASSWORD);
  await page.getByRole("button", { name: "Đăng nhập" }).click();
  await expect(page).not.toHaveURL(/dang-nhap/);
}

export async function logout(page: Page) {
  await page.context().clearCookies();
  await page.goto("/");
}

/** Unique text per run so repeated runs never collide (titles, PDFs). */
export const runId = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
