// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ReasonDialog } from "@/components/domain/moderation-dialogs";
import { APPEAL_STATUS_LABELS, AUDIT_KIND_LABELS, REPORT_CATEGORY_LABELS, REPORT_STATUS_LABELS } from "@/lib/moderation/labels";

afterEach(cleanup);

describe("moderation labels", () => {
  it("never attach the word 'lừa đảo'", () => {
    const all = [REPORT_CATEGORY_LABELS, REPORT_STATUS_LABELS, APPEAL_STATUS_LABELS, AUDIT_KIND_LABELS].flatMap(Object.values);
    expect(all.join(" ").toLowerCase()).not.toContain("lừa đảo");
    expect(REPORT_CATEGORY_LABELS.scam).toBe("Có dấu hiệu gian lận, đòi tiền");
  });
});

describe("ReasonDialog", () => {
  function setup(send = vi.fn().mockResolvedValue(null)) {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <ReasonDialog trigger="Từ chối" title="Từ chối tin" label="Lý do" confirm="Gửi" send={send} />
      </QueryClientProvider>,
    );
    return send;
  }

  it("requires a reason before sending (reject/hide need one)", async () => {
    const send = setup();
    await userEvent.click(screen.getByRole("button", { name: "Từ chối" }));
    await userEvent.click(await screen.findByRole("button", { name: "Gửi" }));

    expect(await screen.findByText("Bắt buộc nhập")).toBeTruthy();
    expect(send).not.toHaveBeenCalled();
  });

  it("sends the trimmed reason and shows the API error text when it fails", async () => {
    const send = setup(vi.fn().mockResolvedValue("listing.invalid_transition"));
    await userEvent.click(screen.getByRole("button", { name: "Từ chối" }));
    await userEvent.type(await screen.findByLabelText("Lý do"), "  Ảnh không đúng phòng  ");
    await userEvent.click(screen.getByRole("button", { name: "Gửi" }));

    expect(send).toHaveBeenCalledWith("Ảnh không đúng phòng");
    expect(await screen.findByText("Không thể thực hiện ở trạng thái hiện tại")).toBeTruthy();
  });
});
