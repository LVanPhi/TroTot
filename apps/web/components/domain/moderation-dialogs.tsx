"use client";

import { FileSearch } from "lucide-react";
import { useState, type ReactNode } from "react";
import { toast } from "sonner";

import { FormError, secondaryButton, SubmitButton } from "@/components/domain/form-controls";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { api, API_BASE_URL } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";

/**
 * A decision that needs a text (reject/hide reason, report or appeal note). `send` returns an error code or null;
 * the dialog stays open on error.
 */
export function ReasonDialog({
  trigger,
  title,
  description,
  label,
  required = true,
  maxLength = 500,
  confirm,
  extra,
  send,
}: {
  trigger: ReactNode;
  title: string;
  description?: string;
  label: string;
  required?: boolean;
  maxLength?: number;
  confirm: string;
  extra?: ReactNode;
  send: (text: string) => Promise<string | null>;
}) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [pending, setPending] = useState(false);
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger className={secondaryButton}>{trigger}</DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          {description ? <DialogDescription>{description}</DialogDescription> : null}
        </DialogHeader>
        <form
          className="flex flex-col gap-3"
          onSubmit={async (e) => {
            e.preventDefault();
            if (required && text.trim() === "") return setError("Bắt buộc nhập");
            if (text.length > maxLength) return setError(`Tối đa ${maxLength} ký tự`);
            setPending(true);
            const code = await send(text.trim());
            setPending(false);
            if (code) return setError(messageFor(code));
            setText("");
            setError(undefined);
            setOpen(false);
          }}
        >
          <label htmlFor="reason-text" className="font-medium">
            {label}
          </label>
          <textarea id="reason-text" rows={4} className="rounded-lg border border-input p-2" value={text} onChange={(e) => setText(e.target.value)} />
          {extra}
          <FormError message={error} />
          <SubmitButton pending={pending}>{confirm}</SubmitButton>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Opening an owner document (US-605, BR-10): the purpose is required; every link issued is logged; the link expires
 * after 5 minutes and opens in a new tab (the file is served with no-store).
 */
export function OpenDocumentButton({ documentId }: { documentId: number }) {
  return (
    <ReasonDialog
      trigger={
        <span className="inline-flex items-center gap-2">
          <FileSearch aria-hidden className="size-4" />
          Mở giấy tờ
        </span>
      }
      title="Mở giấy tờ xác minh"
      description="Lần mở được ghi nhật ký kèm mục đích. Link hết hạn sau 5 phút."
      label="Mục đích mở giấy tờ"
      maxLength={200}
      confirm="Mở"
      send={async (purpose) => {
        const { data, error } = await api.POST("/api/v1/moderation/owner-documents/{id}/access", {
          params: { path: { id: documentId } },
          body: { purpose },
        });
        if (error || !data) return toProblem(error).code;
        window.open(`${API_BASE_URL}${data.url}`, "_blank", "noopener,noreferrer");
        toast.success("Đã mở giấy tờ ở thẻ mới.");
        return null;
      }}
    />
  );
}
