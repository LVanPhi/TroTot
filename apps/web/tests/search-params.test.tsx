// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { ListingCard, type ListingItem } from "@/components/domain/listing-card";
import { appliedChips, DEFAULT_STATE, toQuery } from "@/lib/search/params";

afterEach(cleanup);

describe("search params", () => {
  it("leaves empty values out of the query", () => {
    expect(toQuery(DEFAULT_STATE)).toEqual({
      q: undefined, type: undefined, minRent: undefined, maxRent: undefined, selfContained: undefined, amenities: undefined,
      radiusM: 3000, lat: undefined, lng: undefined,
    });
  });

  it("reads typed money and joins amenities", () => {
    const q = toQuery({ ...DEFAULT_STATE, q: " phòng ", type: "room", maxRent: "2.500.000", selfContained: true, amenities: ["wifi", "parking"] });
    expect(q).toMatchObject({ q: "phòng", type: "room", maxRent: 2_500_000, selfContained: true, amenities: "wifi,parking" });
  });

  it("chips describe the conditions the API understood", () => {
    const chips = appliedChips({ type: "room", maxRent: 2_500_000, selfContained: true, amenities: ["parking"], nearCampus: true }, { parking: "Chỗ để xe" });
    expect(chips).toEqual(["Phòng trọ", "Dưới 2,5 triệu", "Khép kín", "Chỗ để xe", "Gần trường"]);
    expect(appliedChips({ minRent: 2_000_000, maxRent: "3000000" }, {})).toEqual(["2 triệu – 3 triệu"]);
  });
});

describe("ListingCard", () => {
  const room: ListingItem = {
    listingId: "3ff5edcd-fa5a-484d-8ee2-c7174001e7f0",
    listingType: "room",
    title: "Phòng trọ khép kín gần ĐH Công nghiệp",
    wardName: "Phường Thử",
    streetAddress: "Số 1",
    location: { lat: 21.05, lng: 105.73 },
    coverImageUrl: null,
    room: { monthlyRentVnd: 2_500_000, areaM2: 20, isSelfContained: true },
    job: null,
    distanceM: 350,
    verifiedUntil: "2027-02-18T03:00:00Z",
  };

  it("shows price, area, distance and the verified label, linking to the detail page", () => {
    render(<ListingCard item={room} />);

    expect(screen.getByText(/2,5 triệu\/tháng · 20 m² · khép kín/)).toBeTruthy();
    expect(screen.getByText("Phường Thử · 350 m")).toBeTruthy();
    expect(screen.getByText("Đã xác minh")).toBeTruthy();
    expect(screen.getByRole("link").getAttribute("href")).toBe(`/tin/${room.listingId}`);
  });

  it("job shows pay per unit", () => {
    render(<ListingCard item={{ ...room, listingType: "job", room: null, job: { employerName: "Quán A", payVnd: 25_000, payUnit: "hour" } }} />);
    expect(screen.getByText("25.000 ₫/giờ")).toBeTruthy();
  });
});
