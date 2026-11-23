// Shared settings for the k6 scenarios (docs/09-testing.md, Kiểm thử tải).
//   BASE_URL   web origin, default https://$DOMAIN or http://localhost:3000
//   API_URL    API origin, default = BASE_URL (Caddy routes /api) — set http://localhost:8080 in dev
//   LAT, LNG   search centre (default: first campus of the demo data)
//   SPREAD_IPS=1  give every iteration its own client IP (X-Forwarded-For from 198.18.0.0/15, the benchmarking range).
//              Only works where the proxy in front trusts the k6 machine (the tunnel stack's Caddy trusts private
//              ranges, so run k6 on its Docker network). Without it every request shares one IP and the per-IP limits
//              (300/min, search 60/min, guest checks 5/day) answer 429 long before the server is loaded.
export const BASE_URL = __ENV.BASE_URL || "http://localhost:3000";
export const API_URL = __ENV.API_URL || BASE_URL;
export const LAT = Number(__ENV.LAT || 21.0537);
export const LNG = Number(__ENV.LNG || 105.7353);

const spreadIps = __ENV.SPREAD_IPS === "1";
let clientIp = null;
const rnd = (n) => Math.floor(Math.random() * n);

/** Call at the start of each iteration: one simulated visitor per iteration. */
export function newVisitor() {
  if (spreadIps) clientIp = `198.${18 + rnd(2)}.${rnd(256)}.${1 + rnd(254)}`;
}

const headers = () => (clientIp ? { "X-Forwarded-For": clientIp } : {});

/** Tag API calls so thresholds can target them: http_req_duration{api:yes}. */
export const api = (name) => ({ tags: { api: "yes", name }, headers: headers() });

/** Web page requests (not counted in the API threshold). */
export const page = (name) => ({ tags: { name }, headers: headers() });

/** Results of a run, ready for research/results/e6-<date>.json (handleSummary). */
export function summaryFile(name, data) {
  const day = new Date().toISOString().slice(0, 10);
  const label = __ENV.RUN_LABEL ? `-${__ENV.RUN_LABEL}` : "";   // e.g. RUN_LABEL=tunnel keeps a second run apart
  return {
    stdout: `\n${name}: ${JSON.stringify(data.metrics.http_req_duration?.values ?? {}, null, 0)}\n`,
    [`research/results/e6-${day}-${name}${label}.json`]: JSON.stringify(
      { scenario: name, label: __ENV.RUN_LABEL || null, spreadIps: __ENV.SPREAD_IPS === "1", date: new Date().toISOString(), base: BASE_URL, metrics: data.metrics, thresholds: data.root_group },
      null,
      2,
    ),
  };
}
