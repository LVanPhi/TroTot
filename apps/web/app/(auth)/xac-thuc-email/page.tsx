import type { Metadata } from "next";
import { Suspense } from "react";

import { AuthCard } from "@/components/domain/form-controls";

import { VerifyEmailForm } from "./verify-email-form";

export const metadata: Metadata = { title: "Xác thực email" };

export default function VerifyEmailPage() {
  return (
    <AuthCard title="Xác thực email" description="Nhập mã 6 chữ số chúng tôi vừa gửi tới email của bạn.">
      <Suspense>
        <VerifyEmailForm />
      </Suspense>
    </AuthCard>
  );
}
