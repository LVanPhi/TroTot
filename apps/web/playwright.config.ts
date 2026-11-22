import { defineConfig, devices } from "@playwright/test";

/**
 * E2E on the full Docker stack (docs/09-testing.md, Đầu cuối): api, worker with LLM_FAKE=1, web, demo data.
 * The stack is started by .github/workflows/nightly.yml (job e2e) or by hand; tests only talk to the browser.
 */
export default defineConfig({
  testDir: "./e2e",
  timeout: 120_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3000",
    locale: "vi-VN",
    timezoneId: "Asia/Ho_Chi_Minh",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "mobile-chrome", use: { ...devices["Pixel 7"] } }],
});
