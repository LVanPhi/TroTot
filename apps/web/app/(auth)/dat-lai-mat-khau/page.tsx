import type { Metadata } from "next";
import { Suspense } from "react";

import { AuthCard } from "@/components/domain/form-controls";

import { ResetForm } from "./reset-form";

export const metadata: Metadata = { title: "Đặt lại mật khẩu" };

export default function ResetPasswordPage() {
  return (
    <AuthCard title="Đặt lại mật khẩu" description="Đặt lại xong, mọi phiên đăng nhập cũ sẽ bị đăng xuất.">
      <Suspense>
        <ResetForm />
      </Suspense>
    </AuthCard>
  );
}
