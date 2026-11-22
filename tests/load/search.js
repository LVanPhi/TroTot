// search.js — natural-language search (hybrid when worker-embed answers). 20 VUs × 3 min; p95 < 1,000 ms.
import http from "k6/http";
import { check, sleep } from "k6";

import { api, API_URL, LAT, LNG, newVisitor, summaryFile } from "./common.js";

export const options = {
  vus: 20,
  duration: "3m",
  thresholds: {
    "http_req_duration{api:yes}": ["p(95)<1000"],
    http_req_failed: ["rate<0.01"],
  },
};

const queries = [
  "phòng khép kín dưới 2,5 triệu có chỗ để xe",
  "phòng trọ yên tĩnh gần trường có điều hòa",
  "phòng 2tr5 có máy giặt",
  "việc làm thêm buổi tối phục vụ quán cà phê",
  "gia sư toán cuối tuần",
  "phòng rộng có ban công thoáng mát",
];

export default function () {
  newVisitor();
  const q = encodeURIComponent(queries[Math.floor(Math.random() * queries.length)]);
  const res = http.get(`${API_URL}/api/v1/listings/search?q=${q}&lat=${LAT}&lng=${LNG}&radiusM=5000`, api("search"));
  check(res, {
    "search 200": (r) => r.status === 200,
    "has mode": (r) => r.status === 200 && ["hybrid", "fulltext", "filter"].includes(r.json("mode")),
  });
  sleep(1 + Math.random() * 2);
}

export const handleSummary = (data) => summaryFile("search", data);
