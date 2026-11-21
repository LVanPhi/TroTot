import type { Metadata } from "next";
import { Suspense } from "react";

import { AuthCard } from "@/components/domain/form-controls";

import { LoginForm } from "./login-form";

export const metadata: Metadata = { title: "Đăng nhập" };

export default function LoginPage() {
  return (
    <AuthCard title="Đăng nhập">
      <Suspense>
        <LoginForm />
      </Suspense>
    </AuthCard>
  );
}
