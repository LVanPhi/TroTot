// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import { StatusBadge, StatusTimeline } from "@/components/domain/listing-status";
import { EMPTY_LISTING } from "@/lib/listings/model";

// maplibre needs WebGL; the form test only needs a field that sets a location.
vi.mock("next/dynamic", () => ({
  default: () =>
    function FakePicker({ onChange, error }: { onChange: (v: { lat: number; lng: number }) => void; error?: string }) {
      return (
        <>
          <button type="button" onClick={() => onChange({ lat: 21, lng: 105.8 })}>
            Ghim thử
          </button>
          {error ? <p role="alert">{error}</p> : null}
        </>
      );
    },
}));
vi.mock("@/lib/api/client", () => ({ api: { GET: vi.fn().mockResolvedValue({ data: [{ code: "wifi", name: "Wifi" }] }) } }));

const { ListingForm } = await import("@/components/domain/listing-form");

afterEach(cleanup);

const VALID = {
  ...EMPTY_LISTING,
  title: "Phòng đẹp",
  description: "Phòng 20m2 khép kín, có điều hòa, nóng lạnh, gần trường.",
  wardCode: "00004",
  streetAddress: "Số 1",
  location: { lat: 21, lng: 105.8 },
  rent: "2tr5",
  areaM2: "20",
};

function renderForm(submit = vi.fn().mockResolvedValue(null), initial = EMPTY_LISTING) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ListingForm
        initial={initial}
        initialCommune={initial.wardCode ? { code: initial.wardCode, name: "Phường Thử", provinceCode: "", provinceName: "" } : null}
        typeLocked={false}
        submitLabel="Lưu nháp"
        submit={submit}
      />
    </QueryClientProvider>,
  );
  return submit;
}

describe("ListingForm", () => {
  it("shows field errors and does not submit an empty form", async () => {
    const submit = renderForm();

    await userEvent.click(screen.getByRole("button", { name: "Lưu nháp" }));

    expect(await screen.findByText("Nhập tiêu đề")).toBeTruthy();
    expect(screen.getByText("Ghim vị trí trên bản đồ")).toBeTruthy();
    expect(screen.getByText("Giá thuê từ 100.000 đến 100.000.000 đồng")).toBeTruthy();
    expect(submit).not.toHaveBeenCalled();
  });

  it("switching to job shows job fields instead of room fields", async () => {
    renderForm();

    await userEvent.click(screen.getByLabelText("Việc làm thêm"));

    expect(screen.getByLabelText("Tên nơi làm việc")).toBeTruthy();
    expect(screen.queryByLabelText("Giá thuê mỗi tháng")).toBeNull();
  });

  it("submits valid values and places API validation errors on the matching field", async () => {
    const submit = vi.fn().mockResolvedValue({ code: "validation.failed", errors: { "room.monthlyRentVnd": ["Giá không hợp lệ (máy chủ)"] } });
    renderForm(submit, VALID);

    await userEvent.click(screen.getByRole("button", { name: "Lưu nháp" }));

    expect(submit).toHaveBeenCalledOnce();
    expect(await screen.findByText("Giá không hợp lệ (máy chủ)")).toBeTruthy();
  });
});

describe("StatusBadge and StatusTimeline", () => {
  it("status has text and icon", () => {
    const { container } = render(<StatusBadge status="screening" />);
    expect(screen.getByText("Đang sàng lọc")).toBeTruthy();
    expect(container.querySelector("svg")).not.toBeNull();
  });

  it("timeline shows reason and actor label", () => {
    render(
      <StatusTimeline
        items={[{ fromStatus: "pending_review", toStatus: "rejected", reason: "Thiếu ảnh", actor: "Cán bộ xác minh", createdAt: "2026-11-20T03:00:00Z" }]}
      />,
    );
    expect(screen.getByText("Lý do: Thiếu ảnh")).toBeTruthy();
    expect(screen.getByText(/Cán bộ xác minh/)).toBeTruthy();
  });
});
