"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { FileUp, Trash2 } from "lucide-react";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { toast } from "sonner";

import { FormError, secondaryButton, SubmitButton, TextField } from "@/components/domain/form-controls";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";
import { applyProblemToForm, messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { formatDateTime } from "@/lib/format";
import { DOC_STATUS, DOC_TYPES, ownerProfileSchema, type OwnerProfileValues } from "@/lib/listings/model";

const PROFILE_KEY = ["me", "owner-profile"] as const;
const DOCS_KEY = ["me", "owner-documents"] as const;
const MAX_DOC_BYTES = 10 * 1024 * 1024;
const DOC_MIME = ["image/jpeg", "image/png", "image/webp", "application/pdf"];

/** US-501, US-502, US-504: owner profile, encrypted documents with mandatory consent. */
export function OwnerProfile() {
  const profile = useQuery({
    queryKey: PROFILE_KEY,
    queryFn: async () => {
      const result = await api.GET("/api/v1/me/owner-profile");
      return result.response.status === 404 ? null : (result.data ?? null);
    },
  });
  if (profile.isPending) return <p aria-busy="true">Đang tải…</p>;
  return (
    <div className="flex flex-col gap-6">
      <section className="flex flex-col gap-3 rounded-xl border border-border p-4">
        <h2 className="text-lg font-semibold">Thông tin chủ trọ / nhà tuyển dụng</h2>
        {profile.data?.verifiedUntil ? (
          <p role="status" className="text-sm">Đã xác minh đến {formatDateTime(profile.data.verifiedUntil)}.</p>
        ) : (
          <p className="text-sm text-muted-foreground">Hồ sơ cần ít nhất một giấy tờ được cán bộ chấp nhận để được xác minh.</p>
        )}
        <ProfileForm
          initial={{
            ownerType: profile.data?.ownerType === "business" ? "business" : "individual",
            businessName: profile.data?.businessName ?? "",
            taxCode: profile.data?.taxCode ?? "",
          }}
        />
      </section>
      {profile.data ? <Documents /> : <p>Lưu hồ sơ trước để tải giấy tờ.</p>}
    </div>
  );
}

function ProfileForm({ initial }: { initial: OwnerProfileValues }) {
  const queryClient = useQueryClient();
  const form = useForm<OwnerProfileValues>({ resolver: zodResolver(ownerProfileSchema), defaultValues: initial });
  const { errors, isSubmitting } = form.formState;
  const business = useWatch({ control: form.control, name: "ownerType" }) === "business";

  const submit = form.handleSubmit(async (v) => {
    const { error } = await api.PUT("/api/v1/me/owner-profile", {
      body: { ownerType: v.ownerType, businessName: business ? v.businessName : null, taxCode: business ? v.taxCode : null },
    });
    if (error) return void applyProblemToForm(error, form.setError, ["businessName", "taxCode", "ownerType"]);
    toast.success("Đã lưu hồ sơ.");
    await queryClient.invalidateQueries({ queryKey: PROFILE_KEY });
  });

  return (
    <form onSubmit={submit} noValidate className="flex flex-col gap-4">
      <fieldset className="flex gap-4">
        <legend className="mb-1 font-medium">Bạn là</legend>
        <label className="flex min-h-11 items-center gap-2">
          <input type="radio" value="individual" className="size-4" {...form.register("ownerType")} />
          Cá nhân
        </label>
        <label className="flex min-h-11 items-center gap-2">
          <input type="radio" value="business" className="size-4" {...form.register("ownerType")} />
          Doanh nghiệp / hộ kinh doanh
        </label>
      </fieldset>
      {business ? (
        <>
          <TextField id="businessName" label="Tên doanh nghiệp" error={errors.businessName?.message} {...form.register("businessName")} />
          <TextField id="taxCode" label="Mã số thuế" placeholder="0101234567 hoặc 0101234567-001" error={errors.taxCode?.message} {...form.register("taxCode")} />
        </>
      ) : null}
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>Lưu hồ sơ</SubmitButton>
    </form>
  );
}

function Documents() {
  const queryClient = useQueryClient();
  const docs = useQuery({ queryKey: DOCS_KEY, queryFn: async () => (await api.GET("/api/v1/me/owner-documents")).data ?? [] });
  const [docType, setDocType] = useState<keyof typeof DOC_TYPES>("id_card");
  const [file, setFile] = useState<File | null>(null);
  const [consent, setConsent] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [pending, setPending] = useState(false);

  const upload = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!consent) return setError("Cần đồng ý xử lý dữ liệu cá nhân trước khi tải giấy tờ");
    if (!file) return setError("Chọn tệp giấy tờ");
    if (!DOC_MIME.includes(file.type)) return setError("Chỉ nhận ảnh JPEG, PNG, WebP hoặc PDF");
    if (file.size > MAX_DOC_BYTES) return setError("Tệp tối đa 10 MB");
    setError(undefined);
    setPending(true);
    const form = new FormData();
    form.append("file", file);
    form.append("docType", docType);
    form.append("consent", "true");
    const { error: apiError } = await api.POST("/api/v1/me/owner-documents", { body: form as unknown as string, bodySerializer: () => form });
    setPending(false);
    if (apiError) return setError(messageFor(toProblem(apiError).code));
    toast.success("Đã tải giấy tờ. Cán bộ sẽ xem xét.");
    setFile(null);
    await queryClient.invalidateQueries({ queryKey: DOCS_KEY });
  };

  const remove = async (id: number) => {
    const { error: apiError } = await api.DELETE("/api/v1/me/owner-documents/{id}", { params: { path: { id } } });
    if (apiError) return toast.error(messageFor(toProblem(apiError).code));
    await queryClient.invalidateQueries({ queryKey: DOCS_KEY });
  };

  return (
    <section className="flex flex-col gap-4 rounded-xl border border-border p-4">
      <h2 className="text-lg font-semibold">Giấy tờ xác minh</h2>
      {docs.data && docs.data.length > 0 ? (
        <ul className="flex flex-col divide-y divide-border">
          {docs.data.map((d) => (
            <li key={String(d.id)} className="flex flex-wrap items-center justify-between gap-2 py-2">
              <span>
                {DOC_TYPES[d.docType as keyof typeof DOC_TYPES] ?? d.docType} · {formatDateTime(d.createdAt)} ·{" "}
                {d.erasedAt ? "Đã xóa tệp" : (DOC_STATUS[d.status as keyof typeof DOC_STATUS] ?? d.status)}
              </span>
              {!d.erasedAt ? (
                <button type="button" className={`${secondaryButton} gap-2`} onClick={() => void remove(Number(d.id))} aria-label="Xóa giấy tờ">
                  <Trash2 aria-hidden className="size-4" />
                  Xóa
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-muted-foreground">Chưa có giấy tờ nào.</p>
      )}
      <form onSubmit={upload} className="flex flex-col gap-3" noValidate>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="docType">Loại giấy tờ</Label>
          <select id="docType" className="h-11 rounded-lg border border-input px-3" value={docType} onChange={(e) => setDocType(e.target.value as keyof typeof DOC_TYPES)}>
            {Object.entries(DOC_TYPES).map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </div>
        <label className="inline-flex min-h-11 w-fit cursor-pointer items-center gap-2 rounded-lg border border-dashed border-border px-4 hover:bg-muted">
          <FileUp aria-hidden className="size-5" />
          {file ? file.name : "Chọn tệp (ảnh hoặc PDF, tối đa 10 MB)"}
          <input type="file" accept={DOC_MIME.join(",")} className="sr-only" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        </label>
        <label className="flex items-start gap-2 text-sm">
          <input type="checkbox" className="mt-1 size-4" checked={consent} onChange={(e) => setConsent(e.target.checked)} />
          Tôi đồng ý để Trọ &amp; Việc Sạch xử lý giấy tờ này chỉ nhằm xác minh tư cách chủ trọ / nhà tuyển dụng. Tệp được mã
          hóa, chỉ cán bộ xác minh mở được và mỗi lần mở đều được ghi nhật ký. Tôi có thể xóa tệp bất cứ lúc nào.
        </label>
        <FormError message={error} />
        <SubmitButton pending={pending}>Tải giấy tờ</SubmitButton>
      </form>
    </section>
  );
}
