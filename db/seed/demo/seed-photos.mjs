// Demo "reused photos" set (docs/08-deployment.md, Dữ liệu demo): two owners post rooms with the same three photos,
// so the worker records image matches and the moderation screen shows "Ảnh dùng lại". Goes through the real API (and
// the worker must be running), so phash/CLIP vectors and screening results are genuine. Idempotent by title.
//
// Usage (after `migrator seed-demo`, with api + worker running):
//   node db/seed/demo/seed-photos.mjs            # API_BASE_URL defaults to http://localhost:8080
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const API = process.env.API_BASE_URL ?? "http://localhost:8080";
const PASSWORD = process.env.Demo__Password ?? "Demo-TroViec-2026";
const PHOTOS = ["phong-1.png", "phong-2.png", "phong-3.png"].map((f) => fileURLToPath(new URL(`./photos/${f}`, import.meta.url)));
const LISTINGS = [
  { email: "chutro@demo.troviec.test", title: "Phòng mẫu có ảnh (demo) – chủ 1" },
  { email: "chutro2@demo.troviec.test", title: "Phòng mẫu có ảnh (demo) – chủ 2, ảnh dùng lại" },
];

async function call(path, { token, method = "GET", json, form } = {}) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(json ? { "Content-Type": "application/json" } : {}) },
    body: json ? JSON.stringify(json) : form,
  });
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(`${method} ${path} → ${response.status} ${body?.code ?? ""}`);
  return body;
}

const login = async (email) => (await call("/api/v1/auth/login", { method: "POST", json: { email, password: PASSWORD } })).accessToken;
const campus = { lat: 21.0537, lng: 105.7353 };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const created = [];
for (const [i, item] of LISTINGS.entries()) {
  const token = await login(item.email);
  const mine = await call("/api/v1/me/listings?pageSize=50", { token });
  if (mine.items.some((l) => l.title === item.title)) {
    console.log(`exists: ${item.title}`);
    continue;
  }
  const ward = (await call("/api/v1/meta/communes?q=phuong", {}))[0].code;
  const { listingId } = await call("/api/v1/listings", {
    token,
    method: "POST",
    json: {
      listingType: "room",
      title: item.title,
      description: "Phòng 22m2 khép kín, cửa sổ thoáng, có điều hòa và nóng lạnh, gần trường và chợ.",
      wardCode: ward,
      streetAddress: `Số ${10 + i} ngõ 5 đường Cầu Diễn`,
      location: { lat: campus.lat + 0.002 * i, lng: campus.lng + 0.002 * i },
      room: { monthlyRentVnd: 2_300_000, depositVnd: 2_300_000, areaM2: 22, isSelfContained: true, amenityCodes: ["wifi", "air_conditioner"] },
      job: null,
    },
  });
  for (const path of PHOTOS) {
    const form = new FormData();
    form.append("file", new Blob([await readFile(path)], { type: "image/png" }), "phong.png");
    await call(`/api/v1/listings/${listingId}/images`, { token, method: "POST", form });
  }
  await call(`/api/v1/listings/${listingId}/submit`, { token, method: "POST", json: { version: 1 } });
  created.push(listingId);
  console.log(`submitted: ${item.title}`);
}

// Approve once the worker has screened them (screening → pending_review).
const moderator = await login("canbo@demo.troviec.test");
for (const id of created) {
  for (let attempt = 0; attempt < 60; attempt++) {
    const screen = await call(`/api/v1/moderation/listings/${id}`, { token: moderator });
    if (screen.listing.status === "pending_review") {
      await call(`/api/v1/moderation/listings/${id}/approve`, { token: moderator, method: "POST", json: { version: screen.listing.version } });
      console.log(`approved: ${id}`);
      break;
    }
    if (attempt === 59) throw new Error(`listing ${id} was not screened in time — is the worker running?`);
    await sleep(2000);
  }
}
