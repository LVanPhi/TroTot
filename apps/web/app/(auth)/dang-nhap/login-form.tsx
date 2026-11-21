"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useForm, useWatch } from "react-hook-form";

import { FormError, SubmitButton, TextField, linkClass } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";
import { applyProblemToForm, messageFor } from "@/lib/api/errors";
import { loginSchema, type LoginValues } from "@/lib/auth/schemas";
import { session } from "@/lib/auth/session";
import { ME_QUERY_KEY } from "@/lib/auth/use-auth";

const FIELDS = ["email", "password"] as const;

/** Only same-site paths: "/toi" yes, "//evil.test" or "https://…" no (open-redirect guard). */
export function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") ? next : "/toi";
}

export function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const queryClient = useQueryClient();
  const form = useForm<LoginValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: params.get("email") ?? "", password: "" },
  });
  const { errors, isSubmitting } = form.formState;
  const email = useWatch({ control: form.control, name: "email" });
  const unverified = errors.root?.server?.type === "email_not_verified";

  async function onSubmit(values: LoginValues) {
    const { data, error } = await api.POST("/api/v1/auth/login", { body: values });
    if (error !== undefined || !data) {
      const problem = applyProblemToForm(error, form.setError, FIELDS);
      // Tag this case so the form can offer the "enter verification code" link.
      if (problem.code === "auth.email_not_verified")
        form.setError("root.server", { type: "email_not_verified", message: messageFor(problem.code) });
      return;
    }
    session.signIn(data.accessToken);
    queryClient.setQueryData(ME_QUERY_KEY, data.user);
    router.replace(safeNext(params.get("next")));
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4">
      <TextField id="email" label="Email" type="email" autoComplete="email" error={errors.email?.message} {...form.register("email")} />
      <TextField id="password" label="Mật khẩu" type="password" autoComplete="current-password" error={errors.password?.message} {...form.register("password")} />
      <FormError message={errors.root?.server?.message} />
      {unverified ? (
        <Link href={`/xac-thuc-email?email=${encodeURIComponent(email.trim())}`} className={linkClass}>
          Nhập mã xác thực email
        </Link>
      ) : null}
      <SubmitButton pending={isSubmitting}>Đăng nhập</SubmitButton>
      <div className="flex flex-col items-center gap-2 text-sm text-muted-foreground">
        <Link href="/quen-mat-khau" className={linkClass}>
          Quên mật khẩu?
        </Link>
        <p>
          Chưa có tài khoản?{" "}
          <Link href="/dang-ky" className={linkClass}>
            Đăng ký
          </Link>
        </p>
      </div>
    </form>
  );
}
