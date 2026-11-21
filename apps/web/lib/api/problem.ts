/** RFC 9457 problem details as returned by the API (docs/04-api.md, Lỗi). */
export interface Problem {
  type?: string;
  title?: string;
  status?: number;
  code: string;
  detail?: string | null;
  traceId?: string;
  errors?: Record<string, string[]> | null;
}

/** Narrow an unknown error body to a Problem; anything else becomes a generic system error. */
export function toProblem(body: unknown, status?: number): Problem {
  if (typeof body === "object" && body !== null && typeof (body as { code?: unknown }).code === "string") {
    return body as Problem;
  }
  return { code: "system.error", status };
}
