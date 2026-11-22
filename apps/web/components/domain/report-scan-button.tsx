"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { Flag } from "lucide-react";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";
import { z } from "zod";

import { FormError, secondaryButton, SubmitButton } from "@/components/domain/form-controls";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { api } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { REPORT_CATEGORIES } from "@/lib/scans/view";

const schema = z.object({
  category: z.enum(["scam", "misleading", "inappropriate", "other"], { message: "Chọn một lý do" }),
  description: z.string().trim().max(1000, "Tối đa 1.000 ký tự"),
});
type Values = z.infer<typeof schema>;

/** POST /scans/{id}/reports or /listings/{id}/reports (US-406, neutral category labels). */
export function ReportButton({ target }: { target: { scanId: string } | { listingId: string } }) {
  const [open, setOpen] = useState(false);
  const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { description: "" } });
  const { errors, isSubmitting } = form.formState;

  const submit = form.handleSubmit(async (values) => {
    const body = { category: values.category, description: values.description || null };
    const { error } =
      "scanId" in target
        ? await api.POST("/api/v1/scans/{scanId}/reports", { params: { path: { scanId: target.scanId } }, body })
        : await api.POST("/api/v1/listings/{listingId}/reports", { params: { path: { listingId: target.listingId } }, body });
    if (error) {
      applyProblemToForm(error, form.setError, ["category", "description"]);
      return;
    }
    setOpen(false);
    toast.success("Đã gửi báo cáo. Cán bộ sẽ xem xét.");
  });

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger className={secondaryButton}>
        <Flag aria-hidden className="mr-2 size-4" />
        Báo cáo
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{"scanId" in target ? "Báo cáo nội dung này" : "Báo cáo tin này"}</DialogTitle>
          <DialogDescription>Báo cáo giúp cán bộ đối chiếu. Thông tin của bạn không hiển thị cho người khác.</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-1 font-medium">Lý do</legend>
            {REPORT_CATEGORIES.map((c) => (
              <label key={c.value} className="flex min-h-11 items-center gap-2">
                <input type="radio" value={c.value} {...form.register("category")} className="size-4" />
                {c.label}
              </label>
            ))}
            {errors.category ? (
              <p role="alert" className="text-sm text-destructive">
                {errors.category.message}
              </p>
            ) : null}
          </fieldset>
          <div className="flex flex-col gap-1.5">
            <label htmlFor="report-description" className="font-medium">
              Mô tả thêm (không bắt buộc)
            </label>
            <textarea id="report-description" rows={3} className="rounded-lg border border-input p-2" {...form.register("description")} />
            {errors.description ? (
              <p role="alert" className="text-sm text-destructive">
                {errors.description.message}
              </p>
            ) : null}
          </div>
          <FormError message={errors.root?.server?.message} />
          <SubmitButton pending={isSubmitting}>Gửi báo cáo</SubmitButton>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Report button of the scan result page. */
export function ReportScanButton({ scanId }: { scanId: string }) {
  return <ReportButton target={{ scanId }} />;
}
