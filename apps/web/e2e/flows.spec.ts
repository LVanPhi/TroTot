import { expect, test } from "@playwright/test";

import { ACCOUNTS, CAMPUS, login, logout, runId } from "./helpers";

/**
 * The four end-to-end flows of docs/09-testing.md (Đầu cuối). Each run creates its own data (unique titles and files),
 * so the suite can run again and again on the same database.
 */

const DEPOSIT_SCAM =
  "Cho thuê phòng trọ khép kín giá rẻ 1tr5 gần trường. Chủ nhà đang ở xa nên các bạn chuyển cọc trước 1 triệu " +
  "để giữ chỗ, liên hệ 0912345678. Nhiều người đang hỏi lắm, chốt ngay hôm nay nhé.";

test("1. Khách kiểm tra tin đòi cọc thấy mức Cao, dấu hiệu và câu trích", async ({ page }) => {
  await page.goto("/kiem-tra");
  await page.getByLabel("Nội dung tin").fill(DEPOSIT_SCAM);
  await page.getByRole("button", { name: "Kiểm tra" }).click();

  await expect(page).toHaveURL(/\/kiem-tra\/[0-9a-f-]{36}$/);
  await expect(page.getByText("Mức rủi ro: Cao")).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText("Đòi cọc trước khi cho xem phòng")).toBeVisible();
  await expect(page.locator("blockquote").filter({ hasText: /cọc/i }).first()).toBeVisible();
  await expect(page.getByRole("note")).toContainText("không phải kết luận về người đăng");
  await expect(page.locator("body")).not.toContainText(/lừa đảo/i);
});

test("2. Chủ trọ đăng tin, cán bộ duyệt, khách tìm thấy", async ({ browser }) => {
  const title = `Phòng khép kín E2E ${runId()}`;
  const owner = await browser.newContext({ geolocation: CAMPUS, permissions: ["geolocation"] });
  const page = await owner.newPage();
  await login(page, ACCOUNTS.owner);

  await page.goto("/toi/tin-cua-toi/moi");
  await page.getByLabel("Tiêu đề").fill(title);
  await page.getByLabel("Mô tả").fill("Phòng 20m2 khép kín, có điều hòa, nóng lạnh, giờ giấc tự do, gần trường đại học.");
  await page.getByRole("combobox", { name: "Phường/xã" }).fill("phuong");
  await page.getByRole("option").first().click();
  await page.getByLabel("Địa chỉ (số nhà, ngõ, đường)").fill("Số 1 ngõ 2 đường Cầu Diễn");
  await page.getByRole("button", { name: "Dùng vị trí của tôi" }).click();
  await expect(page.getByText(/^Vị trí: /)).toBeVisible();
  await page.getByLabel("Giá thuê mỗi tháng").fill("2tr2");
  await page.getByLabel("Diện tích (m²)").fill("20");
  await page.getByLabel("Phòng khép kín (vệ sinh riêng)").check();
  await page.getByRole("button", { name: "Lưu nháp" }).click();

  await expect(page).toHaveURL(/\/sua\?moi=1$/);
  await page.locator('input[type="file"]').setInputFiles("e2e/fixtures/room.png");
  await expect(page.getByRole("img", { name: "Ảnh số 1" })).toBeVisible();
  await page.getByRole("button", { name: "Xong, tới trang gửi duyệt" }).click();
  await page.getByRole("button", { name: "Gửi duyệt" }).click();
  await expect(page.getByText("Chờ duyệt").first()).toBeVisible({ timeout: 90_000 });   // screening → pending_review
  await owner.close();

  const moderator = await browser.newPage();
  await login(moderator, ACCOUNTS.moderator);
  await moderator.goto("/kiem-duyet");
  await moderator.getByRole("link", { name: new RegExp(title) }).click();
  await moderator.getByRole("button", { name: "Duyệt (90 ngày)" }).click();
  await expect(moderator).toHaveURL(/\/kiem-duyet$/);
  await moderator.close();

  const guest = await browser.newPage();
  await guest.goto(`/tin?q=${encodeURIComponent("phòng khép kín")}`);
  await expect(guest.getByRole("link", { name: new RegExp(title) })).toBeVisible();
  await guest.close();
});

test("3. Báo cáo → ẩn → kháng nghị → cán bộ khác/ admin chấp nhận", async ({ browser }) => {
  // A verified demo listing of the demo owner, found through the public search.
  const student = await browser.newPage();
  await login(student, ACCOUNTS.student);
  await student.goto("/tin");
  await student.getByRole("link", { name: /Phòng trọ .* gần ĐH/ }).first().click();
  const listingUrl = student.url();
  await student.getByRole("button", { name: "Báo cáo" }).click();
  await student.getByLabel("Có dấu hiệu gian lận, đòi tiền").check();
  await student.getByRole("button", { name: "Gửi báo cáo" }).click();
  await expect(student.getByText("Đã gửi báo cáo")).toBeVisible();
  await student.close();

  const moderator = await browser.newPage();
  await login(moderator, ACCOUNTS.moderator);
  await moderator.goto("/kiem-duyet/bao-cao");
  await moderator.getByRole("button", { name: /Có dấu hiệu gian lận, đòi tiền/ }).first().click();
  await moderator.getByRole("button", { name: "Xác nhận" }).click();
  await moderator.getByLabel("Ghi chú (bắt buộc)").fill("Xác nhận qua E2E");
  await moderator.getByLabel(/Ẩn tin này luôn/).check();
  await moderator.getByRole("button", { name: "Xác nhận" }).last().click();
  await expect(moderator.getByText("Đã xử lý báo cáo")).toBeVisible();

  const owner = await browser.newPage();
  await login(owner, ACCOUNTS.owner);
  const listingId = listingUrl.split("/").pop();
  await owner.goto(`/toi/tin-cua-toi/${listingId}`);
  await expect(owner.getByText("Tạm ẩn").first()).toBeVisible();
  await owner.getByRole("button", { name: "Kháng nghị" }).click();
  await owner.getByLabel("Lý do").fill("Tin đúng sự thật, đề nghị xem lại.");
  await owner.getByRole("button", { name: "Gửi kháng nghị" }).click();
  await expect(owner.getByText("Kháng nghị đang chờ xử lý")).toBeVisible();
  await owner.close();

  // The moderator who hid the listing cannot decide the appeal (BR-04, D16).
  await moderator.goto("/kiem-duyet/khang-nghi");
  const card = moderator.locator("li").filter({ hasText: "Tin đúng sự thật, đề nghị xem lại." }).first();
  await card.getByRole("button", { name: "Chấp nhận" }).click();
  await moderator.getByLabel("Ghi chú (bắt buộc)").fill("Thử tự xử lý");
  await moderator.getByRole("button", { name: "Chấp nhận" }).last().click();
  await expect(moderator.getByText("Kháng nghị phải do cán bộ khác xử lý")).toBeVisible();
  await moderator.close();

  const admin = await browser.newPage();
  await login(admin, ACCOUNTS.admin);
  await admin.goto("/kiem-duyet/khang-nghi");
  const adminCard = admin.locator("li").filter({ hasText: "Tin đúng sự thật, đề nghị xem lại." }).first();
  await adminCard.getByRole("button", { name: "Chấp nhận" }).click();
  await admin.getByLabel("Ghi chú (bắt buộc)").fill("Đã xem lại, tin hợp lệ");
  await admin.getByRole("button", { name: "Chấp nhận" }).last().click();
  await expect(admin.getByText("Đã xử lý kháng nghị")).toBeVisible();
  await admin.close();

  const guest = await browser.newPage();
  const response = await guest.goto(listingUrl);
  expect(response?.status()).toBe(200);
  await logout(guest);
  await guest.close();
});

test("4. Chủ trọ tải giấy tờ, cán bộ mở với mục đích, nhật ký có dòng truy cập", async ({ browser }) => {
  const owner = await browser.newPage();
  await login(owner, ACCOUNTS.owner);
  await owner.goto("/toi/ho-so-chu");
  // A different PDF every run: the same file twice is refused as a duplicate (US-502).
  const pdf = Buffer.from(`%PDF-1.7\n% e2e ${runId()}\n1 0 obj << /Type /Catalog >> endobj\ntrailer\n%%EOF\n`);
  await owner.locator('input[type="file"]').setInputFiles({ name: "cccd.pdf", mimeType: "application/pdf", buffer: pdf });
  await owner.getByRole("checkbox").check();
  await owner.getByRole("button", { name: "Tải giấy tờ" }).click();
  await expect(owner.getByText("Đã tải giấy tờ")).toBeVisible();
  await owner.close();

  const moderator = await browser.newPage();
  await login(moderator, ACCOUNTS.moderator);
  await moderator.goto("/kiem-duyet/ho-so");
  await moderator.getByRole("button", { name: /Lê Văn Cường/ }).first().click();
  await moderator.getByRole("button", { name: "Mở giấy tờ" }).last().click();
  await moderator.getByLabel("Mục đích mở giấy tờ").fill("Đối chiếu tên chủ hộ (E2E)");
  const popupPromise = moderator.waitForEvent("popup");
  await moderator.getByRole("button", { name: "Mở", exact: true }).click();
  const popup = await popupPromise;
  await popup.waitForLoadState();
  expect(popup.url()).toMatch(/\/api\/v1\/files\/documents\//);
  await popup.close();

  await moderator.goto("/kiem-duyet/nhat-ky");
  await expect(moderator.getByText("Đối chiếu tên chủ hộ (E2E)").first()).toBeVisible();
  await moderator.close();
});
