"use client";

import { useQueryClient } from "@tanstack/react-query";
import { Hourglass } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { toast } from "sonner";

import { FormError, linkClass, secondaryButton, SubmitButton } from "@/components/domain/form-controls";
import { StatusBadge, StatusTimeline } from "@/components/domain/listing-status";
import { VersionConflictDialog } from "@/components/domain/version-conflict-dialog";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem, type Problem } from "@/lib/api/problem";
import { formatDateTime } from "@/lib/format";
import { ACTION_LABELS, allowedActions, MISSING_LABELS, type ListingAction } from "@/lib/listings/model";
import { listingKey, ListingLoadError, statusOf, useHistory, useMyListing, useOpenAppeal } from "@/lib/listings/use-listing";

type LifecycleAction = "submit" | "renew" | "unpublish" | "remove";

/** Poster's view of one listing: status, allowed actions (by life cycle), timeline, appeal (US-304–308, US-701–702). */
export function ListingManage({ id }: { id: string }) {
  const queryClient = useQueryClient();
  const { data, error, refetch } = useMyListing(id);
  const status = statusOf(data);
  const history = useHistory(id, data?.status);
  const appeals = useOpenAppeal(id, Boolean(data));
  const openAppeal = appeals.data?.find((a) => a.status === "open");
  const [problem, setProblem] = useState<Problem | null>(null);
  const [conflict, setConflict] = useState(false);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [appealOpen, setAppealOpen] = useState(false);

  if (error) {
    const notFound = error instanceof ListingLoadError && error.problem.status === 404;
    return <p role="alert">{notFound ? "Không tìm thấy tin." : "Chưa tải được tin, vui lòng thử lại."}</p>;
  }
  if (!data) return <p aria-busy="true">Đang tải…</p>;

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: listingKey(id) }),
      queryClient.invalidateQueries({ queryKey: ["me", "listings"] }),
      queryClient.invalidateQueries({ queryKey: ["me", "appeals"] }),
    ]);
  };

  const run = async (action: LifecycleAction) => {
    setProblem(null);
    const params = { path: { listingId: id } };
    const version = Number(data.version);
    const result =
      action === "remove"
        ? await api.POST("/api/v1/listings/{listingId}/remove", { params, body: { version, reason: null } })
        : action === "submit"
          ? await api.POST("/api/v1/listings/{listingId}/submit", { params, body: { version } })
          : action === "renew"
            ? await api.POST("/api/v1/listings/{listingId}/renew", { params, body: { version } })
            : await api.POST("/api/v1/listings/{listingId}/unpublish", { params, body: { version } });
    if (!result.response.ok) {
      // The generated client declares no error bodies for these routes; read the problem details anyway.
      const p = toProblem((result as { error?: unknown }).error);
      if (p.code === "listing.version_conflict") setConflict(true);
      else setProblem(p);
      return;
    }
    toast.success(action === "submit" || action === "renew" ? "Đã gửi duyệt. Tin đang được sàng lọc." : "Đã cập nhật tin.");
    await refresh();
  };

  const actions = allowedActions(status, { expiresAt: data.expiresAt, hasOpenAppeal: Boolean(openAppeal) });
  const button = (a: ListingAction) => {
    if (a === "edit")
      return (
        <Link key={a} href={`/toi/tin-cua-toi/${id}/sua`} className={secondaryButton}>
          {ACTION_LABELS.edit}
        </Link>
      );
    if (a === "appeal")
      return (
        <button key={a} type="button" className={secondaryButton} onClick={() => setAppealOpen(true)}>
          {ACTION_LABELS.appeal}
        </button>
      );
    if (a === "remove")
      return (
        <button key={a} type="button" className={secondaryButton} onClick={() => setConfirmRemove(true)}>
          {ACTION_LABELS.remove}
        </button>
      );
    return (
      <button key={a} type="button" className={secondaryButton} onClick={() => void run(a)}>
        {ACTION_LABELS[a]}
      </button>
    );
  };

  const missing = (problem as (Problem & { missing?: string[] }) | null)?.missing;
  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-col gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">{data.title}</h1>
        <div className="flex flex-wrap items-center gap-2">
          <StatusBadge status={status} />
          {data.expiresAt && status === "verified" ? (
            <span className="text-sm text-muted-foreground">Hiển thị đến {formatDateTime(data.expiresAt)}</span>
          ) : null}
        </div>
      </div>

      {status === "screening" ? (
        <p role="status" className="flex items-start gap-2 rounded-lg border border-border bg-muted/40 p-3">
          <Hourglass aria-hidden className="mt-0.5 size-4 shrink-0" />
          Đang sàng lọc tự động, thường dưới một phút. Trang tự cập nhật khi xong.
        </p>
      ) : null}
      {status === "pending_review" ? (
        <p role="status" className="rounded-lg border border-border bg-muted/40 p-3">Tin đang chờ cán bộ xác minh duyệt.</p>
      ) : null}
      {openAppeal ? (
        <p role="status" className="rounded-lg border border-border bg-muted/40 p-3">
          Kháng nghị đang chờ xử lý. Trong thời gian này tin không đổi trạng thái được.
        </p>
      ) : null}

      {actions.length > 0 ? <div className="flex flex-wrap gap-2">{actions.map(button)}</div> : null}
      {problem ? (
        <div role="alert" className="flex flex-col gap-1 rounded-lg border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive">
          <span>{messageFor(problem.code)}</span>
          {missing && missing.length > 0 ? (
            <ul className="list-disc pl-5">
              {missing.map((m) => (
                <li key={m}>{MISSING_LABELS[m] ?? m}</li>
              ))}
            </ul>
          ) : null}
          {missing ? (
            <Link href={missing.includes("owner_verification") ? "/toi/ho-so-chu" : "/toi"} className={linkClass}>
              Hoàn tất xác minh
            </Link>
          ) : null}
        </div>
      ) : null}

      <section className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">Lịch sử trạng thái</h2>
        {history.data ? <StatusTimeline items={history.data} /> : <p aria-busy="true">Đang tải…</p>}
      </section>

      {appeals.data && appeals.data.length > 0 ? (
        <section className="flex flex-col gap-2">
          <h2 className="text-lg font-semibold">Kháng nghị</h2>
          <ul className="flex flex-col gap-2">
            {appeals.data.map((a) => (
              <li key={a.id} className="rounded-lg border border-border p-3 text-sm">
                <p>
                  {formatDateTime(a.createdAt)} ·{" "}
                  {a.status === "open" ? "Đang chờ xử lý" : a.status === "accepted" ? "Được chấp nhận" : "Bị bác"}
                </p>
                <p>Lý do: {a.reason}</p>
                {a.decisionNote ? <p>Ghi chú của cán bộ: {a.decisionNote}</p> : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <Dialog open={confirmRemove} onOpenChange={setConfirmRemove}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Gỡ tin này?</DialogTitle>
            <DialogDescription>Tin đã gỡ không hiển thị lại được; muốn đăng lại phải tạo tin mới.</DialogDescription>
          </DialogHeader>
          <button
            type="button"
            className={secondaryButton}
            onClick={() => {
              setConfirmRemove(false);
              void run("remove");
            }}
          >
            Gỡ tin
          </button>
        </DialogContent>
      </Dialog>
      <AppealDialog id={id} open={appealOpen} onClose={() => setAppealOpen(false)} onDone={refresh} />
      <VersionConflictDialog
        open={conflict}
        onReload={() => {
          setConflict(false);
          void refetch();
        }}
      />
    </div>
  );
}

function AppealDialog({ id, open, onClose, onDone }: { id: string; open: boolean; onClose: () => void; onDone: () => Promise<void> }) {
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [pending, setPending] = useState(false);
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Kháng nghị quyết định</DialogTitle>
          <DialogDescription>Trong 7 ngày kể từ khi tin bị từ chối hoặc tạm ẩn. Mỗi quyết định kháng nghị một lần.</DialogDescription>
        </DialogHeader>
        <form
          className="flex flex-col gap-3"
          onSubmit={async (e) => {
            e.preventDefault();
            if (reason.trim().length < 1 || reason.length > 1000) return setError("Lý do từ 1 đến 1.000 ký tự");
            setPending(true);
            const { error: apiError } = await api.POST("/api/v1/listings/{listingId}/appeals", {
              params: { path: { listingId: id } },
              body: { reason },
            });
            setPending(false);
            if (apiError) return setError(messageFor(toProblem(apiError).code));
            toast.success("Đã gửi kháng nghị.");
            onClose();
            await onDone();
          }}
        >
          <label htmlFor="appeal-reason" className="font-medium">
            Lý do
          </label>
          <textarea id="appeal-reason" rows={4} className="rounded-lg border border-input p-2" value={reason} onChange={(e) => setReason(e.target.value)} />
          <FormError message={error} />
          <SubmitButton pending={pending}>Gửi kháng nghị</SubmitButton>
        </form>
      </DialogContent>
    </Dialog>
  );
}
