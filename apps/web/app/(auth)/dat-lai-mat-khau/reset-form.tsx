"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useForm } from "react-hook-form";
import { toast } from "sonner";

import { FormError, SubmitButton, TextField, linkClass } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { resetSchema, type ResetValues } from "@/lib/auth/schemas";

export function ResetForm() {
  const router = useRouter();
  const token = useSearchParams().get("token") ?? "";
  const form = useForm<ResetValues>({ resolver: zodResolver(resetSchema), defaultValues: { newPassword: "", confirm: "" } });
  const { errors, isSubmitting } = form.formState;

  if (!token) {
    return (
      <p role="alert" className="text-destructive">
        Link không hợp lệ.{" "}
        <Link href="/quen-mat-khau" className={linkClass}>
          Xin link mới
        </Link>
      </p>
    );
  }

  async function onSubmit(values: ResetValues) {
    const { error } = await api.POST("/api/v1/auth/password/reset", { body: { token, newPassword: values.newPassword } });
    if (error !== undefined) {
      applyProblemToForm(error, form.setError, ["newPassword"]);
      return;
    }
    toast.success("Đã đặt mật khẩu mới. Mời bạn đăng nhập lại.");
    router.push("/dang-nhap");
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4">
      <TextField
        id="newPassword"
        label="Mật khẩu mới"
        type="password"
        autoComplete="new-password"
        hint="Ít nhất 10 ký tự, không dùng mật khẩu quá phổ biến"
        error={errors.newPassword?.message}
        {...form.register("newPassword")}
      />
      <TextField id="confirm" label="Nhập lại mật khẩu mới" type="password" autoComplete="new-password" error={errors.confirm?.message} {...form.register("confirm")} />
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>Đặt mật khẩu mới</SubmitButton>
    </form>
  );
}
