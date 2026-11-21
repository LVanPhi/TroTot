"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useState } from "react";
import { useForm } from "react-hook-form";

import { AuthCard, FormError, SubmitButton, TextField, linkClass } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { forgotSchema, type ForgotValues } from "@/lib/auth/schemas";

export default function ForgotPasswordPage() {
  const [sent, setSent] = useState(false);
  const form = useForm<ForgotValues>({ resolver: zodResolver(forgotSchema), defaultValues: { email: "" } });
  const { errors, isSubmitting } = form.formState;

  async function onSubmit(values: ForgotValues) {
    const { error } = await api.POST("/api/v1/auth/password/forgot", { body: values });
    if (error !== undefined) {
      applyProblemToForm(error, form.setError, ["email"]);
      return;
    }
    setSent(true);
  }

  return (
    <AuthCard title="Quên mật khẩu" description="Nhập email đã đăng ký, chúng tôi gửi link đặt lại mật khẩu (hiệu lực 30 phút).">
      {sent ? (
        <p role="status" className="rounded-lg border border-border bg-muted/40 p-4">
          Nếu email này đã đăng ký, link đặt lại mật khẩu đã được gửi. Hãy kiểm tra hộp thư (kể cả thư rác).
        </p>
      ) : (
        <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4">
          <TextField id="email" label="Email" type="email" autoComplete="email" error={errors.email?.message} {...form.register("email")} />
          <FormError message={errors.root?.server?.message} />
          <SubmitButton pending={isSubmitting}>Gửi link đặt lại</SubmitButton>
        </form>
      )}
      <Link href="/dang-nhap" className={`${linkClass} text-center text-sm`}>
        Quay lại đăng nhập
      </Link>
    </AuthCard>
  );
}
