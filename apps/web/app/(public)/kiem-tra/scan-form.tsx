"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ImagePlus, X } from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { Controller, useForm } from "react-hook-form";

import { CommuneCombobox, type Commune } from "@/components/domain/commune-combobox";
import { FormError, linkClass, SubmitButton, TextField } from "@/components/domain/form-controls";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { useSession } from "@/lib/auth/use-auth";
import { formatDateTime } from "@/lib/format";
import { buildScanForm, IMAGE_TYPES, imageProblem, MAX_IMAGES, scanSchema, sharedText, type ScanFormValues } from "@/lib/scans/form";
import { takeSharedPayload } from "@/lib/scans/share-cache";
import { num } from "@/lib/scans/view";

export const QUOTA_KEY = ["scans", "quota"] as const;

function QuotaLine() {
  const { status } = useSession();
  const { data } = useQuery({
    queryKey: [...QUOTA_KEY, status],
    enabled: status !== "unknown",
    queryFn: async () => (await api.GET("/api/v1/scans/quota")).data ?? null,
  });
  if (!data) return null;
  const remaining = num(data.remaining);
  if (remaining > 0)
    return (
      <p className="text-sm text-muted-foreground">
        Bạn còn {remaining}/{num(data.limit)} lượt kiểm tra hôm nay.
      </p>
    );
  return (
    <p role="status" className="text-sm">
      Bạn đã dùng hết lượt hôm nay, lượt mới có từ {formatDateTime(data.resetAt)}.
      {status !== "signed_in" ? (
        <>
          {" "}
          <Link href="/dang-nhap?next=/kiem-tra" className={linkClass}>
            Đăng nhập
          </Link>{" "}
          để có thêm lượt.
        </>
      ) : null}
    </p>
  );
}

export function ScanForm() {
  const router = useRouter();
  const params = useSearchParams();
  const queryClient = useQueryClient();
  const [images, setImages] = useState<File[]>([]);
  const [imageError, setImageError] = useState<string | null>(null);
  const [commune, setCommune] = useState<Commune | null>(null);
  const [notice, setNotice] = useState<string | null>(
    params.get("shared") === "fallback" ? "Vui lòng chọn lại ảnh bạn muốn kiểm tra." : null,
  );

  const form = useForm<ScanFormValues>({
    resolver: zodResolver(scanSchema),
    defaultValues: { text: "", rent: "", wardCode: "", imageCount: 0 },
  });
  const { errors, isSubmitting } = form.formState;

  useEffect(() => {
    form.setValue("imageCount", images.length);
  }, [images, form]);

  useEffect(() => {
    if (params.get("shared") !== "1") return;
    void takeSharedPayload().then((shared) => {
      if (!shared) return;
      form.setValue("text", sharedText(shared));
      const ok = shared.images.filter((f) => imageProblem(f) === null).slice(0, MAX_IMAGES);
      setImages(ok);
      if (ok.length < shared.images.length) setNotice("Một số ảnh được chia sẻ không dùng được (quá 5 ảnh, sai định dạng hoặc quá 5 MB).");
      router.replace("/kiem-tra");
    });
  }, [params, form, router]);

  const previews = useMemo(() => images.map((f) => URL.createObjectURL(f)), [images]);
  useEffect(() => () => previews.forEach((u) => URL.revokeObjectURL(u)), [previews]);

  const addImages = (files: FileList | null) => {
    if (!files) return;
    const picked = Array.from(files);
    const bad = picked.map(imageProblem).find((p) => p !== null);
    const good = picked.filter((f) => imageProblem(f) === null);
    const next = [...images, ...good];
    setImageError(bad ?? (next.length > MAX_IMAGES ? "Tối đa 5 ảnh" : null));
    setImages(next.slice(0, MAX_IMAGES));
  };

  const submit = form.handleSubmit(async (values) => {
    const multipart = buildScanForm(values, images);
    const { data, error } = await api.POST("/api/v1/scans", {
      // The schema types the multipart body as one file; we send the whole FormData unchanged.
      body: multipart as unknown as string,
      bodySerializer: () => multipart,
    });
    if (error || !data) {
      const problem = applyProblemToForm(error, form.setError, ["text"]);
      if (problem.code === "validation.failed" && problem.errors?.claimedRentVnd)
        form.setError("rent", { type: "server", message: problem.errors.claimedRentVnd[0] });
      if (problem.code === "validation.failed" && problem.errors?.claimedWardCode)
        form.setError("wardCode", { type: "server", message: problem.errors.claimedWardCode[0] });
      if (problem.code === "scan.quota_exceeded") void queryClient.invalidateQueries({ queryKey: QUOTA_KEY });
      return;
    }
    const { scanId } = data;
    void queryClient.invalidateQueries({ queryKey: QUOTA_KEY });
    router.push(`/kiem-tra/${scanId}`);
  });

  return (
    <form onSubmit={submit} noValidate className="flex flex-col gap-5">
      {notice ? (
        <p role="status" className="rounded-lg border border-border bg-muted/40 p-3 text-sm">
          {notice}
        </p>
      ) : null}
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="scan-text">Nội dung tin</Label>
        <textarea
          id="scan-text"
          rows={8}
          placeholder="Dán nội dung tin phòng trọ hoặc việc làm thêm bạn thấy trên mạng"
          aria-invalid={errors.text ? true : undefined}
          aria-describedby={errors.text ? "scan-text-error" : undefined}
          className="rounded-lg border border-input p-3 text-base"
          {...form.register("text")}
        />
        {errors.text ? (
          <p id="scan-text-error" role="alert" className="text-sm text-destructive">
            {errors.text.message}
          </p>
        ) : null}
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor="scan-images">Ảnh chụp tin (tối đa 5)</Label>
        <label
          htmlFor="scan-images"
          className="inline-flex min-h-11 w-fit cursor-pointer items-center gap-2 rounded-lg border border-dashed border-border px-4 hover:bg-muted"
        >
          <ImagePlus aria-hidden className="size-5" />
          Tải ảnh
        </label>
        <input
          id="scan-images"
          type="file"
          multiple
          accept={IMAGE_TYPES.join(",")}
          className="sr-only"
          onChange={(e) => {
            addImages(e.target.files);
            e.target.value = "";
          }}
        />
        {images.length > 0 ? (
          <ul className="flex flex-wrap gap-2">
            {images.map((file, i) => (
              <li key={`${file.name}-${i}`} className="relative">
                {/* eslint-disable-next-line @next/next/no-img-element -- local blob preview, not optimisable */}
                <img src={previews[i]} alt={`Ảnh số ${i + 1}`} width={80} height={80} className="size-20 rounded-lg object-cover" />
                <button
                  type="button"
                  aria-label={`Bỏ ảnh số ${i + 1}`}
                  onClick={() => setImages(images.filter((_, j) => j !== i))}
                  className="absolute -top-2 -right-2 inline-flex size-8 items-center justify-center rounded-full border border-border bg-background"
                >
                  <X aria-hidden className="size-4" />
                </button>
              </li>
            ))}
          </ul>
        ) : null}
        {imageError ? (
          <p role="alert" className="text-sm text-destructive">
            {imageError}
          </p>
        ) : null}
      </div>

      <details className="rounded-lg border border-border p-3">
        <summary className="min-h-11 cursor-pointer content-center font-medium">Thêm giá và khu vực (giúp so giá)</summary>
        <div className="mt-3 flex flex-col gap-4">
          <TextField id="scan-rent" label="Giá thuê mỗi tháng" inputMode="numeric" placeholder="Ví dụ 2.500.000 hoặc 2tr5" error={errors.rent?.message} {...form.register("rent")} />
          <Controller
            control={form.control}
            name="wardCode"
            render={({ field }) => (
              <CommuneCombobox
                label="Phường/xã"
                value={commune}
                error={errors.wardCode?.message}
                onChange={(c) => {
                  setCommune(c);
                  field.onChange(c?.code ?? "");
                }}
              />
            )}
          />
        </div>
      </details>

      <FormError message={errors.root?.server?.message} />
      <QuotaLine />
      <SubmitButton pending={isSubmitting}>Kiểm tra</SubmitButton>
      <p className="text-xs text-muted-foreground">
        Nội dung bạn gửi được giữ 30 ngày để phục vụ kết quả rồi tự xóa. Hệ thống không mở các đường link trong tin.
      </p>
    </form>
  );
}
