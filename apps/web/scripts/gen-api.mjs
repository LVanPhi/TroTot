// Generates lib/api/schema.d.ts from the running API's OpenAPI document.
// OPENAPI_URL overrides the default (e.g. when port 8080 is taken on your machine).
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";

const url = process.env.OPENAPI_URL ?? "http://localhost:8080/openapi/v1.json";
// The package's "./*.js" export maps to .mjs, so locate bin/cli.js from its package.json instead.
const packageDir = dirname(createRequire(import.meta.url).resolve("openapi-typescript/package.json"));
execFileSync(process.execPath, [join(packageDir, "bin", "cli.js"), url, "-o", "lib/api/schema.d.ts"], {
  stdio: "inherit",
});
