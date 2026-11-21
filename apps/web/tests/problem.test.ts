import { describe, expect, it } from "vitest";

import { toProblem } from "@/lib/api/problem";

describe("toProblem", () => {
  it("keeps a problem body with a code", () => {
    const body = { code: "auth.email_taken", status: 409, title: "Email đã được đăng ký" };

    expect(toProblem(body)).toEqual(body);
  });

  it.each([null, "Bad Gateway", { message: "x" }])("maps %j to system.error", (body) => {
    expect(toProblem(body, 502)).toEqual({ code: "system.error", status: 502 });
  });
});
