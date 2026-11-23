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

import { api, API_URL, newVisitor, summaryFile } from "./common.js";

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

// k6 cannot send two files under the same form field from a plain object (an array value is not multipart),
// and the API reads every part named "images" — so the body is built by hand.
function utf8(str) {
  const bin = unescape(encodeURIComponent(str));
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function multipart(fields, files) {
  const boundary = `----k6${Math.random().toString(16).slice(2)}`;
  const CRLF = "\r\n";
  const chunks = [];
  for (const [name, value] of fields) {
    chunks.push(utf8(`--${boundary}${CRLF}Content-Disposition: form-data; name="${name}"${CRLF}${CRLF}${value}${CRLF}`));
  }
  for (const [name, filename, data] of files) {
    chunks.push(utf8(`--${boundary}${CRLF}Content-Disposition: form-data; name="${name}"; filename="${filename}"${CRLF}` +
      `Content-Type: image/png${CRLF}${CRLF}`));
    chunks.push(new Uint8Array(data));
    chunks.push(utf8(CRLF));
  }
  chunks.push(utf8(`--${boundary}--${CRLF}`));
  const body = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0));
  let offset = 0;
  for (const c of chunks) {
    body.set(c, offset);
    offset += c.length;
  }
  return { body: body.buffer, contentType: `multipart/form-data; boundary=${boundary}` };
}

export default function () {
  newVisitor();
  // Unique text per iteration: the input_sha256 cache would otherwise answer instantly and hide the real cost.
  const text = `${texts[Math.floor(Math.random() * texts.length)]} #${__VU}-${__ITER}-${Date.now()}`;
  const form = multipart([["text", text]], [["images", "a.png", shot1], ["images", "b.png", shot2]]);
  const params = api("create");
  params.headers["Content-Type"] = form.contentType;
  const created = http.post(`${API_URL}/api/v1/scans`, form.body, params);
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
