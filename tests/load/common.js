// Shared settings for the k6 scenarios (docs/09-testing.md, Kiểm thử tải).
//   BASE_URL   web origin, default https://$DOMAIN or http://localhost:3000
//   API_URL    API origin, default = BASE_URL (Caddy routes /api) — set http://localhost:8080 in dev
//   LAT, LNG   search centre (default: first campus of the demo data)
export const BASE_URL = __ENV.BASE_URL || "http://localhost:3000";
export const API_URL = __ENV.API_URL || BASE_URL;
export const LAT = Number(__ENV.LAT || 21.0537);
export const LNG = Number(__ENV.LNG || 105.7353);

/** Tag API calls so thresholds can target them: http_req_duration{api:yes}. */
export const api = (name) => ({ tags: { api: "yes", name } });

/** Results of a run, ready for research/results/e6-<date>.json (handleSummary). */
export function summaryFile(name, data) {
  const day = new Date().toISOString().slice(0, 10);
  return {
    stdout: `\n${name}: ${JSON.stringify(data.metrics.http_req_duration?.values ?? {}, null, 0)}\n`,
    [`research/results/e6-${day}-${name}.json`]: JSON.stringify(
      { scenario: name, date: new Date().toISOString(), base: BASE_URL, metrics: data.metrics, thresholds: data.root_group },
      null,
      2,
    ),
  };
}
