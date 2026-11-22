"use client";

import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { secondaryButton } from "@/components/domain/form-controls";
import { OpenDocumentButton } from "@/components/domain/moderation-dialogs";
import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { can, useMe } from "@/lib/auth/use-auth";
import { formatDateTime } from "@/lib/format";
import { DOC_STATUS, DOC_TYPES } from "@/lib/listings/model";

export interface OwnerDocument {
  id: number | string;
  docType: string;
  status: string;
  createdAt: string;
  reviewedAt: string | null;
  erasedAt: string | null;
}

export interface OwnerPanelData {
  userId: number | string;
  fullName: string;
  phone?: string | null;
  ownerType?: string | null;
  businessName?: string | null;
  taxCode?: string | null;
  verifiedUntil?: string | null;
  documents: OwnerDocument[];
}

/** Owner profile + documents for moderators: review each document, open it with a purpose, verify the profile. */
export function OwnerPanel({ data, invalidate }: { data: OwnerPanelData; invalidate: readonly unknown[] }) {
  const queryClient = useQueryClient();
  const { data: me } = useMe();
  const refresh = () => queryClient.invalidateQueries({ queryKey: invalidate });

  const review = async (id: number, decision: "accepted" | "rejected") => {
    const { error } = await api.POST("/api/v1/moderation/owner-documents/{id}/review", { params: { path: { id } }, body: { decision } });
    if (error) return toast.error(messageFor(toProblem(error).code));
    await refresh();
  };
  const verify = async () => {
    const { data: result, error } = await api.POST("/api/v1/moderation/owner-profiles/{userId}/verify", {
      params: { path: { userId: Number(data.userId) } },
    });
    if (error || !result) return toast.error(messageFor(toProblem(error).code));
    toast.success(`Đã xác minh đến ${formatDateTime(result.verifiedUntil)}`);
    await refresh();
  };

  return (
    <div className="flex flex-col gap-3">
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <dt className="text-muted-foreground">Họ tên</dt>
        <dd>{data.fullName}</dd>
        {data.phone !== undefined ? (
          <>
            <dt className="text-muted-foreground">Số điện thoại</dt>
            <dd>{data.phone ?? "Chưa xác thực"}</dd>
          </>
        ) : null}
        <dt className="text-muted-foreground">Hồ sơ chủ</dt>
        <dd>
          {data.ownerType == null
            ? "Chưa tạo"
            : data.ownerType === "business"
              ? `Doanh nghiệp: ${data.businessName} (MST ${data.taxCode})`
              : "Cá nhân"}
        </dd>
        <dt className="text-muted-foreground">Xác minh</dt>
        <dd>{data.verifiedUntil ? `Đến ${formatDateTime(data.verifiedUntil)}` : "Chưa xác minh"}</dd>
      </dl>
      {data.documents.length > 0 ? (
        <ul className="flex flex-col divide-y divide-border rounded-lg border border-border">
          {data.documents.map((d) => (
            <li key={String(d.id)} className="flex flex-wrap items-center justify-between gap-2 p-2 text-sm">
              <span>
                {DOC_TYPES[d.docType as keyof typeof DOC_TYPES] ?? d.docType} · {formatDateTime(d.createdAt)} ·{" "}
                {d.erasedAt ? "Đã xóa tệp" : (DOC_STATUS[d.status as keyof typeof DOC_STATUS] ?? d.status)}
              </span>
              {d.erasedAt ? null : (
                <span className="flex flex-wrap gap-2">
                  {can(me, "document.view") ? <OpenDocumentButton documentId={Number(d.id)} /> : null}
                  {can(me, "owner.verify") && d.status !== "accepted" ? (
                    <button type="button" className={secondaryButton} onClick={() => void review(Number(d.id), "accepted")}>
                      Chấp nhận
                    </button>
                  ) : null}
                  {can(me, "owner.verify") && d.status !== "rejected" ? (
                    <button type="button" className={secondaryButton} onClick={() => void review(Number(d.id), "rejected")}>
                      Từ chối
                    </button>
                  ) : null}
                </span>
              )}
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-sm text-muted-foreground">Chưa có giấy tờ.</p>
      )}
      {can(me, "owner.verify") && data.ownerType != null ? (
        <button type="button" className={`${secondaryButton} w-fit`} onClick={() => void verify()}>
          Xác minh hồ sơ 12 tháng
        </button>
      ) : null}
    </div>
  );
}
