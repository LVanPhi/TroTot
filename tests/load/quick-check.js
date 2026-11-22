// quick-check.js — submit a quick check (text + 2 screenshots), poll the result every 0.5 s.
// constant-arrival-rate 6 checks/minute for 5 minutes (≈ 10× the expected load of 30 students); the measure is the
// server-side p95 of completedAt − createdAt read from GET /scans/{id} (docs/09-testing.md). Not a closed loop of
// N virtual users: each worker handles one scan at a time, so a closed loop only measures queueing.
//
// Before running on production: raise Quota__GuestScansPerDay for the k6 machine (or use X-Forwarded-For through a
// trusted proxy in a test stack) — otherwise the 6th check of the day answers 429.
import http from "k6/http";
import { check, sleep } from "k6";
import { Trend, Rate } from "k6/metrics";

import { api, API_URL, summaryFile } from "./common.js";

const processing = new Trend("scan_processing_ms", true);
const failed = new Rate("scan_failed");

export const options = {
  scenarios: {
    checks: {
      executor: "constant-arrival-rate",
      rate: Number(__ENV.RATE || 6),
      timeUnit: "1m",
      duration: __ENV.DURATION || "5m",
      preAllocatedVUs: 5,
      maxVUs: 20,
    },
  },
  thresholds: {
    scan_processing_ms: ["p(95)<10000"],
    scan_failed: ["rate<0.01"],
    http_req_failed: ["rate<0.01"],
  },
};

// Two small screenshots of a listing (PNG) committed next to this script.
const shot1 = open("./fixtures/screenshot-1.png", "b");
const shot2 = open("./fixtures/screenshot-2.png", "b");
const texts = [
  "Cho thuê phòng trọ khép kín 1tr5 gần trường. Chủ nhà ở xa nên chuyển cọc trước giữ chỗ, gọi 0900111222.",
  "Tuyển nhân viên phục vụ quán cà phê ca tối, làm tại quán, lương 25k/giờ, phỏng vấn trực tiếp.",
  "Phòng 20m2 có điều hòa, nóng lạnh, giờ giấc tự do, gần chợ. Xem phòng trước khi đặt cọc.",
];

export default function () {
  // Unique text per iteration: the input_sha256 cache would otherwise answer instantly and hide the real cost.
  const text = `${texts[Math.floor(Math.random() * texts.length)]} #${__VU}-${__ITER}-${Date.now()}`;
  const created = http.post(
    `${API_URL}/api/v1/scans`,
    { text, images: [http.file(shot1, "a.png", "image/png"), http.file(shot2, "b.png", "image/png")] },
    api("create"),
  );
  if (!check(created, { "202 accepted": (r) => r.status === 202 })) return;
  const id = created.json("scanId");

  for (let i = 0; i < 120; i++) {
    sleep(0.5);
    const scan = http.get(`${API_URL}/api/v1/scans/${id}`, api("poll"));
    if (scan.status !== 200) continue;
    const status = scan.json("status");
    if (status === "completed" || status === "failed") {
      processing.add(Date.parse(scan.json("completedAt")) - Date.parse(scan.json("createdAt")));
      failed.add(status === "failed");
      return;
    }
  }
  failed.add(true);   // not finished within 60 s
}

export const handleSummary = (data) => summaryFile("quick-check", data);
