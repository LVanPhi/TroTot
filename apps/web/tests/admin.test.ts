import { describe, expect, it } from "vitest";

import { activationSummary } from "@/app/(admin)/quan-tri/mo-hinh/model-versions";

const agg = (version: string, caution: number, high: number) => ({
  id: 1, component: "aggregator", name: "weighted-rules", version, isActive: false, activatedAt: null, thresholds: { caution, high },
});

describe("model activation confirmation (US-803)", () => {
  it("states the new and current thresholds for an aggregator", () => {
    expect(activationSummary(agg("agg-v1", 45, 75), { ...agg("agg-v0", 40, 70), isActive: true })).toBe(
      "Ngưỡng mới: Cần thận trọng từ 45, Cao từ 75 (hiện tại: Cần thận trọng từ 40, Cao từ 70). Áp dụng cho mọi lượt chấm sau khi bật.",
    );
  });

  it("other components just say the running version turns off", () => {
    expect(activationSummary({ ...agg("5", 0, 0), component: "ocr", name: "tesseract-vie", thresholds: null }, undefined)).toBe(
      "Bật tesseract-vie 5; phiên bản đang chạy sẽ tắt.",
    );
  });
});
