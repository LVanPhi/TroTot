import { describe, expect, it } from "vitest";

import { allowedActions, EMPTY_LISTING, listingSchema, ownerProfileSchema, toRequest, type ListingValues } from "@/lib/listings/model";

const now = new Date("2026-11-22T00:00:00Z");
const inDays = (d: number) => new Date(now.getTime() + d * 86_400_000).toISOString();

const room: ListingValues = {
  ...EMPTY_LISTING,
  title: "Phòng trọ khép kín",
  description: "Phòng 20m2 khép kín, có điều hòa, nóng lạnh, giờ giấc tự do.",
  wardCode: "00004",
  streetAddress: "Số 1 ngõ 2",
  location: { lat: 21.05, lng: 105.73 },
  rent: "2tr5",
  deposit: "",
  areaM2: "20,5",
  isSelfContained: true,
  amenityCodes: ["wifi"],
};

describe("allowedActions (life cycle)", () => {
  it.each([
    ["draft", undefined, ["edit", "submit", "remove"]],
    ["screening", undefined, []],
    ["pending_review", undefined, ["remove"]],
    ["verified", inDays(30), ["unpublish", "remove"]],
    ["verified", inDays(5), ["renew", "unpublish", "remove"]],
    ["expired", inDays(-1), ["renew", "unpublish", "remove"]],
    ["rejected", undefined, ["appeal", "unpublish", "remove"]],
    ["hidden", undefined, ["appeal", "remove"]],
    ["removed", undefined, []],
  ] as const)("%s", (status, expiresAt, expected) => {
    expect(allowedActions(status, { expiresAt, now })).toEqual(expected);
  });

  it("an open appeal blocks every action (BR-04)", () => {
    expect(allowedActions("rejected", { hasOpenAppeal: true, now })).toEqual([]);
  });
});

describe("listing form", () => {
  it("valid room maps to the API body; empty deposit is null, 0 stays 0", () => {
    expect(listingSchema.safeParse(room).success).toBe(true);
    const body = toRequest(room);
    expect(body.room).toEqual({ monthlyRentVnd: 2_500_000, depositVnd: null, areaM2: 20.5, isSelfContained: true, amenityCodes: ["wifi"] });
    expect(body.job).toBeNull();
    expect(toRequest({ ...room, deposit: "0" }).room?.depositVnd).toBe(0);
  });

  it.each([
    ["title", { title: "  " }],
    ["description", { description: "ngắn quá" }],
    ["rent", { rent: "50k" }],
    ["areaM2", { areaM2: "0" }],
    ["location", { location: null }],
    ["wardCode", { wardCode: "" }],
  ] as const)("room: invalid %s", (field, patch) => {
    const result = listingSchema.safeParse({ ...room, ...patch });
    expect(result.success).toBe(false);
    expect(result.error?.issues.map((i) => i.path[0])).toContain(field);
  });

  it("job needs employer, pay and 1–48 hours; room fields are ignored", () => {
    const job: ListingValues = { ...room, listingType: "job", rent: "", areaM2: "", employerName: "Quán A", pay: "25k", payUnit: "hour", hoursPerWeek: "20" };
    expect(listingSchema.safeParse(job).success).toBe(true);
    expect(toRequest(job).job).toEqual({ employerName: "Quán A", payVnd: 25_000, payUnit: "hour", hoursPerWeek: 20 });
    const bad = listingSchema.safeParse({ ...job, hoursPerWeek: "60", employerName: "" });
    expect(bad.error?.issues.map((i) => i.path[0]).sort()).toEqual(["employerName", "hoursPerWeek"]);
  });
});

describe("owner profile form", () => {
  it("business needs name and a 10 or 10-3 digit tax code", () => {
    expect(ownerProfileSchema.safeParse({ ownerType: "individual", businessName: "", taxCode: "" }).success).toBe(true);
    expect(ownerProfileSchema.safeParse({ ownerType: "business", businessName: "A", taxCode: "0101234567-001" }).success).toBe(true);
    const bad = ownerProfileSchema.safeParse({ ownerType: "business", businessName: "", taxCode: "12345" });
    expect(bad.error?.issues.map((i) => i.path[0]).sort()).toEqual(["businessName", "taxCode"]);
  });
});
