// browse.js — home page, filtered search, listing detail. 50 VUs × 5 min; API p95 < 500 ms, errors < 1%.
// k6 run tests/load/browse.js -e BASE_URL=https://example.com
import http from "k6/http";
import { check, sleep } from "k6";

import { api, API_URL, BASE_URL, LAT, LNG, summaryFile } from "./common.js";

export const options = {
  vus: 50,
  duration: "5m",
  thresholds: {
    "http_req_duration{api:yes}": ["p(95)<500"],
    http_req_failed: ["rate<0.01"],
  },
};

const filters = [
  "type=room&maxRent=3000000",
  "type=room&selfContained=true&amenities=wifi",
  "type=job",
  "type=room&minRent=1500000&maxRent=2500000&sort=rent_asc",
  "sort=newest",
];

export default function () {
  check(http.get(`${BASE_URL}/`), { "home 200": (r) => r.status === 200 });

  const q = filters[Math.floor(Math.random() * filters.length)];
  const list = http.get(`${API_URL}/api/v1/listings?${q}&lat=${LAT}&lng=${LNG}&radiusM=5000&pageSize=20`, api("listings"));
  check(list, { "listings 200": (r) => r.status === 200 });

  const items = list.status === 200 ? list.json("items") : [];
  if (items.length > 0) {
    const id = items[Math.floor(Math.random() * items.length)].listingId;
    check(http.get(`${API_URL}/api/v1/listings/${id}`, api("detail")), { "detail 200": (r) => r.status === 200 });
    check(http.get(`${API_URL}/api/v1/listings/${id}/reviews`, api("reviews")), { "reviews 200": (r) => r.status === 200 });
    check(http.get(`${BASE_URL}/tin/${id}`), { "detail page 200": (r) => r.status === 200 });
  }
  sleep(1 + Math.random() * 2);
}

export const handleSummary = (data) => summaryFile("browse", data);
