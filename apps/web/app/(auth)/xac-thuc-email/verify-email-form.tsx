"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter, useSearchParams } from "next/navigation";
import { useForm } from "react-hook-form";
import { toast } from "sonner";

import { FormError, SubmitButton, TextField, secondaryButton } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { verifyEmailSchema, type VerifyEmailValues } from "@/lib/auth/schemas";

const FIELDS = ["email", "code"] as const;

export function VerifyEmailForm() {
  const router = useRouter();
  const params = useSearchParams();
  const form = useForm<VerifyEmailValues>({
    resolver: zodResolver(verifyEmailSchema),
    defaultValues: { email: params.get("email") ?? "", code: "" },
  });
  const { errors, isSubmitting } = form.formState;

  async function onSubmit(values: VerifyEmailValues) {
    const { error } = await api.POST("/api/v1/auth/email/verify", { body: values });
    if (error !== undefined) {
      applyProblemToForm(error, form.setError, FIELDS);
      return;
    }
    toast.success("Email đã được xác thực. Mời bạn đăng nhập.");
    router.push(`/dang-nhap?email=${encodeURIComponent(values.email.trim())}`);
  }

  async function resend() {
    const email = form.getValues("email").trim();
    if (!email) {
      form.setError("email", { message: "Nhập email để nhận lại mã" });
      return;
    }
    await api.POST("/api/v1/auth/email/resend", { body: { email } });
    // The API always answers 204, whether or not the email exists.
    toast.info("Nếu email này đang chờ xác thực, mã mới đã được gửi.");
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4">
      <TextField id="email" label="Email" type="email" autoComplete="email" error={errors.email?.message} {...form.register("email")} />
      <TextField
        id="code"
        label="Mã xác thực"
        inputMode="numeric"
        autoComplete="one-time-code"
        maxLength={6}
        hint="Mã gồm 6 chữ số, hiệu lực 10 phút"
        error={errors.code?.message}
        {...form.register("code")}
      />
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>Xác thực</SubmitButton>
      <button type="button" onClick={resend} className={secondaryButton}>
        Gửi lại mã
      </button>
    </form>
  );
}
