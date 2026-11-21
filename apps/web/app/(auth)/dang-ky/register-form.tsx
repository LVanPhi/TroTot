"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useForm } from "react-hook-form";

import { FormError, SubmitButton, TextField, linkClass } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { registerSchema, type RegisterValues } from "@/lib/auth/schemas";

const FIELDS = ["accountType", "fullName", "email", "password"] as const;

const choices = [
  { value: "student", title: "Sinh viên", detail: "Đăng ký bằng email do trường cấp" },
  { value: "owner", title: "Chủ trọ / nhà tuyển dụng", detail: "Email bất kỳ; cần xác minh hồ sơ trước khi đăng tin" },
] as const;

export function RegisterForm() {
  const router = useRouter();
  const form = useForm<RegisterValues>({
    resolver: zodResolver(registerSchema),
    defaultValues: { accountType: "student", fullName: "", email: "", password: "" },
  });
  const { errors, isSubmitting } = form.formState;

  async function onSubmit(values: RegisterValues) {
    const { error } = await api.POST("/api/v1/auth/register", { body: values });
    if (error !== undefined) {
      applyProblemToForm(error, form.setError, FIELDS);
      return;
    }
    router.push(`/xac-thuc-email?email=${encodeURIComponent(values.email.trim())}`);
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4">
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-sm font-medium">Bạn là</legend>
        {choices.map((choice) => (
          <label
            key={choice.value}
            className="flex min-h-11 cursor-pointer items-start gap-3 rounded-lg border border-border p-3 has-[:checked]:border-primary has-[:checked]:bg-primary/5"
          >
            <input type="radio" value={choice.value} className="mt-1 size-4" {...form.register("accountType")} />
            <span className="flex flex-col">
              <span className="font-medium">{choice.title}</span>
              <span className="text-sm text-muted-foreground">{choice.detail}</span>
            </span>
          </label>
        ))}
        {errors.accountType ? <p role="alert" className="text-sm text-destructive">{errors.accountType.message}</p> : null}
      </fieldset>
      <TextField id="fullName" label="Họ và tên" autoComplete="name" error={errors.fullName?.message} {...form.register("fullName")} />
      <TextField id="email" label="Email" type="email" autoComplete="email" error={errors.email?.message} {...form.register("email")} />
      <TextField
        id="password"
        label="Mật khẩu"
        type="password"
        autoComplete="new-password"
        hint="Ít nhất 10 ký tự, không dùng mật khẩu quá phổ biến"
        error={errors.password?.message}
        {...form.register("password")}
      />
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>Đăng ký</SubmitButton>
      <p className="text-center text-sm text-muted-foreground">
        Đã có tài khoản?{" "}
        <Link href="/dang-nhap" className={linkClass}>
          Đăng nhập
        </Link>
      </p>
    </form>
  );
}
