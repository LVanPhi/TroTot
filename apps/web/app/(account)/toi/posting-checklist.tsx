import { CheckCircle2, CircleDashed } from "lucide-react";

import type { Me } from "@/lib/api/client";

/** BR-02 steps, worded for users. Order matches how people usually complete them. */
const steps: Record<string, string> = {
  email_verification: "Xác thực email do trường cấp",
  phone_verification: "Xác thực số điện thoại",
  owner_verification: "Hồ sơ chủ trọ / nhà tuyển dụng được cán bộ xác minh",
};

/** What is still missing before the user may submit a listing; text + icon, never colour alone. */
export function PostingChecklist({ posting }: { posting: Me["posting"] }) {
  if (posting.allowed) {
    return (
      <p role="status" className="flex items-center gap-2 rounded-lg border border-border p-3">
        <CheckCircle2 aria-hidden className="size-5 shrink-0 text-primary" />
        <span>Bạn đã đủ điều kiện đăng tin.</span>
      </p>
    );
  }
  return (
    <div className="flex flex-col gap-2">
      <p>Để đăng tin, bạn cần hoàn tất:</p>
      <ul className="flex flex-col gap-2">
        {posting.missing.map((code) => (
          <li key={code} className="flex items-center gap-2 rounded-lg border border-border p-3">
            <CircleDashed aria-hidden className="size-5 shrink-0 text-muted-foreground" />
            <span>
              <span className="sr-only">Chưa xong: </span>
              {steps[code] ?? code}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
